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
using Stratara.Abstractions.Session;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Resilience;

namespace Stratara.Sagas.Services;

/// <summary>
/// Hosted background service that subscribes to the event-bundle topic, deserializes incoming bundles
/// into events, restores the session context, and dispatches to <see cref="ISagaManager"/>. Runs
/// <see cref="Environment.ProcessorCount"/> parallel subscriptions wrapped in the
/// <c>MessageBus</c> resilience pipeline.
/// </summary>
/// <remarks>
/// When <see cref="IBusEnvelopeSigner"/> is registered and <see cref="BusEnvelopeIntegrityOptions.Mode"/>
/// is non-<see cref="BusEnvelopeIntegrityMode.Off"/>, the bundle's signature is verified before the
/// session context is restored; Strict-mode failures throw, Permissive-mode failures log a warning.
/// </remarks>
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal worker; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (logger, bus, scope, pipeline, mapper, envelope options, " +
                    "integrity options, signer) and are not a hand-called API surface.")]
internal sealed class SagaWorker(
    ILogger<SagaWorker> logger,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    IServiceScopeFactory scopeFactory,
    IEventMapperFactory eventMapperFactory,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<BusEnvelopeJsonOptions> envelopeOptions,
    IOptions<BusEnvelopeIntegrityOptions> integrityOptions,
    IBusEnvelopeSigner? signer = null) : BackgroundService
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.MessageBus);
    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly BusEnvelopeIntegrityMode _integrityMode = integrityOptions.Value.Mode;

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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var degreeOfParallelism = Environment.ProcessorCount;
        var workers = new Task[degreeOfParallelism];
        for (var i = 0; i < degreeOfParallelism; i++)
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
        BusEnvelopeJsonGuard.EnsureWithinSizeLimit(Encoding.UTF8.GetByteCount(eventBundle.SessionContextJson), _envelopeOptions.MaxBodyBytes, "SessionContextJson");
        VerifyEnvelopeIntegrity(eventBundle);

        using var scope = scopeFactory.CreateScope();

        var sessionContextProvider = scope.ServiceProvider.GetRequiredService<ISessionContextProvider>();
        var sessionContext = JsonSerializer.Deserialize<SessionContext>(eventBundle.SessionContextJson, _deserializeOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session context from event bundle.");
        sessionContextProvider.Set(sessionContext);

        var sagaManager = scope.ServiceProvider.GetRequiredService<ISagaManager>();
        var events = await eventMapperFactory.MapToEventsAsync(eventBundle.Events, cancellationToken);
        await sagaManager.HandleAsync(events, cancellationToken);
    }

    private void VerifyEnvelopeIntegrity(EventBundle bundle)
    {
        var result = BusEnvelopeIntegrityVerifier.Verify(signer, _integrityMode, BusEnvelopeCanonical.Of(bundle), bundle.Signature);
        if (result is BusEnvelopeIntegrityResult.Skipped or BusEnvelopeIntegrityResult.Verified)
        {
            return;
        }

        var firstEventId = bundle.Events.Count > 0 ? bundle.Events[0].Id : Guid.Empty;
        var eventCount = bundle.Events.Count;

        if (result == BusEnvelopeIntegrityResult.RejectedStrict)
        {
            logger.LogEventBundleIntegrityRejected(firstEventId, eventCount);
            throw new InvalidOperationException(
                $"EventBundle (first event {firstEventId}, {eventCount} events) failed integrity verification under Strict mode. " +
                "Confirm that publishers and consumers share the same BusEnvelopeIntegrityOptions.SharedKey " +
                "and that the bus is not relaying tampered messages.");
        }

        logger.LogEventBundleIntegrityWarning(firstEventId, eventCount);
    }
}
