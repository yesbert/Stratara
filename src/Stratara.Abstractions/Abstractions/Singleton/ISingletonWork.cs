namespace Stratara.Abstractions.Singleton;

/// <summary>
/// A unit of work that runs once per cluster, on a period: an outbox drain, a flush, a recovery
/// sweep. The host registers an implementation and the execution model runs it in one place in the
/// cluster, identified by <see cref="Name"/>, while the cluster agrees on its membership, and no lock is
/// needed to make that so; two runs on one silo never overlap. A silo the cluster has declared dead may
/// still be running the work until it learns of the declaration, while another silo has already taken it
/// over, so a run should tolerate an overlapping run elsewhere — claim what it processes with a
/// compare-and-set, or be idempotent.
/// </summary>
public interface ISingletonWork
{
    /// <summary>The name that identifies the work across the cluster.</summary>
    string Name { get; }

    /// <summary>How often the work runs. A run that takes longer than the period delays the next one; it is never doubled.</summary>
    TimeSpan Period { get; }

    /// <summary>Runs the work once. Resolved from a fresh scope for every run, so it may take scoped dependencies.</summary>
    /// <param name="cancellationToken">Cancelled when the host running the work shuts down.</param>
    /// <returns>A task that completes when the run has ended.</returns>
    Task RunAsync(CancellationToken cancellationToken);
}
