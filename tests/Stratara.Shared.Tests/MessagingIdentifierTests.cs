using Microsoft.Extensions.Options;
using Moq;
using Stratara.Shared.Messaging;
using Xunit;

namespace Stratara.Shared.Tests;

public class MessagingIdentifierTests
{
    private static IOptions<MessagingOptions> CreateOptions(
        string? commandTopic = null,
        string? commandSubscription = null,
        string? eventBundleTopic = null,
        string? eventBundleSubscription = null)
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "Command",
                    Value = commandTopic,
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "CommandSubscription",
                            Value = commandSubscription ?? string.Empty
                        }
                    ]
                },
                new MessagingOptions.TopicOptions
                {
                    Name = "EventBundle",
                    Value = eventBundleTopic,
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "EventBundleSubscription",
                            Value = eventBundleSubscription ?? string.Empty
                        }
                    ]
                }
            ]
        };

        var mock = new Mock<IOptions<MessagingOptions>>();
        mock.Setup(m => m.Value).Returns(options);
        return mock.Object;
    }

    [Fact]
    public void Uses_Defaults_When_Options_Missing()
    {
        // Arrange
        var sut = new MessagingIdentifier(CreateOptions());

        // Act & Assert
        Assert.Equal("command", sut.CommandTopic);
        Assert.Equal("command-subscription", sut.CommandSubscription);
        Assert.Equal("event-bundle", sut.EventBundleTopic);
        Assert.Equal("event-bundle-subscription", sut.EventBundleSubscription);
    }

    [Fact]
    public void Caches_Values_After_First_Access()
    {
        // Arrange
        var sut = new MessagingIdentifier(CreateOptions(commandTopic: "cmd", commandSubscription: "cmd-sub", eventBundleTopic: "evt", eventBundleSubscription: "evt-sub"));

        // Act
        var firstCommandTopic = sut.CommandTopic;
        var secondCommandTopic = sut.CommandTopic;
        var firstEventTopic = sut.EventBundleTopic;
        var secondEventTopic = sut.EventBundleTopic;

        // Assert
        Assert.Same(firstCommandTopic, secondCommandTopic);
        Assert.Same(firstEventTopic, secondEventTopic);
        Assert.Equal("cmd", firstCommandTopic);
        Assert.Equal("evt", firstEventTopic);
    }
}
