using BenchmarkDotNet.Attributes;
using Stratara.Abstractions.Merging.ChangeTracking;
using Stratara.Benchmarks.Models;
using Stratara.Shared.Merging;
using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Benchmarks.Merging;

[MemoryDiagnoser]
public class TreatyChangeSetBenchmark
{
    private UpdateTreatyCommand _changes = null!;
    private Treaty _current = null!;
    private Treaty _source = null!;

    [GlobalSetup]
    public void Setup()
    {
        var baseDate = DateTimeOffset.UtcNow;

        _source = new Treaty
        {
            Id = Guid.NewGuid(),
            Name = "Initial Treaty",
            EffectiveFrom = baseDate,
            TreatyNumber = 123456,
            TreatyCode = "TREATY-001",
            TreatyType = "Quota Share",
            StartDate = new DateOnly(2024, 1, 1),
            EndDate = new DateOnly(2024, 12, 31),
            Currency = "EUR",
            PremiumEstimate = 1_000_000m,
            PaymentFrequency = "Annual",
            CedentName = "Cedent A",
            BrokerName = "Broker B",
            Layers = ["Layer1", "Layer2"],
            CoveredRisks = ["Fire", "Flood"],
            Status = "Draft",
            CreatedAt = baseDate,
            LastModifiedAt = null
        };

        _current = _source with
        {
            CedentName = "Cedent A Updated",
            Currency = "USD"
        };

        _changes = new UpdateTreatyCommand
        {
            CedentName = "Cedent A Updated",
            Currency = "USD",
            PremiumEstimate = 1_000_000m,
            TreatyId = _source.Id,
            SourceVersion = 42
        };
    }

    [Benchmark]
    public IReadOnlyList<ChangeDetail> CreateChangeSet_WithTwoChanges() =>
        ChangeSetBuilder<Treaty, UpdateTreatyCommand>.CreateChangeSet(_source, _current, _changes);

    [Benchmark]
    public IReadOnlyList<ChangeDetail> CreateChangeSet_WithNoChange() =>
        ChangeSetBuilder<Treaty, UpdateTreatyCommand>.CreateChangeSet(_source, _source, _changes with
        {
            CedentName = _source.CedentName,
            Currency = _source.Currency
        });
}