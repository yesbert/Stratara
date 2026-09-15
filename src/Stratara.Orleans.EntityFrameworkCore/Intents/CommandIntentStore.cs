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
/// entry of the command-envelope type, and its resume bookkeeping is the entry's attempt count, hand-over
/// time, last failure and kept state. Every operation is one statement on a context of its own.
/// </summary>
/// <typeparam name="TContext">A write context derived from the framework's write context.</typeparam>
internal sealed class CommandIntentStore<TContext>(IDbContextFactory<TContext> contextFactory) : ICommandIntentStore
    where TContext : DbContext, IWriteDbContext
{
    private const int FailureLength = 2048;
    private static readonly string CommandTypeName = typeof(CommandEnvelope).GetQualifiedTypeName();

    public async Task RecordAsync(Guid intentId, CommandEnvelope envelope, Guid? aggregateId, bool heavy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Set<OutboxEntry>().Add(new OutboxEntry
        {
            Id = intentId,
            BucketId = BucketCalculator.GetBucketId(intentId),
            DataJson = JsonSerializer.Serialize(envelope),
            DataTypeName = CommandTypeName,
            Timestamp = DateTimeOffset.UtcNow,
            AggregateId = aggregateId,
            Heavy = heavy,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordedIntent>> GetDueAsync(DateTimeOffset handedOverBefore, int batchSize, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await context.Set<OutboxEntry>().AsNoTracking()
            .Where(e => e.DataTypeName == CommandTypeName
                        && e.KeptAt == null
                        && e.Timestamp <= handedOverBefore
                        && (e.LastHandedOverAt == null || e.LastHandedOverAt <= handedOverBefore))
            .OrderBy(e => e.Timestamp)
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
                e.LastFailure))
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

    public async Task RenewAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.KeptAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LastHandedOverAt, (DateTimeOffset?)now), cancellationToken);
    }

    public async Task RecordFailureAsync(Guid intentId, string failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var recorded = failure.Length > FailureLength ? failure[..FailureLength] : failure;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LastFailure, recorded), cancellationToken);
    }

    public async Task KeepAsync(Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Set<OutboxEntry>()
            .Where(e => e.Id == intentId && e.KeptAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.KeptAt, (DateTimeOffset?)now), cancellationToken);
    }
}
