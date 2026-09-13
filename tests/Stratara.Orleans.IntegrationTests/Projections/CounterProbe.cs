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
        if (await context.CounterViews.AnyAsync(v => v.StreamId == @event.StreamId, cancellationToken))
        {
            return;
        }

        context.CounterViews.Add(new CounterView
        {
            StreamId = @event.StreamId,
            Value = 0,
            LastVersion = @event.Version,
            AppliedByTenant = Tenant(),
            Applications = 1,
            AppliedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleAsync(IEvent<CounterIncremented> @event, CancellationToken cancellationToken)
    {
        Guard(@event.StreamId, @event.EventTypeName);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var view = await context.CounterViews.SingleOrDefaultAsync(v => v.StreamId == @event.StreamId, cancellationToken)
                   ?? throw new PrecedingFactMissingException(@event.StreamId, @event.EventTypeName);
        if (view.LastVersion >= @event.Version)
        {
            return;
        }

        view.Value += @event.Data.By;
        view.LastVersion = @event.Version;
        view.AppliedByTenant = Tenant();
        view.Applications++;
        view.AppliedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
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
