namespace Stratara.Orleans.Singleton;

/// <summary>Settings for the singleton-work runner.</summary>
public sealed class SingletonWorkOptions
{
    /// <summary>
    /// How often the cluster makes sure each work's grain is alive. The grain keeps itself active
    /// while its silo lives; this is what brings it back on another silo when that silo is lost.
    /// Cannot be shorter than the cluster's minimum reminder period.
    /// </summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromMinutes(1);
}
