using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Runs one scenario as a host that takes commands on stdin and answers on stdout. Hosted by the
/// benchmark executable's <c>--poc-host</c> mode — a separate process the tests can kill — because a
/// host cannot run inside this assembly's own start-up: a module initializer holds the module lock,
/// and the first continuation that touches this module on another thread waits for it forever.
/// </summary>
public static class PocHostEntry
{
    public const string ReplyPrefix = "poc> ";

    public static readonly IReadOnlyDictionary<string, Func<IPocScenario>> Scenarios = new Dictionary<string, Func<IPocScenario>>(StringComparer.Ordinal)
    {
        ["timers"] = () => new TimersScenario(),
        ["intent"] = () => new IntentScenario(),
        ["bus"] = () => new BusScenario(),
        ["projection-bus"] = () => new ProjectionScenario(ProjectionPath.Bus),
        ["projection-bus-durable"] = () => new ProjectionScenario(ProjectionPath.Bus, durableBundles: true),
        ["projection-grain"] = () => new ProjectionScenario(ProjectionPath.Grain),
        ["projection-hybrid"] = () => new ProjectionScenario(ProjectionPath.Hybrid),
        ["saga"] = () => new SagaScenario(),
    };

    public static async Task<int> RunAsync(string scenarioName)
    {
        if (!Scenarios.TryGetValue(scenarioName, out var create))
        {
            await Console.Error.WriteLineAsync($"Unknown scenario '{scenarioName}'. Known: {string.Join(", ", Scenarios.Keys)}.");
            return 2;
        }

        var scenario = create();
        var settings = PocHostSettings.FromEnvironment();
        using var host = await scenario.BuildAsync(settings);

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.StartAsync(startTimeout.Token);
        Reply("ready");

        while (await Console.In.ReadLineAsync() is { } line)
        {
            var command = line.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (command == "exit")
            {
                break;
            }

            try
            {
                Reply(await scenario.HandleAsync(host.Services, command));
            }
            catch (Exception ex)
            {
                Reply("error " + ex.GetType().Name + ": " + ex.Message.ReplaceLineEndings(" "));
            }
        }

        await host.StopAsync();
        return 0;
    }

    public static void Reply(string text) => Console.Out.WriteLine(ReplyPrefix + text);
}

/// <summary>What every scenario needs, read from the environment the test sets.</summary>
public sealed record PocHostSettings(
    string StoreConnectionString,
    string ReadStoreConnectionString,
    string OrleansConnectionString,
    string RedisConnectionString,
    string RabbitConnectionString,
    int SiloPort,
    int GatewayPort)
{
    public static PocHostSettings FromEnvironment() => new(
        Require("POC_STORE"),
        Environment.GetEnvironmentVariable("POC_READ") ?? Require("POC_STORE"),
        Require("POC_ORLEANS"),
        Require("POC_REDIS"),
        Environment.GetEnvironmentVariable("POC_RABBIT") ?? string.Empty,
        int.Parse(Require("POC_SILO_PORT")),
        int.Parse(Require("POC_GATEWAY_PORT")));

    public static Dictionary<string, string> ToEnvironment(
        string store, string orleans, string redis, string rabbit, int siloPort, int gatewayPort, string? read = null) => new()
    {
        ["POC_STORE"] = store,
        ["POC_READ"] = read ?? store,
        ["POC_ORLEANS"] = orleans,
        ["POC_REDIS"] = redis,
        ["POC_RABBIT"] = rabbit,
        ["POC_SILO_PORT"] = siloPort.ToString(),
        ["POC_GATEWAY_PORT"] = gatewayPort.ToString(),
    };

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable {name} is not set.");
}

/// <summary>A host shape the tests kill and restart, with the commands they steer it by.</summary>
public interface IPocScenario
{
    Task<IHost> BuildAsync(PocHostSettings settings);

    Task<string> HandleAsync(IServiceProvider services, string command);
}
