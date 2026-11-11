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
        ProjectSelector.ItemsSource = _benchmark.ProjectStore.Projects.ToList();
        ProjectSelector.SelectedItem = _benchmark.ActiveProject;
        _projectUiUpdating = false;
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
        var hasProject = project != null;
        var baselineLocked = afterCount > 0;

        SaveProjectButton.Content = hasProject ? "Сохранить условия" : "Создать проект";
        SaveProjectButton.IsEnabled = !capturing;
        NewProjectButton.IsEnabled = !capturing;
        ProjectSelector.IsEnabled = !capturing;
        LaunchGameButton.IsEnabled = hasProject && !capturing;
        OpenRunFolderButton.IsEnabled = hasProject;
        CaptureButton.IsEnabled = hasProject && !baselineLocked && !capturing;
        ManualCaptureButton.IsEnabled = hasProject && !baselineLocked && !capturing;
        AfterCaptureButton.IsVisible = beforeCount > 0;
        AfterCaptureButton.IsEnabled = hasProject && !capturing;
        ManualAfterButton.IsVisible = beforeCount > 0;
        ManualAfterButton.IsEnabled = hasProject && !capturing;
        GoOptimizeButton.IsVisible = beforeCount > 0;

        if (!hasProject)
        {
            BenchmarkStageTitle.Text = "Шаг 1 — зафиксируйте результат ДО";
            BenchmarkStageText.Text = "Создайте проект до применения оптимизаций. Базовый замер станет точкой сравнения.";
        }
        else if (beforeCount == 0)
        {
            BenchmarkStageTitle.Text = "Шаг 1 — выполните замер ДО";
            BenchmarkStageText.Text = "Запустите игру, дойдите до указанной сцены и начните захват. Пока оптимизацию не применяйте.";
        }
        else if (afterCount == 0)
        {
            BenchmarkStageTitle.Text = $"База сохранена: {beforeCount} замер(а) ДО";
            BenchmarkStageText.Text = beforeCount < 3
                ? "Можно повторить ДО для надёжной базы или перейти к оптимизации. После неё выполните замер ПОСЛЕ той же сцены."
                : "Базовая серия готова. Перейдите к оптимизации, затем выполните замер ПОСЛЕ той же сцены.";
        }
        else
        {
            BenchmarkStageTitle.Text = "Сравнение готово";
            BenchmarkStageText.Text = $"Сохранено серий: {beforeCount} ДО и {afterCount} ПОСЛЕ. База заблокирована; замеры ПОСЛЕ можно повторять.";
        }

        CaptureHistoryList.ItemsSource = project?.Captures.ToList() ?? new List<FrameCapture>();
        HistoryEmpty.IsVisible = beforeCount + afterCount == 0;
        RefreshComparison(project);
    }

    private void RefreshComparison(GameBenchmarkProject? project)
    {
        var comparison = project == null ? null : _benchmark.Compare(project);
        ComparisonResult.IsVisible = comparison != null;
        if (comparison == null) return;
        ComparisonTitle.Text = comparison.Average.Status == "good" && comparison.Low1.Status == "good"
            ? "Подтверждён положительный результат"
            : comparison.Average.Status == "bad" || comparison.Low1.Status == "bad"
                ? "Есть признаки ухудшения"
                : "Существенный прирост не подтверждён";
        ComparisonText.Text = $"Использована медиана серий: {comparison.BeforeRunCount} ДО и {comparison.AfterRunCount} ПОСЛЕ. Для времени кадра положительный процент означает меньшее время.";
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
    }

    private void LoadProjectForm(GameBenchmarkProject project)
    {
        ProjectNameInput.Text = project.Name;
        GamePathInput.Text = project.GamePath;
        SceneInput.Text = project.Scene;
        ResolutionInput.Text = project.Resolution;
        GraphicsPresetInput.Text = project.GraphicsPreset;
        DurationInput.Text = project.CaptureSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private int ReadDuration()
    {
        if (!int.TryParse(DurationInput.Text, out var seconds) || seconds is < 15 or > 600)
            throw new ArgumentException("Длительность захвата должна быть целым числом от 15 до 600 секунд.");
        return seconds;
    }

    private GameBenchmarkProject SaveCurrentProject()
    {
        var project = _benchmark.ActiveProject;
        if (project == null)
            return _benchmark.CreateProject(ProjectNameInput.Text ?? string.Empty, GamePathInput.Text ?? string.Empty,
                SceneInput.Text ?? string.Empty, ResolutionInput.Text ?? string.Empty,
                GraphicsPresetInput.Text ?? string.Empty, ReadDuration());
        _benchmark.UpdateProject(project, ProjectNameInput.Text ?? string.Empty, GamePathInput.Text ?? string.Empty,
            SceneInput.Text ?? string.Empty, ResolutionInput.Text ?? string.Empty,
            GraphicsPresetInput.Text ?? string.Empty, ReadDuration());
        return project;
    }

    private void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _benchmark.SetActiveProject(project.Id);
            RefreshProjectList();
            StatusText.Text = "Проект сохранён • выполните базовый замер ДО";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void BrowseGame_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите исполняемый файл игры",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Исполняемые файлы") { Patterns = new[] { "*.exe" } } }
        });
        if (files.Count == 0) return;
        GamePathInput.Text = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(ProjectNameInput.Text))
            ProjectNameInput.Text = Path.GetFileNameWithoutExtension(GamePathInput.Text);
    }

    private void LaunchGame_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var project = SaveCurrentProject();
            _capture.LaunchGame(project, out var message);
            CaptureStatusText.Text = message;
            StatusText.Text = message;
            RefreshProjectList();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void CaptureBefore_Click(object? sender, RoutedEventArgs e) => await StartCapture("Before");
    private async void CaptureAfter_Click(object? sender, RoutedEventArgs e) => await StartCapture("After");

    private async Task StartCapture(string stage)
    {
        if (_captureCts != null) return;
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
            var environment = await BuildEnvironment(project);
            _captureCts = new CancellationTokenSource();
            CaptureProgress.Maximum = project.CaptureSeconds;
            CaptureProgress.Value = project.CaptureSeconds;
            CaptureProgress.IsVisible = true;
            CancelCaptureButton.IsVisible = true;
            CancelCaptureButton.IsEnabled = true;
            CaptureStatusText.Text = "Запуск PresentMon. Подтвердите запрос UAC, если он появится.";
            RefreshProjectUI();
            var progress = new Progress<int>(remaining =>
            {
                CaptureProgress.Value = remaining;
                CaptureStatusText.Text = $"Идёт захват {StageName(stage)} • осталось примерно {remaining} с. Не переключайте сцену.";
            });
            var capture = await _capture.CaptureAsync(project, stage, environment, progress, _captureCts.Token);
            CaptureStatusText.Text = $"Сохранено: {capture.AverageFps:0.#} FPS • 1% Low {capture.Low1Fps:0.#} • 0.1% Low {capture.Low01Fps:0.#}. CSV и отчёт записаны.";
            StatusText.Text = stage == "Before" ? "Базовый замер ДО сохранён" : "Замер ПОСЛЕ сохранён • сравнение обновлено";
            RefreshProjectList();
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = ex.Message;
            StatusText.Text = "Захват не завершён";
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

    private void CancelCapture_Click(object? sender, RoutedEventArgs e)
    {
        if (_captureCts == null) return;
        CancelCaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "Останавливаем PresentMon и сохраняем доступные данные…";
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
            CaptureStatusText.Text = $"Ручной замер {StageName(stage)} сохранён вместе с конфигурацией системы.";
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
            GraphicsPreset = project.GraphicsPreset
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
