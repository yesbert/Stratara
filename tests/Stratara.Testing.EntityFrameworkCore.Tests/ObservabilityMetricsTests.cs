using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Diagnostics;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// Verifies that the Stratara observability instruments are published on the shared meter and that the
/// event-append counter records through the real event-sourcing write stack.
/// </summary>
public class ObservabilityMetricsTests
{
    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(s => s.AddAggregatesFromAssemblyContaining<Account>());

    [Fact]
    public void All_observability_instruments_are_published_on_the_shared_meter()
    {
        // Touch the static class so its instruments are constructed before the listener starts.
        _ = ApplicationDiagnostics.Metrics.EventsAppended;
        _ = ApplicationDiagnostics.Metrics.EventSourceAppendConflicts;

        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName)
                {
                    published.Add(instrument.Name);
                }
            }
        };
        listener.Start();

        // Exhaustive, not a Contains-list: instrument names are a public observability contract, so
        // an instrument added without being pinned here must fail too, not just a renamed one.
        string[] expected =
        [
            "command.duration",
            "event_source.append.conflicts",
            "event_source.events.appended",
            "messaging.dead_lettered",
            "orleans.completion.failed",
            "orleans.completion.flushed",
            "orleans.heavy.permits_in_use",
            "orleans.intent.kept",
            "orleans.intent.recorded",
            "orleans.intent.resumed",
            "orleans.reader.applied",
            "orleans.reader.stalled",
            "outbox.published",
            "projection.bundle.duration",
            "projection.events.processed",
            "saga.bundle.duration",
            "saga.events.processed",
            "saga.inflight"
        ];

        Assert.Equal(expected, published.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_instrument_is_created_from_its_published_name_constant()
    {
        (string Constant, string InstrumentName)[] pairs =
        [
            (ApplicationDiagnostics.Metrics.EventSourceAppendConflictsName, ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Name),
            (ApplicationDiagnostics.Metrics.EventsAppendedName, ApplicationDiagnostics.Metrics.EventsAppended.Name),
            (ApplicationDiagnostics.Metrics.OutboxEntriesPublishedName, ApplicationDiagnostics.Metrics.OutboxEntriesPublished.Name),
            (ApplicationDiagnostics.Metrics.MessagesDeadLetteredName, ApplicationDiagnostics.Metrics.MessagesDeadLettered.Name),
            (ApplicationDiagnostics.Metrics.CommandDurationName, ApplicationDiagnostics.Metrics.CommandDuration.Name),
            (ApplicationDiagnostics.Metrics.ProjectionEventsProcessedName, ApplicationDiagnostics.Metrics.ProjectionEventsProcessed.Name),
            (ApplicationDiagnostics.Metrics.ProjectionBundleDurationName, ApplicationDiagnostics.Metrics.ProjectionBundleDuration.Name),
            (ApplicationDiagnostics.Metrics.SagaEventsProcessedName, ApplicationDiagnostics.Metrics.SagaEventsProcessed.Name),
            (ApplicationDiagnostics.Metrics.SagaBundleDurationName, ApplicationDiagnostics.Metrics.SagaBundleDuration.Name),
            (ApplicationDiagnostics.Metrics.SagasInFlightName, ApplicationDiagnostics.Metrics.SagasInFlight.Name),
            (ApplicationDiagnostics.Metrics.OrleansReaderAppliedName, ApplicationDiagnostics.Metrics.OrleansReaderApplied.Name),
            (ApplicationDiagnostics.Metrics.OrleansReaderStalledName, ApplicationDiagnostics.Metrics.OrleansReaderStalled.Name),
            (ApplicationDiagnostics.Metrics.OrleansIntentRecordedName, ApplicationDiagnostics.Metrics.OrleansIntentRecorded.Name),
            (ApplicationDiagnostics.Metrics.OrleansIntentResumedName, ApplicationDiagnostics.Metrics.OrleansIntentResumed.Name),
            (ApplicationDiagnostics.Metrics.OrleansIntentKeptName, ApplicationDiagnostics.Metrics.OrleansIntentKept.Name),
            (ApplicationDiagnostics.Metrics.OrleansCompletionFlushedName, ApplicationDiagnostics.Metrics.OrleansCompletionFlushed.Name),
            (ApplicationDiagnostics.Metrics.OrleansCompletionFailedName, ApplicationDiagnostics.Metrics.OrleansCompletionFailed.Name),
            (ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUseName, ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Name),
        ];

        Assert.All(pairs, pair => Assert.Equal(pair.Constant, pair.InstrumentName));
    }

    [Fact]
    public async Task EventsAppended_counter_records_one_measurement_per_appended_event()
    {
        // The meter is process-global and other test classes append events concurrently, so we count
        // only measurements tagged with this test's dedicated aggregate type.
        const string probeAggregate = nameof(MetricsProbe);
        var total = 0L;
        var tenantId = EventStoreTestHost.DefaultTenantId;
        var id = Guid.CreateVersion7();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "event_source.events.appended")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == ApplicationDiagnostics.MetricTags.AggregateType
                    && (tag.Value as string) == probeAggregate)
                {
                    Interlocked.Add(ref total, measurement);
                }
            }
        });
        listener.Start();

        await using var host = CreateHost();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<MetricsProbe>(id, new MetricsProbeCreated(id, tenantId));
            await events.AppendAsync<MetricsProbe>(id, new MetricsProbeTouched());
            await events.SaveChangesAsync();
        });

        listener.Dispose();

        Assert.Equal(2L, Interlocked.Read(ref total));
    }
}
