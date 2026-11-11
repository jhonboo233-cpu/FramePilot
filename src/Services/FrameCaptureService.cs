using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FramePilot.Models;

namespace FramePilot.Services;

internal sealed class FrameCaptureService
{
    private const string PresentMonResource = "FramePilot.PresentMon.exe";
    private const string PresentMonSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    private readonly BenchmarkService _benchmark;
    private string? _activeSession;
    private Process? _activeProcess;

    public FrameCaptureService(BenchmarkService benchmark) => _benchmark = benchmark;

    public List<GameProcessInfo> GetGameProcesses(GameBenchmarkProject project)
    {
        var result = new List<GameProcessInfo>();
        if (!OperatingSystem.IsWindows()) return result;
        var selectedName = project.EffectiveProcessName;
        string? gameDirectory = null;
        try
        {
            gameDirectory = Path.GetDirectoryName(Path.GetFullPath(project.GamePath));
            if (!string.IsNullOrEmpty(gameDirectory))
                gameDirectory = Path.TrimEndingDirectorySeparator(gameDirectory) + Path.DirectorySeparatorChar;
        }
        catch { }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processName = process.ProcessName + ".exe";
                    var executablePath = string.Empty;
                    var windowTitle = string.Empty;
                    try { executablePath = process.MainModule?.FileName ?? string.Empty; } catch { }
                    try { windowTitle = process.MainWindowTitle ?? string.Empty; } catch { }

                    var exact = processName.Equals(selectedName, StringComparison.OrdinalIgnoreCase);
                    var inGameDirectory = false;
                    if (!string.IsNullOrEmpty(executablePath) && !string.IsNullOrEmpty(gameDirectory))
                    {
                        try
                        {
                            inGameDirectory = Path.GetFullPath(executablePath)
                                .StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                    }
                    var hasWindow = !string.IsNullOrWhiteSpace(windowTitle);
                    if (!exact && !inGameDirectory && !hasWindow) continue;
                    result.Add(new GameProcessInfo
                    {
                        ProcessId = process.Id,
                        ProcessName = processName,
                        ExecutablePath = executablePath,
                        MainWindowTitle = windowTitle,
                        DisplayName = $"{processName} • PID {process.Id}" +
                                      (hasWindow ? $" • {windowTitle}" : string.Empty)
                    });
                }
                catch { }
            }
        }
        return result
            .OrderBy(x => !x.ProcessName.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => !IsInDirectory(x.ExecutablePath, gameDirectory))
            .ThenBy(x => string.IsNullOrWhiteSpace(x.MainWindowTitle))
            .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ProcessId)
            .ToList();
    }

    private static bool IsInDirectory(string executablePath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(directory)) return false;
        try { return Path.GetFullPath(executablePath).StartsWith(directory, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public CaptureReadiness CheckReadiness(GameBenchmarkProject project)
    {
        var result = new CaptureReadiness { IsReady = true };
        void Check(bool success, string ok, string error)
        {
            result.Checks.Add((success ? "✓ " : "✗ ") + (success ? ok : error));
            if (!success) result.IsReady = false;
        }

        var windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        Check(windows11, "Windows 11 поддерживает захват.", "Автоматический захват доступен только в Windows 11.");
        if (!windows11) return result;
        Check(File.Exists(project.GamePath), "EXE проекта найден.", "Исполняемый файл игры не найден. Укажите правильный путь.");
        var targetName = project.EffectiveProcessName;
        Check(!string.IsNullOrWhiteSpace(targetName), $"Игровой процесс: {targetName}.", "Не выбран игровой процесс.");
        Check(!string.IsNullOrWhiteSpace(targetName) && IsTargetRunning(targetName),
            $"Процесс {targetName} запущен.",
            $"Процесс {targetName} не найден. Запустите игру, обновите список и выберите фактический игровой EXE.");
        try
        {
            ExtractPresentMon();
            Check(true, "PresentMon проверен по SHA-256.", string.Empty);
        }
        catch (Exception ex) { Check(false, string.Empty, "PresentMon не готов: " + ex.Message); }
        try
        {
            var directory = AppStorage.GetProjectDirectory(project.Id);
            var probe = Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "FramePilot readiness check");
            File.Delete(probe);
            Check(true, "Папка CSV доступна для записи.", string.Empty);
        }
        catch (Exception ex) { Check(false, string.Empty, "Папка проекта недоступна для записи: " + ex.Message); }
        return result;
    }

    private static bool IsTargetRunning(string processName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(processName)) return false;
        var name = Path.GetFileNameWithoutExtension(processName);
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool LaunchGame(GameBenchmarkProject project, out string message)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Запуск игры доступен в Windows.");
        if (!File.Exists(project.GamePath)) throw new FileNotFoundException("Исполняемый файл игры не найден.", project.GamePath);
        if (IsTargetRunning(project.EffectiveProcessName))
        {
            message = $"Игровой процесс {project.EffectiveProcessName} уже запущен. Перейдите в нужную сцену и начните замер.";
            return false;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = project.GamePath,
            WorkingDirectory = Path.GetDirectoryName(project.GamePath) ?? string.Empty,
            UseShellExecute = true
        });
        message = "Игра запущена. Дождитесь загрузки, обновите список процессов, выберите фактический игровой EXE и повторите выбранную сцену.";
        return true;
    }

    public async Task<FrameCapture> CaptureAsync(
        GameBenchmarkProject project,
        string stage,
        CaptureEnvironment environment,
        IProgress<CaptureProgressInfo> progress,
        int runNumber,
        CancellationToken cancellationToken)
    {
        var readiness = CheckReadiness(project);
        if (!readiness.IsReady)
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                readiness.Checks.Where(x => x.StartsWith("✗ ", StringComparison.Ordinal))));

        environment.ProcessName = project.EffectiveProcessName;
        void Report(string phase, int remaining, int total) => progress.Report(new CaptureProgressInfo
        {
            Phase = phase,
            RemainingSeconds = remaining,
            TotalSeconds = total,
            RunNumber = runNumber,
            TargetRuns = project.RunsPerSeries
        });

        try
        {
            var warmup = Math.Clamp(project.WarmupSeconds, 0, 60);
            Report("Warmup", warmup, warmup);
            for (var remaining = warmup; remaining > 0; remaining--)
            {
                await Task.Delay(1000, cancellationToken);
                Report("Warmup", remaining - 1, warmup);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTargetRunning(project.EffectiveProcessName))
                throw new InvalidOperationException($"Процесс {project.EffectiveProcessName} завершился во время прогрева. Перезапустите игру и повторите прогон.");

            var tool = ExtractPresentMon();
            var directory = AppStorage.GetProjectDirectory(project.Id);
            var rawName = $"{stage.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv";
            var rawPath = Path.Combine(directory, rawName);
            var session = "FramePilot_" + Guid.NewGuid().ToString("N");
            project.Pending = new PendingCapture
            {
                Stage = stage,
                RawCsvFileName = rawName,
                SessionName = session,
                Environment = environment,
                StartedAt = DateTime.Now
            };
            _benchmark.SaveProjects();
            _activeSession = session;

            var arguments = $"--process_name \"{project.EffectiveProcessName}\" --output_file \"{rawPath}\" " +
                            $"--timed {project.CaptureSeconds} --terminate_after_timed --session_name {session} " +
                            "--stop_existing_session --v2_metrics --no_console_stats";
            var info = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = arguments,
                WorkingDirectory = directory,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (OptimizationService.IsAdministrator())
            {
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
            }
            else
            {
                info.UseShellExecute = true;
                info.Verb = "runas";
            }

            _activeProcess = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить PresentMon.");
            using var registration = cancellationToken.Register(() => TerminateSession(session));
            var remainingCapture = project.CaptureSeconds;
            Report("Capture", remainingCapture, project.CaptureSeconds);
            while (!_activeProcess.HasExited)
            {
                await Task.Delay(1000, cancellationToken);
                remainingCapture = Math.Max(0, remainingCapture - 1);
                Report("Capture", remainingCapture, project.CaptureSeconds);
            }
            await _activeProcess.WaitForExitAsync(CancellationToken.None);
            var exitCode = _activeProcess.ExitCode;
            for (var attempt = 0; attempt < 20 && !File.Exists(rawPath); attempt++)
                await Task.Delay(250, CancellationToken.None);
            if (!File.Exists(rawPath))
                throw new InvalidOperationException($"PresentMon завершился с кодом {exitCode}, CSV не создан. Проверьте, что {project.EffectiveProcessName} действительно является игровым процессом и в сцене отображаются кадры.");

            var capture = ParseCsv(rawPath, stage, environment);
            _benchmark.AddCapture(project, capture);
            Report("Capture", 0, project.CaptureSeconds);
            return capture;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Прогон остановлен. Прогрев не записывается; частичный CSV, если он был создан, сохранён в папке проекта.");
        }
        finally
        {
            project.Pending = null;
            _benchmark.SaveProjects();
            _activeProcess?.Dispose();
            _activeProcess = null;
            _activeSession = null;
        }
    }

    public void CancelCapture()
    {
        if (_activeSession is string session) TerminateSession(session);
    }

    public FrameCapture? TryRecoverPending(GameBenchmarkProject project)
    {
        var pending = project.Pending;
        if (pending == null) return null;
        var path = Path.Combine(AppStorage.GetProjectDirectory(project.Id), pending.RawCsvFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var capture = ParseCsv(path, pending.Stage, pending.Environment);
            _benchmark.AddCapture(project, capture);
            return capture;
        }
        catch { return null; }
    }

    internal static FrameCapture ParseCsv(string path, string stage, CaptureEnvironment environment)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("CSV пуст.");
        var headers = ParseCsvLine(headerLine);
        var indexes = headers.Select((name, index) => (name: name.Trim().Trim('\uFEFF'), index))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
        var frameTimes = new List<double>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var columns = ParseCsvLine(line);
            if (GetText(columns, indexes, "FrameType") is string frameType &&
                (frameType.Contains("generated", StringComparison.OrdinalIgnoreCase) ||
                 frameType.Contains("interpolated", StringComparison.OrdinalIgnoreCase))) continue;
            var value = GetNumber(columns, indexes, "MsBetweenPresents")
                        ?? GetNumber(columns, indexes, "MsBetweenDisplayChange")
                        ?? GetNumber(columns, indexes, "MsBetweenAppStart");
            if (value is > 0 and < 60000 && double.IsFinite(value.Value)) frameTimes.Add(value.Value);
        }
        if (frameTimes.Count < 30)
            throw new InvalidDataException($"В CSV только {frameTimes.Count} пригодных кадров. Нужно минимум 30. Повторите захват в активной игровой сцене.");

        var averageMs = frameTimes.Average();
        return new FrameCapture
        {
            Stage = stage,
            CaptureMode = "Auto",
            AverageFps = 1000 / averageMs,
            Low1Fps = LowFps(frameTimes, 0.01),
            Low01Fps = LowFps(frameTimes, 0.001),
            AverageFrameTimeMs = averageMs,
            MaxFrameTimeMs = frameTimes.Max(),
            DurationSeconds = frameTimes.Sum() / 1000,
            FrameCount = frameTimes.Count,
            RawCsvFileName = Path.GetFileName(path),
            Environment = environment
        };
    }

    private static double LowFps(List<double> frameTimes, double fraction)
    {
        var count = Math.Max(1, (int)Math.Ceiling(frameTimes.Count * fraction));
        var slowest = frameTimes.OrderByDescending(x => x).Take(count).Average();
        return 1000 / slowest;
    }

    private static double? GetNumber(List<string> columns, Dictionary<string, int> indexes, string name)
    {
        var text = GetText(columns, indexes, name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? GetText(List<string> columns, Dictionary<string, int> indexes, string name)
        => indexes.TryGetValue(name, out var index) && index < columns.Count ? columns[index].Trim() : null;

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else value.Append(ch);
        }
        values.Add(value.ToString());
        return values;
    }

    private static string ExtractPresentMon()
    {
        var directory = Path.Combine(AppStorage.DataDirectory, "tools");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PresentMon-2.6.0-x64.exe");
        if (!File.Exists(path) || !HashMatches(path))
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(PresentMonResource)
                ?? throw new InvalidOperationException("Встроенный PresentMon не найден.");
            var temporary = path + ".tmp";
            using (var target = File.Create(temporary)) source.CopyTo(target);
            if (!HashMatches(temporary))
            {
                File.Delete(temporary);
                throw new InvalidOperationException("Контрольная сумма PresentMon не совпала. Захват остановлен.");
            }
            File.Copy(temporary, path, true);
            File.Delete(temporary);
        }
        ExtractLicense(directory);
        return path;
    }

    private static void ExtractLicense(string directory)
    {
        var path = Path.Combine(directory, "PresentMon-LICENSE.txt");
        if (File.Exists(path)) return;
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("FramePilot.PresentMon-LICENSE.txt");
        if (source == null) return;
        using var target = File.Create(path);
        source.CopyTo(target);
    }

    private static bool HashMatches(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(PresentMonSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TerminateSession(string session)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var tool = Path.Combine(AppStorage.DataDirectory, "tools", "PresentMon-2.6.0-x64.exe");
            if (!File.Exists(tool)) return;
            var info = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = $"--session_name {session} --terminate_existing_session --no_csv --no_console_stats",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = !OptimizationService.IsAdministrator()
            };
            if (info.UseShellExecute) info.Verb = "runas";
            using var process = Process.Start(info);
            process?.WaitForExit(10000);
        }
        catch { }
    }
}
