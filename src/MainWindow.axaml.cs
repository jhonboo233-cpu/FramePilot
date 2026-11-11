using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FramePilot.Models;
using FramePilot.Services;
using Microsoft.Win32;

namespace FramePilot;

public partial class MainWindow : Window
{
    private readonly AuditService _audit = new();
    private readonly OptimizationService _optimization = new();
    private readonly BenchmarkService _benchmark = new();
    private readonly FrameCaptureService _capture;
    private CancellationTokenSource? _captureCts;
    private SystemReport? _lastReport;
    private Task<SystemReport>? _memoryTask;
    private bool _projectUiUpdating;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        _capture = new FrameCaptureService(_benchmark);
        _initialized = true;
        UpdateAdminStatus();
        UpdatePlan();
        RefreshBackup();
        foreach (var project in _benchmark.ProjectStore.Projects.ToList())
            _capture.TryRecoverPending(project);
        RefreshProjectList();
        RefreshCpuResults();
        FindingsList.ItemsSource = new[]
        {
            new Finding { Level = "info", Title = "Начните с диагностики", Description = "Приложение соберёт сведения о системе и покажет только подтверждённые рекомендации. Никакие параметры при проверке не изменяются." }
        };
        FindingCount.Text = "готов к проверке";
        if (_benchmark.ProjectStore.Projects.Count == 0 ||
            (_benchmark.ActiveProject?.Captures.Any(x => x.Stage == "Before") == true &&
             _benchmark.ActiveProject.Captures.All(x => x.Stage != "After")))
            Navigate_Click(NavBenchmark, new RoutedEventArgs());
        _ = LoadMemoryInfoAsync();
    }

    private void Navigate_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var button in new[] { NavAudit, NavOptimize, NavRestore, NavBenchmark })
            button.Classes.Remove("selected");
        if (sender is not Button selected) return;
        selected.Classes.Add("selected");

        AuditView.IsVisible = selected == NavAudit;
        OptimizeView.IsVisible = selected == NavOptimize;
        RestoreView.IsVisible = selected == NavRestore;
        BenchmarkView.IsVisible = selected == NavBenchmark;

        if (selected == NavAudit)
        {
            PageTitle.Text = "Диагностика системы";
            PageSubtitle.Text = "Сначала понимаем ограничение — затем меняем настройки.";
        }
        else if (selected == NavOptimize)
        {
            PageTitle.Text = "Обратимая оптимизация";
            PageSubtitle.Text = "Только выбранные параметры, предварительный план и автоматическая копия.";
            UpdatePlan();
        }
        else if (selected == NavRestore)
        {
            PageTitle.Text = "Возврат исходных настроек";
            PageSubtitle.Text = "Восстановление значений, которые изменил FramePilot.";
            RefreshBackup();
        }
        else
        {
            PageTitle.Text = "Измерение результата";
            PageSubtitle.Text = "Сравнивайте одинаковые сцены и сохраняйте честные результаты.";
            _ = LoadMemoryInfoAsync();
        }
    }

    private void UpdateAdminStatus()
    {
        var admin = OptimizationService.IsAdministrator();
        AdminButton.Content = admin ? "Права администратора получены" : "Обычный режим · Повысить права";
    }

    private async void Admin_Click(object? sender, RoutedEventArgs e)
    {
        if (OptimizationService.IsAdministrator())
        {
            await MessageWindow.Show(this, "Права администратора", "Приложение уже запущено с правами администратора. Они нужны для HAGS, временной остановки служб и создания точки восстановления.");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            await MessageWindow.Show(this, "Режим не поддерживается", "Повышение прав доступно в Windows. Запустите FramePilot на целевом компьютере.");
            return;
        }
        var restart = await MessageWindow.Confirm(this, "Повысить права?", "FramePilot будет перезапущен через системный запрос UAC. Несохранённые поля бенчмарка нужно будет ввести заново.", "Перезапустить");
        if (!restart) return;
        try
        {
            OptimizationService.RestartAsAdministrator();
            Close();
        }
        catch (Exception ex) { await MessageWindow.Show(this, "Не удалось повысить права", ex.Message); }
    }

    private async void RunAudit_Click(object? sender, RoutedEventArgs e)
    {
        RunAuditButton.IsEnabled = false;
        AuditProgress.IsVisible = true;
        StatusText.Text = "Собираем сведения о системе…";
        try
        {
            var report = await _audit.RunAsync();
            _lastReport = report;
            CpuText.Text = report.Cpu;
            CpuDetailsText.Text = report.CpuDetails;
            GpuText.Text = report.Gpu;
            GpuDetailsText.Text = report.GpuDetails;
            MemoryText.Text = report.Memory;
            ApplyMemoryInfo(report);
            WindowsText.Text = report.Windows;
            WindowsDetailsText.Text = report.WindowsDetails;
            FindingsList.ItemsSource = report.Findings;
            FindingCount.Text = $"{report.Findings.Count} записей";
            StatusText.Text = "Диагностика завершена • настройки не изменялись";
        }
        catch (Exception ex)
        {
            await MessageWindow.Show(this, "Ошибка диагностики", ex.Message);
            StatusText.Text = "Диагностика завершилась с ошибкой";
        }
        finally
        {
            AuditProgress.IsVisible = false;
            RunAuditButton.IsEnabled = true;
        }
    }

    private async Task LoadMemoryInfoAsync()
    {
        if (_lastReport != null)
        {
            ApplyMemoryInfo(_lastReport);
            return;
        }
        try
        {
            _memoryTask ??= _audit.RunAsync();
            var report = await _memoryTask;
            _lastReport ??= report;
            ApplyMemoryInfo(_lastReport);
        }
        catch
        {
            BenchmarkMemoryText.Text = "Не определена";
            BenchmarkMemorySpeedText.Text = "Не определена";
            BenchmarkMemoryChannelText.Text = "Не определён";
            _memoryTask = null;
        }
    }

    private void ApplyMemoryInfo(SystemReport report)
    {
        MemoryDetailsText.Text = report.MemoryDetails;
        MemoryChannelText.Text = report.MemoryChannel;
        MemoryChannelDetailsText.Text = report.MemoryChannelDetails;
        BenchmarkMemoryText.Text = report.Memory;
        BenchmarkMemorySpeedText.Text = report.MemorySpeed;
        BenchmarkMemoryChannelText.Text = report.MemoryChannel;
        BenchmarkMemoryDetailsText.Text = string.IsNullOrWhiteSpace(report.MemoryChannelDetails)
            ? "Данные будут сохранены вместе с игровым замером."
            : report.MemoryChannelDetails + " Частота и канальный режим сохранятся вместе с игровым замером.";
    }

    private OptimizationOptions CurrentOptions() => new()
    {
        GameMode = GameModeToggle.IsChecked == true,
        DisableCapture = CaptureToggle.IsChecked == true,
        HighPerformance = PowerToggle.IsChecked == true,
        StopServices = ServicesToggle.IsChecked == true,
        OptimizeShaderCache = ShaderCacheToggle.IsChecked == true,
        Hags = HagsToggle.IsChecked == true,
        RestorePoint = RestorePointToggle.IsChecked == true
    };

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initialized) UpdatePlan();
    }

    private void UpdatePlan()
    {
        if (PlanList != null) PlanList.ItemsSource = OptimizationService.BuildPlan(CurrentOptions());
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var options = CurrentOptions();
        var plan = OptimizationService.BuildPlan(options);
        if (!options.GameMode && !options.DisableCapture && !options.HighPerformance && !options.StopServices && !options.OptimizeShaderCache && !options.Hags)
        {
            await MessageWindow.Show(this, "Ничего не выбрано", "Включите хотя бы один параметр Windows. Создание точки восстановления само по себе не является оптимизацией.");
            return;
        }
        var confirmed = await MessageWindow.Confirm(this, "Применить оптимизацию?", string.Join(Environment.NewLine, plan) + Environment.NewLine + Environment.NewLine + "Исходные значения будут сохранены перед изменением.", "Применить");
        if (!confirmed) return;

        ApplyButton.IsEnabled = false;
        StatusText.Text = "Применяем выбранные параметры…";
        OptimizeLog.Text = "Выполняется операция…";
        try
        {
            var result = await _optimization.ApplyAsync(options);
            OptimizeLog.Text = string.Join(Environment.NewLine, result.Messages);
            StatusText.Text = result.Success ? "Оптимизация завершена • доступен откат" : "Оптимизация не выполнена";
            if (result.Success && _benchmark.ActiveProject?.Captures.Any(x => x.Stage == "Before") == true)
                StatusText.Text = "Оптимизация завершена • вернитесь в Бенчмарк для замера ПОСЛЕ";
            RefreshBackup();
            if (result.RestartRecommended)
                OptimizeLog.Text += Environment.NewLine + "Рекомендуется перезагрузить компьютер перед A/B-тестом HAGS.";
        }
        catch (Exception ex)
        {
            OptimizeLog.Text = ex.ToString();
            StatusText.Text = "Ошибка оптимизации";
        }
        finally { ApplyButton.IsEnabled = true; }
    }

    private void RefreshBackup_Click(object? sender, RoutedEventArgs e) => RefreshBackup();

    private void RefreshBackup()
    {
        var backup = AppStorage.LoadBackup();
        BackupEmpty.IsVisible = backup == null;
        BackupInfo.IsVisible = backup != null;
        RestoreButton.IsEnabled = backup != null;
        if (backup == null) return;
        BackupDate.Text = $"Создана: {backup.CreatedAt:dd.MM.yyyy, HH:mm:ss}";
        BackupChangesList.ItemsSource = backup.AppliedChanges.Select(x => $"• {x}").ToList();
        BackupPathText.Text = AppStorage.BackupPath;
    }

    private void OpenBackupFolder_Click(object? sender, RoutedEventArgs e)
    {
        try { AppStorage.OpenDataDirectory(); }
        catch (Exception ex) { StatusText.Text = $"Не удалось открыть папку: {ex.Message}"; }
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageWindow.Confirm(this, "Восстановить исходные значения?", "Будут применены параметры из последней резервной копии FramePilot. Работавшие до оптимизации службы из списка будут запущены снова. Другие настройки Windows не изменятся.", "Восстановить");
        if (!confirmed) return;
        RestoreButton.IsEnabled = false;
        RestoreLog.Text = "Выполняется восстановление…";
        StatusText.Text = "Восстанавливаем исходные параметры…";
        try
        {
            var result = await _optimization.RestoreAsync();
            RestoreLog.Text = string.Join(Environment.NewLine, result.Messages);
            if (result.RestartRecommended) RestoreLog.Text += Environment.NewLine + "Перезагрузите компьютер, чтобы завершить восстановление HAGS.";
            StatusText.Text = result.Success ? "Исходные настройки восстановлены" : "Откат завершён с ошибками";
        }
        catch (Exception ex) { RestoreLog.Text = ex.ToString(); StatusText.Text = "Ошибка восстановления"; }
        finally { RestoreButton.IsEnabled = AppStorage.LoadBackup() != null; }
    }

    private async void RunCpu_Click(object? sender, RoutedEventArgs e)
    {
        RunCpuButton.IsEnabled = false;
        CpuProgress.IsVisible = true;
        StatusText.Text = "Выполняется короткий CPU-тест…";
        try
        {
            await _benchmark.RunCpuAsync();
            RefreshCpuResults();
            StatusDot.Fill = Brush.Parse("#65E1B4");
            StatusText.Text = "CPU-тест завершён • результат сохранён";
        }
        catch (Exception ex) { StatusText.Text = $"CPU-тест не выполнен: {ex.Message}"; }
        finally { RunCpuButton.IsEnabled = true; CpuProgress.IsVisible = false; }
    }

    private void RefreshCpuResults()
    {
        var latest = _benchmark.Store.CpuLatest;
        var baseline = _benchmark.Store.CpuBaseline;
        if (latest == null) return;
        CpuSingleText.Text = $"{latest.SingleThreadMs:0} мс";
        CpuMultiText.Text = $"{latest.MultiThreadMs:0} мс • {latest.LogicalProcessors} потоков";
        if (baseline != null && baseline.CreatedAt != latest.CreatedAt)
        {
            var singleDelta = (baseline.SingleThreadMs - latest.SingleThreadMs) / baseline.SingleThreadMs * 100;
            var multiDelta = (baseline.MultiThreadMs - latest.MultiThreadMs) / baseline.MultiThreadMs * 100;
            CpuCompareText.Text = $"Один поток: {Signed(singleDelta)}% • все потоки: {Signed(multiDelta)}%. Положительное значение означает меньшее время.";
        }
        else CpuCompareText.Text = "Это базовый замер. Повторный результат будет сравнен с ним.";
    }
}
