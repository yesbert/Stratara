using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Sample.OutboxWorker.Commands;
using Stratara.Sample.OutboxWorker.Infrastructure;
using Stratara.Sample.OutboxWorker.Messaging;
using Stratara.Sample.OutboxWorker.Outbox;
using Stratara.Sample.OutboxWorker.Queries;
using Stratara.Sample.OutboxWorker.Workers;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<InMemoryAccountRepository>();
builder.Services.AddSingleton<InMemoryOutbox>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<CommandOutboxDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.OutboxWorker"));

builder.Services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

builder.Services.AddHostedService<OutboxDrainWorker>();
builder.Services.AddHostedService<MediatorCommandWorker>();

using var host = builder.Build();
await host.StartAsync();

var dispatcher = host.Services.GetRequiredService<CommandOutboxDispatcher>();
using var scope = host.Services.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

Console.WriteLine("=== Stratara Outbox + Worker ===");
Console.WriteLine();

var accountId = Guid.NewGuid();

Console.WriteLine("--- Publisher enqueues 3 commands (returns immediately, doesn't wait for handlers) ---");
dispatcher.Enqueue(new OpenAccountCommand(accountId, "Alice", InitialBalance: 100m));
dispatcher.Enqueue(new DepositCommand(accountId, 50m));
dispatcher.Enqueue(new DepositCommand(accountId, 25m));
Console.WriteLine($"  Enqueued — outbox has {host.Services.GetRequiredService<InMemoryOutbox>().PendingCount} pending");
Console.WriteLine();

Console.WriteLine("--- Wait for outbox-drain + command-worker to catch up ---");
await Task.Delay(TimeSpan.FromMilliseconds(500));
Console.WriteLine($"  Outbox now has {host.Services.GetRequiredService<InMemoryOutbox>().PendingCount} pending");
Console.WriteLine();

Console.WriteLine("--- Read-side: query the repository synchronously ---");
var balance = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"  Balance: {balance:C}");
Console.WriteLine();

Console.WriteLine("--- Enqueue WithdrawCommand $40, wait, query again ---");
dispatcher.Enqueue(new WithdrawCommand(accountId, 40m));
await Task.Delay(TimeSpan.FromMilliseconds(200));
balance = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"  Balance: {balance:C}");
Console.WriteLine();

await host.StopAsync();
Console.WriteLine("Done.");
