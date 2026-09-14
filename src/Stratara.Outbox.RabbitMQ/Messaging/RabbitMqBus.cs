using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Outbox.RabbitMQ.Messaging;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IMessageBus"/>. Publishes JSON-serialized payloads
/// to fanout exchanges and exposes a subscription helper with automatic recovery, durable worker queues,
/// and auto-deleted client queues (subscriptions prefixed with <c>default-</c>).
/// </summary>
/// <remarks>
/// <para>
/// Connection settings come from configuration: either the <c>RABBITMQ_HOST</c> / <c>RABBITMQ_PORT</c> /
/// <c>RABBITMQ_USERNAME</c> / <c>RABBITMQ_PASSWORD</c> variables (Kubernetes-style) or the
/// <c>rabbitmq</c> connection string (Aspire / appsettings-style). The publisher uses
/// publisher-confirms via <c>ThrottlingRateLimiter</c> with up to 50 000 outstanding confirms.
/// </para>
/// <para>
/// A worker subscription is a quorum queue named <c>&lt;subscription&gt;.v2</c> that dead-letters,
/// through the default exchange, to the quorum queue <c>&lt;subscription&gt;.dead-letter</c>. A
/// message whose handler throws is redelivered until the bound in <see cref="MessageRetryOptions"/>
/// for its failure kind is reached — read from the delivery count the broker stamps on every
/// redelivery (<c>x-acquired-count</c> from RabbitMQ 4.3, <c>x-delivery-count</c> before) — and
/// then rejected without requeue, which moves it to the dead-letter queue. The broker's own
/// <c>x-delivery-limit</c> sits one above the larger bound as a backstop for redeliveries the
/// consumer did not ask for, such as a consumer that died mid-message. A worker queue that already
/// exists keeps the limit it was declared with: when the bounds change between deployments the
/// queue is used as it is and a warning names it, rather than the subscription failing to open.
/// The <c>.v2</c> suffix exists because a classic queue of the old name cannot be redeclared as a
/// quorum queue: old and new consumers coexist on the same exchange during a rollout, and the
/// operator deletes the old queue once it is drained.
/// </para>
/// <para>
/// A client subscription keeps the classic behaviour: a concurrency conflict is requeued, any other
/// failure is rejected and dropped, because its queue is exclusive and auto-deleting and there is
/// nobody to return a dead-lettered message to.
/// </para>
/// <para>
/// Publishes set <c>mandatory=true</c>: if no queue is bound to the target exchange at publish
/// time (startup race, subscriber outage, rolling re-deploy window) the broker returns the
/// message and the awaited <c>BasicPublishAsync</c> throws <c>PublishReturnException</c>
/// (publisher-confirm tracking is enabled on the channel). The dispatchers catch that and fall
/// back to the outbox table; <c>OutboxWorker</c> retries until a subscriber is bound.
/// </para>
/// <para>
/// Subscriptions whose name starts with <c>default-</c> are treated as transient client queues
/// (<c>durable=false, exclusive=true, autoDelete=true</c>) so they are bound to the connection
/// lifetime; worker subscriptions are <c>durable + non-exclusive</c> so multiple replicas share the
/// queue. The <c>exclusive=true</c> bit on the client path is required by RabbitMQ 4.x — the
/// <c>durable=false + exclusive=false + autoDelete=true</c> combination is rejected by default
/// (deprecated <c>transient_nonexcl_queues</c> feature).
/// </para>
/// </remarks>
internal sealed class RabbitMqBus(
    ILogger<RabbitMqBus> logger,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    IOptions<BusEnvelopeJsonOptions> envelopeOptions,
    IOptions<MessageRetryOptions> retryOptions) : IMessageBus, IAsyncDisposable
{
    internal const string WorkerQueueSuffix = ".v2";
    internal const string DeadLetterSuffix = ".dead-letter";
    private const string AcquiredCountHeader = "x-acquired-count";
    private const string DeliveryCountHeader = "x-delivery-count";
    private const string QuorumQueueType = "quorum";
    private const string DeliveryLimitArgument = "x-delivery-limit";
    private const int MaxOutstandingConfirms = 50_000;
    private static readonly TimeSpan NetworkRecoveryInterval = TimeSpan.FromSeconds(10);

    private static readonly CreateChannelOptions ChannelOpts = new(
        true,
        true,
        new ThrottlingRateLimiter(MaxOutstandingConfirms)
    );

    private static readonly BasicProperties Props = new()
    {
        Persistent = true
    };

    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly HashSet<string> _declaredExchanges = new(StringComparer.Ordinal);
    private readonly BusEnvelopeJsonOptions _envelopeOptions = envelopeOptions.Value;
    private readonly MessageRetryPolicy _retryPolicy = new(retryOptions.Value);
    private readonly JsonSerializerOptions _deserializeOptions = BusEnvelopeJsonGuard.CreateOptions(envelopeOptions.Value.MaxDepth);
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _cleanupTasks = new();
    private IConnection? _publishConnection;
    private IChannel? _publishChannel;

    /// <inheritdoc/>
    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        await EnsurePublishChannelAsync(cancellationToken);
        await EnsureExchangeDeclaredAsync(topic, cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(message);

        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            await _publishChannel!.BasicPublishAsync(topic, string.Empty, true, Props, body, cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task EnsurePublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel is { IsOpen: true })
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_publishChannel is { IsOpen: true })
            {
                return;
            }

            if (_publishChannel is not null)
            {
                try { await _publishChannel.DisposeAsync(); }
                catch (Exception ex) { logger.LogPublishChannelCleanupFailed(ex); }
            }
            if (_publishConnection is not null)
            {
                try { await _publishConnection.DisposeAsync(); }
                catch (Exception ex) { logger.LogPublishChannelCleanupFailed(ex); }
            }

            var factory = CreateConnectionFactory();
            factory.AutomaticRecoveryEnabled = true;
            factory.NetworkRecoveryInterval = NetworkRecoveryInterval;
            _publishConnection = await factory.CreateConnectionAsync(cancellationToken);
            _publishChannel = await _publishConnection.CreateChannelAsync(ChannelOpts, cancellationToken);

            lock (_declaredExchanges)
            {
                _declaredExchanges.Clear();
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureExchangeDeclaredAsync(string topic, CancellationToken cancellationToken)
    {
        lock (_declaredExchanges)
        {
            if (_declaredExchanges.Contains(topic))
            {
                return;
            }
        }

        await _publishChannel!.ExchangeDeclareAsync(topic, ExchangeType.Fanout, cancellationToken: cancellationToken);

        lock (_declaredExchanges)
        {
            _declaredExchanges.Add(topic);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Drain subscription-cleanup tasks first so subscribers finish their channel.DisposeAsync
        // before the publish channel goes away. Round-3-Audit Finding R3-Sec-010: previously the
        // cleanup tasks were fire-and-forget Task.Run, allowing host shutdown to race with an
        // in-flight ReceivedAsync handler and either drop messages or surface secondary exceptions.
        try
        {
            await Task.WhenAll(_cleanupTasks);
        }
        catch (Exception ex)
        {
            logger.LogSubscriptionCleanupFailed("subscription-cleanup-drain", ex);
        }

        try
        {
            if (_publishChannel is not null) { await _publishChannel.DisposeAsync(); }
            if (_publishConnection is not null) { await _publishConnection.DisposeAsync(); }
        }
        catch (Exception ex)
        {
            logger.LogSubscriptionCleanupFailed("publish-channel", ex);
        }
        _publishLock.Dispose();
        _initLock.Dispose();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="subscription"/> is a client subscription, whose queue cannot outlive the
    /// channel that declares it.
    /// </exception>
    public async Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default)
    {
        if (IsClientSubscription(subscription))
        {
            throw new InvalidOperationException(
                $"Cannot establish '{subscription}' ahead of its consumer: a client subscription is declared " +
                "exclusive and auto-deleting, so its queue is removed the moment the declaring channel closes. " +
                "Establishing it early would look successful and retain nothing. Only a durable worker " +
                "subscription can be established before its handler attaches.");
        }

        var factory = CreateConnectionFactory();
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await DeclareAndBindAsync(connection, channel, topic, subscription, cancellationToken);
    }

    private static bool IsClientSubscription(string subscription) =>
        subscription.StartsWith("default-", StringComparison.Ordinal);

    /// <summary>The queue a worker subscription consumes from.</summary>
    internal static string WorkerQueueName(string subscription) => subscription + WorkerQueueSuffix;

    /// <summary>The queue a worker subscription's dead-lettered messages end on.</summary>
    internal static string DeadLetterQueueName(string subscription) => subscription + DeadLetterSuffix;

    private static string QueueName(string subscription) =>
        IsClientSubscription(subscription) ? subscription : WorkerQueueName(subscription);

    // Establishing and subscribing must declare the same queue with the same arguments: RabbitMQ
    // rejects a redeclaration whose properties differ, so a drift here would surface as a channel
    // error on whichever path ran second. The one difference tolerated is the delivery limit an
    // earlier deployment's bounds declared; see DeclareWorkerQueueAsync.
    private async Task DeclareAndBindAsync(IConnection connection, IChannel channel, string topic, string subscription, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(topic, ExchangeType.Fanout, cancellationToken: cancellationToken);

        if (IsClientSubscription(subscription))
        {
            await channel.QueueDeclareAsync(subscription, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(subscription, topic, string.Empty, cancellationToken: cancellationToken);
            return;
        }

        var deadLetterQueue = DeadLetterQueueName(subscription);
        await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: QuorumQueueArguments(), cancellationToken: cancellationToken);

        var queue = WorkerQueueName(subscription);
        await DeclareWorkerQueueAsync(connection, queue, deadLetterQueue, subscription, cancellationToken);
        await channel.QueueBindAsync(queue, topic, string.Empty, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Declares the worker queue on a channel of its own, because a declaration the broker refuses
    /// closes the channel it arrived on. The refusal that is expected is an existing queue whose
    /// <c>x-delivery-limit</c> came from the retry bounds of an earlier deployment: the broker
    /// compares that argument on every redeclaration and a quorum queue cannot change it in place.
    /// Such a queue is used as it is. The framework's bounds still decide every redelivery a
    /// consumer asks for, and on RabbitMQ 4.3 and later the limit counts no other kind; before 4.3 it
    /// counts every redelivery, so a bound raised above the old limit is cut short by the broker until
    /// the drained queue is deleted and declared again. The warning names the queue for that step.
    /// Any other refusal still fails the declaration: a queue of that name with another type or
    /// another dead-letter route would lose the messages a handler cannot take.
    /// </summary>
    private async Task DeclareWorkerQueueAsync(IConnection connection, string queue, string deadLetterQueue, string subscription, CancellationToken cancellationToken)
    {
        var declaring = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            await declaring.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: WorkerQueueArguments(deadLetterQueue), cancellationToken: cancellationToken);
            return;
        }
        catch (OperationInterruptedException refused) when (IsDeliveryLimitMismatch(refused))
        {
            logger.LogWorkerQueueDeclaredWithOtherArguments(subscription, queue, _retryPolicy.BrokerDeliveryLimit, refused.ShutdownReason!.ReplyText);
        }
        finally
        {
            await declaring.DisposeAsync();
        }

        // The queue exists, or the refusal would have been a different one; a passive declaration
        // confirms it without comparing arguments, and fails loudly if it was deleted in between.
        await using var confirming = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await confirming.QueueDeclarePassiveAsync(queue, cancellationToken);
    }

    /// <summary>
    /// The broker names the argument it compared in its refusal
    /// (<c>inequivalent arg 'x-delivery-limit' for queue …</c>). Should that wording ever change, the
    /// refusal is no longer recognised and the declaration fails as it did before — loudly, not
    /// silently.
    /// </summary>
    private static bool IsDeliveryLimitMismatch(OperationInterruptedException refused) =>
        refused.ShutdownReason is { ReplyCode: Constants.PreconditionFailed } reason
        && reason.ReplyText.Contains("'" + DeliveryLimitArgument + "'", StringComparison.Ordinal);

    private static Dictionary<string, object?> QuorumQueueArguments() => new(StringComparer.Ordinal)
    {
        ["x-queue-type"] = QuorumQueueType,
    };

    /// <summary>
    /// The worker queue dead-letters through the default exchange straight to its dead-letter queue,
    /// so the arguments depend on the subscription alone — a subscription bound to two topics would
    /// otherwise fail its second declaration, because the dead-letter exchange is part of what the
    /// broker compares. At-least-once dead-lettering keeps the message in the worker queue until the
    /// dead-letter queue has confirmed it; that strategy requires <c>x-overflow: reject-publish</c>.
    /// </summary>
    private Dictionary<string, object?> WorkerQueueArguments(string deadLetterQueue) => new(StringComparer.Ordinal)
    {
        ["x-queue-type"] = QuorumQueueType,
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-dead-letter-routing-key"] = deadLetterQueue,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish",
        [DeliveryLimitArgument] = _retryPolicy.BrokerDeliveryLimit,
    };

    /// <summary>
    /// How many times the message has been delivered, counting this one. RabbitMQ 4.3 and later
    /// stamp <c>x-acquired-count</c> with the number of earlier deliveries on every redelivery and
    /// increment <c>x-delivery-count</c> only for a redelivery the consumer did not ask for; earlier
    /// versions have only the latter, which counts every requeue. The first present header wins.
    /// A delivery the broker does not flag as redelivered is a first delivery whatever headers it
    /// carries — a message an operator returned from the dead-letter queue arrives that way, with
    /// the count of its earlier life still on it, and starts over.
    /// </summary>
    private static int DeliveryAttempt(BasicDeliverEventArgs args)
    {
        if (!args.Redelivered || args.BasicProperties.Headers is not { } headers)
        {
            return 1;
        }

        if (!headers.TryGetValue(AcquiredCountHeader, out var earlier))
        {
            headers.TryGetValue(DeliveryCountHeader, out earlier);
        }

        return earlier switch
        {
            long count => (int)count + 1,
            int count => count + 1,
            _ => 1,
        };
    }

    /// <summary>
    /// Records a conflict once the message is actually back on the queue, so a delivery that ends on
    /// the dead-letter queue is logged as dead-lettered only, not first as requeued.
    /// </summary>
    private void LogConflictRequeued(Exception cause)
    {
        if (cause is ConcurrencyException conflict)
        {
            logger.LogConcurrencyConflictRequeued(conflict.StreamId, conflict.AggregateTypeName);
        }
    }

    private async Task SettleFailedAsync(IChannel channel, BasicDeliverEventArgs args, string topic, string subscription, MessageFailureKind kind, Exception cause, CancellationToken cancellationToken)
    {
        if (IsClientSubscription(subscription))
        {
            var requeue = kind == MessageFailureKind.Conflict;
            await channel.BasicNackAsync(args.DeliveryTag, false, requeue, cancellationToken);
            if (requeue)
            {
                LogConflictRequeued(cause);
            }

            return;
        }

        var attempt = DeliveryAttempt(args);
        if (_retryPolicy.Decide(attempt, kind) == MessageDisposition.Redeliver)
        {
            await channel.BasicNackAsync(args.DeliveryTag, false, true, cancellationToken);
            LogConflictRequeued(cause);
            return;
        }

        await channel.BasicNackAsync(args.DeliveryTag, false, false, cancellationToken);
        var reason = MessageRetryPolicy.ReasonFor(kind);
        logger.LogMessageDeadLettered(topic, subscription, reason, attempt);
        ApplicationDiagnostics.Metrics.MessagesDeadLettered.Add(1,
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Topic, topic),
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Subscription, subscription),
            new KeyValuePair<string, object?>(ApplicationDiagnostics.MetricTags.Reason, reason));
    }

    /// <inheritdoc/>
    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
    {
        var factory = CreateConnectionFactory();
        factory.AutomaticRecoveryEnabled = true;
        factory.NetworkRecoveryInterval = NetworkRecoveryInterval;
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await DeclareAndBindAsync(connection, channel, topic, subscription, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                var body = args.Body.ToArray();
                BusEnvelopeJsonGuard.EnsureWithinSizeLimit(body.Length, _envelopeOptions.MaxBodyBytes, topic);
                var message = JsonSerializer.Deserialize<T>(body, _deserializeOptions);
                if (message is not null)
                {
                    await handler(message);
                }

                await channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
            }
            catch (ConcurrencyException ce)
            {
                await SettleFailedAsync(channel, args, topic, subscription, MessageFailureKind.Conflict, ce, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogMessageProcessingFailed(topic, e);
                await SettleFailedAsync(channel, args, topic, subscription, MessageFailureKind.Failure, e, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(QueueName(subscription), false, consumer, cancellationToken);

        cancellationToken.Register(() =>
        {
            // Track the cleanup task so DisposeAsync can await it during graceful host shutdown
            // (Round-3-Audit Finding R3-Sec-010) — fire-and-forget Task.Run let the host tear
            // down the bus while a subscription was still mid-cleanup.
            var cleanup = Task.Run(async () =>
            {
                try
                {
                    logger.LogSubscriptionCleanup(subscription);
                    await channel.DisposeAsync();
                    await connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogSubscriptionCleanupFailed(subscription, ex);
                }
            });
            _cleanupTasks.Add(cleanup);
        });
    }

    [SuppressMessage("Major Code Smell", "S2068:Hard-coded credentials are security-sensitive",
        Justification = "The two matches are prose inside the exception thrown when credentials are missing " +
                        "outside Development. It names RABBITMQ_USERNAME and RABBITMQ_PASSWORD so an operator " +
                        "knows what to set, and spells out RABBITMQ_PASSWORD=guest as the way to opt into the " +
                        "guest account deliberately. Neither literal is assigned to anything; the credentials " +
                        "themselves are read from configuration.")]
    private ConnectionFactory CreateConnectionFactory()
    {
        var host = configuration["RABBITMQ_HOST"];
        if (!string.IsNullOrEmpty(host))
        {
            var username = configuration["RABBITMQ_USERNAME"];
            var password = configuration["RABBITMQ_PASSWORD"];

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                if (!hostEnvironment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"RabbitMQ credentials are missing on host '{hostEnvironment.EnvironmentName}' " +
                        $"(RABBITMQ_HOST='{host}', RABBITMQ_USERNAME or RABBITMQ_PASSWORD unset). " +
                        "The default 'guest' account is only fallen back to in Development — every other " +
                        "environment name, including Production, Staging and any custom one, must set both " +
                        "RABBITMQ_USERNAME and RABBITMQ_PASSWORD, or supply a 'rabbitmq' connection string. " +
                        "To use 'guest' deliberately outside Development, set RABBITMQ_USERNAME=guest and " +
                        "RABBITMQ_PASSWORD=guest explicitly.");
                }
                logger.LogRabbitMqGuestFallback(host);
                username ??= "guest";
                password ??= "guest";
            }

            return new ConnectionFactory
            {
                HostName = host,
                Port = int.TryParse(configuration["RABBITMQ_PORT"], out var port) ? port : 5672,
                UserName = username,
                Password = password,
            };
        }

        var connectionString = configuration.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("RabbitMQ connection-string 'rabbitmq' is not configured.");

        try
        {
            return new ConnectionFactory { Uri = new Uri(connectionString) };
        }
        catch (UriFormatException)
        {
            // Avoid echoing the offending connection-string (which can contain credentials) into the
            // exception message that propagates to OTel exception-recorders and the host logger.
            throw new InvalidOperationException(
                "RabbitMQ connection-string 'rabbitmq' is malformed. Expected an amqp:// URI; verify the configuration value.");
        }
    }
}