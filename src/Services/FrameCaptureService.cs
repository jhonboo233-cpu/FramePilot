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

    public bool LaunchGame(GameBenchmarkProject project, out string message)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Запуск игры доступен в Windows.");
        if (!File.Exists(project.GamePath)) throw new FileNotFoundException("Исполняемый файл игры не найден.", project.GamePath);
        var processName = Path.GetFileNameWithoutExtension(project.GamePath);
        if (Process.GetProcessesByName(processName).Length > 0)
        {
            message = "Игра уже запущена. Перейдите в нужную сцену и начните замер.";
            return false;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = project.GamePath,
            WorkingDirectory = Path.GetDirectoryName(project.GamePath) ?? string.Empty,
            UseShellExecute = true
        });
        message = "Игра запущена. Дождитесь загрузки, повторите выбранную сцену и только затем начните замер.";
        return true;
    }

    public async Task<FrameCapture> CaptureAsync(
        GameBenchmarkProject project,
        string stage,
        CaptureEnvironment environment,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Автоматический захват доступен только в Windows 11.");
        if (!File.Exists(project.GamePath)) throw new FileNotFoundException("Исполняемый файл игры не найден. Укажите правильный путь.", project.GamePath);
        var tool = ExtractPresentMon();
        var directory = AppStorage.GetProjectDirectory(project.Id);
        var rawName = $"{stage.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
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

        try
        {
            var arguments = $"--process_name \"{Path.GetFileName(project.GamePath)}\" --output_file \"{rawPath}\" " +
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
            var remaining = project.CaptureSeconds;
            progress.Report(remaining);
            while (!_activeProcess.HasExited)
            {
                await Task.Delay(1000, cancellationToken);
                remaining = Math.Max(0, remaining - 1);
                progress.Report(remaining);
            }
            await _activeProcess.WaitForExitAsync(CancellationToken.None);
            var exitCode = _activeProcess.ExitCode;
            for (var attempt = 0; attempt < 20 && !File.Exists(rawPath); attempt++)
                await Task.Delay(250, CancellationToken.None);
            if (!File.Exists(rawPath))
                throw new InvalidOperationException($"PresentMon завершился с кодом {exitCode}, CSV не создан. Проверьте, что выбранный EXE действительно является игровым процессом и в сцене отображаются кадры.");

            var capture = ParseCsv(rawPath, stage, environment);
            _benchmark.AddCapture(project, capture);
            progress.Report(0);
            return capture;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Захват остановлен. Частичный CSV, если он был создан, сохранён в папке проекта.");
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
