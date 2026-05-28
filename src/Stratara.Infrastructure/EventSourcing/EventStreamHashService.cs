using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Default <see cref="IEventStreamHashService"/> that walks unhashed event-stream entries in
/// batches (10_000), computes a SHA-256 chain hash linking each entry to its predecessor, and
/// persists the updated entries.
/// </summary>
/// <remarks>
/// The first entry in the very first batch chains off a fixed <c>GENESIS</c> seed. A small commit
/// delay (5 seconds) is enforced before hashing to give a still-open writer enough time to flush
/// concurrent transactions. The service is invoked from <see cref="EventStreamHashWorker"/>.
/// </remarks>
internal sealed class EventStreamHashService(IWriteUnitOfWork unitOfWork) : IEventStreamHashService
{
    private const int BatchSize = 10_000;
    private static readonly TimeSpan CommitDelaySeconds = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public async Task HashEventsAsync(CancellationToken stoppingToken = default)
    {
        var cutOff = DateTimeOffset.UtcNow - CommitDelaySeconds;

        await using var transaction = await unitOfWork.StartAsync(stoppingToken);
        var repository = unitOfWork.CreateEventStreamRepository(transaction);

        var eventEntries = await repository.GetUnhashedEventsAsync(BatchSize, cutOff, stoppingToken);

        while (eventEntries.Count > 0 && !stoppingToken.IsCancellationRequested)
        {
            var firstEntry = eventEntries[0];
            var previousEntry = await repository.GetPreviousEventAsync(firstEntry.SequenceNumber, stoppingToken);
            var previousHash = previousEntry?.Hash ?? SHA256.HashData("GENESIS"u8.ToArray());
            List<EventStreamEntry> updatedEntries = [];

            foreach (var entry in eventEntries)
            {
                entry.PreviousHash = previousHash;
                entry.Hash = ComputeHash(entry);
                previousHash = entry.Hash;
                updatedEntries.Add(entry);
            }

            repository.UpdateRange(updatedEntries);

            await transaction.SaveChangesAsync(stoppingToken);

            cutOff = DateTimeOffset.UtcNow - CommitDelaySeconds;
            eventEntries = await repository.GetUnhashedEventsAsync(BatchSize, cutOff, stoppingToken);
        }
    }

    private static byte[] ComputeHash(EventStreamEntry entry)
    {
        // Build incrementally so we never materialise the full hash-input string or byte[] —
        // at BatchSize=10_000 events the savings are ~10-20 MB Gen0 garbage per batch.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> numberBuffer = stackalloc byte[32];

        hash.AppendData(Encoding.UTF8.GetBytes(Convert.ToHexString(entry.PreviousHash!)));
        hash.AppendData(Pipe);
        Utf8Formatter.TryFormat(entry.SequenceNumber, numberBuffer, out var written);
        hash.AppendData(numberBuffer[..written]);
        hash.AppendData(Pipe);
        Utf8Formatter.TryFormat(entry.Version, numberBuffer, out written);
        hash.AppendData(numberBuffer[..written]);
        hash.AppendData(Pipe);
        Utf8Formatter.TryFormat(entry.Timestamp.ToUnixTimeMilliseconds(), numberBuffer, out written);
        hash.AppendData(numberBuffer[..written]);
        hash.AppendData(Pipe);
        hash.AppendData(Encoding.UTF8.GetBytes(entry.EventTypeName));
        hash.AppendData(Pipe);
        hash.AppendData(Encoding.UTF8.GetBytes(entry.DataJson));

        return hash.GetHashAndReset();
    }

    private static ReadOnlySpan<byte> Pipe => "|"u8;
}