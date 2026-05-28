using BenchmarkDotNet.Attributes;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Benchmarks;

[MemoryDiagnoser]
public class EventStreamBenchmark
{
    private readonly MyAggregate _aggregate = new();
    private readonly SomethingHappened _eventData = new("Foo");

    private readonly Event<SomethingHappened> _wrappedEvent = new(
        Guid.NewGuid(),
        0,
        new SomethingHappened("Foo"),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid());

    [Benchmark]
    public void DirectApplyCall()
    {
        var events = new List<SomethingHappened>
        {
            _eventData
        };

        foreach (var @event in events)
        {
            _aggregate.Apply(@event);
        }
    }

    [Benchmark]
    public void EventApplierCall()
    {
        var events = new List<IEvent>
        {
            _wrappedEvent
        };

        EventStream.Aggregate<MyAggregate>(events);
    }
}