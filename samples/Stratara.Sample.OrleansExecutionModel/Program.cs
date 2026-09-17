using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;
using Stratara.Sample.OrleansExecutionModel;
using Stratara.Testing.Orleans;

// The Orleans execution model in one console run: one silo in this process, with in-memory reminders and grain
// directory and an in-memory SQLite store. The registrations below are the ones a production silo makes.
Console.WriteLine("=== Stratara Orleans execution model ===");

var log = new SampleLog();
await using var host = await ExecutionModelTestHost.CreateAsync(services => services
    .AddSingleton(log)
    .AddAggregatesFromAssemblyContaining<Account>()
    .AddTrustedType<OpenAccount>()
    .AddTrustedType<WelcomeState>()
    .AddTrustedType<WelcomeScheduled>()
    .AddScoped<ICommandHandler<OpenAccount>, OpenAccountHandler>()
    .AddScoped<IProjection, BalanceProjection>()
    .AddScoped<ISaga, WelcomeProcess>()
    .AddStrataraAggregateGrains()
    .AddStrataraProjectionGrains()
    .AddStrataraSagaGrains());

// 1. The command runs in the account's activation — one writer per aggregate across the cluster.
await host.DispatchAsync(new OpenAccount(Guid.NewGuid(), "Ada", 100m));
Console.WriteLine($"Command handled in the aggregate's activation ({log.CommandScheduler}).");

// 2. The projection reads the store from its checkpoint; the wait returns once every reader is at the head.
await host.WaitForReadersAsync();
Console.WriteLine($"Projection applied: Ada's balance is {log.Balances["Ada"].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.");

// 3. The process scheduled a durable timeout a second out; it reaches the process with its recorded state.
var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
while (log.Welcomed.IsEmpty && DateTimeOffset.UtcNow < deadline)
{
    await Task.Delay(50);
}

Console.WriteLine(log.Welcomed.TryPeek(out var welcomed)
    ? $"Process timeout fired: welcome sent to {welcomed}."
    : "Process timeout did not fire.");

Console.WriteLine("Done.");
