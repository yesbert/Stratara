using BenchmarkDotNet.Attributes;
using Stratara.Abstractions.EventSourcing;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Benchmarks;

/// <summary>
/// Measures in-memory aggregate replay through the compiled-expression apply-dispatch at
/// 10k / 100k / 1M events. Event-list construction happens in <see cref="Setup"/>, so the
/// benchmark isolates the per-event dispatch cost and the allocation it produces (excludes
/// the database read that a real rehydration would add).
/// </summary>
[MemoryDiagnoser]
public class ReplayScalingBenchmark
{
    [Params(10_000, 100_000, 1_000_000)]
    public int EventCount;

    private IReadOnlyList<IEvent> _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        var data = new SomethingHappened("x");
        var events = new List<IEvent>(EventCount);
        for (var i = 0; i < EventCount; i++)
        {
            events.Add(new Event<SomethingHappened>(
                Guid.NewGuid(), i, data,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        }

        _events = events;
    }

    [Benchmark]
    public MyAggregate Replay() => EventStream.Aggregate<MyAggregate>(_events);
}
