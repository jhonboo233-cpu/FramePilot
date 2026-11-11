using System.Text.Json.Serialization;

namespace FramePilot.Models;

public sealed class SystemReport
{
    public string Cpu { get; set; } = "Не определён";
    public string CpuDetails { get; set; } = string.Empty;
    public string Gpu { get; set; } = "Не определена";
    public string GpuDetails { get; set; } = string.Empty;
    public string Memory { get; set; } = "Не определена";
    public string MemoryDetails { get; set; } = string.Empty;
    public string MemorySpeed { get; set; } = "Не определена";
    public string MemoryChannel { get; set; } = "Не определён";
    public string MemoryChannelDetails { get; set; } = string.Empty;
    public string Windows { get; set; } = "Не определена";
    public string WindowsDetails { get; set; } = string.Empty;
    public List<Finding> Findings { get; set; } = new();
}

public sealed class Finding
{
    public string Level { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Badge => Level switch
    {
        "good" => "ПОРЯДОК",
        "warning" => "ВНИМАНИЕ",
        "action" => "МОЖНО УЛУЧШИТЬ",
        _ => "СВЕДЕНИЯ"
    };
    public string Color => Level switch
    {
        "good" => "#49D7A5",
        "warning" => "#F3B95F",
        "action" => "#69B7FF",
        _ => "#8B95A8"
    };
}

public sealed class OptimizationOptions
{
    public bool GameMode { get; set; } = true;
    public bool DisableCapture { get; set; } = true;
    public bool HighPerformance { get; set; }
    public bool StopServices { get; set; }
    public bool OptimizeShaderCache { get; set; }
    public bool Hags { get; set; }
    public bool RestorePoint { get; set; } = true;
}

public sealed class RegistryBackup
{
    public string Root { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public int Value { get; set; }
}

public sealed class ServiceBackup
{
    public string Name { get; set; } = string.Empty;
    public bool WasRunning { get; set; }
}

public sealed class BackupSnapshot
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? ActivePowerScheme { get; set; }
    public List<RegistryBackup> RegistryValues { get; set; } = new();
    public List<ServiceBackup> Services { get; set; } = new();
    public List<string> AppliedChanges { get; set; } = new();
}

public sealed class OperationResult
{
    public bool Success { get; set; }
    public List<string> Messages { get; set; } = new();
    public bool RestartRecommended { get; set; }
}

public sealed class GameBenchmark
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Game { get; set; } = string.Empty;
    public double BeforeAverage { get; set; }
    public double BeforeLow { get; set; }
    public double AfterAverage { get; set; }
    public double AfterLow { get; set; }
    public string MemorySpeed { get; set; } = string.Empty;
    public string MemoryChannel { get; set; } = string.Empty;
    public double AverageDelta => (AfterAverage - BeforeAverage) / BeforeAverage * 100.0;
    public double LowDelta => (AfterLow - BeforeLow) / BeforeLow * 100.0;
}

public sealed class CpuBenchmarkResult
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double SingleThreadMs { get; set; }
    public double MultiThreadMs { get; set; }
    public int LogicalProcessors { get; set; }
}

public sealed class BenchmarkStore
{
    public List<GameBenchmark> GameResults { get; set; } = new();
    public CpuBenchmarkResult? CpuBaseline { get; set; }
    public CpuBenchmarkResult? CpuLatest { get; set; }
}

public sealed class BenchmarkProjectStore
{
    public List<GameBenchmarkProject> Projects { get; set; } = new();
    public string? ActiveProjectId { get; set; }
}

public sealed class GameBenchmarkProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string Scene { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string GraphicsPreset { get; set; } = string.Empty;
    public int CaptureSeconds { get; set; } = 60;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<FrameCapture> Captures { get; set; } = new();
    public PendingCapture? Pending { get; set; }
    public string DisplayName => Name;
}

public sealed class PendingCapture
{
    public string Stage { get; set; } = "Before";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public string RawCsvFileName { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public CaptureEnvironment Environment { get; set; } = new();
}

public sealed class FrameCapture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Stage { get; set; } = "Before";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string CaptureMode { get; set; } = "Auto";
    public double AverageFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double AverageFrameTimeMs { get; set; }
    public double MaxFrameTimeMs { get; set; }
    public double DurationSeconds { get; set; }
    public int FrameCount { get; set; }
    public string RawCsvFileName { get; set; } = string.Empty;
    public CaptureEnvironment Environment { get; set; } = new();
    public string StageLabel => Stage == "Before" ? "ДО" : "ПОСЛЕ";
    public string ModeLabel => CaptureMode == "Auto" ? "PresentMon" : "ручной ввод";
    public string Summary => $"{AverageFps:0.#} FPS • 1% {Low1Fps:0.#} • 0.1% {Low01Fps:0.#} • {FrameCount} кадров";
}

public sealed class CaptureEnvironment
{
    public string Cpu { get; set; } = string.Empty;
    public string Gpu { get; set; } = string.Empty;
    public string GpuDriver { get; set; } = string.Empty;
    public string Memory { get; set; } = string.Empty;
    public string MemorySpeed { get; set; } = string.Empty;
    public string MemoryChannel { get; set; } = string.Empty;
    public string Windows { get; set; } = string.Empty;
    public string Hags { get; set; } = string.Empty;
    public string PowerScheme { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string Scene { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string GraphicsPreset { get; set; } = string.Empty;
}

public sealed class CaptureSummary
{
    public double AverageFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double AverageFrameTimeMs { get; set; }
    public int RunCount { get; set; }
}

public sealed class MetricComparison
{
    public double Before { get; set; }
    public double After { get; set; }
    public double Delta { get; set; }
    public string Status => Delta >= 3 ? "good" : Delta <= -3 ? "bad" : "neutral";
    public string Color => Status == "good" ? "#65E1B4" : Status == "bad" ? "#FF9696" : "#A9B5C6";
    public string StatusText => Status == "good" ? "УЛУЧШЕНИЕ" : Status == "bad" ? "УХУДШЕНИЕ" : "В ПРЕДЕЛАХ РАЗБРОСА";
}

public sealed class BenchmarkComparison
{
    public MetricComparison Average { get; set; } = new();
    public MetricComparison Low1 { get; set; } = new();
    public MetricComparison Low01 { get; set; } = new();
    public MetricComparison FrameTime { get; set; } = new();
    public int BeforeRunCount { get; set; }
    public int AfterRunCount { get; set; }
    public List<string> Warnings { get; set; } = new();
}

[JsonSerializable(typeof(BackupSnapshot))]
[JsonSerializable(typeof(BenchmarkStore))]
[JsonSerializable(typeof(BenchmarkProjectStore))]
internal partial class AppJsonContext : JsonSerializerContext;
