using System.Diagnostics.Metrics;
using Stratara.Diagnostics;

namespace Stratara.Outbox.RabbitMQ.Tests.Diagnostics;

/// <summary>One measurement observed on Stratara's shared meter.</summary>
internal sealed record CapturedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Listens to Stratara's shared meter for the duration of a test. The meter is process-global, so
/// assertions filter on a tag the test controls rather than on totals.
/// </summary>
internal static class MeterCapture
{
    public static (MeterListener Listener, List<CapturedMeasurement> Measurements) Start()
    {
        List<CapturedMeasurement> measurements = [];
        var gate = new Lock();

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
            lock (gate)
            {
                measurements.Add(new CapturedMeasurement(instrument.Name, Convert.ToDouble(value), copy));
            }
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();

        return (listener, measurements);
    }
}
