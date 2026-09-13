namespace Stratara.Orleans.Singleton;

/// <summary>
/// A unit of work that runs once per cluster, on a period: an outbox drain, a flush, a recovery
/// sweep. The host registers an implementation; the framework runs it in one grain keyed by its
/// <see cref="Name"/>, so no two silos run it at the same time and no lock or deployment assumption
/// is needed to make that so. Two runs of the same work never overlap.
/// </summary>
public interface ISingletonWork
{
    /// <summary>The name that identifies the work across the cluster. One grain per name.</summary>
    string Name { get; }

    /// <summary>How often the work runs. A run that takes longer than the period delays the next one; it is never doubled.</summary>
    TimeSpan Period { get; }

    /// <summary>One run. Resolved from a fresh scope each time, so it may take scoped dependencies.</summary>
    /// <param name="cancellationToken">Cancelled when the silo hosting the work shuts down.</param>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Settings for the singleton-work runner.</summary>
public sealed class SingletonWorkOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Orleans:SingletonWork";

    /// <summary>
    /// How often the cluster makes sure each work's grain is alive. The grain keeps itself active
    /// while its silo lives; this is what brings it back on another silo when that silo is lost.
    /// Cannot be shorter than the cluster's minimum reminder period.
    /// </summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}
