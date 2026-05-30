using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Validation;
using Stratara.Sample.Validation;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.Validation"));

builder.Services
    .AddMediator()
    .AddStrataraValidation()
    .AddValidatorsFromAssemblyContaining<Program>()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

Console.WriteLine("=== Stratara Validation ===");
Console.WriteLine();

Console.WriteLine("--- Valid command (alice@example.com, age 30) ---");
var userId = await mediator.HandleAsync(new RegisterUserCommand("alice@example.com", Age: 30));
Console.WriteLine($"  Accepted: {userId}");
Console.WriteLine();

Console.WriteLine("--- Warning only (bob@example.com, age 150) — passes through, handler still runs ---");
await mediator.HandleAsync(new RegisterUserCommand("bob@example.com", Age: 150));
Console.WriteLine("  Accepted despite the age warning (Warning/Info never block).");
Console.WriteLine();

Console.WriteLine("--- Invalid command (not-an-email, age 16) — blocked before the handler ---");
try
{
    await mediator.HandleAsync(new RegisterUserCommand("not-an-email", Age: 16));
}
catch (StrataraValidationException ex)
{
    Console.WriteLine($"  Rejected with {ex.Failures.Count} failure(s):");
    foreach (var failure in ex.Failures)
    {
        Console.WriteLine($"    [{failure.ErrorCode}] {failure.PropertyName}: {failure.ErrorMessage}");
    }
}
Console.WriteLine();

Console.WriteLine("Done.");
