using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Stratara.Diagnostics;

namespace Stratara.Orleans.Diagnostics;

/// <summary>
/// The age of the oldest entry each store reader has not applied yet, published on the framework's
/// meter as an observable gauge. A reader reports the time recorded with the first entry it read and
/// did not apply, and clears it once it has applied everything the store had; the gauge reports the
/// seconds since that time for every reader that has one.
/// </summary>
internal static class StoreReaderLag
{
    private static readonly ConcurrentDictionary<(string Consumer, int Partition), DateTimeOffset> Oldest = new();

    /// <summary>The gauge, created once on the framework's meter.</summary>
    public static readonly ObservableGauge<double> Gauge = ApplicationDiagnostics.Metrics.Meter.CreateObservableGauge(
        ApplicationDiagnostics.Metrics.OrleansReaderLagName,
        Observe,
        unit: "s",
        description: "Seconds since the time recorded with the oldest entry a store reader has not applied, by consumer and partition.");

    public static void Report(string consumer, int partition, DateTimeOffset oldestUnapplied) =>
        Oldest[(consumer, partition)] = oldestUnapplied;

    public static void Clear(string consumer, int partition) =>
        Oldest.TryRemove((consumer, partition), out _);

    private static IEnumerable<Measurement<double>> Observe()
    {
        var now = TimeProvider.System.GetUtcNow();
        foreach (var ((consumer, partition), oldest) in Oldest)
        {
            yield return new Measurement<double>(
                Math.Max(0, (now - oldest).TotalSeconds),
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Projection, consumer),
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Partition, partition));
        }
    }
}
