using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
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
/// Concurrency conflicts on the consumer side are NACKed with <c>requeue=true</c>; all other
/// handler exceptions are NACKed with <c>requeue=false</c>.
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
internal sealed class RabbitMqBus(ILogger<RabbitMqBus> logger, IConfiguration configuration, IHostEnvironment hostEnvironment, IOptions<BusEnvelopeJsonOptions> envelopeOptions) : IMessageBus, IAsyncDisposable
{
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
    public async Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
    {
        var factory = CreateConnectionFactory();
        factory.AutomaticRecoveryEnabled = true;
        factory.NetworkRecoveryInterval = NetworkRecoveryInterval;
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(topic, ExchangeType.Fanout, cancellationToken: cancellationToken);

        var isClientSubscription = subscription.StartsWith("default-", StringComparison.Ordinal);
        var durable = !isClientSubscription;
        var exclusive = isClientSubscription;
        var autoDelete = isClientSubscription;

        await channel.QueueDeclareAsync(subscription, durable, exclusive, autoDelete, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(subscription, topic, string.Empty, cancellationToken: cancellationToken);

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
                logger.LogConcurrencyConflictRequeued(ce.StreamId, ce.AggregateTypeName);
                await channel.BasicNackAsync(args.DeliveryTag, false, true, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogMessageProcessingFailed(topic, e);
                await channel.BasicNackAsync(args.DeliveryTag, false, false, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(subscription, false, consumer, cancellationToken);

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

    private ConnectionFactory CreateConnectionFactory()
    {
        var host = configuration["RABBITMQ_HOST"];
        if (!string.IsNullOrEmpty(host))
        {
            var username = configuration["RABBITMQ_USERNAME"];
            var password = configuration["RABBITMQ_PASSWORD"];

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                if (hostEnvironment.IsProduction())
                {
                    throw new InvalidOperationException(
                        $"RabbitMQ credentials are missing on Production host '{hostEnvironment.EnvironmentName}' " +
                        $"(RABBITMQ_HOST='{host}', RABBITMQ_USERNAME or RABBITMQ_PASSWORD unset). " +
                        "Refusing to fall back to the default 'guest' account in Production — set both " +
                        "RABBITMQ_USERNAME and RABBITMQ_PASSWORD, or supply a 'rabbitmq' connection string.");
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