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
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;

namespace Stratara.Projections.Tests.Services;

/// <summary>
/// The projection instruments were pinned by name and never by recording site: both could stop
/// being written and the suite would pass, with dashboards and alerts going quiet and nothing
/// saying so.
/// </summary>
public class ProjectionWorkerMetricsTests
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
    public async Task ABundleRecordsBothProjectionInstruments()
    {
        var eventType = $"MetricsProbe.{Guid.CreateVersion7():N}";
        var harness = new MetricsHarness();
        var (listener, measurements) = Capture();

        using (listener)
        {
            await harness.Sut.HandleEventBundleAsync(BundleWith(eventType), CancellationToken.None);
        }

        var processed = measurements
            .Where(m => m.Instrument == "projection.events.processed")
            .Where(m => Equals(m.Tags.GetValueOrDefault("event.type"), eventType))
            .ToList();

        var measurement = Assert.Single(processed);
        Assert.Equal(1, measurement.Value);
        Assert.Equal("success", measurement.Tags.GetValueOrDefault("outcome"));

        Assert.Contains(measurements, m => m.Instrument == "projection.bundle.duration");
    }

    [Fact]
    public async Task AFailingBundleIsRecordedAsFailure()
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
            .Where(m => m.Instrument == "projection.events.processed")
            .Where(m => Equals(m.Tags.GetValueOrDefault("event.type"), eventType))
            .ToList();

        var measurement = Assert.Single(processed);
        Assert.Equal("failure", measurement.Tags.GetValueOrDefault("outcome"));
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
        public ProjectionWorker Sut { get; }

        public MetricsHarness(bool failing = false)
        {
            var pipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
            pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

            var mapper = new Mock<IEventMapperFactory>();
            mapper.Setup(m => m.MapToEventsAsync(It.IsAny<IReadOnlyList<EventMessage>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IEvent>());

            var projectionManager = new Mock<IProjectionManager>();
            if (failing)
            {
                projectionManager
                    .Setup(s => s.HandleAsync(It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("projection blew up"));
            }

            var services = new ServiceCollection();
            services.AddSingleton(new Mock<ISessionContextProvider>().Object);
            services.AddSingleton(projectionManager.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Sut = new ProjectionWorker(
                NullLogger<ProjectionWorker>.Instance,
                new Mock<IMessageBus>().Object,
                new Mock<IMessagingIdentifier>().Object,
                scopeFactory,
                mapper.Object,
                pipelineProvider.Object,
                Options.Create(new BusEnvelopeJsonOptions { MaxBodyBytes = 1_048_576 }),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off }),
                Options.Create(new ProjectionOptions()),
                signer: null);
        }
    }
}
