using System.Diagnostics.Metrics;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Tests.Diagnostics;

/// <summary>One measurement observed on Stratara's shared meter.</summary>
internal sealed record CapturedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// The measurements captured during a test, and the lock that guards them.
///
/// The list is deliberately not handed out. Stratara's meter is process-global, so a callback
/// from another test's activity can still be adding to it while this test reads — including
/// after the listener is disposed, because disposal does not wait for a callback already in
/// flight. Enumerating the live list therefore fails intermittently with
/// "Collection was modified; enumeration operation may not execute". Read through
/// <see cref="Snapshot"/>, which copies under the same lock the writes take.
/// </summary>
internal sealed class CapturedMeasurements
{
    private readonly List<CapturedMeasurement> _measurements = [];
    private readonly Lock _gate = new();

    internal void Add(CapturedMeasurement measurement)
    {
        lock (_gate)
        {
            _measurements.Add(measurement);
        }
    }

    /// <summary>A stable copy that is safe to enumerate and assert against.</summary>
    public IReadOnlyList<CapturedMeasurement> Snapshot()
    {
        lock (_gate)
        {
            return [.. _measurements];
        }
    }
}

/// <summary>
/// Listens to Stratara's shared meter for the duration of a test. The meter is process-global, so
/// assertions filter on a tag the test controls rather than on totals.
/// </summary>
internal static class MeterCapture
{
    public static (MeterListener Listener, CapturedMeasurements Measurements) Start()
    {
        var measurements = new CapturedMeasurements();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _)
            where T : struct
        {
            var copy = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            measurements.Add(new CapturedMeasurement(instrument.Name, Convert.ToDouble(value), copy));
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();

        return (listener, measurements);
    }
}
