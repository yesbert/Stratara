using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Mediator;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Partitioning;
using Stratara.Resilience;

namespace Stratara.Outbox.RabbitMQ.Mediator;

/// <summary>
/// Hosted background service that subscribes to the configured command topic, deserializes
/// incoming <c>CommandEnvelope</c>s, restores the session context, and dispatches each command
/// through <c>IMediator</c>. Commands implementing <c>IAggregateScopedCommand</c> are serialized
/// per bucket via <c>BucketLockPool</c> so that concurrent
/// dispatches to the same aggregate do not race.
/// </summary>
/// <remarks>
/// The worker runs <see cref="Environment.ProcessorCount"/> parallel subscriptions, each wrapped
/// in the <c>MessageBus</c> resilience pipeline. Failures inside <c>DispatchAsync</c> propagate to
/// the subscription handler, which is NACKed by the <see cref="Stratara.Abstractions.Messaging.IMessageBus"/>
/// implementation (with concurrency conflicts requeued and other errors dead-lettered).
/// </remarks>
[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "DI-resolved sealed internal worker; primary-constructor parameters reflect intrinsic " +
                    "framework dependencies (logger, bus, scope, pipeline, resolver, serializer, envelope options, " +
                    "integrity options, signer) and are not a hand-called API surface.")]
internal sealed class MediatorCommandWorker(
    ILogger<MediatorCommandWorker> logger,
    IMessageBus messageBus,
    IMessagingIdentifier messagingIdentifier,
    IServiceScopeFactory scopeFactory,
    ResiliencePipelineProvider<string> pipelineProvider,
    ITrustedTypeResolver typeResolver,
    ISecureJsonSerializer serializer,
    IOptions<BusEnvelopeJsonOptions> envelopeOptions,
    IOptions<BusEnvelopeIntegrityOptions> integrityOptions,
    IBusEnvelopeSigner? signer = null) : BackgroundService
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceNames.MessageBus);
    private readonly BucketLockPool _bucketLockPool = new();
    private readonly ITrustedTypeResolver _typeResolver = typeResolver;
    private readonly ISecureJsonSerializer _serializer = serializer;
    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly BusEnvelopeIntegrityMode _integrityMode = integrityOptions.Value.Mode;

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogCommandWorkerStarted();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogCommandWorkerStopped();
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
            await messageBus.SubscribeAsync<CommandEnvelope>(
                messagingIdentifier.CommandTopic,
                messagingIdentifier.CommandSubscription,
                async commandEnvelope => await DispatchAsync(commandEnvelope, ct),
                ct);
        }, stoppingToken);
    }

    internal async Task DispatchAsync(CommandEnvelope commandEnvelope, CancellationToken cancellationToken)
    {
        var type = GetCommandType(commandEnvelope.CommandTypeName);
        BusEnvelopeJsonGuard.EnsureWithinSizeLimit(Encoding.UTF8.GetByteCount(commandEnvelope.SessionContextJson), _envelopeOptions.MaxBodyBytes, "SessionContextJson");
        VerifyEnvelopeIntegrity(commandEnvelope);
        var sessionContext = JsonSerializer.Deserialize<SessionContext>(commandEnvelope.SessionContextJson, _deserializeOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session context from command envelope.");
        var command = await _serializer.DeserializeAsync(commandEnvelope.CommandJson, type, sessionContext.TenantId, sessionContext.ActorUserId, cancellationToken)
            ?? throw new InvalidOperationException($"Failed to deserialize command of type {type.FullName}.");

        if (command is IAggregateScopedCommand scoped)
        {
            var bucketId = BucketCalculator.GetBucketId(scoped.AggregateId);
            using var releaser = await _bucketLockPool.AcquireAsync(bucketId, cancellationToken);
            await ExecuteHandlerAsync(command, sessionContext, cancellationToken);
        }
        else
        {
            await ExecuteHandlerAsync(command, sessionContext, cancellationToken);
        }
    }

    private async Task ExecuteHandlerAsync(object command, SessionContext sessionContext, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var sessionContextProvider = scope.ServiceProvider.GetRequiredService<ISessionContextProvider>();
        sessionContextProvider.Set(sessionContext);

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.HandleAsync((dynamic)command, cancellationToken);
    }

    private Type GetCommandType(string typeName) => _typeResolver.Resolve(typeName);

    private void VerifyEnvelopeIntegrity(CommandEnvelope envelope)
    {
        var result = BusEnvelopeIntegrityVerifier.Verify(signer, _integrityMode, BusEnvelopeCanonical.Of(envelope), envelope.Signature);
        if (result is BusEnvelopeIntegrityResult.Skipped or BusEnvelopeIntegrityResult.Verified)
        {
            return;
        }

        if (result == BusEnvelopeIntegrityResult.RejectedStrict)
        {
            logger.LogCommandEnvelopeIntegrityRejected(envelope.Id);
            throw new InvalidOperationException(
                $"CommandEnvelope {envelope.Id} failed integrity verification under Strict mode. " +
                "Confirm that publishers and consumers share the same BusEnvelopeIntegrityOptions.SharedKey " +
                "and that the bus is not relaying tampered messages.");
        }

        logger.LogCommandEnvelopeIntegrityWarning(envelope.Id);
    }
}
