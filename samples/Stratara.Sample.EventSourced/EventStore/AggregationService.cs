namespace Stratara.Sample.EventSourced.EventStore;

public sealed class AggregationService(InMemoryEventStore store)
{
    public TAggregate Aggregate<TAggregate>(Guid streamId)
        where TAggregate : new()
    {
        var aggregate = new TAggregate();
        var aggregateType = typeof(TAggregate);

        foreach (var @event in store.Read(streamId))
        {
            var applyMethod = aggregateType.GetMethod("Apply", [@event.GetType()]);
            applyMethod?.Invoke(aggregate, [@event]);
        }

        return aggregate;
    }
}
