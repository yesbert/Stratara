namespace Stratara.Orleans.Hosting;

/// <summary>
/// Clears everything the Orleans execution model keeps beside the event stream — the cluster's reminders and
/// with them every durable timer, its membership, the grain directory's entries, and the checkpoints of the
/// store-reading projections and sagas the host registers — so that a deployment returns to nothing scheduled and
/// nothing remembered. The event stream is never touched; the checkpoints are rebuilt from it when the host starts again.
/// </summary>
/// <remarks>
/// Run it while no silo of the cluster runs: a running silo writes its membership and its reminders back. Resolve it
/// from the host's own composition: the checkpoints it removes are those of the projections and sagas registered
/// there, and another consumer's checkpoints in the same read store — a projection the host no longer registers
/// included — stay.
/// </remarks>
public interface IExecutionModelReset
{
    /// <summary>Clears the execution model's state and reports what was removed.</summary>
    /// <param name="cancellationToken">Propagated to every store the reset clears.</param>
    /// <returns>How many reminders, membership rows, checkpoints and directory entries were removed.</returns>
    Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a reset removed.</summary>
/// <param name="Reminders">The reminders of the host's service, durable timers included.</param>
/// <param name="MembershipRows">The membership rows of the host's cluster.</param>
/// <param name="Checkpoints">The checkpoints of the store-reading projections and sagas the host registers.</param>
/// <param name="DirectoryEntries">The entries the host's directory cleanup removed.</param>
public sealed record ExecutionModelResetReport(int Reminders, int MembershipRows, int Checkpoints, long DirectoryEntries);
