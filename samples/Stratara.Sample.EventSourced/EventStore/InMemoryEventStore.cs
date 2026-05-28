using System.Collections.Concurrent;

namespace Stratara.Sample.EventSourced.EventStore;

public sealed class InMemoryEventStore
{
    private readonly ConcurrentDictionary<Guid, List<object>> _streams = new();
    private readonly List<(Guid StreamId, object Event)> _pending = [];
    private readonly IEnumerable<IProjection> _projections;

    public InMemoryEventStore(IEnumerable<IProjection> projections)
    {
        _projections = projections;
    }

    public void Append(Guid streamId, object @event) => _pending.Add((streamId, @event));

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (streamId, @event) in _pending)
        {
            var stream = _streams.GetOrAdd(streamId, _ => []);
            stream.Add(@event);

            foreach (var projection in _projections)
            {
                await projection.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
            }
        }
        _pending.Clear();
    }

    public IReadOnlyList<object> Read(Guid streamId) =>
        _streams.TryGetValue(streamId, out var stream) ? stream : [];
}
