using System.Diagnostics;
using System.Text.Json;
using FramePilot.Models;

namespace FramePilot.Services;

internal static class AppStorage
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FramePilot");

    public static string BackupPath => Path.Combine(DataDirectory, "backup-last.json");
    public static string BenchmarkPath => Path.Combine(DataDirectory, "benchmarks.json");
    public static string BenchmarkProjectsPath => Path.Combine(DataDirectory, "benchmark-projects.json");
    public static string BenchmarkRunsDirectory => Path.Combine(DataDirectory, "BenchmarkRuns");

    public static string GetProjectDirectory(string projectId)
    {
        var path = Path.Combine(BenchmarkRunsDirectory, projectId);
        Directory.CreateDirectory(path);
        return path;
    }

    static AppStorage() => Directory.CreateDirectory(DataDirectory);

    public static BackupSnapshot? LoadBackup()
    {
        try
        {
            return File.Exists(BackupPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(BackupPath), AppJsonContext.Default.BackupSnapshot)
                : null;
        }
        catch { return null; }
    }

    public static void SaveBackup(BackupSnapshot snapshot)
    {
        if (File.Exists(BackupPath))
        {
            var archive = Path.Combine(DataDirectory, $"backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(BackupPath, archive, overwrite: true);
        }
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.BackupSnapshot));
    }

    public static BenchmarkStore LoadBenchmarks()
    {
        try
        {
            return File.Exists(BenchmarkPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(BenchmarkPath), AppJsonContext.Default.BenchmarkStore) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveBenchmarks(BenchmarkStore store)
        => File.WriteAllText(BenchmarkPath, JsonSerializer.Serialize(store, AppJsonContext.Default.BenchmarkStore));

    public static BenchmarkProjectStore LoadBenchmarkProjects()
    {
        try
        {
            return File.Exists(BenchmarkProjectsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(BenchmarkProjectsPath), AppJsonContext.Default.BenchmarkProjectStore) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveBenchmarkProjects(BenchmarkProjectStore store)
    {
        var json = JsonSerializer.Serialize(store, AppJsonContext.Default.BenchmarkProjectStore);
        var temporary = BenchmarkProjectsPath + ".tmp";
        File.WriteAllText(temporary, json);
        if (File.Exists(BenchmarkProjectsPath))
            File.Replace(temporary, BenchmarkProjectsPath, null);
        else
            File.Move(temporary, BenchmarkProjectsPath);
    }

    public static void OpenDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", DataDirectory) { UseShellExecute = true });
    }
}

internal static class ProcessTools
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = string.Join(Environment.NewLine, new[] { await stdout, await stderr }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return (process.ExitCode, output.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
