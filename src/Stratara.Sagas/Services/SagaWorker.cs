using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Stratara.Contracts.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Partitioning;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Partitioning;
using Stratara.Resilience;

namespace Stratara.Sagas.Services;

/// <summary>
/// Background service that subscribes to the event-bundle topic under the saga subscription and dispatches
/// each bundle through the <see cref="ISagaManager"/>. It opens <see cref="SagaOptions.DegreeOfParallelism"/>
/// consumers on the subscription — one per processor unless configured — and dispatches bundles about one
/// aggregate one at a time within the process, so a follow-up fact cannot overtake the fact that created
/// its entity on a neighbouring consumer.
/// </summary>
/// <remarks>
/// Each bundle is processed in a fresh DI scope with the bundle's <c>SessionContext</c> restored onto
/// <c>ISessionContextProvider</c>. The named resilience pipeline <c>MessageBus</c> wraps subscription
/// creation so transient broker outages are retried. A saga that throws
/// <see cref="PrecedingFactMissingException"/> has its bundle retried under the <c>PrecedingFact</c>
/// policy, with every aggregate lock released between attempts; any other failure propagates on the first
/// occurrence. When <see cref="IBusEnvelopeSigner"/> is registered and
/// <see cref="BusEnvelopeIntegrityOptions.Mode"/> is non-<see cref="BusEnvelopeIntegrityMode.Off"/>, the
/// bundle's signature is verified before the session context is restored; Strict-mode failures throw,
/// Permissive-mode failures log a warning.
/// </remarks>
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal worker; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (logger, bus, scope, pipeline, mapper, envelope options, " +
                    "integrity options, saga options, signer) and are not a hand-called API surface.")]
internal sealed class SagaWorker(
    ILogger<SagaWorker> logger,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<BusEnvelopeJsonOptions> envelopeOptions,
    IOptions<BusEnvelopeIntegrityOptions> integrityOptions,
    IOptions<SagaOptions> sagaOptions,
    IBusEnvelopeSigner? signer = null) : BackgroundService
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.MessageBus);
    private readonly ResiliencePipeline _precedingFactPipeline = pipelineProvider.GetPipeline(ResilienceNames.PrecedingFact);
    private readonly BucketLockPool _bucketLockPool = new();
    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly BusEnvelopeIntegrityMode _integrityMode = integrityOptions.Value.Mode;
    private readonly int _degreeOfParallelism = EffectiveDegreeOfParallelism(sagaOptions.Value.DegreeOfParallelism);

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogSagaWorkerStarted();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogSagaWorkerStopped();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _bucketLockPool.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Task[_degreeOfParallelism];
        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            workers[i] = CreateSubscriptionAsync(stoppingToken);
        }

        await Task.WhenAll(workers);
    }

    private async Task CreateSubscriptionAsync(CancellationToken stoppingToken)
    {
        await _pipeline.ExecuteAsync(async ct =>
        {
            await messageBus.SubscribeAsync<EventBundle>(messagingIdentifier.EventBundleTopic, messagingIdentifier.EventBundleSagaSubscription,
                async eventBundle => await HandleEventBundleAsync(eventBundle, ct), ct);
        }, stoppingToken);
    }

    internal async Task HandleEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var outcome = ApplicationDiagnostics.Outcomes.Failure;
        ApplicationDiagnostics.Metrics.SagasInFlight.Add(1);
        try
        {
            BusEnvelopeJsonGuard.EnsureWithinSizeLimit(Encoding.UTF8.GetByteCount(eventBundle.SessionContextJson), _envelopeOptions.MaxBodyBytes, "SessionContextJson");
            VerifyEnvelopeIntegrity(eventBundle);

            var bucketIds = BucketIdsOf(eventBundle);
            var attempt = 0;
            await _precedingFactPipeline.ExecuteAsync(async ct =>
            {
                attempt++;
                await DispatchUnderLocksAsync(eventBundle, bucketIds, attempt, ct);
            }, cancellationToken);

            outcome = ApplicationDiagnostics.Outcomes.Success;
        }
        finally
        {
            ApplicationDiagnostics.Metrics.SagasInFlight.Add(-1);
            ApplicationDiagnostics.Metrics.SagaBundleDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Outcome, outcome));
            foreach (var @event in eventBundle.Events)
            {
                ApplicationDiagnostics.Metrics.SagaEventsProcessed.Add(
                    1,
                    new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.EventType, @event.EventTypeName),
                    new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Outcome, outcome));
            }
        }
    }

    private async Task DispatchUnderLocksAsync(EventBundle eventBundle, int[] bucketIds, int attempt, CancellationToken cancellationToken)
    {
        var releasers = new IDisposable?[bucketIds.Length];
        try
        {
            for (var i = 0; i < bucketIds.Length; i++)
            {
                releasers[i] = await _bucketLockPool.AcquireAsync(bucketIds[i], cancellationToken);
            }

            await DispatchAsync(eventBundle, cancellationToken);
        }
        catch (PrecedingFactMissingException ex)
        {
            logger.LogSagaPrecedingFactMissing(ex, ex.StreamId, ex.EventTypeName, attempt);
            throw;
        }
        finally
        {
            for (var i = releasers.Length - 1; i >= 0; i--)
            {
                releasers[i]?.Dispose();
            }
        }
    }

    private async Task DispatchAsync(EventBundle eventBundle, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var sessionContextProvider = scope.ServiceProvider.GetRequiredService<ISessionContextProvider>();
        var sessionContext = JsonSerializer.Deserialize<SessionContext>(eventBundle.SessionContextJson, _deserializeOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session context from event bundle.");
        sessionContextProvider.Set(sessionContext);

        var sagaManager = scope.ServiceProvider.GetRequiredService<ISagaManager>();
        var events = await eventMapperFactory.MapToEventsAsync(eventBundle.Events, cancellationToken);
        await sagaManager.HandleAsync(events, cancellationToken);
    }

    private static int[] BucketIdsOf(EventBundle eventBundle) =>
        eventBundle.Events
            .Select(e => BucketCalculator.GetBucketId(e.StreamId))
            .Distinct()
            .Order()
            .ToArray();

    private static int EffectiveDegreeOfParallelism(int? configured) =>
        configured is > 0 ? configured.Value : Environment.ProcessorCount;

    private void VerifyEnvelopeIntegrity(EventBundle bundle)
    {
        var result = BusEnvelopeIntegrityVerifier.Verify(signer, _integrityMode, BusEnvelopeCanonical.Of(bundle), bundle.Signature, out var failure);
        if (result is BusEnvelopeIntegrityResult.Skipped or BusEnvelopeIntegrityResult.Verified)
        {
            return;
        }

        var firstEventId = bundle.Events.Count > 0 ? bundle.Events[0].Id : Guid.Empty;
        var eventCount = bundle.Events.Count;
        var unsigned = failure == BusEnvelopeIntegrityFailure.Absent;

        if (result == BusEnvelopeIntegrityResult.RejectedStrict)
        {
            if (unsigned)
            {
                logger.LogEventBundleUnsignedRejected(firstEventId, eventCount);
                throw new InvalidOperationException(
                    $"EventBundle (first event {firstEventId}, {eventCount} events) carries no signature and the mode is Strict. " +
                    "A publisher is not signing: register the signer on every publisher host, or roll the fleet through Permissive mode first.");
            }

            logger.LogEventBundleIntegrityRejected(firstEventId, eventCount);
            throw new InvalidOperationException(
                $"EventBundle (first event {firstEventId}, {eventCount} events) carries a signature that does not verify and the mode is Strict. " +
                "Confirm that publishers and consumers share the same BusEnvelopeIntegrityOptions.SharedKey " +
                "and that the bus is not relaying tampered messages.");
        }

        if (unsigned)
        {
            logger.LogEventBundleUnsignedWarning(firstEventId, eventCount);
            return;
        }

        logger.LogEventBundleIntegrityWarning(firstEventId, eventCount);
    }
}
