using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.CommitOrder;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The head of each partition, as the silo's store readers last asked the store for it, shared by every reader on the
/// silo, so that an idle poll costs the store one question per partition rather than one read per reader. A head is at
/// most as old as the asking reader allows; one reader asks the store while the others wait for its answer. The head is
/// the one <see cref="ICommittedPositionReader.HeadAsync"/> gives, held back as the reader's own read is, so a reader at
/// or past it would read nothing. A reader whose head the store cannot tell without walking the partition — one that
/// leaves <see cref="ICommittedPositionReader.HeadAsync"/> to the interface — is never asked, and every poll reads as
/// before; so does every poll whose head could not be read.
/// </summary>
internal sealed class PartitionHeads(IServiceScopeFactory scopeFactory)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (long Head, long TakenAt)> _heads = [];
    private readonly Dictionary<int, Task<long?>> _asking = [];
    private bool? _answersInOneQuery;

    /// <summary>
    /// Whether the partition may hold an entry past <paramref name="position"/>, from a head no older than
    /// <paramref name="maxAge"/>; <see langword="true"/> wherever the head is not known.
    /// </summary>
    public async Task<bool> HasAnythingPastAsync(int partition, long position, TimeSpan maxAge)
    {
        if (_answersInOneQuery is false)
        {
            return true;
        }

        return await HeadAsync(partition, maxAge) is not { } head || position < head;
    }

    private Task<long?> HeadAsync(int partition, TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (_heads.TryGetValue(partition, out var known) && Stopwatch.GetElapsedTime(known.TakenAt) < maxAge)
            {
                return Task.FromResult<long?>(known.Head);
            }

            if (_asking.TryGetValue(partition, out var asking))
            {
                return asking;
            }

            var ask = AskAsync(partition, maxAge * 2);
            _asking[partition] = ask;
            return ask;
        }
    }

    /// <summary>
    /// Asks the store for the head, for no longer than <paramref name="timeout"/>, so that a question the store does not
    /// answer holds the polls that wait for it for no longer than a poll interval. The time it is recorded with is taken
    /// before the question, so a head is never thought younger than it is.
    /// </summary>
    private async Task<long?> AskAsync(int partition, TimeSpan timeout)
    {
        await Task.Yield();
        var takenAt = Stopwatch.GetTimestamp();
        using var expiry = new CancellationTokenSource(timeout);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
            _answersInOneQuery ??= AnswersHeadInOneQuery(reader);
            if (_answersInOneQuery is false)
            {
                return null;
            }

            var head = await reader.HeadAsync(partition, expiry.Token);
            lock (_gate)
            {
                _heads[partition] = (head, takenAt);
            }

            return head;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || expiry.IsCancellationRequested)
        {
            _ = ex;
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _asking.Remove(partition);
            }
        }
    }

    /// <summary>Whether <paramref name="reader"/> answers its head itself rather than through the interface's walk of the partition.</summary>
    internal static bool AnswersHeadInOneQuery(ICommittedPositionReader reader)
    {
        var map = reader.GetType().GetInterfaceMap(typeof(ICommittedPositionReader));
        var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(ICommittedPositionReader.HeadAsync));
        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(ICommittedPositionReader);
    }
}
