using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A silo with the durable-intent dispatcher, the drain and the authorizing mediator, whose provider answers either from
/// the session context or from the current web request. Commands: <c>record probeId userId</c> records a role-guarded
/// command as a host that died before the hand-over leaves it and answers its intent id; <c>ran probeId</c>;
/// <c>intent intentId</c> answers <c>attempts=N kept=bool failure=…</c>; <c>attempt-logs intentId</c> counts the
/// attempt-failed events for it.
/// </summary>
/// <remarks>
/// The guarded command type is emitted when the scenario builds. A type carrying <c>[RequireRole]</c> in a loaded assembly
/// fails the start of every host whose mediator does not authorize — the mediator's start-up check scans every loaded
/// assembly — so it exists only in the process that runs this scenario.
/// </remarks>
public sealed class IntentAuthorizationScenario(bool sessionDriven) : IPocScenario
{
    public const string Role = "administrator";
    public static readonly Guid Administrator = Guid.Parse("0f6c1a9e-6a7d-4b8e-9d1c-3a2b4c5d6e7f");

    private readonly GuardedRuns _runs = new();
    private readonly EventCounter _events = new();
    private Type _command = null!;
    private string _store = string.Empty;

    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);
        _store = settings.StoreConnectionString;
        _command = EmitGuardedCommand();

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });
        builder.Logging.AddProvider(_events);
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.AddBackendServices();
        TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(builder.Services).Register(_command);
        builder.Services
            .AddSingleton(_runs)
            .AddScoped(typeof(ICommandHandler<>).MakeGenericType(_command), typeof(GuardedProbeHandler<>).MakeGenericType(_command))
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName, options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromSeconds(1));
        if (sessionDriven)
        {
            builder.Services.AddAuthorizingMediator<SessionRoleProvider>();
        }
        else
        {
            builder.Services.AddAuthorizingMediator<RequestRoleProvider>();
        }

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "record":
            {
                var probe = (ICommand)Activator.CreateInstance(_command)!;
                _command.GetProperty("ProbeId")!.SetValue(probe, Guid.Parse(parts[1]));
                var session = PocSessions.ForUser(Guid.NewGuid(), Guid.Parse(parts[2]));
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(session);
                var intentId = Guid.CreateVersion7();
                var record = typeof(IntentRecorder).GetMethod(nameof(IntentRecorder.RecordAsync))!.MakeGenericMethod(_command);
                await (Task)record.Invoke(scope.ServiceProvider.GetRequiredService<IntentRecorder>(), [intentId, probe, session, null, false, CancellationToken.None])!;
                return intentId.ToString();
            }
            case "ran":
                return _runs.Ran.ContainsKey(Guid.Parse(parts[1])) ? "true" : "false";
            case "intent":
            {
                await using var connection = new NpgsqlConnection(_store);
                await connection.OpenAsync();
                await using var query = new NpgsqlCommand("SELECT attempt_count, kept_at IS NOT NULL, coalesce(last_failure, '') FROM outbox_entry WHERE id = @id", connection);
                query.Parameters.AddWithValue("id", Guid.Parse(parts[1]));
                await using var reader = await query.ExecuteReaderAsync();
                return await reader.ReadAsync()
                    ? $"attempts={reader.GetInt32(0)} kept={(reader.GetBoolean(1) ? "true" : "false")} failure={reader.GetString(2).ReplaceLineEndings(" ")}"
                    : "none";
            }
            case "attempt-logs":
                return _events.Count(LogEvents.Orleans.IntentAttemptFailed, parts[1]).ToString(CultureInfo.InvariantCulture);
            default:
                return "error unknown command " + parts[0];
        }
    }

    /// <summary><c>[RequireRole("administrator")] public sealed class GuardedProbe : ICommand { public Guid ProbeId { get; set; } }</c></summary>
    private static Type EmitGuardedCommand()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Stratara.Orleans.Scenarios.GuardedCommands"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("GuardedCommands");
        var type = module.DefineType("Stratara.Orleans.Scenarios.GuardedCommands.GuardedProbe", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        type.AddInterfaceImplementation(typeof(ICommand));
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(RequireRoleAttribute).GetConstructor([typeof(string)])!, [Role]));
        type.DefineDefaultConstructor(MethodAttributes.Public);

        var field = type.DefineField("_probeId", typeof(Guid), FieldAttributes.Private);
        var property = type.DefineProperty("ProbeId", PropertyAttributes.None, typeof(Guid), null);
        const MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = type.DefineMethod("get_ProbeId", accessor, typeof(Guid), Type.EmptyTypes);
        var get = getter.GetILGenerator();
        get.Emit(OpCodes.Ldarg_0);
        get.Emit(OpCodes.Ldfld, field);
        get.Emit(OpCodes.Ret);
        var setter = type.DefineMethod("set_ProbeId", accessor, null, [typeof(Guid)]);
        var set = setter.GetILGenerator();
        set.Emit(OpCodes.Ldarg_0);
        set.Emit(OpCodes.Ldarg_1);
        set.Emit(OpCodes.Stfld, field);
        set.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        return type.CreateType();
    }

    /// <summary>Answers from the session context — the shape the intent path needs.</summary>
    public sealed class SessionRoleProvider(ISessionContextProvider sessions) : IAuthorizationProvider
    {
        public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult(role == Role && sessions.Current?.ActorUserId == Administrator);
    }

    /// <summary>Answers from the current web request, which a silo resuming a command does not have.</summary>
    public sealed class RequestRoleProvider : IAuthorizationProvider
    {
        public static readonly AsyncLocal<string[]?> CurrentRequestRoles = new();

        public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentRequestRoles.Value?.Contains(role) ?? false);
    }

    public sealed class GuardedRuns
    {
        public ConcurrentDictionary<Guid, bool> Ran { get; } = new();
    }

    public sealed class GuardedProbeHandler<TCommand>(GuardedRuns runs) : ICommandHandler<TCommand>
        where TCommand : class, ICommand
    {
        public Task HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            runs.Ran[(Guid)typeof(TCommand).GetProperty("ProbeId")!.GetValue(command)!] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class EventCounter : ILoggerProvider
    {
        private readonly ConcurrentQueue<(int EventId, string Message)> _entries = new();

        public int Count(int eventId, string contains) => _entries.Count(entry => entry.EventId == eventId && entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));

        public ILogger CreateLogger(string categoryName) => new Capture(_entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 0)
                {
                    entries.Enqueue((eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
