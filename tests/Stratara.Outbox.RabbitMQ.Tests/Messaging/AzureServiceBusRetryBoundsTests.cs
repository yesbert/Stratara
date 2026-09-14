using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;
using Stratara.Outbox.AzureServiceBus.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Messaging;

/// <summary>
/// <c>outbox-and-messaging</c> → <em>A message a handler cannot take is retried a bounded number of
/// times and then kept</em>, on Azure Service Bus: a subscription whose own <c>MaxDeliveryCount</c>
/// is below the configured bounds would dead-letter under the broker's reason, outside the
/// framework's log and counter; where the subscription can be read, the bounds are lowered to fit.
/// </summary>
public sealed class AzureServiceBusRetryBoundsTests
{
    private const string Topic = "event-bundle-topic";
    private const string Subscription = "event-bundle-subscription";

    private readonly FakeLogger<AzureServiceBusBus> _logger = new();
    private readonly Mock<ServiceBusAdministrationClient> _administration = new();

    private AzureServiceBusBus CreateBus(ServiceBusAdministrationClient? administration, MessageRetryOptions? retry = null) =>
        new(_logger, Mock.Of<ServiceBusClient>(), Options.Create(new BusEnvelopeJsonOptions()), Options.Create(retry ?? new MessageRetryOptions()), administration);

    private void SubscriptionAllows(int maxDeliveryCount) =>
        _administration
            .Setup(a => a.GetSubscriptionAsync(Topic, Subscription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                ServiceBusModelFactory.SubscriptionProperties(
                    Topic, Subscription,
                    lockDuration: TimeSpan.FromMinutes(1),
                    requiresSession: false,
                    defaultMessageTimeToLive: TimeSpan.MaxValue,
                    autoDeleteOnIdle: TimeSpan.MaxValue,
                    deadLetteringOnMessageExpiration: false,
                    maxDeliveryCount: maxDeliveryCount,
                    enableBatchedOperations: true,
                    status: EntityStatus.Active,
                    forwardTo: null,
                    forwardDeadLetteredMessagesTo: null,
                    userMetadata: string.Empty),
                Mock.Of<Response>()));

    [Fact]
    public async Task ASubscriptionWithServiceBusDefaults_LowersTheConflictBoundUnderItsLimit_AndWarns()
    {
        SubscriptionAllows(10);
        var bus = CreateBus(_administration.Object);

        var policy = await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(9, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(10, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(2, MessageFailureKind.Failure));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(3, MessageFailureKind.Failure));
        var warning = Assert.Single(_logger.Collector.GetSnapshot(), r => r.Id.Id == LogEvents.Messaging.BrokerDeliveryLimitBelowBounds);
        Assert.Contains(Subscription, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALimitBelowTheAttemptBound_LowersThatBoundToTheLimit()
    {
        SubscriptionAllows(2);
        var bus = CreateBus(_administration.Object, new MessageRetryOptions { MaxDeliveryAttempts = 5, MaxConflictRequeues = 5 });

        var policy = await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(1, MessageFailureKind.Failure));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(2, MessageFailureKind.Failure));
        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(1, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(2, MessageFailureKind.Conflict));
    }

    [Fact]
    public async Task ASubscriptionThatLeavesRoom_KeepsTheConfiguredBounds_WithoutAWarning()
    {
        SubscriptionAllows(101);
        var bus = CreateBus(_administration.Object);

        var policy = await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(100, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(101, MessageFailureKind.Conflict));
        Assert.Empty(_logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task TheSubscriptionIsReadOnce_PerTopicAndSubscription()
    {
        SubscriptionAllows(10);
        var bus = CreateBus(_administration.Object);

        await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);
        await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        _administration.Verify(a => a.GetSubscriptionAsync(Topic, Subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<Exception> RefusedReads => new()
    {
        new RequestFailedException(403, "Manage rights required"),
        new UnauthorizedAccessException("no claim"),
        new HttpRequestException("management endpoint unreachable"),
        new TimeoutException("management endpoint slow"),
    };

    [Theory]
    [MemberData(nameof(RefusedReads), DisableDiscoveryEnumeration = true)]
    public async Task AReadThatFails_EndsTheCheckNotTheSubscription_AndTheConfiguredBoundsApply(Exception refusal)
    {
        _administration
            .Setup(a => a.GetSubscriptionAsync(Topic, Subscription, It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);
        var bus = CreateBus(_administration.Object);

        var policy = await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(100, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(101, MessageFailureKind.Conflict));
    }

    [Fact]
    public async Task ACancelledRead_IsNotSwallowed()
    {
        _administration
            .Setup(a => a.GetSubscriptionAsync(Topic, Subscription, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var bus = CreateBus(_administration.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutAnAdministrationClient_TheConfiguredBoundsApply()
    {
        var bus = CreateBus(administration: null);

        var policy = await bus.ResolveRetryPolicyAsync(Topic, Subscription, CancellationToken.None);

        Assert.Equal(MessageDisposition.Redeliver, policy.Decide(100, MessageFailureKind.Conflict));
        Assert.Equal(MessageDisposition.DeadLetter, policy.Decide(101, MessageFailureKind.Conflict));
    }
}
