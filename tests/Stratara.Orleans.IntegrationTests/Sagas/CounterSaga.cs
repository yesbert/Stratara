using System.Collections.Concurrent;
using JetBrains.Annotations;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>What the saga saw, in the order it saw it, with the session it saw it under.</summary>
public sealed class SagaLog
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<(string Step, Guid Tenant)>> _seen = new();

    public void Record(Guid streamId, string step, Guid tenant) =>
        _seen.GetOrAdd(streamId, _ => new ConcurrentQueue<(string, Guid)>()).Enqueue((step, tenant));

    public IReadOnlyList<(string Step, Guid Tenant)> Sequence(Guid streamId) =>
        _seen.TryGetValue(streamId, out var queue) ? [.. queue] : [];
}

/// <summary>
/// A saga written to the shipped contract and nothing else: the <c>ISaga</c> marker and
/// <c>HandleAsync(TEvent, CancellationToken)</c> methods the framework discovers. It runs in the saga
/// grain without a single change.
/// </summary>
public sealed class CounterSaga(SagaLog log, ISessionContextProvider sessionContextProvider) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        log.Record(@event.CounterId, "created", Tenant());
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(CounterIncremented @event, CancellationToken cancellationToken)
    {
        log.Record(@event.CounterId, $"incremented:{@event.By}", Tenant());
        return Task.CompletedTask;
    }

    private Guid Tenant() => sessionContextProvider.Current?.TenantId ?? Guid.Empty;
}
