namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// The version each stream was at when the aggregation service last rebuilt it, without a bound, in the
/// current scope. The event source appends against it when the host asks for appends conditional on the
/// aggregated version, and forgets it with every save.
/// </summary>
internal sealed class AggregatedStreamVersions
{
    private readonly Dictionary<Guid, long> _versions = new();

    public void Record(Guid streamId, long version) => _versions[streamId] = version;

    public bool TryGet(Guid streamId, out long version) => _versions.TryGetValue(streamId, out version);

    public void Clear() => _versions.Clear();
}
