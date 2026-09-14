using System.Diagnostics;
using System.Threading.Channels;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// A separately started host under the test's control: the benchmark executable in its
/// <c>--poc-host</c> mode, which runs a scenario from this assembly. Commands go in on stdin, one
/// line each; replies come back on stdout. <see cref="Kill"/> is a real process kill — the state a
/// crash leaves is what the tests after it are about.
/// </summary>
public sealed class PocHostProcess : IAsyncDisposable
{
    private const string HostAssembly = "Stratara.Orleans.Benchmarks";
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(2);

    private readonly Process _process;
    private readonly Channel<string> _replies = Channel.CreateUnbounded<string>();
    private readonly List<string> _log = [];

    private PocHostProcess(Process process)
    {
        _process = process;
    }

    public bool HasExited => _process.HasExited;

    public int ProcessId => _process.Id;

    public IReadOnlyList<string> Log => _log;

    public static async Task<PocHostProcess> StartAsync(string scenario, IReadOnlyDictionary<string, string> environment, TimeSpan? readyTimeout = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { HostAssemblyPath(), "--poc-host", scenario },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var host = new PocHostProcess(process);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            host._log.Add(args.Data);
            if (args.Data.StartsWith(PocHostEntry.ReplyPrefix, StringComparison.Ordinal))
            {
                host._replies.Writer.TryWrite(args.Data[PocHostEntry.ReplyPrefix.Length..]);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                host._log.Add("stderr: " + args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var ready = await host.ReadReplyAsync(readyTimeout ?? ReplyTimeout);
        if (ready != "ready")
        {
            throw new InvalidOperationException($"The host did not come up; first reply was '{ready}'. Log:{Environment.NewLine}{string.Join(Environment.NewLine, host._log)}");
        }

        return host;
    }

    /// <summary>
    /// The benchmark executable beside this test assembly's output: the same configuration and
    /// framework, one project directory over.
    /// </summary>
    private static string HostAssemblyPath()
    {
        var testOutput = Path.GetDirectoryName(typeof(PocHostProcess).Assembly.Location)!;
        var framework = Path.GetFileName(testOutput);
        var configuration = Path.GetFileName(Path.GetDirectoryName(testOutput)!);
        var testsRoot = Path.GetFullPath(Path.Combine(testOutput, "..", "..", "..", ".."));
        var path = Path.Combine(testsRoot, HostAssembly, "bin", configuration, framework, HostAssembly + ".dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"The host executable is not built: {path}. Build {HostAssembly} in the same configuration first.");
    }

    /// <summary>Sends one command line and returns the host's reply.</summary>
    public async Task<string> SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        return await ReadReplyAsync();
    }

    /// <summary>
    /// Sends a command that is expected to end the process from the inside, and waits for the exit
    /// instead of a reply.
    /// </summary>
    public async Task SendExpectingExitAsync(string command, TimeSpan timeout)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        using var exitTimeout = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(exitTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The host did not exit within {timeout} after '{command}'. Log:{Environment.NewLine}{string.Join(Environment.NewLine, _log)}");
        }
    }

    /// <summary>Kills the process outright. Nothing in it gets to clean up.</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                // "exit" is answered by the process ending, not by a reply line.
                await _process.StandardInput.WriteLineAsync("exit");
                await _process.StandardInput.FlushAsync();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (Exception)
            {
                Kill();
            }
        }

        _process.Dispose();
    }

    private Task<string> ReadReplyAsync() => ReadReplyAsync(ReplyTimeout);

    private async Task<string> ReadReplyAsync(TimeSpan replyTimeout)
    {
        using var timeout = new CancellationTokenSource(replyTimeout);
        try
        {
            return await _replies.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No reply from the host within {replyTimeout}. Log:{Environment.NewLine}{string.Join(Environment.NewLine, _log)}");
        }
    }
}
