using BenchmarkDotNet.Running;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.Benchmarks;

/// <summary>
/// Two jobs in one executable: the host the integration tests kill and restart
/// (<c>--poc-host scenario</c>), and the benchmark runs whose raw output goes to the evidence
/// directory. Without arguments it prints what it does and exits, because the local gauntlet runs
/// every executable under <c>tests/</c>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var code = await RunAsync(args);
        Environment.Exit(code);
        return code;
    }

    /// <summary>
    /// The runs leave threads behind — child processes' readers, silo threads — that would keep the
    /// process alive after the work is done; <see cref="Main"/> ends it explicitly.
    /// </summary>
    private static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--poc-host", var scenario])
        {
            return await PocHostEntry.RunAsync(scenario);
        }

        if (args is ["--append-throughput", var evidenceRoot, ..])
        {
            var appends = args.Length > 2 ? int.Parse(args[2]) : 10_000;
            var repetitions = args.Length > 3 ? int.Parse(args[3]) : 3;
            return await AppendThroughputRun.RunAsync(evidenceRoot, appends, repetitions);
        }

        if (args is ["--durable-bundles", var durableEvidenceRoot, ..])
        {
            var appends = args.Length > 2 ? int.Parse(args[2]) : 10_000;
            var repetitions = args.Length > 3 ? int.Parse(args[3]) : 3;
            return await DurableBundlesRun.RunAsync(durableEvidenceRoot, appends, repetitions);
        }

        if (args is ["--read-model-latency", var latencyEvidenceRoot, ..])
        {
            var events = args.Length > 2 ? int.Parse(args[2]) : 2_000;
            var rate = args.Length > 3 ? int.Parse(args[3]) : 50;
            var repetitions = args.Length > 4 ? int.Parse(args[4]) : 3;
            return await ReadModelLatencyRun.RunAsync(latencyEvidenceRoot, events, rate, repetitions);
        }

        if (args is ["--commands-per-aggregate", var commandsEvidenceRoot, ..])
        {
            var commands = args.Length > 2 ? int.Parse(args[2]) : 2_000;
            var repetitions = args.Length > 3 ? int.Parse(args[3]) : 3;
            return await CommandsPerAggregateRun.RunAsync(commandsEvidenceRoot, commands, repetitions);
        }

        if (args is ["--resources", var resourcesEvidenceRoot, ..])
        {
            var idle = args.Length > 2 ? int.Parse(args[2]) : 60;
            var load = args.Length > 3 ? int.Parse(args[3]) : 60;
            var rate = args.Length > 4 ? int.Parse(args[4]) : 200;
            return await ResourcesRun.RunAsync(resourcesEvidenceRoot, idle, load, rate);
        }

        if (args is ["--rebuild", var rebuildEvidenceRoot, ..])
        {
            var events = args.Length > 2 ? int.Parse(args[2]) : 100_000;
            var liveRate = args.Length > 3 ? int.Parse(args[3]) : 20;
            return await RebuildRun.RunAsync(rebuildEvidenceRoot, events, liveRate);
        }

        if (args.Length == 0)
        {
            Console.WriteLine(
                "Stratara.Orleans.Benchmarks runs only with explicit arguments: --poc-host <scenario> starts a host the " +
                "integration tests kill and restart; --append-throughput <evidence-dir> [appends] [repetitions] runs B1; " +
                "any other argument goes to BenchmarkDotNet. Every measurement is pre-registered in " +
                "openspec/changes/prove-an-orleans-execution-model/evidence/expectations.md before it runs.");
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
