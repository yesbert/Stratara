using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Sample.EventSourced.Commands;
using Stratara.Sample.EventSourced.Domain;
using Stratara.Sample.EventSourced.EventStore;
using Stratara.Sample.EventSourced.Projections;
using Stratara.Sample.EventSourced.Queries;
using Stratara.Abstractions.Mediator;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<AccountBalanceProjection>();
builder.Services.AddSingleton<IProjection>(sp => sp.GetRequiredService<AccountBalanceProjection>());
builder.Services.AddSingleton<InMemoryEventStore>();
builder.Services.AddSingleton<AggregationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.EventSourced"));

builder.Services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var store = scope.ServiceProvider.GetRequiredService<InMemoryEventStore>();

Console.WriteLine("=== Stratara Event-Sourced ===");
Console.WriteLine();

Console.WriteLine("--- Open account ---");
var accountId = await mediator.HandleAsync(new OpenAccountCommand("Alice", InitialBalance: 100m));
Console.WriteLine($"  Opened {accountId} with $100");
Console.WriteLine();

Console.WriteLine("--- Deposit $50 + Deposit $25 + Withdraw $40 ---");
await mediator.HandleAsync(new DepositCommand(accountId, 50m));
await mediator.HandleAsync(new DepositCommand(accountId, 25m));
await mediator.HandleAsync(new WithdrawCommand(accountId, 40m));
Console.WriteLine();

Console.WriteLine("--- Read-side via projection ---");
var view = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"  {view.OwnerName}'s balance: {view.Balance:C}");
Console.WriteLine();

Console.WriteLine("--- Withdraw $999 (should fail — write-side rebuilds aggregate from events to check invariant) ---");
try
{
    await mediator.HandleAsync(new WithdrawCommand(accountId, 999m));
}
catch (InsufficientBalanceException ex)
{
    Console.WriteLine($"  Rejected: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine("--- Underlying event stream (this is what's persisted) ---");
foreach (var @event in store.Read(accountId))
{
    Console.WriteLine($"  {@event}");
}
Console.WriteLine();

Console.WriteLine("Done.");
