using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Sample.MoneyTransferSaga.Commands;
using Stratara.Sample.MoneyTransferSaga.Infrastructure;
using Stratara.Sample.MoneyTransferSaga.Messaging;
using Stratara.Sample.MoneyTransferSaga.Outbox;
using Stratara.Sample.MoneyTransferSaga.Queries;
using Stratara.Sample.MoneyTransferSaga.Sagas;
using Stratara.Sample.MoneyTransferSaga.Workers;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<InMemoryAccountRepository>();
builder.Services.AddSingleton<InMemoryOutbox>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<CommandOutboxDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.MoneyTransferSaga"));

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

Console.WriteLine("=== Stratara Money-Transfer Saga ===");
Console.WriteLine();

var sourceAccountId = Guid.NewGuid();
var destinationAccountId = Guid.NewGuid();

Console.WriteLine("--- Open two accounts via outbox (Alice $200, Bob $50) ---");
dispatcher.Enqueue(new OpenAccountCommand(sourceAccountId, "Alice", InitialBalance: 200m));
dispatcher.Enqueue(new OpenAccountCommand(destinationAccountId, "Bob", InitialBalance: 50m));
await Task.Delay(TimeSpan.FromMilliseconds(300));
Console.WriteLine($"  Alice: {await mediator.HandleAsync(new GetBalanceQuery(sourceAccountId)):C}");
Console.WriteLine($"  Bob:   {await mediator.HandleAsync(new GetBalanceQuery(destinationAccountId)):C}");
Console.WriteLine();

Console.WriteLine("--- Transfer $75 from Alice to Bob (saga handler enqueues Withdraw + Deposit) ---");
dispatcher.Enqueue(new RequestMoneyTransferCommand(sourceAccountId, destinationAccountId, 75m));
await Task.Delay(TimeSpan.FromMilliseconds(400));
Console.WriteLine($"  Alice: {await mediator.HandleAsync(new GetBalanceQuery(sourceAccountId)):C}");
Console.WriteLine($"  Bob:   {await mediator.HandleAsync(new GetBalanceQuery(destinationAccountId)):C}");
Console.WriteLine();

Console.WriteLine("--- Transfer $999 from Alice to Bob (should fail — saga validates before enqueueing) ---");
try
{
    // The saga's pre-flight balance check runs synchronously in the handler, so we dispatch in-process here.
    await mediator.HandleAsync(new RequestMoneyTransferCommand(sourceAccountId, destinationAccountId, 999m));
}
catch (Stratara.Sample.MoneyTransferSaga.Domain.InsufficientBalanceException ex)
{
    Console.WriteLine($"  Rejected: {ex.Message}");
}
Console.WriteLine();

await host.StopAsync();
Console.WriteLine("Done.");
