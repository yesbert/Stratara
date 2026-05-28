using System.Diagnostics;
using System.Text;

namespace Stratara.Samples.SmokeTests;

internal static class SampleRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static SampleResult RunUntilExit(
        string sampleName,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var dllPath = ResolveSampleDll(sampleName);
        using var process = StartSampleProcess(dllPath, environment);

        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        var exited = process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds);
        if (!exited)
        {
            TerminateProcessTree(process);
            var partial = stdoutTask.IsCompleted ? stdoutTask.Result : "(stdout reader still running)";
            throw new TimeoutException(
                $"Sample '{sampleName}' did not terminate within {(timeout ?? DefaultTimeout).TotalSeconds:N0}s. " +
                $"Captured stdout so far:{Environment.NewLine}{partial}");
        }

        Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5));
        return new SampleResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    public static SampleResult RunUntilMarker(
        string sampleName,
        string markerPhrase,
        Action<string> onMarkerReached,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(markerPhrase);
        ArgumentNullException.ThrowIfNull(onMarkerReached);

        var dllPath = ResolveSampleDll(sampleName);
        using var process = StartSampleProcess(dllPath, environment);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var markerReached = new ManualResetEventSlim(initialState: false);
        var matchedLine = string.Empty;

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }
            lock (stdout)
            {
                stdout.AppendLine(args.Data);
            }
            if (!markerReached.IsSet && args.Data.Contains(markerPhrase, StringComparison.Ordinal))
            {
                matchedLine = args.Data;
                markerReached.Set();
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }
            lock (stderr)
            {
                stderr.AppendLine(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!markerReached.Wait(timeout ?? DefaultTimeout))
        {
            TerminateProcessTree(process);
            throw new TimeoutException(
                $"Sample '{sampleName}' did not emit marker '{markerPhrase}' within {(timeout ?? DefaultTimeout).TotalSeconds:N0}s. " +
                $"Captured stdout so far:{Environment.NewLine}{stdout}");
        }

        try
        {
            onMarkerReached(matchedLine);
        }
        finally
        {
            TerminateProcessTree(process);
            process.WaitForExit();
        }

        return new SampleResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string ResolveSampleDll(string sampleName)
    {
        var configuration = GetConfiguration();
        var testAssemblyDir = Path.GetDirectoryName(typeof(SampleRunner).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot resolve test-assembly directory.");
        var repoRoot = Path.GetFullPath(Path.Combine(testAssemblyDir, "..", "..", "..", "..", ".."));
        var dllPath = Path.Combine(repoRoot, "samples", sampleName, "bin", configuration, "net10.0", $"{sampleName}.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"Sample assembly not found at '{dllPath}'. Build the sample first (a ProjectReference with " +
                $"ReferenceOutputAssembly=false in the smoke-test csproj should handle this automatically).");
        }
        return dllPath;
    }

    private static Process StartSampleProcess(string dllPath, IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
        };
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_NOLOGO"] = "true";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }
        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start sample process for '{dllPath}'.");
        }
        return process;
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string GetConfiguration()
    {
        var testAssemblyDir = Path.GetDirectoryName(typeof(SampleRunner).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot resolve test-assembly directory.");
        return new DirectoryInfo(testAssemblyDir).Parent?.Name ?? "Debug";
    }
}

internal sealed record SampleResult(int ExitCode, string StdOut, string StdErr);
