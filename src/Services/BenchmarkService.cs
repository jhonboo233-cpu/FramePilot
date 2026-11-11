using System.Diagnostics;
using FramePilot.Models;

namespace FramePilot.Services;

internal sealed class BenchmarkService
{
    public BenchmarkStore Store { get; private set; } = AppStorage.LoadBenchmarks();
    public BenchmarkProjectStore ProjectStore { get; private set; } = AppStorage.LoadBenchmarkProjects();

    public GameBenchmarkProject? ActiveProject => ProjectStore.Projects.FirstOrDefault(x => x.Id == ProjectStore.ActiveProjectId);

    public GameBenchmarkProject CreateProject(string name, string gamePath, string scene, string resolution, string graphicsPreset,
        int captureSeconds, int warmupSeconds, int runsPerSeries)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название проекта замера.");
        if (string.IsNullOrWhiteSpace(gamePath)) throw new ArgumentException("Выберите исполняемый файл игры.");
        if (string.IsNullOrWhiteSpace(scene)) throw new ArgumentException("Опишите тестовую сцену или встроенный бенчмарк.");
        var project = new GameBenchmarkProject
        {
            Name = name.Trim(), GamePath = gamePath.Trim(), Scene = scene.Trim(),
            Resolution = resolution.Trim(), GraphicsPreset = graphicsPreset.Trim(),
            CaptureSeconds = Math.Clamp(captureSeconds, 15, 600),
            WarmupSeconds = Math.Clamp(warmupSeconds, 0, 60),
            RunsPerSeries = Math.Clamp(runsPerSeries, 1, 5)
        };
        ProjectStore.Projects.Insert(0, project);
        ProjectStore.ActiveProjectId = project.Id;
        SaveProjects();
        return project;
    }

    public void UpdateProject(GameBenchmarkProject project, string name, string gamePath, string scene, string resolution, string graphicsPreset,
        int captureSeconds, int warmupSeconds, int runsPerSeries)
    {
        if (project.Captures.Count > 0 && !string.Equals(project.GamePath, gamePath.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Нельзя заменить игру после начала замеров. Создайте новый проект.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(scene))
            throw new ArgumentException("Заполните название проекта, путь к игре и тестовую сцену.");
        project.Name = name.Trim();
        project.GamePath = gamePath.Trim();
        project.Scene = scene.Trim();
        project.Resolution = resolution.Trim();
        project.GraphicsPreset = graphicsPreset.Trim();
        project.CaptureSeconds = Math.Clamp(captureSeconds, 15, 600);
        project.WarmupSeconds = Math.Clamp(warmupSeconds, 0, 60);
        project.RunsPerSeries = Math.Clamp(runsPerSeries, 1, 5);
        SaveProjects();
    }

    public void SetActiveProject(string? id)
    {
        ProjectStore.ActiveProjectId = id;
        SaveProjects();
    }

    public void AddCapture(GameBenchmarkProject project, FrameCapture capture)
    {
        project.Captures.Insert(0, capture);
        project.Pending = null;
        SaveProjects();
        WriteCaptureSummary(project, capture);
    }

    public void SaveProjects() => AppStorage.SaveBenchmarkProjects(ProjectStore);

    public CaptureSummary? Summarize(GameBenchmarkProject project, string stage)
    {
        var runs = project.Captures.Where(x => x.Stage == stage).ToList();
        if (runs.Count == 0) return null;
        var averageFps = runs.Select(x => x.AverageFps).Where(x => x > 0 && double.IsFinite(x)).ToArray();
        var medianFps = Median(runs.Select(x => x.AverageFps));
        var minimum = averageFps.Length > 0 ? averageFps.Min() : 0;
        var maximum = averageFps.Length > 0 ? averageFps.Max() : 0;
        return new CaptureSummary
        {
            AverageFps = medianFps,
            Low1Fps = Median(runs.Select(x => x.Low1Fps)),
            Low01Fps = Median(runs.Select(x => x.Low01Fps)),
            AverageFrameTimeMs = Median(runs.Select(x => x.AverageFrameTimeMs)),
            RunCount = runs.Count,
            MinAverageFps = minimum,
            MaxAverageFps = maximum,
            SpreadPercent = medianFps > 0 ? (maximum - minimum) / medianFps * 100 : 0
        };
    }

    public BenchmarkComparison? Compare(GameBenchmarkProject project)
    {
        var before = Summarize(project, "Before");
        var after = Summarize(project, "After");
        if (before == null || after == null) return null;
        var comparison = new BenchmarkComparison
        {
            Average = CompareMetric(before.AverageFps, after.AverageFps),
            Low1 = CompareMetric(before.Low1Fps, after.Low1Fps),
            Low01 = CompareMetric(before.Low01Fps, after.Low01Fps),
            FrameTime = CompareMetric(before.AverageFrameTimeMs, after.AverageFrameTimeMs, true),
            BeforeRunCount = before.RunCount,
            AfterRunCount = after.RunCount,
            BeforeSpread = before.SpreadPercent,
            AfterSpread = after.SpreadPercent,
            IsStable = before.IsStable && after.IsStable
        };
        var baseline = project.Captures.Where(x => x.Stage == "Before").OrderBy(x => x.CreatedAt).First();
        var latest = project.Captures.Where(x => x.Stage == "After").OrderByDescending(x => x.CreatedAt).First();
        AddEnvironmentWarnings(comparison, baseline.Environment, latest.Environment);
        if (before.RunCount < 3 || after.RunCount < 3)
            comparison.Warnings.Add("Результат предварительный: для надёжного вывода выполните минимум 3 замера ДО и 3 замера ПОСЛЕ.");
        if (before.HasExcessiveSpread)
            comparison.Warnings.Add($"Серия ДО нестабильна: разброс среднего FPS {before.SpreadPercent:0.#}% при пороге 5%. Повторите прогоны в той же сцене.");
        if (after.HasExcessiveSpread)
            comparison.Warnings.Add($"Серия ПОСЛЕ нестабильна: разброс среднего FPS {after.SpreadPercent:0.#}% при пороге 5%. Повторите прогоны в той же сцене.");
        return comparison;
    }

    private static MetricComparison CompareMetric(double before, double after, bool lowerIsBetter = false)
    {
        var delta = before > 0 ? (after - before) / before * 100 : 0;
        if (lowerIsBetter) delta = -delta;
        return new MetricComparison { Before = before, After = after, Delta = delta };
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static void AddEnvironmentWarnings(BenchmarkComparison result, CaptureEnvironment before, CaptureEnvironment after)
    {
        CompareCondition(before.Cpu, after.Cpu, "Изменился процессор");
        CompareCondition(before.Gpu, after.Gpu, "Изменилась видеокарта");
        CompareCondition(before.GpuDriver, after.GpuDriver, "Изменился драйвер GPU");
        CompareCondition(before.GameVersion, after.GameVersion, "Изменилась версия игры");
        CompareCondition(before.MemorySpeed, after.MemorySpeed, "Изменилась частота памяти");
        CompareCondition(before.MemoryChannel, after.MemoryChannel, "Изменился канальный режим памяти");
        CompareCondition(before.Resolution, after.Resolution, "Разрешение в замерах отличается");
        CompareCondition(before.GraphicsPreset, after.GraphicsPreset, "Графический пресет в замерах отличается");
        CompareCondition(before.Scene, after.Scene, "Описание тестовой сцены изменилось");
        CompareCondition(before.ProcessName, after.ProcessName, "Изменился захватываемый игровой процесс");

        void CompareCondition(string oldValue, string newValue, string message)
        {
            if (!string.IsNullOrWhiteSpace(oldValue) && !string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                result.Warnings.Add(message + ": сравнение может быть некорректным.");
        }
    }

    private static void WriteCaptureSummary(GameBenchmarkProject project, FrameCapture capture)
    {
        var fileName = string.IsNullOrWhiteSpace(capture.RawCsvFileName) ? capture.Id : capture.RawCsvFileName;
        var path = Path.Combine(AppStorage.GetProjectDirectory(project.Id), fileName + ".summary.txt");
        var text = $"FramePilot 1.3 — {capture.StageLabel}\n" +
                   $"Дата: {capture.CreatedAt:O}\nИгра: {project.Name}\nФайл: {project.GamePath}\n" +
                   $"Игровой процесс: {capture.Environment.ProcessName}\n" +
                   $"Сцена: {capture.Environment.Scene}\nРазрешение: {capture.Environment.Resolution}\n" +
                   $"Пресет: {capture.Environment.GraphicsPreset}\nРежим: {capture.ModeLabel}\n" +
                   $"Средний FPS: {capture.AverageFps:0.###}\n1% Low: {capture.Low1Fps:0.###}\n" +
                   $"0.1% Low: {capture.Low01Fps:0.###}\nСреднее время кадра: {capture.AverageFrameTimeMs:0.###} мс\n" +
                   $"Максимальное время кадра: {capture.MaxFrameTimeMs:0.###} мс\nКадров: {capture.FrameCount}\n";
        File.WriteAllText(path, text);
    }

    public GameBenchmark AddGameResult(string game, double beforeAverage, double beforeLow, double afterAverage, double afterLow, string memorySpeed, string memoryChannel)
    {
        if (string.IsNullOrWhiteSpace(game)) throw new ArgumentException("Укажите название игры или тестовой сцены.");
        var values = new[] { beforeAverage, beforeLow, afterAverage, afterLow };
        if (values.Any(x => !double.IsFinite(x) || x <= 0)) throw new ArgumentException("Все значения FPS должны быть положительными числами.");
        var result = new GameBenchmark
        {
            Game = game.Trim(), BeforeAverage = beforeAverage, BeforeLow = beforeLow,
            AfterAverage = afterAverage, AfterLow = afterLow,
            MemorySpeed = memorySpeed, MemoryChannel = memoryChannel
        };
        Store.GameResults.Insert(0, result);
        if (Store.GameResults.Count > 30) Store.GameResults.RemoveRange(30, Store.GameResults.Count - 30);
        AppStorage.SaveBenchmarks(Store);
        return result;
    }

    public void ClearHistory()
    {
        Store.GameResults.Clear();
        AppStorage.SaveBenchmarks(Store);
    }

    public async Task<CpuBenchmarkResult> RunCpuAsync()
    {
        var result = await Task.Run(() =>
        {
            Workload(300_000);
            var single = Stopwatch.StartNew();
            Workload(5_000_000);
            single.Stop();

            var multi = Stopwatch.StartNew();
            var workers = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(_ => Task.Run(() => Workload(900_000)))
                .ToArray();
            Task.WaitAll(workers);
            multi.Stop();

            return new CpuBenchmarkResult
            {
                SingleThreadMs = single.Elapsed.TotalMilliseconds,
                MultiThreadMs = multi.Elapsed.TotalMilliseconds,
                LogicalProcessors = Environment.ProcessorCount
            };
        });

        Store.CpuBaseline ??= result;
        Store.CpuLatest = result;
        AppStorage.SaveBenchmarks(Store);
        return result;
    }

    private static double Workload(int iterations)
    {
        double value = 0.0001;
        for (var i = 1; i <= iterations; i++)
        {
            value += Math.Sqrt(i % 997 + 1) * 0.000001;
            value = value * 1.00000001 + Math.Sin(i % 31) * 0.0000001;
            if (value > 1_000_000) value *= 0.5;
        }
        return value;
    }
}
