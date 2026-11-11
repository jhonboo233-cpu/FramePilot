using System.Management;
using System.Runtime.InteropServices;
using FramePilot.Models;
using Microsoft.Win32;

namespace FramePilot.Services;

internal sealed class AuditService
{
    public Task<SystemReport> RunAsync() => Task.Run(() =>
    {
        var report = new SystemReport();
        if (!OperatingSystem.IsWindows())
        {
            report.Windows = "Обнаружена не Windows";
            report.WindowsDetails = RuntimeInformation.OSDescription;
            report.Findings.Add(new Finding
            {
                Level = "warning",
                Title = "Диагностика предназначена для Windows 11",
                Description = "Запустите приложение на компьютере с Windows 11. На другой системе параметры не изменяются."
            });
            return report;
        }

        ReadHardware(report);
        ReadSettings(report);
        ReadStorage(report);
        ReadStartup(report);

        if (report.Findings.Count == 0)
            report.Findings.Add(new Finding
            {
                Level = "good",
                Title = "Явных проблем не найдено",
                Description = "Проверьте прирост в одинаковой игровой сцене. Не применяйте дополнительные настройки без необходимости."
            });
        return report;
    });

    private static void ReadHardware(SystemReport report)
    {
        try
        {
            using var cpu = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").Get();
            var item = cpu.Cast<ManagementObject>().FirstOrDefault();
            if (item != null)
            {
                report.Cpu = Convert.ToString(item["Name"])?.Trim() ?? report.Cpu;
                report.CpuDetails = $"{item["NumberOfCores"]} ядер • {item["NumberOfLogicalProcessors"]} потоков • до {item["MaxClockSpeed"]} МГц";
            }
        }
        catch (Exception ex) { AddError(report, "Не удалось прочитать сведения о процессоре", ex); }

        try
        {
            using var gpu = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController").Get();
            var item = gpu.Cast<ManagementObject>().FirstOrDefault();
            if (item != null)
            {
                report.Gpu = Convert.ToString(item["Name"])?.Trim() ?? report.Gpu;
                report.GpuDetails = $"Драйвер {item["DriverVersion"]}";
            }
        }
        catch (Exception ex) { AddError(report, "Не удалось прочитать сведения о видеокарте", ex); }

        try
        {
            ulong total = 0;
            var modules = new List<(ulong Capacity, uint Configured, uint Rated, string Locator, uint InterleaveDepth)>();
            using var memory = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, InterleaveDataDepth, InterleavePosition FROM Win32_PhysicalMemory").Get();
            foreach (ManagementObject module in memory)
            {
                var capacity = Convert.ToUInt64(module["Capacity"]);
                total += capacity;
                modules.Add((
                    capacity,
                    ToUInt32(module["ConfiguredClockSpeed"]),
                    ToUInt32(module["Speed"]),
                    Convert.ToString(module["DeviceLocator"])?.Trim() ?? string.Empty,
                    ToUInt32(module["InterleaveDataDepth"])));
            }

            var gb = total / 1024d / 1024d / 1024d;
            report.Memory = $"{gb:0.#} ГБ";
            var speeds = modules.Select(x => x.Configured > 0 ? x.Configured : x.Rated)
                .Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
            if (speeds.Count > 0)
            {
                var effective = string.Join(" / ", speeds);
                var ddrClock = string.Join(" / ", speeds.Select(x => x / 2));
                report.MemorySpeed = $"{effective} МТ/с • DDR ≈{ddrClock} МГц";
            }
            report.MemoryDetails = $"{modules.Count} модул(я/ей) • {report.MemorySpeed}";

            var rated = modules.Select(x => x.Rated).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
            if (rated.Count > 0 && !rated.SequenceEqual(speeds))
                report.MemoryDetails += $" • заявлено {string.Join(" / ", rated)} МТ/с";

            var equalCapacity = modules.Select(x => x.Capacity).Distinct().Count() == 1;
            var interleaveDetected = modules.Count > 0 && modules.All(x => x.InterleaveDepth > 1);
            if (modules.Count == 1)
            {
                report.MemoryChannel = "Обычно одноканальный*";
                report.MemoryChannelDetails = "Установлен один модуль. Второй совместимый модуль может повысить пропускную способность памяти, особенно в CPU-bound играх.";
                report.Findings.Add(new Finding
                {
                    Level = "warning",
                    Title = "Установлен один модуль памяти",
                    Description = "На большинстве платформ два совместимых модуля в рекомендованных слотах дают двухканальный режим. Точную конфигурацию проверьте в BIOS или HWiNFO."
                });
            }
            else if ((modules.Count == 2 || modules.Count == 4) && equalCapacity)
            {
                report.MemoryChannel = "Двухканальный: вероятно*";
                report.MemoryChannelDetails = interleaveDetected
                    ? $"{modules.Count} одинаковых по объёму модуля; WMI сообщает чередование памяти. Это признак двухканального режима, но не универсальное подтверждение."
                    : $"{modules.Count} одинаковых по объёму модуля — типичная двухканальная конфигурация. Это оценка, а не прямое измерение каналов.";
            }
            else
            {
                report.MemoryChannel = "Точный режим не определён";
                report.MemoryChannelDetails = modules.Count == 0
                    ? "Windows не вернула сведения о модулях памяти."
                    : "Количество или объёмы модулей не позволяют надёжно оценить канальный режим. Возможна асимметричная конфигурация.";
            }
            if (modules.Count > 0)
                report.MemoryChannelDetails += $" Слоты: {string.Join(", ", modules.Select(x => string.IsNullOrWhiteSpace(x.Locator) ? "без имени" : x.Locator))}. Точный режим проверьте в BIOS или HWiNFO.";
            if (gb < 15.5)
                report.Findings.Add(new Finding { Level = "warning", Title = "Оперативной памяти может быть недостаточно", Description = "Для современных игр и фоновых программ часто требуется более 16 ГБ. Следите за загрузкой RAM во время игры." });
        }
        catch (Exception ex) { AddError(report, "Не удалось прочитать сведения о памяти", ex); }

        try
        {
            using var os = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem").Get();
            var item = os.Cast<ManagementObject>().FirstOrDefault();
            if (item != null)
            {
                report.Windows = Convert.ToString(item["Caption"])?.Replace("Microsoft ", "").Trim() ?? report.Windows;
                report.WindowsDetails = $"Сборка {item["BuildNumber"]} • {IntPtr.Size * 8}-разрядная система";
                if (int.TryParse(Convert.ToString(item["BuildNumber"]), out var build) && build < 22000)
                    report.Findings.Add(new Finding { Level = "warning", Title = "Обнаружена не Windows 11", Description = "Приложение рассчитано на Windows 11. Часть параметров может отличаться." });
            }
        }
        catch (Exception ex) { AddError(report, "Не удалось определить версию Windows", ex); }
    }

    private static void ReadSettings(SystemReport report)
    {
        try
        {
            var gameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
            report.Findings.Add(gameMode == 0
                ? new Finding { Level = "action", Title = "Режим игры отключён", Description = "Его можно включить на странице оптимизации. Изменение обратимо." }
                : new Finding { Level = "good", Title = "Режим игры включён", Description = "Дополнительных действий с этим параметром не требуется." });

            var capture = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
            if (capture == 1)
                report.Findings.Add(new Finding { Level = "action", Title = "Разрешена фоновая запись", Description = "Если игровые клипы не нужны, отключение записи может снизить фоновую нагрузку." });

            var hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            report.Findings.Add(new Finding
            {
                Level = "info",
                Title = hags == 2 ? "HAGS включён" : hags == 1 ? "HAGS отключён" : "HAGS использует значение по умолчанию",
                Description = "Не переключайте без A/B-теста: эффект зависит от драйвера, видеокарты и игры. После изменения нужна перезагрузка."
            });
        }
        catch (Exception ex) { AddError(report, "Не удалось проверить игровые параметры", ex); }

        try
        {
            using var computer = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").Get();
            var item = computer.Cast<ManagementObject>().FirstOrDefault();
            if (item != null && item["AutomaticManagedPagefile"] is bool managed && !managed)
                report.Findings.Add(new Finding { Level = "warning", Title = "Файл подкачки настроен вручную", Description = "Приложение не меняет его автоматически. Убедитесь, что системе доступен достаточный объём виртуальной памяти." });
        }
        catch { }
    }

    private static void ReadStorage(SystemReport report)
    {
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT Name, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3").Get();
            foreach (ManagementObject disk in disks)
            {
                var size = Convert.ToUInt64(disk["Size"]);
                var free = Convert.ToUInt64(disk["FreeSpace"]);
                if (size == 0) continue;
                var percent = free * 100d / size;
                if (percent < 15)
                    report.Findings.Add(new Finding { Level = "warning", Title = $"Мало свободного места на диске {disk["Name"]}", Description = $"Свободно {percent:0.#}%. Оставьте запас для обновлений, файла подкачки и кэша шейдеров." });
            }
        }
        catch (Exception ex) { AddError(report, "Не удалось проверить свободное место", ex); }
    }

    private static void ReadStartup(SystemReport report)
    {
        try
        {
            using var startup = new ManagementObjectSearcher("SELECT Name FROM Win32_StartupCommand").Get();
            var names = startup.Cast<ManagementObject>().Select(x => Convert.ToString(x["Name"])).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            report.Findings.Add(new Finding
            {
                Level = names.Count > 10 ? "warning" : "info",
                Title = $"Автозагрузка: {names.Count} элементов",
                Description = names.Count > 10
                    ? "Проверьте список в диспетчере задач и отключите только ненужные программы. Приложение не удаляет элементы автоматически."
                    : "Количество элементов умеренное. Закрывайте ненужные фоновые программы перед игровым замером."
            });
        }
        catch { }
    }

    internal static int? ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, false);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static uint ToUInt32(object? value)
    {
        if (value == null) return 0;
        try { return Convert.ToUInt32(value); }
        catch { return 0; }
    }

    private static void AddError(SystemReport report, string title, Exception ex)
        => report.Findings.Add(new Finding { Level = "warning", Title = title, Description = ex.Message });
}
