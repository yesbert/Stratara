using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Diagnostics;
using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;

namespace Stratara.Sagas.Tests.Services;

/// <summary>
/// The saga instruments were pinned by name and never by recording site: all three could stop being
/// written and the suite would pass, with dashboards and alerts going quiet and nothing saying so.
/// </summary>
public class SagaWorkerMetricsTests
{
    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private static (MeterListener Listener, List<Measurement> Measurements) Capture()
    {
        List<Measurement> measurements = [];
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
            var copy = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (gate)
            {
                measurements.Add(new Measurement(instrument.Name, Convert.ToDouble(value), copy));
            }
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();

        return (listener, measurements);
    }

    [Fact]
    public async Task ABundleRecordsAllThreeSagaInstruments()
    {
        var eventType = $"MetricsProbe.{Guid.CreateVersion7():N}";
        var harness = new MetricsHarness();
        var (listener, measurements) = Capture();

        using (listener)
        {
            await harness.Sut.HandleEventBundleAsync(BundleWith(eventType), CancellationToken.None);
        }

        var processed = measurements
            .Where(m => m.Instrument == "saga.events.processed")
            .Where(m => Equals(m.Tags.GetValueOrDefault("event.type"), eventType))
            .ToList();

        var measurement = Assert.Single(processed);
        Assert.Equal(1, measurement.Value);
        Assert.Equal("success", measurement.Tags.GetValueOrDefault("outcome"));

        Assert.Contains(measurements, m => m.Instrument == "saga.bundle.duration");
        Assert.Contains(measurements, m => m.Instrument == "saga.inflight" && m.Value == 1);
        Assert.Contains(measurements, m => m.Instrument == "saga.inflight" && m.Value == -1);
    }

    [Fact]
    public async Task AFailingBundleIsRecordedAsFailure_AndStillLeavesTheInFlightGauge()
    {
        var eventType = $"MetricsProbe.{Guid.CreateVersion7():N}";
        var harness = new MetricsHarness(failing: true);
        var (listener, measurements) = Capture();

        using (listener)
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                harness.Sut.HandleEventBundleAsync(BundleWith(eventType), CancellationToken.None));
        }

        var processed = measurements
            .Where(m => m.Instrument == "saga.events.processed")
            .Where(m => Equals(m.Tags.GetValueOrDefault("event.type"), eventType))
            .ToList();

        var measurement = Assert.Single(processed);
        Assert.Equal("failure", measurement.Tags.GetValueOrDefault("outcome"));
        Assert.Contains(measurements, m => m.Instrument == "saga.inflight" && m.Value == -1);
    }

    private static EventBundle BundleWith(string eventTypeName)
    {
        var events = new[]
        {
            new EventMessage(
                Id: Guid.CreateVersion7(),
                Version: 1,
                DataJson: "{}",
                StreamId: Guid.NewGuid(),
                EventTypeName: eventTypeName,
                AggregateTypeName: "TestAggregate",
                ActorTenantId: Guid.Empty,
                ActorUserId: Guid.Empty,
                TenantId: Guid.Empty,
                UserId: null),
        };

        return new EventBundle(events, JsonSerializer.Serialize(SessionContext.Empty()));
    }

    private sealed class MetricsHarness
    {
        public SagaWorker Sut { get; }

        public MetricsHarness(bool failing = false)
        {
            var pipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
            pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

            var mapper = new Mock<IEventMapperFactory>();
            mapper.Setup(m => m.MapToEventsAsync(It.IsAny<IReadOnlyList<EventMessage>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IEvent>());

            var sagaManager = new Mock<ISagaManager>();
            if (failing)
            {
                sagaManager
                    .Setup(s => s.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("saga blew up"));
            }

            var services = new ServiceCollection();
            services.AddSingleton(new Mock<ISessionContextProvider>().Object);
            services.AddSingleton(sagaManager.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Sut = new SagaWorker(
                NullLogger<SagaWorker>.Instance,
                new Mock<IMessageBus>().Object,
                new Mock<IMessagingIdentifier>().Object,
                scopeFactory,
                mapper.Object,
                pipelineProvider.Object,
                Options.Create(new BusEnvelopeJsonOptions { MaxBodyBytes = 1_048_576 }),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off }),
                signer: null);
        }
    }
}
