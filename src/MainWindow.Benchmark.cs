using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FramePilot.Models;
using FramePilot.Services;
using Microsoft.Win32;

namespace FramePilot;

public partial class MainWindow
{
    private void RefreshProjectList()
    {
        _projectUiUpdating = true;
        try
        {
            ProjectSelector.ItemsSource = _benchmark.ProjectStore.Projects.ToList();
            ProjectSelector.SelectedItem = _benchmark.ActiveProject;
        }
        finally { _projectUiUpdating = false; }
        var project = _benchmark.ActiveProject;
        if (project == null) ClearProjectForm();
        else LoadProjectForm(project);
        RefreshProjectUI();
    }

    private void RefreshProjectUI()
    {
        var project = _benchmark.ActiveProject;
        var capturing = _captureCts != null;
        var beforeCount = project?.Captures.Count(x => x.Stage == "Before") ?? 0;
        var afterCount = project?.Captures.Count(x => x.Stage == "After") ?? 0;
        var targetRuns = Math.Clamp(project?.RunsPerSeries ?? 3, 1, 5);
        var beforeSummary = project == null ? null : _benchmark.Summarize(project, "Before");
        var hasProject = project != null;
        var baselineLocked = afterCount > 0;

        SaveProjectButton.Content = hasProject ? "Сохранить условия" : "Создать проект";
        SaveProjectButton.IsEnabled = !capturing;
        NewProjectButton.IsEnabled = !capturing;
        ProjectSelector.IsEnabled = !capturing;
        LaunchGameButton.IsEnabled = hasProject && !capturing;
        OpenRunFolderButton.IsEnabled = hasProject;
        CaptureButton.IsEnabled = hasProject && !baselineLocked && !capturing;
        CaptureButton.Content = "Замер ДО • " + RunProgressLabel(beforeCount + 1, targetRuns);
        ManualCaptureButton.IsEnabled = hasProject && !baselineLocked && !capturing;
        AfterCaptureButton.IsVisible = beforeCount > 0;
        AfterCaptureButton.IsEnabled = hasProject && !capturing;
        AfterCaptureButton.Content = "Замер ПОСЛЕ • " + RunProgressLabel(afterCount + 1, targetRuns);
        ManualAfterButton.IsVisible = beforeCount > 0;
        ManualAfterButton.IsEnabled = hasProject && !capturing;
        GoOptimizeButton.IsVisible = beforeCount > 0;
        GameProcessSelector.IsEnabled = hasProject && !capturing && OperatingSystem.IsWindows();
        RefreshProcessesButton.IsEnabled = hasProject && !capturing && OperatingSystem.IsWindows();
        CheckCaptureReadinessButton.IsEnabled = hasProject && !capturing;

        if (!hasProject)
        {
            BenchmarkStageTitle.Text = "Шаг 1 — зафиксируйте результат ДО";
            BenchmarkStageText.Text = "Создайте проект до применения оптимизаций. Базовый замер станет точкой сравнения.";
        }
        else if (beforeCount == 0)
        {
            BenchmarkStageTitle.Text = $"Шаг 1 — серия ДО • 0/{targetRuns}";
            BenchmarkStageText.Text = "Запустите игру, выберите фактический процесс и дойдите до сцены. Нажмите F9 или кнопку: сначала прогрев без записи, затем замер. Пока оптимизацию не применяйте.";
        }
        else if (afterCount == 0 && (beforeCount < targetRuns || beforeSummary?.HasExcessiveSpread == true))
        {
            BenchmarkStageTitle.Text = $"Базовая серия ДО • {beforeCount}/{targetRuns}";
            BenchmarkStageText.Text = beforeSummary?.HasExcessiveSpread == true
                ? $"Разброс базы {beforeSummary.SpreadPercent:0.#}% выше порога 5%. Повторите ту же сцену и добавьте прогон ДО через F9 или кнопку."
                : "Повторите ту же сцену между прогонами. F9 запустит следующий замер ДО; кнопку оптимизации можно использовать и после достижения цели серии.";
        }
        else if (afterCount == 0)
        {
            BenchmarkStageTitle.Text = $"База сохранена: {beforeCount} замер(а) ДО";
            BenchmarkStageText.Text = "Базовая серия готова. Перейдите к оптимизации, затем повторите ту же сцену. F9 запустит первый прогон ПОСЛЕ.";
        }
        else
        {
            BenchmarkStageTitle.Text = $"Сравнение готово • {beforeCount} ДО / {afterCount} ПОСЛЕ";
            BenchmarkStageText.Text = "База заблокирована после первого замера ПОСЛЕ. Повторяйте сцену и добавляйте прогоны ПОСЛЕ через F9 или кнопку, особенно при большом разбросе.";
        }

        CaptureHistoryList.ItemsSource = project?.Captures.ToList() ?? new List<FrameCapture>();
        HistoryEmpty.IsVisible = beforeCount + afterCount == 0;
        SeriesStabilityText.Text = BuildSeriesStability(project);
        RefreshComparison(project);
    }

    private static string RunProgressLabel(int nextRun, int targetRuns)
        => nextRun <= targetRuns ? $"{nextRun}/{targetRuns}" : $"доп. №{nextRun}";

    private string BuildSeriesStability(GameBenchmarkProject? project)
    {
        if (project == null) return "Серии появятся после создания проекта и первого замера.";
        var target = Math.Clamp(project.RunsPerSeries, 1, 5);
        return Describe("ДО", _benchmark.Summarize(project, "Before"), target) + Environment.NewLine +
               Describe("ПОСЛЕ", _benchmark.Summarize(project, "After"), target);

        static string Describe(string label, CaptureSummary? summary, int targetRuns)
        {
            if (summary == null) return $"{label}: 0/{targetRuns} • прогонов пока нет";
            var range = summary.RunCount > 1 ? $" • диапазон {summary.MinAverageFps:0.#}–{summary.MaxAverageFps:0.#} FPS" : string.Empty;
            var verdict = summary.HasExcessiveSpread ? " • выше 5%: повторите прогоны" :
                summary.RunCount < 3 ? " • для надёжности нужно минимум 3" : " • разброс в норме";
            return $"{label}: {summary.RunCount}/{targetRuns} • медиана {summary.AverageFps:0.#} FPS{range} • разброс {summary.SpreadPercent:0.#}%{verdict}";
        }
    }

    private void RefreshComparison(GameBenchmarkProject? project)
    {
        var comparison = project == null ? null : _benchmark.Compare(project);
        ComparisonResult.IsVisible = comparison != null;
        if (comparison == null) return;
        ComparisonTitle.Text = !comparison.IsStable
            ? "Результат требует повторной проверки"
            : comparison.Average.Status == "good" && comparison.Low1.Status == "good"
                ? "Подтверждён положительный результат"
                : comparison.Average.Status == "bad" || comparison.Low1.Status == "bad"
                    ? "Есть признаки ухудшения"
                    : "Существенный прирост не подтверждён";
        ComparisonText.Text = $"Использована медиана серий: {comparison.BeforeRunCount} ДО и {comparison.AfterRunCount} ПОСЛЕ. Для времени кадра положительный процент означает меньшее время.";
        ComparisonStabilityText.Text = comparison.IsStable
            ? $"Серии стабильны по принятому порогу: разброс ДО {comparison.BeforeSpread:0.#}%, ПОСЛЕ {comparison.AfterSpread:0.#}% (не более 5%, минимум 3 прогона)."
            : $"Нужна повторная проверка: разброс ДО {comparison.BeforeSpread:0.#}%, ПОСЛЕ {comparison.AfterSpread:0.#}%; порог 5%, минимум 3 прогона в каждой серии.";
        ComparisonStabilityText.Foreground = Brush.Parse(comparison.IsStable ? "#65E1B4" : "#E7C987");
        SetMetric(comparison.Average, " FPS", AvgResultText, AvgDeltaText, AvgStatusText);
        SetMetric(comparison.Low1, " FPS", Low1ResultText, Low1DeltaText, Low1StatusText);
        SetMetric(comparison.Low01, " FPS", Low01ResultText, Low01DeltaText, Low01StatusText);
        SetMetric(comparison.FrameTime, " мс", FrameTimeResultText, FrameTimeDeltaText, FrameTimeStatusText);
        ComparisonWarningsText.Text = string.Join(Environment.NewLine, comparison.Warnings.Select(x => "• " + x));
    }

    private static void SetMetric(MetricComparison metric, string unit, TextBlock result, TextBlock delta, TextBlock status)
    {
        result.Text = $"{metric.Before:0.#} → {metric.After:0.#}{unit}";
        delta.Text = Signed(metric.Delta) + "%";
        delta.Foreground = Brush.Parse(metric.Color);
        status.Text = metric.StatusText;
        status.Foreground = Brush.Parse(metric.Color);
    }

    private void ProjectSelector_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_projectUiUpdating) return;
        var project = ProjectSelector.SelectedItem as GameBenchmarkProject;
        _benchmark.SetActiveProject(project?.Id);
        if (project == null) ClearProjectForm();
        else LoadProjectForm(project);
        RefreshProjectUI();
    }

    private void NewProject_Click(object? sender, RoutedEventArgs e)
    {
        _benchmark.SetActiveProject(null);
        _projectUiUpdating = true;
        ProjectSelector.SelectedItem = null;
        _projectUiUpdating = false;
        ClearProjectForm();
        RefreshProjectUI();
        StatusText.Text = "Новый игровой проект • сначала сохраните замер ДО";
    }

    private void ClearProjectForm()
    {
        ProjectNameInput.Text = string.Empty;
        GamePathInput.Text = string.Empty;
        SceneInput.Text = string.Empty;
        ResolutionInput.Text = string.Empty;
        GraphicsPresetInput.Text = string.Empty;
        DurationInput.Text = "60";
        WarmupInput.Text = "10";
        SeriesInput.Text = "3";
        _processUiUpdating = true;
        GameProcessSelector.ItemsSource = null;
        GameProcessSelector.SelectedItem = null;
        _processUiUpdating = false;
    }

    private void LoadProjectForm(GameBenchmarkProject project)
    {
        ProjectNameInput.Text = project.Name;
        GamePathInput.Text = project.GamePath;
        SceneInput.Text = project.Scene;
        ResolutionInput.Text = project.Resolution;
        GraphicsPresetInput.Text = project.GraphicsPreset;
        DurationInput.Text = project.CaptureSeconds.ToString(CultureInfo.InvariantCulture);
        WarmupInput.Text = project.WarmupSeconds.ToString(CultureInfo.InvariantCulture);
        SeriesInput.Text = project.RunsPerSeries.ToString(CultureInfo.InvariantCulture);
        RefreshProcessList(project);
    }

    private static int ReadInteger(TextBox input, int minimum, int maximum, string message)
    {
        if (!int.TryParse(input.Text, out var value) || value < minimum || value > maximum)
            throw new ArgumentException(message);
        return value;
    }

    private int ReadDuration() => ReadInteger(DurationInput, 15, 600, "Длительность захвата должна быть целым числом от 15 до 600 секунд.");
    private int ReadWarmup() => ReadInteger(WarmupInput, 0, 60, "Прогрев должен быть целым числом от 0 до 60 секунд.");
    private int ReadSeriesRuns() => ReadInteger(SeriesInput, 1, 5, "Число прогонов в серии должно быть целым числом от 1 до 5.");

    private GameBenchmarkProject SaveCurrentProject()
    {
        var project = _benchmark.ActiveProject;
        if (project == null)
            return _benchmark.CreateProject(ProjectNameInput.Text ?? string.Empty, GamePathInput.Text ?? string.Empty,
                SceneInput.Text ?? string.Empty, ResolutionInput.Text ?? string.Empty,
                GraphicsPresetInput.Text ?? string.Empty, ReadDuration(), ReadWarmup(), ReadSeriesRuns());
        _benchmark.UpdateProject(project, ProjectNameInput.Text ?? string.Empty, GamePathInput.Text ?? string.Empty,
            SceneInput.Text ?? string.Empty, ResolutionInput.Text ?? string.Empty,
            GraphicsPresetInput.Text ?? string.Empty, ReadDuration(), ReadWarmup(), ReadSeriesRuns());
        return project;
    }

    private void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _benchmark.SetActiveProject(project.Id);
            RefreshProjectList();
            StatusText.Text = "Проект сохранён • выберите игровой процесс и выполните серию ДО";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void BrowseGame_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите исполняемый файл игры или лаунчера",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Исполняемые файлы") { Patterns = new[] { "*.exe" } } }
        });
        if (files.Count == 0) return;
        GamePathInput.Text = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(ProjectNameInput.Text))
            ProjectNameInput.Text = Path.GetFileNameWithoutExtension(GamePathInput.Text);
    }

    private void RefreshProcesses_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _benchmark.SetActiveProject(project.Id);
            RefreshProcessList(project);
            RefreshProjectUI();
            CaptureStatusText.Text = OperatingSystem.IsWindows()
                ? $"Список обновлён. Выбран процесс: {project.EffectiveProcessName}. Если указан лаунчер, выберите настоящий EXE игры."
                : "Список игровых процессов доступен в Windows 11.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void RefreshProcessList(GameBenchmarkProject project)
    {
        var items = new List<GameProcessInfo>
        {
            new() { ProcessId = 0, ProcessName = string.Empty, DisplayName = $"Автоматически по EXE проекта: {Path.GetFileName(project.GamePath)}" }
        };
        if (OperatingSystem.IsWindows()) items.AddRange(_capture.GetGameProcesses(project));
        _processUiUpdating = true;
        try
        {
            GameProcessSelector.ItemsSource = items;
            GameProcessSelector.SelectedItem = items.FirstOrDefault(x => x.ProcessId > 0 &&
                x.ProcessName.Equals(project.TargetProcessName, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }
        finally { _processUiUpdating = false; }
    }

    private void GameProcessSelector_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_processUiUpdating || GameProcessSelector.SelectedItem is not GameProcessInfo selected) return;
        var project = _benchmark.ActiveProject;
        if (project == null) return;
        project.TargetProcessName = selected.ProcessId == 0 ? string.Empty : selected.ProcessName;
        _benchmark.SaveProjects();
        CaptureStatusText.Text = $"Для PresentMon выбран процесс: {project.EffectiveProcessName}. Проверьте готовность перед серией.";
    }

    private void CheckCaptureReadiness_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _benchmark.SetActiveProject(project.Id);
            var readiness = _capture.CheckReadiness(project);
            CaptureStatusText.Text = readiness.Summary;
            StatusText.Text = readiness.IsReady ? "Готовность подтверждена • можно начинать прогон" : "Есть препятствия для автоматического захвата";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void LaunchGame_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _capture.LaunchGame(project, out var message);
            RefreshProjectList();
            CaptureStatusText.Text = message;
            StatusText.Text = message;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void CaptureBefore_Click(object? sender, RoutedEventArgs e) => await StartCapture("Before");
    private async void CaptureAfter_Click(object? sender, RoutedEventArgs e) => await StartCapture("After");

    private async Task HandleGlobalHotkey()
    {
        if (_captureCts != null)
        {
            StopCapture();
            return;
        }
        var project = _benchmark.ActiveProject;
        if (project == null)
        {
            Navigate_Click(NavBenchmark, new RoutedEventArgs());
            StatusText.Text = "F9: сначала создайте и сохраните игровой проект.";
            return;
        }
        var before = _benchmark.Summarize(project, "Before");
        var stage = SelectHotkeyStage(project, before);
        await StartCapture(stage);
    }

    internal static string SelectHotkeyStage(GameBenchmarkProject project, CaptureSummary? beforeSummary)
        => BenchmarkWorkflow.SelectHotkeyStage(project, beforeSummary);

    private async Task StartCapture(string stage)
    {
        if (_captureCts != null) return;
        _captureCts = new CancellationTokenSource();
        var cancellationToken = _captureCts.Token;
        CancelCaptureButton.IsVisible = true;
        CancelCaptureButton.IsEnabled = true;
        CaptureStatusText.Text = "Подготовка условий замера… F9 остановит прогон.";
        RefreshProjectUI();
        try
        {
            var project = SaveCurrentProject();
            if (stage == "After" && !project.Captures.Any(x => x.Stage == "Before"))
                throw new ArgumentException("Сначала сохраните хотя бы один замер ДО.");
            if (stage == "Before" && project.Captures.Any(x => x.Stage == "After"))
                throw new ArgumentException("Базовая серия заблокирована после замера ПОСЛЕ. Создайте новый проект для другой базы.");
            if (project.Pending != null && _capture.TryRecoverPending(project) == null)
            {
                project.Pending = null;
                _benchmark.SaveProjects();
            }
            var runNumber = project.Captures.Count(x => x.Stage == stage) + 1;
            var environment = await BuildEnvironment(project);
            cancellationToken.ThrowIfCancellationRequested();
            CaptureProgress.IsVisible = true;
            var progress = new Progress<CaptureProgressInfo>(info =>
            {
                CaptureProgress.Maximum = Math.Max(1, info.TotalSeconds);
                CaptureProgress.Value = info.RemainingSeconds;
                CaptureStatusText.Text = info.Phase == "Warmup"
                    ? $"Прогрев {StageName(stage)} • {RunProgressLabel(info.RunNumber, info.TargetRuns)} • осталось {info.RemainingSeconds} с. Кадры не записываются; F9 — остановка."
                    : $"Захват {StageName(stage)} • {RunProgressLabel(info.RunNumber, info.TargetRuns)} • осталось примерно {info.RemainingSeconds} с. Не переключайте сцену; F9 — остановка.";
            });
            var capture = await _capture.CaptureAsync(project, stage, environment, progress, runNumber, cancellationToken);
            CaptureStatusText.Text = $"Сохранено: {capture.AverageFps:0.#} FPS • 1% Low {capture.Low1Fps:0.#} • 0.1% Low {capture.Low01Fps:0.#}. CSV и отчёт записаны. Повторите сцену перед следующим прогоном.";
            StatusText.Text = stage == "Before" ? "Прогон ДО сохранён • серия и разброс обновлены" : "Прогон ПОСЛЕ сохранён • сравнение и разброс обновлены";
            RefreshProjectList();
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = ex.Message;
            StatusText.Text = cancellationToken.IsCancellationRequested ? "Прогон остановлен пользователем" : "Захват не завершён";
            if (!cancellationToken.IsCancellationRequested)
                await MessageWindow.Show(this, "Автоматический захват", ex.Message + "\n\nЕсли игра не поддерживается, используйте ручной режим.");
        }
        finally
        {
            _captureCts?.Dispose();
            _captureCts = null;
            CaptureProgress.IsVisible = false;
            CancelCaptureButton.IsVisible = false;
            RefreshProjectUI();
        }
    }

    private void CancelCapture_Click(object? sender, RoutedEventArgs e) => StopCapture();

    private void StopCapture()
    {
        if (_captureCts == null || _captureCts.IsCancellationRequested) return;
        CancelCaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "Останавливаем прогон. Прогрев не попадёт в результат; доступный частичный CSV останется в папке проекта.";
        _captureCts.Cancel();
    }

    private void ManualBefore_Click(object? sender, RoutedEventArgs e) => SaveManualCapture("Before");
    private void ManualAfter_Click(object? sender, RoutedEventArgs e) => SaveManualCapture("After");

    private async void SaveManualCapture(string stage)
    {
        try
        {
            var project = SaveCurrentProject();
            if (stage == "After" && !project.Captures.Any(x => x.Stage == "Before"))
                throw new ArgumentException("Сначала сохраните замер ДО.");
            if (stage == "Before" && project.Captures.Any(x => x.Stage == "After"))
                throw new ArgumentException("Базовая серия заблокирована после замера ПОСЛЕ.");
            var average = ParsePositive(ManualAvgInput.Text);
            var low1 = ParsePositive(ManualLow1Input.Text);
            var low01 = ParsePositive(ManualLow01Input.Text);
            if (low1 > average || low01 > low1)
                throw new ArgumentException("Обычно 1% Low не выше среднего FPS, а 0.1% Low не выше 1% Low. Проверьте значения.");
            var capture = new FrameCapture
            {
                Stage = stage,
                CaptureMode = "Manual",
                AverageFps = average,
                Low1Fps = low1,
                Low01Fps = low01,
                AverageFrameTimeMs = 1000 / average,
                MaxFrameTimeMs = 1000 / average,
                Environment = await BuildEnvironment(project)
            };
            _benchmark.AddCapture(project, capture);
            ManualAvgInput.Text = ManualLow1Input.Text = ManualLow01Input.Text = string.Empty;
            CaptureStatusText.Text = $"Ручной замер {StageName(stage)} сохранён вместе с конфигурацией системы. Повторите сцену перед следующим прогоном.";
            StatusText.Text = "Ручной игровой замер сохранён";
            RefreshProjectList();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task<CaptureEnvironment> BuildEnvironment(GameBenchmarkProject project)
    {
        var report = await _audit.RunAsync();
        _lastReport = report;
        ApplyMemoryInfo(report);
        string hags = "Не определён";
        string power = "Не определена";
        string gameVersion = project.GameVersion;
        try
        {
            hags = AuditService.ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch
            {
                2 => "Включён",
                1 => "Отключён",
                _ => "По умолчанию"
            };
            power = OptimizationService.GetActivePowerScheme() ?? power;
            if (File.Exists(project.GamePath))
                gameVersion = FileVersionInfo.GetVersionInfo(project.GamePath).FileVersion ?? string.Empty;
            project.GameVersion = gameVersion;
        }
        catch { }
        return new CaptureEnvironment
        {
            Cpu = report.Cpu,
            Gpu = report.Gpu,
            GpuDriver = report.GpuDetails,
            Memory = report.Memory,
            MemorySpeed = report.MemorySpeed,
            MemoryChannel = report.MemoryChannel,
            Windows = report.Windows + " " + report.WindowsDetails,
            Hags = hags,
            PowerScheme = power,
            GameVersion = gameVersion,
            Scene = project.Scene,
            Resolution = project.Resolution,
            GraphicsPreset = project.GraphicsPreset,
            ProcessName = project.EffectiveProcessName
        };
    }

    private void GoOptimize_Click(object? sender, RoutedEventArgs e) => Navigate_Click(NavOptimize, new RoutedEventArgs());

    private void OpenRunFolder_Click(object? sender, RoutedEventArgs e)
    {
        var project = _benchmark.ActiveProject;
        if (project == null || !OperatingSystem.IsWindows()) return;
        Process.Start(new ProcessStartInfo("explorer.exe", AppStorage.GetProjectDirectory(project.Id)) { UseShellExecute = true });
    }

    private static string StageName(string stage) => stage == "Before" ? "ДО" : "ПОСЛЕ";

    private static double ParsePositive(string? text)
    {
        if (!double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
            throw new ArgumentException("Показатели FPS должны быть положительными числами.");
        return value;
    }

    private static string Signed(double value) => value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
