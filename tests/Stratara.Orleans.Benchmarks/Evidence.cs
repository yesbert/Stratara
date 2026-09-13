using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// Where a run's raw output goes and what is recorded beside it: the commit, the machine, the
/// runtime and the container images, so a number can always be traced to the conditions it was
/// measured under.
/// </summary>
public static class Evidence
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string CreateRunDirectory(string evidenceRoot, string measurement)
    {
        var directory = Path.Combine(evidenceRoot, "raw", measurement, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void WriteEnvironment(string runDirectory, IReadOnlyDictionary<string, string> images, object? settings = null)
    {
        var environment = new
        {
            commit = GitCommit(),
            recordedAt = DateTimeOffset.UtcNow,
            machine = new
            {
                description = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                runtime = RuntimeInformation.FrameworkDescription,
            },
            images,
            settings,
        };
        File.WriteAllText(Path.Combine(runDirectory, "environment.json"), JsonSerializer.Serialize(environment, Json));
    }

    public static void WriteResult(string runDirectory, object result) =>
        File.WriteAllText(Path.Combine(runDirectory, "result.json"), JsonSerializer.Serialize(result, Json));

    private static string GitCommit()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, UseShellExecute = false });
            var commit = git?.StandardOutput.ReadToEnd().Trim();
            git?.WaitForExit();
            return string.IsNullOrEmpty(commit) ? "unknown" : commit;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
