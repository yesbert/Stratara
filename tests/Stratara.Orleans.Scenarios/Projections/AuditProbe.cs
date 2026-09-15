using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>One row per fact the projection saw, with when it saw it — the trace B4 reads to tell whether a projection kept applying while another was rebuilt.</summary>
public sealed class CounterAuditProjection(IDbContextFactory<PocReadDbContext> contextFactory) : IRebuildableProjection
{
    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.CounterAudits.ExecuteDeleteAsync(cancellationToken);
    }

    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken) => RecordAsync(@event, cancellationToken);

    public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken) => RecordAsync(@event, cancellationToken);

    private async Task RecordAsync(IEvent @event, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO poc_counter_audit (stream_id, version, applied_at) VALUES ({@event.StreamId}, {@event.Version}, {DateTimeOffset.UtcNow})
             ON CONFLICT (stream_id, version) DO NOTHING
             """,
            cancellationToken);
    }
}

/// <summary>A single running total, updated in place — the kind of view a replay rebuilds from zero.</summary>
public sealed class CounterTotalsProjection(IDbContextFactory<PocReadDbContext> contextFactory) : IRebuildableProjection
{
    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.CounterTotals.ExecuteDeleteAsync(cancellationToken);
    }

    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken) => AddAsync(created: 1, incremented: 0, cancellationToken);

    public Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken) => AddAsync(created: 0, incremented: 1, cancellationToken);

    private async Task AddAsync(int created, int incremented, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO poc_counter_totals (id, created, incremented, applied_at) VALUES (1, {created}, {incremented}, now())
             ON CONFLICT (id) DO UPDATE SET created = poc_counter_totals.created + {created}, incremented = poc_counter_totals.incremented + {incremented}, applied_at = now()
             """,
            cancellationToken);
    }
}
