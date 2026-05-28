using BenchmarkDotNet.Attributes;
using Stratara.Shared.Merging.SmartMerging;

namespace Stratara.Benchmarks.Merging;

public record DummyCommand
{
    public string? Name { get; set; }
    public int Age { get; set; }
}

public record DummyAggregate
{
    public string? Name { get; set; }
    public int Age { get; set; }
}

[MemoryDiagnoser]
public class SmartMergerBenchmark
{
    private DummyCommand _command = null!;
    private DummyAggregate _current = null!;
    private DummyAggregate _source = null!;

    [GlobalSetup]
    public void Setup()
    {
        _command = new DummyCommand { Name = "X", Age = 42 };
        _source = new DummyAggregate { Name = "X", Age = 40 };
        _current = new DummyAggregate { Name = "X", Age = 40 };

        _ = SmartMerger<DummyCommand, DummyAggregate>.Merge(_command, _source, _current);
    }

    private static DummyCommand ManualMerge(DummyCommand command, DummyAggregate source, DummyAggregate current)
    {
        var result = new DummyCommand
        {
            Name = command.Name != source.Name && source.Name == current.Name
                ? command.Name
                : current.Name,

            Age = command.Age != source.Age && source.Age == current.Age
                ? command.Age
                : current.Age
        };

        return result;
    }

    [Benchmark(Baseline = true)]
    public DummyCommand Merge_With_Cache() => SmartMerger<DummyCommand, DummyAggregate>.Merge(_command, _source, _current);

    [Benchmark]
    public DummyCommand Merge_Cold_Compile()
    {
        var coldCommand = new DummyCommand { Name = "X", Age = 42 };
        var coldSource = new DummyAggregate { Name = "X", Age = 40 };
        var coldCurrent = new DummyAggregate { Name = "X", Age = 40 };

        return SmartMerger<DummyCommand, DummyAggregate>.Merge(coldCommand, coldSource, coldCurrent);
    }

    [Benchmark]
    public DummyCommand Merge_Manual() => ManualMerge(_command, _source, _current);
}