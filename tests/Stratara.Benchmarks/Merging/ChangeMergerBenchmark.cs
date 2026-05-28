using BenchmarkDotNet.Attributes;
using Stratara.Benchmarks.Models;
using Stratara.Shared.Merging;
using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Benchmarks.Merging;

[MemoryDiagnoser]
public class ChangeMergerBenchmark
{
    private Treaty? _changes;
    private Treaty? _current;
    private Treaty? _source;

    [GlobalSetup]
    public void Setup()
    {
        var now = DateTimeOffset.UtcNow;

        _source = new Treaty
        {
            Id = Guid.NewGuid(),
            Name = "Treaty 2023",
            EffectiveFrom = now,
            TreatyNumber = 1001,
            TreatyCode = "T001",
            TreatyType = "Quota Share",
            StartDate = new DateOnly(2023, 1, 1),
            EndDate = new DateOnly(2023, 12, 31),
            Currency = "EUR",
            PremiumEstimate = 1000000m,
            PaymentFrequency = "Annual",
            CedentName = "Alpha Re",
            BrokerName = "Global Brokers",
            Layers = ["Layer1", "Layer2"],
            CoveredRisks = ["Fire", "Flood"],
            Status = "Draft",
            CreatedAt = now,
            LastModifiedAt = now
        };

        _current = _source with { Name = "Treaty 2023 Rev 1", PremiumEstimate = 1200000m };

        _changes = new Treaty
        {
            Name = "Treaty 2023 Rev 1",
            CedentName = "Beta Re",
            BrokerName = "Global Brokers",
            PremiumEstimate = 1200000m,
            StartDate = new DateOnly(2025, 2, 15)
        };
    }

    [Benchmark]
    public ChangeMergeResult<Treaty> Merge_ExpressionCached() =>
        ChangeMerger<Treaty, Treaty>.ApplyChanges(_source!, _current!, _changes!);
}