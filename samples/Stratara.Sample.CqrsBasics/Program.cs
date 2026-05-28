using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Sample.CqrsBasics.Commands;
using Stratara.Sample.CqrsBasics.Domain;
using Stratara.Sample.CqrsBasics.Infrastructure;
using Stratara.Sample.CqrsBasics.Queries;
using Stratara.Abstractions.Mediator;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<InMemoryAccountRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.CqrsBasics"));

builder.Services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

Console.WriteLine("=== Stratara CQRS Basics ===");
Console.WriteLine();

Console.WriteLine("--- Open account ---");
var accountId = await mediator.HandleAsync(new OpenAccountCommand("Alice", InitialBalance: 100m));
Console.WriteLine($"  Opened {accountId} with $100");
Console.WriteLine();

Console.WriteLine("--- Deposit $50 ---");
await mediator.HandleAsync(new DepositCommand(accountId, 50m));
var balance = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"  Balance after deposit: {balance:C}");
Console.WriteLine();

Console.WriteLine("--- Withdraw $75 ---");
await mediator.HandleAsync(new WithdrawCommand(accountId, 75m));
balance = await mediator.HandleAsync(new GetBalanceQuery(accountId));
Console.WriteLine($"  Balance after withdraw: {balance:C}");
Console.WriteLine();

Console.WriteLine("--- Withdraw $999 (should fail) ---");
try
{
    await mediator.HandleAsync(new WithdrawCommand(accountId, 999m));
}
catch (InsufficientBalanceException ex)
{
    Console.WriteLine($"  Rejected: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine("Done.");
