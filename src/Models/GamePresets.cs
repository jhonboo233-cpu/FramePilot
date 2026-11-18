namespace FramePilot.Models;

public sealed class GamePreset
{
    public string Id { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string TestScene { get; set; } = string.Empty;
    public string Resolution { get; set; } = "1920×1080";
    public string ProfileName { get; set; } = string.Empty;
    public int CaptureSeconds { get; set; } = 300;
    public int WarmupSeconds { get; set; }
    public int RunsPerSeries { get; set; } = 1;
    public List<string> Preparation { get; set; } = new();
    public List<PresetSetting> GameSettings { get; set; } = new();
    public List<PresetSetting> NvidiaSettings { get; set; } = new();
    public List<string> Avoid { get; set; } = new();
    public string DisplayName => GameName;
    public string CapturePlan => "Один непрерывный замер ДО и один ПОСЛЕ • каждый не более 5 минут";
}

public sealed class PresetSetting
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
