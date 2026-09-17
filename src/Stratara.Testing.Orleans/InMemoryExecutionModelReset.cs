using Microsoft.EntityFrameworkCore;
using Orleans;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;

namespace Stratara.Testing.Orleans;

/// <summary>
/// The reset of <see cref="ExecutionModelTestHost"/>, behind the execution model's own port: the in-memory reminders,
/// the in-memory grain directory, and the checkpoints of the store readers the host registers. Membership is the one
/// silo's own and is left alone.
/// </summary>
internal sealed class InMemoryExecutionModelReset(
    IDbContextFactory<StrataraTestReadDbContext> readContextFactory,
    IReminderTable reminders,
    InMemoryGrainDirectory directory,
    IEnumerable<INudgeTarget> storeReaders) : IExecutionModelReset
{
    public async Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default)
    {
        var registered = (await reminders.ReadRows(0, uint.MaxValue)).Reminders.Count;
        await reminders.TestOnlyClearTable();

        int checkpoints;
        await using (var context = await readContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var consumers = storeReaders.SelectMany(reader => reader.ConsumerNames).Distinct(StringComparer.Ordinal).ToList();
            checkpoints = consumers.Count == 0
                ? 0
                : await context.Set<ProjectionCheckpoint>().Where(c => consumers.Contains(c.Projection)).ExecuteDeleteAsync(cancellationToken);
        }

        return new ExecutionModelResetReport(registered, MembershipRows: 0, checkpoints, directory.Clear());
    }
}
