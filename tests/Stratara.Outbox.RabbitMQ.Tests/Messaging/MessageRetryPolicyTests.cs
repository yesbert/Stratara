using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Messaging;

/// <summary>
/// The decision table both transports apply (design D3 of <c>dead-letter-what-a-handler-cannot-take</c>):
/// a failure is redelivered under <c>MaxDeliveryAttempts</c> and dead-lettered at it, a conflict is
/// redelivered under <c>MaxConflictRequeues</c> requeues and dead-lettered past it.
/// </summary>
public class MessageRetryPolicyTests
{
    private static MessageRetryPolicy Policy(int attempts = 3, int requeues = 100) =>
        new(new MessageRetryOptions { MaxDeliveryAttempts = attempts, MaxConflictRequeues = requeues });

    [Theory]
    [InlineData(1, MessageDisposition.Redeliver)]
    [InlineData(2, MessageDisposition.Redeliver)]
    [InlineData(3, MessageDisposition.DeadLetter)]
    [InlineData(4, MessageDisposition.DeadLetter)]
    public void Failure_IsDeadLetteredAtTheAttemptBound(int attempt, MessageDisposition expected)
    {
        Assert.Equal(expected, Policy(attempts: 3).Decide(attempt, MessageFailureKind.Failure));
    }

    [Fact]
    public void Failure_WithABoundOfOne_IsDeadLetteredOnTheFirstDelivery()
    {
        Assert.Equal(MessageDisposition.DeadLetter, Policy(attempts: 1).Decide(1, MessageFailureKind.Failure));
    }

    [Theory]
    [InlineData(1, MessageDisposition.Redeliver)]
    [InlineData(5, MessageDisposition.Redeliver)]
    [InlineData(6, MessageDisposition.DeadLetter)]
    public void Conflict_IsRedeliveredUpToTheRequeueBoundAndDeadLetteredPastIt(int attempt, MessageDisposition expected)
    {
        Assert.Equal(expected, Policy(requeues: 5).Decide(attempt, MessageFailureKind.Conflict));
    }

    [Fact]
    public void Conflict_IsNotBoundedByTheFailureBound()
    {
        Assert.Equal(MessageDisposition.Redeliver, Policy(attempts: 1, requeues: 100).Decide(50, MessageFailureKind.Conflict));
    }

    [Fact]
    public void BrokerDeliveryLimit_IsOneAboveTheLargerBound()
    {
        Assert.Equal(101, Policy(attempts: 3, requeues: 100).BrokerDeliveryLimit);
        Assert.Equal(8, Policy(attempts: 7, requeues: 2).BrokerDeliveryLimit);
    }

    [Fact]
    public void ReasonFor_NamesTheKind()
    {
        Assert.Equal("conflict", MessageRetryPolicy.ReasonFor(MessageFailureKind.Conflict));
        Assert.Equal("failure", MessageRetryPolicy.ReasonFor(MessageFailureKind.Failure));
    }

    [Fact]
    public void Options_DefaultToThreeAttemptsAndAHundredRequeues()
    {
        var options = new MessageRetryOptions();

        Assert.Equal(3, options.MaxDeliveryAttempts);
        Assert.Equal(100, options.MaxConflictRequeues);
        Assert.True(MessageRetryOptionsValidation.IsValid(options));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(3, 0)]
    [InlineData(-1, -1)]
    public void Options_WithABoundBelowOne_AreInvalid(int attempts, int requeues)
    {
        Assert.False(MessageRetryOptionsValidation.IsValid(new MessageRetryOptions { MaxDeliveryAttempts = attempts, MaxConflictRequeues = requeues }));
    }

    [Fact]
    public void AddMessaging_BindsTheBoundsFromTheMessageRetrySection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessageRetry:MaxDeliveryAttempts"] = "5",
            ["MessageRetry:MaxConflictRequeues"] = "7",
        });

        builder.AddMessaging();

        var options = builder.Services.BuildServiceProvider().GetRequiredService<IOptions<MessageRetryOptions>>().Value;
        Assert.Equal(5, options.MaxDeliveryAttempts);
        Assert.Equal(7, options.MaxConflictRequeues);
    }

    [Fact]
    public void AddMessaging_RefusesABoundOfZero()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessageRetry:MaxDeliveryAttempts"] = "0",
        });

        builder.AddMessaging();

        var ex = Assert.Throws<OptionsValidationException>(() =>
            builder.Services.BuildServiceProvider().GetRequiredService<IOptions<MessageRetryOptions>>().Value);
        Assert.Contains("MaxDeliveryAttempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAzureServiceBus_BindsTheSameSectionWhenTheHostCarriesConfiguration()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessageRetry:MaxConflictRequeues"] = "9",
        });

        builder.Services.AddAzureServiceBus("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v");

        var options = builder.Services.BuildServiceProvider().GetRequiredService<IOptions<MessageRetryOptions>>().Value;
        Assert.Equal(9, options.MaxConflictRequeues);
        Assert.Equal(3, options.MaxDeliveryAttempts);
    }

    [Fact]
    public void AddAzureServiceBus_WithoutConfiguration_KeepsTheDefaults()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBus("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v");

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MessageRetryOptions>>().Value;
        Assert.Equal(3, options.MaxDeliveryAttempts);
        Assert.Equal(100, options.MaxConflictRequeues);
    }
}
