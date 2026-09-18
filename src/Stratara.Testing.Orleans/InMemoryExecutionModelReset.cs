using Microsoft.Extensions.Options;
using Orleans;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;

namespace Stratara.Testing.Orleans;

/// <summary>
/// The reset of <see cref="ExecutionModelTestHost"/>, behind the execution model's own port: the in-memory reminders,
/// the in-memory grain directory, and the checkpoints of the store readers the host registers. Membership is the one
/// silo's own and is left alone.
/// </summary>
/// <remarks>
/// The silo keeps running while this runs, which is what a test between two tests wants, so the readers are stopped
/// first, put at the store's head, and started again: a reader stopped this way forgets the position it had cached,
/// and a reader at the head applies what the next test commits and nothing the last one did. Deleting the checkpoints
/// instead would leave the readers reading the whole store again — into read models this reset does not empty.
/// </remarks>
internal sealed class InMemoryExecutionModelReset(
    IGrainFactory grainFactory,
    IReminderTable reminders,
    InMemoryGrainDirectory directory,
    ICommittedPositionReader positions,
    IProjectionCheckpointStore checkpoints,
    IOptions<CommitOrderOptions> commitOrder,
    IEnumerable<INudgeTarget> storeReaders) : IExecutionModelReset
{
    public async Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default)
    {
        var registered = (await reminders.ReadRows(0, uint.MaxValue)).Reminders.Count;
        await reminders.TestOnlyClearTable();

        var targets = storeReaders.ToList();
        var partitions = commitOrder.Value.PartitionCount;
        var holds = new List<StoreReaderHold>(targets.Count * partitions);
        int moved;
        try
        {
            foreach (var target in targets)
            {
                for (var partition = 0; partition < partitions; partition++)
                {
                    holds.Add(await target.PauseAsync(grainFactory, partition));
                }
            }

            moved = await AtTheHeadAsync(targets, partitions, cancellationToken);
        }
        catch
        {
            // Every reader that was paused is resumed, whatever one of them answers: a reader left paused reads
            // nothing until its pause lapses.
            foreach (var hold in holds)
            {
                await hold.DisposeAsync();
            }

            throw;
        }

        var resuming = new List<Exception>();
        foreach (var hold in holds)
        {
            try
            {
                await hold.ResumeAsync();
            }
            catch (Exception failure)
            {
                resuming.Add(failure);
            }
            finally
            {
                await hold.DisposeAsync();
            }
        }

        if (resuming.Count > 0)
        {
            throw new InvalidOperationException(
                $"{resuming.Count} of the host's store reader groups stayed paused after the reset and read nothing until their pause lapses.",
                resuming[0]);
        }

        return new ExecutionModelResetReport(registered, MembershipRows: 0, moved, directory.Clear());
    }

    /// <summary>Puts every registered consumer's checkpoint at the store's head, and says how many it moved.</summary>
    private async Task<int> AtTheHeadAsync(IReadOnlyList<INudgeTarget> targets, int partitions, CancellationToken cancellationToken)
    {
        var consumers = targets.SelectMany(target => target.ConsumerNames).Distinct(StringComparer.Ordinal).ToList();
        if (consumers.Count == 0)
        {
            return 0;
        }

        var moved = 0;
        for (var partition = 0; partition < partitions; partition++)
        {
            var head = await positions.HeadAsync(partition, cancellationToken);
            foreach (var consumer in consumers)
            {
                // One write, so no moment exists in which the reader has no checkpoint and would read the store from
                // the beginning into read models this does not empty.
                var before = await checkpoints.GetAsync(consumer, partition, positions.Name, cancellationToken);
                if (before == head)
                {
                    continue;
                }

                await checkpoints.SetAsync(consumer, partition, positions.Name, head, cancellationToken);
                moved++;
            }
        }

        return moved;
    }
}
