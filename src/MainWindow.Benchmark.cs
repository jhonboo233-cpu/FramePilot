using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FramePilot.Models;
using FramePilot.Services;

namespace FramePilot;

public partial class MainWindow
{
    private DispatcherTimer? _gameDetectionTimer;
    private bool _gameDetectionRunning;
    private string _requestedPresetId = string.Empty;

    private void InitializeBenchmarkAutoDetection()
    {
        _gameDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameDetectionTimer.Tick += async (_, _) => await DetectGameNow();
        _gameDetectionTimer.Start();
        _ = DetectGameNow();
    }

    private async Task DetectGameNow()
    {
        if (_gameDetectionRunning || _captureCts != null || !OperatingSystem.IsWindows()) return;
        _gameDetectionRunning = true;
        try
        {
            var preferredOnly = !string.IsNullOrWhiteSpace(_requestedPresetId);
            var detected = await _capture.DetectKnownGameAsync(_requestedPresetId, preferredOnly);
            if (detected == null)
            {
                if (preferredOnly && GamePresetCatalog.Find(_requestedPresetId) is { } waiting)
                {
                    DetectedGameText.Text = waiting.GameName;
                    DetectedProcessText.Text = $"Ожидание {waiting.ProcessName}";
                    CaptureStatusText.Text = $"Запустите {waiting.GameName}. Игра будет определена автоматически.";
                }
                return;
            }

            var preset = GamePresetCatalog.All.FirstOrDefault(x =>
                x.ProcessName.Equals(detected.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            var project = _benchmark.ActiveProject;
            var needsProject = project == null ||
                               !project.EffectiveProcessName.Equals(preset.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                               (preferredOnly && project.PresetId != preset.Id);
            if (needsProject)
            {
                project = _benchmark.ProjectStore.Projects.FirstOrDefault(x =>
                              x.PresetId == preset.Id && x.Captures.All(c => c.Stage != "After"))
                          ?? _benchmark.CreateAutoProject(preset, detected);
                _benchmark.SetActiveProject(project.Id);
                LiveFpsText.Text = "—";
                CaptureStatusText.Text = $"{preset.GameName} обнаружена. Войдите в локацию и нажмите «Замер ДО».";
            }
            if (project == null) return;

            var changed = false;
            if (!string.IsNullOrWhiteSpace(detected.ExecutablePath) && project.GamePath != detected.ExecutablePath &&
                project.Captures.Count == 0)
            {
                project.GamePath = detected.ExecutablePath;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(detected.DetectedResolution) && project.Resolution != detected.DetectedResolution)
            {
                project.Resolution = detected.DetectedResolution;
                changed = true;
            }
            if (changed) _benchmark.SaveProjects();
            _requestedPresetId = string.Empty;
            RefreshProjectUI();
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = "Автоопределение: " + ex.Message;
        }
        finally { _gameDetectionRunning = false; }
    }

    private void RefreshProjectList() => RefreshProjectUI();

    private void RefreshProjectUI()
    {
        var project = _benchmark.ActiveProject;
        var before = project?.Captures.Where(x => x.Stage == "Before").OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var after = project?.Captures.Where(x => x.Stage == "After").OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var capturing = _captureCts != null;

        DetectedGameText.Text = project?.PresetName is { Length: > 0 } name ? name : project?.Name ?? "Запустите поддерживаемую игру";
        DetectedProcessText.Text = project == null ? "Tarkov, CS2, Rust или PUBG" : project.EffectiveProcessName;
        DetectedResolutionText.Text = string.IsNullOrWhiteSpace(project?.Resolution) ? "Определяется" : project.Resolution;
        BeforeResultText.Text = before == null ? "ДО: пока нет результата" : $"ДО: {before.AverageFps:0.#} FPS • 1% Low {before.Low1Fps:0.#}";
        AfterResultText.Text = after == null ? "ПОСЛЕ: пока нет результата" : $"ПОСЛЕ: {after.AverageFps:0.#} FPS • 1% Low {after.Low1Fps:0.#}";

        CaptureButton.IsVisible = before == null && !capturing;
        CaptureButton.IsEnabled = project != null && !capturing;
        AfterCaptureButton.IsVisible = before != null && after == null && !capturing;
        AfterCaptureButton.IsEnabled = project != null && !capturing;
        CancelCaptureButton.IsVisible = capturing;
        CancelCaptureButton.IsEnabled = capturing;
        OpenRunFolderButton.IsEnabled = project != null;
        GuidedOptimizePanel.IsVisible = before != null && after == null && !capturing;

        if (capturing)
        {
            BenchmarkStageTitle.Text = "Идёт непрерывный замер";
            BenchmarkStageText.Text = "Играйте в одной локации. Нажмите «Остановить и сохранить» или F9, когда будете готовы.";
        }
        else if (before == null)
        {
            BenchmarkStageTitle.Text = "Шаг 1 — измерьте текущий FPS";
            BenchmarkStageText.Text = "Запустите игру, войдите в локацию и нажмите «Замер ДО». Один сеанс, не более 5 минут.";
        }
        else if (after == null)
        {
            BenchmarkStageTitle.Text = "Шаг 2 — сохраните откат и оптимизируйте";
            BenchmarkStageText.Text = "После оптимизации снова сыграйте в ту же локацию и нажмите «Замер ПОСЛЕ».";
        }
        else
        {
            BenchmarkStageTitle.Text = "Сравнение готово";
            BenchmarkStageText.Text = "Разница показана ниже. Для другого теста выберите игру в «Игровых пресетах».";
        }

        CaptureHistoryList.ItemsSource = project?.Captures.ToList() ?? new List<FrameCapture>();
        HistoryEmpty.IsVisible = project == null || project.Captures.Count == 0;
        RefreshComparison(project);
    }

    private void RefreshComparison(GameBenchmarkProject? project)
    {
        var comparison = project == null ? null : _benchmark.Compare(project);
        ComparisonResult.IsVisible = comparison != null;
        if (comparison == null) return;
        ComparisonTitle.Text = "Разница ДО / ПОСЛЕ";
        ComparisonText.Text = "Один непрерывный замер с каждой стороны • зелёный — улучшение, красный — ухудшение";
        SetMetric(comparison.Average, AvgResultText, AvgDeltaText, AvgStatusText, "FPS");
        SetMetric(comparison.Low1, Low1ResultText, Low1DeltaText, Low1StatusText, "FPS");
        SetMetric(comparison.Low01, Low01ResultText, Low01DeltaText, Low01StatusText, "FPS");
        SetMetric(comparison.FrameTime, FrameTimeResultText, FrameTimeDeltaText, FrameTimeStatusText, "мс");
        ComparisonStabilityText.Text = "Средний FPS и 1% Low показаны по всем записанным кадрам непрерывного сеанса.";
        ComparisonWarningsText.Text = string.Join(Environment.NewLine, comparison.Warnings);
    }

    private static void SetMetric(MetricComparison metric, TextBlock result, TextBlock delta, TextBlock status, string unit)
    {
        result.Text = $"{metric.Before:0.#} → {metric.After:0.#} {unit}";
        delta.Text = $"{(metric.Delta >= 0 ? "+" : string.Empty)}{metric.Delta:0.#}%";
        delta.Foreground = Brush.Parse(metric.Color);
        status.Text = metric.StatusText;
        status.Foreground = Brush.Parse(metric.Color);
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
            StatusText.Text = "F9: сначала запустите игру и дождитесь автоопределения.";
            return;
        }
        await StartCapture(project.Captures.Any(x => x.Stage == "Before") ? "After" : "Before");
    }

    internal static string SelectHotkeyStage(GameBenchmarkProject project, CaptureSummary? beforeSummary) =>
        BenchmarkWorkflow.SelectHotkeyStage(project, beforeSummary);

    private async Task StartCapture(string stage)
    {
        if (_captureCts != null) return;
        var project = _benchmark.ActiveProject;
        if (project == null)
        {
            CaptureStatusText.Text = "Сначала запустите поддерживаемую игру — FramePilot определит её автоматически.";
            return;
        }
        if (stage == "After" && !project.Captures.Any(x => x.Stage == "Before"))
        {
            CaptureStatusText.Text = "Сначала выполните замер ДО.";
            return;
        }
        if (stage == "Before" && project.Captures.Any(x => x.Stage == "Before"))
        {
            CaptureStatusText.Text = "База ДО уже сохранена. Для нового теста нажмите «Применить и протестировать» в пресете игры.";
            return;
        }

        _captureCts = new CancellationTokenSource();
        LiveFpsText.Text = "…";
        CaptureProgress.Value = 0;
        CaptureProgress.IsVisible = true;
        CaptureStatusText.Text = "Подготовка захвата… Если появится системный запрос UAC, подтвердите его.";
        RefreshProjectUI();
        try
        {
            var environment = new CaptureEnvironment
            {
                Cpu = _lastReport?.Cpu ?? string.Empty,
                Gpu = _lastReport?.Gpu ?? string.Empty,
                GpuDriver = _lastReport?.GpuDetails ?? string.Empty,
                Memory = _lastReport?.Memory ?? string.Empty,
                MemorySpeed = _lastReport?.MemorySpeed ?? string.Empty,
                MemoryChannel = _lastReport?.MemoryChannel ?? string.Empty,
                Scene = "Одна и та же игровая локация",
                Resolution = project.Resolution,
                GraphicsPreset = project.GraphicsPreset,
                PresetName = project.PresetName,
                ProcessName = project.EffectiveProcessName
            };
            var progress = new Progress<CaptureProgressInfo>(info =>
            {
                CaptureProgress.Maximum = info.TotalSeconds;
                CaptureProgress.Value = info.TotalSeconds - info.RemainingSeconds;
                if (info.CurrentFps > 0) LiveFpsText.Text = $"{info.CurrentFps:0.#} FPS";
                if (!string.IsNullOrWhiteSpace(info.Resolution)) DetectedResolutionText.Text = info.Resolution;
                CaptureStatusText.Text = info.Phase == "Preparing"
                    ? "Подготовка PresentMon… Подтвердите UAC, если появится системный запрос."
                    : $"Запись кадров • осталось не более {info.RemainingSeconds / 60}:{info.RemainingSeconds % 60:00} • F9 — остановить и сохранить.";
            });
            var capture = await Task.Run(() => _capture.CaptureAsync(
                project, stage, environment, progress, 1, _captureCts.Token));
            LiveFpsText.Text = $"{capture.AverageFps:0.#} FPS";
            project.Resolution = capture.Environment.Resolution;
            _benchmark.SaveProjects();
            CaptureStatusText.Text = stage == "Before"
                ? $"База сохранена: {capture.AverageFps:0.#} FPS. Теперь создайте резервную копию и примените оптимизацию."
                : $"Замер ПОСЛЕ сохранён: {capture.AverageFps:0.#} FPS. Разница показана ниже.";
            StatusText.Text = stage == "Before" ? "Замер ДО сохранён" : "Сравнение ДО / ПОСЛЕ готово";
        }
        catch (Exception ex)
        {
            LiveFpsText.Text = "—";
            CaptureStatusText.Text = ex.Message;
            StatusText.Text = "Замер не завершён";
        }
        finally
        {
            _captureCts?.Dispose();
            _captureCts = null;
            CaptureProgress.IsVisible = false;
            RefreshProjectUI();
        }
    }

    private void CancelCapture_Click(object? sender, RoutedEventArgs e) => StopCapture();

    private void StopCapture()
    {
        if (_captureCts == null || _captureCts.IsCancellationRequested) return;
        CancelCaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "Останавливаем запись и сохраняем результат…";
        _captureCts.Cancel();
    }

    private async void GuidedOptimize_Click(object? sender, RoutedEventArgs e)
    {
        if (_benchmark.ActiveProject?.Captures.Any(x => x.Stage == "Before") != true) return;
        var confirmed = await MessageWindow.Confirm(this, "Создать откат и оптимизировать?",
            "Будут включены игровой режим, отключена фоновая запись, выбрана высокая производительность и подготовлен кэш шейдеров. " +
            "Службы и HAGS не меняются. Сначала будет создана резервная копия; точка восстановления Windows создаётся при правах администратора.",
            "Создать и применить");
        if (!confirmed) return;

        GuidedOptimizeButton.IsEnabled = false;
        GuidedOptimizeLog.Text = "Создание резервной копии и применение параметров…";
        StatusText.Text = "Оптимизация выполняется…";
        try
        {
            var result = await _optimization.ApplyAsync(new OptimizationOptions
            {
                GameMode = true,
                DisableCapture = true,
                HighPerformance = true,
                OptimizeShaderCache = true,
                RestorePoint = true,
                StopServices = false,
                Hags = false
            });
            GuidedOptimizeLog.Text = string.Join(Environment.NewLine, result.Messages);
            RefreshBackup();
            CaptureStatusText.Text = result.Success
                ? "Готово. Сыграйте в ту же локацию и нажмите «Замер ПОСЛЕ»."
                : "Оптимизация не завершена. Проверьте журнал.";
            StatusText.Text = result.Success ? "Оптимизация применена • доступен откат" : "Оптимизация не выполнена";
        }
        catch (Exception ex)
        {
            GuidedOptimizeLog.Text = ex.Message;
            StatusText.Text = "Ошибка оптимизации";
        }
        finally { GuidedOptimizeButton.IsEnabled = true; }
    }

    private void OpenRunFolder_Click(object? sender, RoutedEventArgs e)
    {
        var project = _benchmark.ActiveProject;
        if (project == null || !OperatingSystem.IsWindows()) return;
        Process.Start(new ProcessStartInfo("explorer.exe", AppStorage.GetProjectDirectory(project.Id)) { UseShellExecute = true });
    }
}
