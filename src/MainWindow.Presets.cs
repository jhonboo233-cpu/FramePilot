using Avalonia.Controls;
using Avalonia.Interactivity;
using FramePilot.Models;
using FramePilot.Services;

namespace FramePilot;

public partial class MainWindow
{
    private bool _presetUiUpdating;

    private GamePreset? SelectedPreset => GamePresetSelector.SelectedItem as GamePreset;

    private void InitializePresetUI()
    {
        _presetUiUpdating = true;
        GamePresetSelector.ItemsSource = GamePresetCatalog.All;
        GamePresetSelector.SelectedIndex = 0;
        _presetUiUpdating = false;
        RefreshPresetView();
    }

    private void GamePresetSelector_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_presetUiUpdating) RefreshPresetView();
    }

    private void RefreshPresetView()
    {
        var preset = SelectedPreset;
        if (preset == null) return;
        PresetReferenceText.Text = GamePresetCatalog.ReferenceHardware;
        PresetGameTitle.Text = preset.GameName;
        PresetSubtitle.Text = preset.Subtitle;
        PresetProcessText.Text = preset.ProcessName;
        UsePresetButton.Content = "Применить и протестировать";
    }

    private async void UsePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { } preset) return;
        _requestedPresetId = preset.Id;
        _benchmark.SetActiveProject(null);
        DetectedGameText.Text = preset.GameName;
        DetectedProcessText.Text = $"Ожидание {preset.ProcessName}";
        DetectedResolutionText.Text = "Определяется";
        LiveFpsText.Text = "—";
        RefreshProjectUI();
        Navigate_Click(NavBenchmark, new RoutedEventArgs());
        CaptureStatusText.Text = $"Запустите {preset.GameName}. После автоопределения нажмите «Замер ДО».";
        await DetectGameNow();
    }
}
