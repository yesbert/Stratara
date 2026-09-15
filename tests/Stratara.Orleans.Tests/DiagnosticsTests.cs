using System.Diagnostics.Metrics;
using System.Reflection;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The execution model logs in a band of its own and measures under the names the observability
/// capability publishes, so an operator can filter on the band and query every instrument by a name
/// that is a published constant.
/// </summary>
public sealed class DiagnosticsTests
{
    private static readonly string[] PublishedInstruments =
    [
        ApplicationDiagnostics.Metrics.OrleansCompletionFailedName,
        ApplicationDiagnostics.Metrics.OrleansCompletionFlushedName,
        ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUseName,
        ApplicationDiagnostics.Metrics.OrleansIntentKeptName,
        ApplicationDiagnostics.Metrics.OrleansIntentRecordedName,
        ApplicationDiagnostics.Metrics.OrleansIntentResumedName,
        ApplicationDiagnostics.Metrics.OrleansReaderAppliedName,
        ApplicationDiagnostics.Metrics.OrleansReaderLagName,
        ApplicationDiagnostics.Metrics.OrleansReaderStalledName,
    ];

    [Fact]
    public void Every_event_id_of_the_execution_model_is_in_its_band()
    {
        var ids = typeof(LogEvents.Orleans)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (Name: field.Name, Id: (int)field.GetRawConstantValue()!))
            .ToList();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.InRange(id.Id, 117_000, 117_999));
        Assert.Equal(ids.Count, ids.Select(id => id.Id).Distinct().Count());
    }

    [Fact]
    public void Every_log_message_of_the_execution_model_uses_an_id_of_its_band()
    {
        var used = typeof(OrleansLog)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<Microsoft.Extensions.Logging.LoggerMessageAttribute>())
            .OfType<Microsoft.Extensions.Logging.LoggerMessageAttribute>()
            .Select(attribute => attribute.EventId)
            .ToList();

        Assert.NotEmpty(used);
        Assert.All(used, id => Assert.InRange(id, 117_000, 117_999));
    }

    [Fact]
    public void Every_instrument_of_the_execution_model_is_published_under_a_listed_name()
    {
        _ = ApplicationDiagnostics.Metrics.OrleansReaderApplied;
        _ = StoreReaderLag.Gauge;

        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName
                    && instrument.Name.StartsWith("orleans.", StringComparison.Ordinal))
                {
                    published.Add(instrument.Name);
                }
            },
        };
        listener.Start();

        Assert.Equal(PublishedInstruments.Order(StringComparer.Ordinal), published.Order(StringComparer.Ordinal));
    }
}
