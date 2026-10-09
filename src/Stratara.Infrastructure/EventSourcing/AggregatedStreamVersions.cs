using System.Collections.Concurrent;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// The version each stream was at when the aggregation service first rebuilt it, without a bound, in the current
/// scope since the stream was last saved. The event source appends against it when the host asks for appends
/// conditional on the aggregated version. The first rebuild is the one the handler decided on: a later rebuild of
/// the same stream in the scope — by a validator or a helper — does not move the expectation past a write the
/// decision missed. Safe for the parallel rebuilds a saga or projection dispatch makes within one scope.
/// </summary>
internal sealed class AggregatedStreamVersions
{
    private readonly ConcurrentDictionary<Guid, long> _versions = new();

    public void Record(Guid streamId, long version) => _versions.TryAdd(streamId, version);

    public bool TryGet(Guid streamId, out long version) => _versions.TryGetValue(streamId, out version);

    public void Forget(IEnumerable<Guid> streamIds)
    {
        foreach (var streamId in streamIds)
        {
            _versions.TryRemove(streamId, out _);
        }
    }

    public void Clear() => _versions.Clear();
}
