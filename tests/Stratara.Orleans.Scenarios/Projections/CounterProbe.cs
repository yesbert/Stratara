using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

public sealed class Counter
{
    public Guid Id { get; set; }

    public int Value { get; set; }
}

public sealed record CounterCreated(Guid CounterId);

public sealed record CounterIncremented(Guid CounterId, int By);

/// <summary>
/// Switches the test flips to make the projection misbehave on purpose: a poisoned stream makes it
/// throw as a genuine failure would, and a stream awaiting a prerequisite makes it report the
/// missing fact until the test supplies it.
/// </summary>
public sealed class ProjectionProbeControl
{
    public ConcurrentDictionary<Guid, byte> Poisoned { get; } = new();

    public ConcurrentDictionary<Guid, byte> AwaitingPrerequisite { get; } = new();

    public ConcurrentDictionary<Guid, byte> Prerequisites { get; } = new();

    public ConcurrentDictionary<Guid, int> PrerequisiteReports { get; } = new();
}

/// <summary>
/// The probe projection: discovered by assembly, applies idempotently by version, records the
/// session it applied under, and misbehaves only where <see cref="ProjectionProbeControl"/> says so.
/// </summary>
public sealed class CounterViewProjection(
    IDbContextFactory<PocReadDbContext> contextFactory,
    ISessionContextProvider sessionContextProvider,
    ProjectionProbeControl control) : IRebuildableProjection
{
    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.CounterViews.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
    {
        Guard(@event.StreamId, @event.EventTypeName);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO poc_counter_view (stream_id, value, last_version, applied_by_tenant, applications, applied_at)
             VALUES ({@event.StreamId}, 0, {@event.Version}, {Tenant()}, 1, {DateTimeOffset.UtcNow})
             ON CONFLICT (stream_id) DO NOTHING
             """,
            cancellationToken);
    }

    public async Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken)
    {
        Guard(@event.StreamId, @event.EventTypeName);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.CounterViews.AsNoTracking().AnyAsync(v => v.StreamId == @event.StreamId, cancellationToken))
        {
            throw new PrecedingFactMissingException(@event.StreamId, @event.EventTypeName);
        }

        await context.Database.ExecuteSqlAsync(
            $"""
             UPDATE poc_counter_view
             SET value = value + {@event.Data.By}, last_version = {@event.Version}, applied_by_tenant = {Tenant()},
                 applications = applications + 1, applied_at = {DateTimeOffset.UtcNow}
             WHERE stream_id = {@event.StreamId} AND last_version < {@event.Version}
             """,
            cancellationToken);
    }

    private void Guard(Guid streamId, string eventTypeName)
    {
        if (control.Poisoned.ContainsKey(streamId))
        {
            throw new InvalidOperationException($"Stream {streamId} is poisoned: this is the genuine failure the checkpoint must not pass.");
        }

        if (control.AwaitingPrerequisite.ContainsKey(streamId) && !control.Prerequisites.ContainsKey(streamId))
        {
            control.PrerequisiteReports.AddOrUpdate(streamId, 1, (_, n) => n + 1);
            throw new PrecedingFactMissingException(streamId, eventTypeName);
        }
    }

    private Guid Tenant() => sessionContextProvider.Current?.TenantId ?? Guid.Empty;
}
