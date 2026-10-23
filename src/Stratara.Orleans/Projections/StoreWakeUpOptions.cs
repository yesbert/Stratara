namespace Stratara.Orleans.Projections;

/// <summary>Settings for the store wake-ups a host registers with <c>AddStrataraStoreWakeUps</c>.</summary>
public sealed class StoreWakeUpOptions
{
    /// <summary>
    /// How long the wake-ups of a store-reading role are set aside once the cluster was found to host no silo of that
    /// role. The first commit after it tries the role again, so a silo of the role that joins later is woken within this
    /// period; until then its readers find the commits by their poll. Short by default, because a placement can also
    /// fail for a moment while a silo of the role joins and its metadata has not reached every silo yet: setting the
    /// role aside then costs its readers this long of polling, and a role that is truly absent costs one failed call per
    /// period.
    /// </summary>
    public TimeSpan AbsentRoleRetry { get; set; } = TimeSpan.FromSeconds(10);
}
