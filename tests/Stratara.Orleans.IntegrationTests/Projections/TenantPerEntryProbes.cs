using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>A scoped service that takes the ambient tenant once, when it is constructed — what a tenant-routed connection does.</summary>
public sealed class TenantAtConstruction(ISessionContextProvider sessions)
{
    public Guid Tenant { get; } = sessions.Current?.TenantId ?? Guid.Empty;
}

/// <summary>Which tenant each applied fact's constructed dependency held, by stream and step.</summary>
public sealed class TenantCaptures
{
    private readonly ConcurrentQueue<(Guid StreamId, string Step, Guid Tenant)> _captured = new();

    public void Record(Guid streamId, string step, Guid tenant) => _captured.Enqueue((streamId, step, tenant));

    public IReadOnlyList<(Guid StreamId, string Step, Guid Tenant)> All => [.. _captured];
}

/// <summary>A projection whose dependency takes its tenant at construction.</summary>
public sealed class TenantCaptureProjection(TenantAtConstruction tenant, TenantCaptures captures) : IProjection
{
    [UsedImplicitly]
    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
    {
        captures.Record(@event.StreamId, "created", tenant.Tenant);
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken)
    {
        captures.Record(@event.StreamId, "incremented", tenant.Tenant);
        return Task.CompletedTask;
    }
}

/// <summary>A stateless saga whose dependency takes its tenant at construction.</summary>
public sealed class TenantCaptureSaga(TenantAtConstruction tenant, TenantCaptures captures) : ISaga
{
    [UsedImplicitly]
    public Task HandleAsync(CounterCreated @event, CancellationToken cancellationToken)
    {
        captures.Record(@event.CounterId, "created", tenant.Tenant);
        return Task.CompletedTask;
    }

    [UsedImplicitly]
    public Task HandleAsync(CounterIncremented @event, CancellationToken cancellationToken)
    {
        captures.Record(@event.CounterId, "incremented", tenant.Tenant);
        return Task.CompletedTask;
    }
}

public sealed record TenantStepRecorded(Guid CounterId, int Step);

/// <summary>The state of <see cref="TenantProcess"/>: how many facts of its counter it recorded.</summary>
public sealed class TenantProcessState : ISagaProcessState
{
    public Guid Id { get; set; }

    public int Steps { get; set; }

    public bool Completed => false;

    [UsedImplicitly]
    public void Apply(TenantStepRecorded @event)
    {
        Id = @event.CounterId;
        Steps = @event.Step;
    }
}

/// <summary>A process that records a step for every fact of its counter, correlated by the counter.</summary>
public sealed class TenantProcess : SagaProcess<TenantProcessState>
{
    public override bool Handles(IEvent @event) => @event.Data is CounterCreated or CounterIncremented;

    public override Guid CorrelationOf(IEvent @event) => @event.Data switch
    {
        CounterCreated created => created.CounterId,
        CounterIncremented incremented => incremented.CounterId,
        _ => throw new InvalidOperationException("The process handles counters only."),
    };

    public override Task HandleAsync(TenantProcessState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        context.Emit(new TenantStepRecorded(CorrelationOf(@event), state.Steps + 1));
        return Task.CompletedTask;
    }

    public override Task OnTimeoutAsync(TenantProcessState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// Stands for a state store reached through a connection routed per tenant: the aggregation service records the tenant
/// it was constructed under for every process state it reads.
/// </summary>
public sealed class TenantCapturingAggregation(IAggregationService inner, TenantAtConstruction tenant, TenantCaptures captures) : IAggregationService
{
    public Task<TAggregate?> AggregateAsync<TAggregate>(Guid streamId, long? fromVersion = null, long? toVersion = null, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new()
    {
        if (typeof(TAggregate) == typeof(TenantProcessState))
        {
            captures.Record(streamId, "state-read", tenant.Tenant);
        }

        return inner.AggregateAsync<TAggregate>(streamId, fromVersion, toVersion, cancellationToken);
    }

    public Task<object?> AggregateAsync(Type aggregateType, Guid streamId, long? fromVersion = null, long? toVersion = null, CancellationToken cancellationToken = default) =>
        inner.AggregateAsync(aggregateType, streamId, fromVersion, toVersion, cancellationToken);

    /// <summary>Wraps the aggregation service registered so far.</summary>
    public static void Decorate(IServiceCollection services)
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IAggregationService));
        services.Remove(original);
        services.AddScoped<IAggregationService>(sp => new TenantCapturingAggregation(
            (IAggregationService)ActivatorUtilities.CreateInstance(sp, original.ImplementationType ?? throw new InvalidOperationException("The aggregation service is not registered by type.")),
            sp.GetRequiredService<TenantAtConstruction>(),
            sp.GetRequiredService<TenantCaptures>()));
    }
}
