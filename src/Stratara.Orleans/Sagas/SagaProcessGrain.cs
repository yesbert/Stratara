using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Timers;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Sagas;

/// <summary>One grain per process instance, keyed <c>sagaType|correlationId</c>.</summary>
internal interface ISagaProcessGrain : IGrainWithStringKey
{
    /// <summary>Advances the process with the fact at <paramref name="version"/> of <paramref name="streamId"/>, read back from the store.</summary>
    Task HandleAsync(Guid streamId, long version);

    /// <summary>A timeout the process scheduled is due.</summary>
    Task OnTimeoutAsync(string purpose);

    /// <summary>Whether the process exists and has not completed — the owner check for its timers.</summary>
    Task<bool> IsAliveAsync();
}

/// <summary>
/// Runs one process instance: folds its state from its own stream, hands the fact or the timeout
/// to the process, appends what it emitted, and registers or cancels its timers — all in the grain's
/// turn, so a fact and a timeout never race. The fact itself is re-read from the store by stream and
/// version rather than carried in the call: the store is the truth, the call is a hint.
/// </summary>
internal sealed class SagaProcessGrain(IServiceScopeFactory scopeFactory) : Grain, ISagaProcessGrain
{
    public Task HandleAsync(Guid streamId, long version) => RunAsync(async (process, state, context, services) =>
    {
        var entry = await ReadEntryAsync(services, streamId, version);
        services.GetRequiredService<ISessionContextProvider>().Set(RecordedSession.Of(entry));
        var events = await services.GetRequiredService<IEventMapperFactory>().MapToEventsAsync([entry]);
        foreach (var @event in events.Where(process.Handles))
        {
            await process.HandleAsync(state, @event, context, CancellationToken.None);
        }
    });

    /// <summary>
    /// A timeout has no fact to take a session from; it runs under the session the process's own
    /// stream was created with, so what it emits is owned by the same tenant.
    /// </summary>
    public Task OnTimeoutAsync(string purpose) => RunAsync(async (process, state, context, services) =>
    {
        var (_, stateStream) = ResolveProcess(services);
        var unitOfWork = services.GetRequiredService<IWriteUnitOfWork>();
        await using (var transaction = await unitOfWork.StartAsync())
        {
            var first = await unitOfWork.CreateEventStreamRepository(transaction).GetFirstOrDefaultAsync(stateStream)
                        ?? throw new InvalidOperationException($"Process {this.GetPrimaryKeyString()} has no state stream to time out.");
            services.GetRequiredService<ISessionContextProvider>().Set(RecordedSession.Of(first));
        }

        await process.OnTimeoutAsync(state, purpose, context, CancellationToken.None);
    });

    public async Task<bool> IsAliveAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var (process, stateStream) = ResolveProcess(scope.ServiceProvider);
        var state = await StateLoaders.For(process.StateType).LoadAsync(scope.ServiceProvider, stateStream);
        return state is { Completed: false } && await scope.ServiceProvider.GetRequiredService<IEventSource>().ExistsAsync(stateStream);
    }

    private async Task RunAsync(Func<ISagaProcess, object, SagaProcessContext, IServiceProvider, Task> step)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var (process, stateStream) = ResolveProcess(services);
        var loader = StateLoaders.For(process.StateType);

        var exists = await services.GetRequiredService<IEventSource>().ExistsAsync(stateStream);
        var state = (exists ? await loader.LoadAsync(services, stateStream) : null) ?? loader.Fresh();
        var context = new SagaProcessContext();

        await step(process, state, context, services);

        if (context.Emitted.Count > 0)
        {
            var events = services.GetRequiredService<IEventSource>();
            if (exists)
            {
                await loader.AppendAsync(events, stateStream, context.Emitted);
            }
            else
            {
                await loader.CreateAsync(events, stateStream, context.Emitted);
            }

            await events.SaveChangesAsync();
            state = await loader.LoadAsync(services, stateStream) ?? state;
        }

        var timers = services.GetRequiredService<IDurableTimers>();
        var owner = SagaProcessTimerHost.OwnerOf(this.GetPrimaryKeyString());
        if (state.Completed)
        {
            await timers.CancelAllAsync(owner);
            return;
        }

        foreach (var purpose in context.Cancelled)
        {
            await timers.CancelAsync(owner, purpose);
        }

        foreach (var (purpose, dueAt) in context.Scheduled)
        {
            await timers.RegisterAsync(new TimerRegistration(owner, purpose, dueAt));
        }
    }

    private (ISagaProcess Process, Guid StateStream) ResolveProcess(IServiceProvider services)
    {
        var (sagaType, correlationId) = SagaProcessKey.Parse(this.GetPrimaryKeyString());
        var process = services.GetServices<ISaga>().OfType<ISagaProcess>().FirstOrDefault(p => p.GetType().Name == sagaType)
                      ?? throw new InvalidOperationException($"No saga process named '{sagaType}' is registered on this silo.");
        return (process, SagaProcessKey.StateStreamOf(sagaType, correlationId));
    }

    private static async Task<EventStreamEntry> ReadEntryAsync(IServiceProvider services, Guid streamId, long version)
    {
        var unitOfWork = services.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        var entries = await unitOfWork.CreateEventStreamRepository(transaction).GetManyAsync(streamId, version, version);
        return entries.SingleOrDefault()
               ?? throw new InvalidOperationException($"Stream {streamId} has no entry at version {version}.");
    }

    private sealed class SagaProcessContext : ISagaProcessContext
    {
        public List<object> Emitted { get; } = [];

        public Dictionary<string, DateTimeOffset> Scheduled { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Cancelled { get; } = new(StringComparer.Ordinal);

        public void Emit(object stateEvent) => Emitted.Add(stateEvent);

        public void Schedule(string purpose, DateTimeOffset dueAt)
        {
            Cancelled.Remove(purpose);
            Scheduled[purpose] = dueAt;
        }

        public void Cancel(string purpose)
        {
            Scheduled.Remove(purpose);
            Cancelled.Add(purpose);
        }
    }
}

/// <summary>
/// The grain key of a process instance, <c>sagaType|correlationId</c>, and the id of the stream its
/// state lives in — derived from both, so that a process keyed by an aggregate's id does not write
/// into that aggregate's stream. The state is an aggregate of its own, in a stream of its own.
/// </summary>
internal static class SagaProcessKey
{
    public static string Of(string sagaType, Guid correlationId) => $"{sagaType}|{correlationId:N}";

    public static (string SagaType, Guid CorrelationId) Parse(string key)
    {
        var separator = key.IndexOf('|');
        return (key[..separator], Guid.ParseExact(key[(separator + 1)..], "N"));
    }

    public static Guid StateStreamOf(string sagaType, Guid correlationId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Of(sagaType, correlationId)));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Folds, creates and appends a process's state stream for a state type known only at runtime.</summary>
internal interface IStateLoader
{
    ISagaProcessState Fresh();

    Task<ISagaProcessState?> LoadAsync(IServiceProvider services, Guid correlationId);

    Task CreateAsync(IEventSource events, Guid correlationId, IReadOnlyList<object> emitted);

    Task AppendAsync(IEventSource events, Guid correlationId, IReadOnlyList<object> emitted);
}

internal static class StateLoaders
{
    private static readonly ConcurrentDictionary<Type, IStateLoader> Loaders = new();

    public static IStateLoader For(Type stateType) => Loaders.GetOrAdd(stateType, static type =>
        (IStateLoader)(Activator.CreateInstance(typeof(StateLoader<>).MakeGenericType(type))
                       ?? throw new InvalidOperationException($"Cannot build a state loader for {type.FullName}.")));

    private sealed class StateLoader<TState> : IStateLoader where TState : class, ISagaProcessState, new()
    {
        public ISagaProcessState Fresh() => new TState();

        public async Task<ISagaProcessState?> LoadAsync(IServiceProvider services, Guid correlationId) =>
            await services.GetRequiredService<IAggregationService>().AggregateAsync<TState>(correlationId);

        public Task CreateAsync(IEventSource events, Guid correlationId, IReadOnlyList<object> emitted) =>
            events.CreateRangeAsync<TState>(correlationId, emitted);

        public Task AppendAsync(IEventSource events, Guid correlationId, IReadOnlyList<object> emitted) =>
            events.AppendRangeAsync<TState>(correlationId, emitted);
    }
}

/// <summary>
/// The timer side of processes: owners are process grains, a due timeout is handed to its grain.
/// Owners that are not processes go to the host's own owner check and handler, when it has them.
/// </summary>
internal sealed class SagaProcessTimerHost(IGrainFactory grainFactory, HostTimerServices? host = null) : ITimerOwners, ITimerHandler
{
    private const string Prefix = "saga:";

    public static string OwnerOf(string grainKey) => Prefix + grainKey;

    public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken)
    {
        if (ownerId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return grainFactory.GetGrain<ISagaProcessGrain>(ownerId[Prefix.Length..]).IsAliveAsync();
        }

        return host?.Owners.ExistsAsync(ownerId, cancellationToken) ?? Task.FromResult(false);
    }

    public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
    {
        if (due.OwnerId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return grainFactory.GetGrain<ISagaProcessGrain>(due.OwnerId[Prefix.Length..]).OnTimeoutAsync(due.Purpose);
        }

        return host?.Handler.OnDueAsync(due, cancellationToken) ?? Task.CompletedTask;
    }
}

/// <summary>The host's own timer owner check and handler, kept behind the process one.</summary>
/// <param name="Owners">The host's owner check.</param>
/// <param name="Handler">The host's handler.</param>
internal sealed record HostTimerServices(ITimerOwners Owners, ITimerHandler Handler);
