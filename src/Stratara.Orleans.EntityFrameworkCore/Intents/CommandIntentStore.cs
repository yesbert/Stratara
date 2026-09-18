using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.EntityFrameworkCore.Intents;

/// <summary>
/// The command-intent bookkeeping in the write store's outbox table: a recorded command is an outbox
/// entry of the execution model's own record type, and its resume bookkeeping is the entry's attempt and conflict counts,
/// hand-over time, last failure and kept state. Every operation is one statement on a context of its own.
/// </summary>
/// <typeparam name="TContext">A write context derived from the framework's write context.</typeparam>
internal sealed class CommandIntentStore<TContext>(IDbContextFactory<TContext> contextFactory, TimeProvider timeProvider) : ICommandIntentStore
    where TContext : DbContext, IWriteDbContext
{
    private const int FailureLength = 2048;
    private static string CommandTypeName => CommandIntentRecord.CommandTypeName;

    public Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken) =>
        RecordAsync(intentId, envelope, aggregateId, heavy, timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>One insert; the record's time is the dispatch's, taken before the envelope was built.</summary>
    public async Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Set<OutboxEntry>().Add(new OutboxEntry
        {
            Id = intentId,
            BucketId = BucketCalculator.GetBucketId(intentId),
            DataJson = JsonSerializer.Serialize(envelope),
            DataTypeName = CommandTypeName,
            Timestamp = recordedAt,
            AggregateId = aggregateId,
            Heavy = heavy,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await context.Set<OutboxEntry>().AsNoTracking()
            .Where(e => (e.DataTypeName == CommandTypeName || e.DataTypeName == CommandIntentRecord.PreviousCommandTypeName)
                        && e.KeptAt == null
                        && e.Timestamp <= handedOverBefore
                        && (e.LastHandedOverAt == null || e.LastHandedOverAt <= handedOverBefore))
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return
        [
            .. entries.Select(e => new RecordedIntent(
                e.Id,
                JsonSerializer.Deserialize<CommandEnvelope>(e.DataJson) ?? throw new JsonException($"Recorded command {e.Id} carries no envelope."),
                e.AggregateId,
                e.Heavy,
                e.AttemptCount,
                e.LastHandedOverAt,
                e.LastFailure,
                e.DataTypeName == CommandTypeName,
                e.ConflictCount))
        ];
    }

    public async Task<bool> TryClaimAsync(Guid intentId, DateTimeOffset? expectedLastHandedOverAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidate = context.Set<OutboxEntry>().Where(e => e.Id == intentId && e.KeptAt == null);
        candidate = expectedLastHandedOverAt is { } expected
            ? candidate.Where(e => e.LastHandedOverAt == expected)
            : candidate.Where(e => e.LastHandedOverAt == null);

        var claimed = await candidate.ExecuteUpdateAsync(
            set => set
                .SetProperty(e => e.LastHandedOverAt, (DateTimeOffset?)now)
                .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1),
            cancellationToken);
        return claimed == 1;
    }

    /// <summary>
    /// Two statements whatever the batch: one guarded update over the due ids that stamps the hand-over and counts the
    /// attempt where the row is not kept and its last hand-over is still no later than the latest one read — a claimer
    /// that stamped a row since stamped a later time, so the row drops out — and one read of the rows that now carry
    /// this call's stamp.
    /// </summary>
    /// <remarks>
    /// The stamp is truncated to the millisecond, because that is what every provider stores and the read-back
    /// compares it. Two claimers that stamp in the same millisecond — two drains during a failover, or the drain of a
    /// rolling adoption beside the bus outbox worker — therefore read each other's rows back as their own and both hand
    /// those commands over, with the same stamp. The receiver's first renewal, <see cref="TryRenewFromAsync"/>, moves
    /// the stamp only where it is still the claim's, so the second hand-over finds it moved and is dropped.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> ClaimAsync(IReadOnlyList<RecordedIntent> due, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(due);
        if (due.Count == 0)
        {
            return [];
        }

        var stamp = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var ids = due.Select(intent => intent.Id).ToList();
        var latest = due.Max(intent => intent.LastHandedOverAt);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = context.Set<OutboxEntry>().Where(e => ids.Contains(e.Id) && e.KeptAt == null);
        candidates = latest is { } bound
            ? candidates.Where(e => e.LastHandedOverAt == null || e.LastHandedOverAt <= bound)
            : candidates.Where(e => e.LastHandedOverAt == null);
        await candidates.ExecuteUpdateAsync(
            set => set
                .SetProperty(e => e.LastHandedOverAt, (DateTimeOffset?)stamp)
                .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1),
            cancellationToken);

        return await context.Set<OutboxEntry>().AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.LastHandedOverAt == stamp)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.KeptAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LastHandedOverAt, (DateTimeOffset?)now), cancellationToken);
    }

    /// <summary>
    /// One guarded update: it touches the row only where the last hand-over is still the claim's, so of two hand-overs
    /// of one claim exactly one moves it, and a row that is gone touches nothing.
    /// </summary>
    public async Task<bool> TryRenewFromAsync(Guid intentId, DateTimeOffset claimedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var moved = claimedAt.AddMilliseconds(1);
        var renewal = now > moved ? now : moved;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var renewed = await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.KeptAt == null && e.LastHandedOverAt == claimedAt)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LastHandedOverAt, (DateTimeOffset?)renewal), cancellationToken);
        return renewed == 1;
    }

    public async Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken)
    {
        var recorded = Truncate(failure);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LastFailure, recorded), cancellationToken);
    }

    public async Task RecordConflictAsync(Guid intentId, string failure, CancellationToken cancellationToken)
    {
        var recorded = Truncate(failure);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.LastFailure, recorded)
                    .SetProperty(e => e.ConflictCount, e => e.ConflictCount + 1)
                    .SetProperty(e => e.AttemptCount, e => e.AttemptCount > 0 ? e.AttemptCount - 1 : 0),
                cancellationToken);
    }

    public async Task ReturnAttemptAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.AttemptCount > 0)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.AttemptCount, e => e.AttemptCount - 1), cancellationToken);
    }

    private static string Truncate(string failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Length > FailureLength ? failure[..FailureLength] : failure;
    }

    public async Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.KeptAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.KeptAt, (DateTimeOffset?)now), cancellationToken);
    }
}

/// <summary>
/// The type name a recorded command is stored under, shared by every closed store type. It names the execution
/// model's record rather than the command envelope, so a bus outbox drain — which selects stored commands by the
/// envelope's type name — never reads one. Records written before the name changed carry the envelope's name and
/// are still resumed.
/// </summary>
internal static class CommandIntentRecord
{
    public static readonly string CommandTypeName = typeof(RecordedIntent).GetQualifiedTypeName();

    public static readonly string PreviousCommandTypeName = typeof(CommandEnvelope).GetQualifiedTypeName();
}
