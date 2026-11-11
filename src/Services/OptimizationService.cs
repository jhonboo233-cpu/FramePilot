using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FramePilot.Models;
using Microsoft.Win32;

namespace FramePilot.Services;

internal sealed class OptimizationService
{
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    public static void RestartAsAdministrator()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not string path) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
    }

    public static List<string> BuildPlan(OptimizationOptions options)
    {
        var plan = new List<string>();
        if (options.GameMode) plan.Add("Включить «Режим игры» Windows");
        if (options.DisableCapture) plan.Add("Отключить фоновую запись Xbox Game Bar");
        if (options.HighPerformance) plan.Add("Активировать схему «Высокая производительность»");
        if (options.StopServices) plan.Add("Временно остановить Windows Search, печать и факс, если они работают");
        if (options.OptimizeShaderCache) plan.Add("Проверить и подготовить кэш шейдеров без удаления файлов");
        if (options.Hags) plan.Add("Включить HAGS для последующего A/B-теста");
        if (options.RestorePoint) plan.Add("Попытаться создать точку восстановления Windows");
        if (plan.Count == 0) plan.Add("Ничего не выбрано — настройки не изменятся");
        return plan;
    }

    public Task<OperationResult> ApplyAsync(OptimizationOptions options) => Task.Run(async () =>
    {
        var result = new OperationResult();
        if (!OperatingSystem.IsWindows())
        {
            result.Messages.Add("Оптимизация доступна только в Windows.");
            return result;
        }

        var snapshot = new BackupSnapshot();
        if (options.GameMode || options.DisableCapture || options.HighPerformance || options.Hags || options.StopServices)
            snapshot.ActivePowerScheme = GetActivePowerScheme();

        if (options.RestorePoint)
        {
            if (!IsAdministrator())
                result.Messages.Add("Точка восстановления: пропущено, требуются права администратора. Резервная копия параметров всё равно будет создана.");
            else
            {
                var restore = await ProcessTools.RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'FramePilot — перед оптимизацией' -RestorePointType MODIFY_SETTINGS\"");
                result.Messages.Add(restore.ExitCode == 0
                    ? "Точка восстановления Windows создана."
                    : "Точка восстановления Windows не создана. Возможно, защита системы отключена или действует системный интервал. Локальная резервная копия будет создана.");
            }
        }

        if (options.GameMode)
        {
            var changed = CaptureAndSet(snapshot, Registry.CurrentUser, "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, result);
            changed |= CaptureAndSet(snapshot, Registry.CurrentUser, "HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, result);
            if (changed) snapshot.AppliedChanges.Add("Режим игры включён");
        }

        if (options.DisableCapture)
        {
            var changed = CaptureAndSet(snapshot, Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, result);
            changed |= CaptureAndSet(snapshot, Registry.CurrentUser, "HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0, result);
            if (changed) snapshot.AppliedChanges.Add("Фоновая запись отключена");
        }

        if (options.HighPerformance)
        {
            var power = await ProcessTools.RunAsync("powercfg.exe", $"/setactive {HighPerformanceGuid}");
            result.Messages.Add(power.ExitCode == 0
                ? "Активирована схема «Высокая производительность»."
                : "Схема «Высокая производительность» недоступна. Выберите доступную схему в настройках электропитания Windows.");
            if (power.ExitCode == 0) snapshot.AppliedChanges.Add("Изменена схема электропитания");
        }

        if (options.OptimizeShaderCache)
        {
            if (PrepareShaderCache(result))
                snapshot.AppliedChanges.Add("Кэш шейдеров проверен и подготовлен (файлы не удалялись)");
        }

        if (options.Hags)
        {
            if (!IsAdministrator())
                result.Messages.Add("HAGS не изменён: требуются права администратора.");
            else
            {
                if (CaptureAndSet(snapshot, Registry.LocalMachine, "HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, result))
                {
                    snapshot.AppliedChanges.Add("HAGS включён");
                    result.RestartRecommended = true;
                }
            }
        }

        if (options.StopServices)
            StopOptionalServices(snapshot, result);

        if (snapshot.RegistryValues.Count > 0 || snapshot.AppliedChanges.Count > 0)
        {
            AppStorage.SaveBackup(snapshot);
            result.Messages.Add($"Резервная копия сохранена: {AppStorage.BackupPath}");
            result.Success = true;
        }
        else result.Messages.Add("Настройки не изменены. Выберите хотя бы один параметр.");

        result.Messages.Add("Проверьте результат в одинаковой игровой сцене и сравните 1% Low, а не только средний FPS.");
        return result;
    });

    public Task<OperationResult> RestoreAsync() => Task.Run(async () =>
    {
        var result = new OperationResult();
        var snapshot = AppStorage.LoadBackup();
        if (snapshot == null)
        {
            result.Messages.Add("Резервная копия не найдена. Сначала примените оптимизацию.");
            return result;
        }
        if (!OperatingSystem.IsWindows())
        {
            result.Messages.Add("Восстановление доступно только в Windows.");
            return result;
        }

        var errors = 0;
        foreach (var item in snapshot.RegistryValues)
        {
            try
            {
                var root = item.Root == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                if (item.Root == "HKLM" && !IsAdministrator())
                    throw new UnauthorizedAccessException("Нужны права администратора");
                using var key = root.CreateSubKey(item.Path, true);
                if (item.Existed) key.SetValue(item.Name, item.Value, RegistryValueKind.DWord);
                else if (key.GetValue(item.Name) != null) key.DeleteValue(item.Name, false);
                result.Messages.Add($"Восстановлено: {item.Path} → {item.Name}");
            }
            catch (Exception ex)
            {
                errors++;
                result.Messages.Add($"Не удалось восстановить {item.Name}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ActivePowerScheme))
        {
            var power = await ProcessTools.RunAsync("powercfg.exe", $"/setactive {snapshot.ActivePowerScheme}");
            if (power.ExitCode == 0) result.Messages.Add("Исходная схема электропитания восстановлена.");
            else { errors++; result.Messages.Add("Не удалось восстановить схему электропитания."); }
        }

        if (snapshot.Services.Any(x => x.WasRunning))
        {
            if (!IsAdministrator())
            {
                errors++;
                result.Messages.Add("Службы не восстановлены: запустите FramePilot с правами администратора и повторите откат.");
            }
            else
            {
                foreach (var service in snapshot.Services.Where(x => x.WasRunning))
                {
                    if (SetServiceState(service.Name, true, result))
                        result.Messages.Add($"Служба запущена: {FriendlyServiceName(service.Name)}");
                    else errors++;
                }
            }
        }

        result.RestartRecommended = snapshot.RegistryValues.Any(x => x.Name == "HwSchMode");
        result.Success = errors == 0;
        result.Messages.Add(result.Success
            ? "Откат завершён. Точка восстановления Windows не удаляется."
            : "Откат завершён с ошибками. Проверьте сообщения выше.");
        return result;
    });

    private static readonly string[] OptionalServices = { "WSearch", "Spooler", "Fax" };

    private static void StopOptionalServices(BackupSnapshot snapshot, OperationResult result)
    {
        if (!IsAdministrator())
        {
            result.Messages.Add("Службы не остановлены: требуются права администратора.");
            return;
        }

        foreach (var name in OptionalServices)
        {
            try
            {
                using var found = new ManagementObjectSearcher($"SELECT Name, State FROM Win32_Service WHERE Name='{name}'").Get();
                using var service = found.Cast<ManagementObject>().FirstOrDefault();
                if (service == null) continue;
                snapshot.Services.Add(new ServiceBackup
                {
                    Name = name,
                    WasRunning = string.Equals(Convert.ToString(service["State"]), "Running", StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex)
            {
                result.Messages.Add($"Не удалось проверить службу {FriendlyServiceName(name)}: {ex.Message}");
            }
        }

        var running = snapshot.Services.Where(x => x.WasRunning).ToList();
        if (running.Count == 0)
        {
            result.Messages.Add("Службы из безопасного списка уже остановлены или отсутствуют. Изменения не требуются.");
            return;
        }

        AppStorage.SaveBackup(snapshot);
        result.Messages.Add("Состояние служб сохранено до остановки. Тип запуска служб не изменяется.");
        var stopped = new List<string>();
        foreach (var service in running)
        {
            if (SetServiceState(service.Name, false, result))
                stopped.Add(FriendlyServiceName(service.Name));
        }
        if (stopped.Count > 0)
        {
            snapshot.AppliedChanges.Add($"Временно остановлены службы: {string.Join(", ", stopped)}");
            result.Messages.Add("После игры нажмите «Восстановить настройки»: будут запущены только службы, работавшие до оптимизации.");
        }
    }

    private static bool SetServiceState(string name, bool start, OperationResult result)
    {
        try
        {
            using var found = new ManagementObjectSearcher($"SELECT Name, State FROM Win32_Service WHERE Name='{name}'").Get();
            using var service = found.Cast<ManagementObject>().FirstOrDefault();
            if (service == null)
            {
                result.Messages.Add($"Служба {FriendlyServiceName(name)} не найдена.");
                return true;
            }
            var running = string.Equals(Convert.ToString(service["State"]), "Running", StringComparison.OrdinalIgnoreCase);
            if (running == start) return true;
            var output = service.InvokeMethod(start ? "StartService" : "StopService", null);
            var code = Convert.ToUInt32(output);
            if (code == 0) return true;
            result.Messages.Add($"Служба {FriendlyServiceName(name)}: Windows вернула код {code} при {(start ? "запуске" : "остановке")}.");
            return false;
        }
        catch (Exception ex)
        {
            result.Messages.Add($"Служба {FriendlyServiceName(name)} не {(start ? "запущена" : "остановлена")}: {ex.Message}");
            return false;
        }
    }

    private static string FriendlyServiceName(string name) => name switch
    {
        "WSearch" => "Windows Search",
        "Spooler" => "Диспетчер печати",
        "Fax" => "Факс",
        _ => name
    };

    private static bool PrepareShaderCache(OperationResult result)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directX = Path.Combine(local, "D3DSCache");
            Directory.CreateDirectory(directX);
            var probe = Path.Combine(directX, $".framepilot-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "write check");
            File.Delete(probe);

            var cachePaths = new[]
            {
                directX,
                Path.Combine(local, "NVIDIA", "DXCache"),
                Path.Combine(local, "NVIDIA", "GLCache"),
                Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
                Path.Combine(local, "AMD", "DxCache"),
                Path.Combine(local, "AMD", "GLCache"),
                Path.Combine(local, "Intel", "ShaderCache")
            };
            var existing = cachePaths.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
            var bytes = existing.Sum(GetDirectorySize);
            var root = Path.GetPathRoot(directX);
            var free = root == null ? 0 : new DriveInfo(root).AvailableFreeSpace;
            result.Messages.Add($"Кэш шейдеров: папка DirectX доступна для записи; найдено каталогов: {existing.Count}; объём: {bytes / 1024d / 1024d:0.#} МБ; свободно на диске: {free / 1024d / 1024d / 1024d:0.#} ГБ.");
            result.Messages.Add("Файлы кэша не удалялись. Игры и драйверы продолжат управлять его размером; это не гарантирует прирост FPS.");
            return true;
        }
        catch (Exception ex)
        {
            result.Messages.Add($"Кэш шейдеров не подготовлен: {ex.Message}");
            return false;
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            var options = new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    private static bool CaptureAndSet(BackupSnapshot snapshot, RegistryKey root, string rootName, string path, string name, int value, OperationResult result)
    {
        try
        {
            RegistryBackup backup;
            using (var read = root.OpenSubKey(path, false))
                backup = new RegistryBackup
                {
                    Root = rootName, Path = path, Name = name,
                    Existed = read?.GetValue(name) is int,
                    Value = read?.GetValue(name) is int old ? old : 0
                };
            using (var key = root.CreateSubKey(path, true))
                key.SetValue(name, value, RegistryValueKind.DWord);
            snapshot.RegistryValues.Add(backup);
            result.Messages.Add($"Готово: {name}");
            return true;
        }
        catch (Exception ex)
        {
            result.Messages.Add($"Ошибка {name}: {ex.Message}");
            return false;
        }
    }

    internal static string? GetActivePowerScheme()
    {
        var task = ProcessTools.RunAsync("powercfg.exe", "/getactivescheme");
        task.Wait();
        var match = Regex.Match(task.Result.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value : null;
    }
}
