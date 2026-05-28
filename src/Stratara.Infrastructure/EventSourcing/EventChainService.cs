using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="IEventChainService"/> implementation that periodically anchors the latest
/// hashed event into the event-chain table once a configurable number of new events
/// (<c>AnchorRange</c>, currently 5) have been hashed.
/// </summary>
/// <remarks>
/// Anchors form tamper-evident checkpoints over the event-stream hash chain. The service is
/// designed to be invoked from the <see cref="EventStreamHashWorker"/> after each hashing batch.
/// </remarks>
internal sealed class EventChainService(IWriteUnitOfWork unitOfWork) : IEventChainService
{
    private static readonly int AnchorRange = 5;

    /// <inheritdoc/>
    public async Task AddAnchorIfNeededAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var streamRepository = unitOfWork.CreateEventStreamRepository(transaction);

        var entry = await streamRepository.GetLastHashedEventAsync(cancellationToken);
        if (!await ShouldAddAnchorAsync(entry, cancellationToken))
        {
            return;
        }

        var anchor = new EventChainAnchor
        {
            Id = Guid.CreateVersion7(),
            SequenceNumber = entry!.SequenceNumber,
            AnchorHash = entry.Hash!,
            Timestamp = DateTimeOffset.UtcNow,
            BucketId = entry.BucketId,
            TenantId = entry.TenantId
        };

        var chainRepository = unitOfWork.CreateEventChainRepository(transaction);
        await chainRepository.AddAnchorAsync(anchor, cancellationToken);

        await transaction.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShouldAddAnchorAsync(EventStreamEntry? entry, CancellationToken cancellationToken = default)
    {
        if (entry is null)
        {
            return false;
        }

        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var chainRepository = unitOfWork.CreateEventChainRepository(transaction);

        var lastSequenceNumber = await chainRepository.GetLastSequenceNumberOrDefaultAsync([], cancellationToken);
        var sequenceDifference = entry.SequenceNumber - lastSequenceNumber;
        return sequenceDifference >= AnchorRange;
    }
}
