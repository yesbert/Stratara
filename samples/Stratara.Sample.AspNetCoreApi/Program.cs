using System.Globalization;
using OpenTelemetry.Trace;
using Stratara.Sample.AspNetCoreApi.Endpoints;
using Stratara.Sample.AspNetCoreApi.Infrastructure;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InMemoryAccountRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TracerProvider.Default.GetTracer("Stratara.Sample.AspNetCoreApi"));

builder.Services
    .AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

var app = builder.Build();
app.MapAccountEndpoints();
app.Run();
