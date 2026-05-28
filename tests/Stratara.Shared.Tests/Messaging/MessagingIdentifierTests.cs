using Microsoft.Extensions.Options;
using Stratara.Shared.Messaging;

namespace Stratara.Shared.Tests.Messaging;

public class MessagingIdentifierTests
{
    [Fact]
    public void EventBundleTopic_NoConfig_ReturnsDefault()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("event-bundle", identifier.EventBundleTopic);
    }

    [Fact]
    public void EventBundleSubscription_NoConfig_ReturnsDefault()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("event-bundle-subscription", identifier.EventBundleSubscription);
    }

    [Fact]
    public void CommandTopic_NoConfig_ReturnsDefault()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("command", identifier.CommandTopic);
    }

    [Fact]
    public void CommandSubscription_NoConfig_ReturnsDefault()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("command-subscription", identifier.CommandSubscription);
    }

    [Fact]
    public void NotificationTopic_NoConfig_ReturnsDefault()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("notifications", identifier.NotificationTopic);
    }

    [Fact]
    public void EventBundleTopic_WithConfig_ReturnsConfigured()
    {
        var options = Options.Create(new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "EventBundle",
                    Value = "custom-events"
                }
            ]
        });
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("custom-events", identifier.EventBundleTopic);
    }

    [Fact]
    public void CommandTopic_WithConfig_ReturnsConfigured()
    {
        var options = Options.Create(new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "Command",
                    Value = "custom-commands"
                }
            ]
        });
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("custom-commands", identifier.CommandTopic);
    }

    [Fact]
    public void CommandSubscription_WithConfig_ReturnsConfigured()
    {
        var options = Options.Create(new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "Command",
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "CommandSubscription",
                            Value = "custom-sub"
                        }
                    ]
                }
            ]
        });
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("custom-sub", identifier.CommandSubscription);
    }

    [Fact]
    public void NotificationTopic_WithConfig_ReturnsConfigured()
    {
        var options = Options.Create(new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "Notification",
                    Value = "custom-notifications"
                }
            ]
        });
        var identifier = new MessagingIdentifier(options);

        Assert.Equal("custom-notifications", identifier.NotificationTopic);
    }

    [Fact]
    public void Properties_AreCached_ReturnsSameValueOnRepeatedAccess()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        var first = identifier.EventBundleTopic;
        var second = identifier.EventBundleTopic;

        Assert.Same(first, second);
    }

    [Fact]
    public void EventBundleSubscription_CachedOnSecondAccess()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        var first = identifier.EventBundleSubscription;
        var second = identifier.EventBundleSubscription;

        Assert.Same(first, second);
    }

    [Fact]
    public void CommandSubscription_CachedOnSecondAccess()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        var first = identifier.CommandSubscription;
        var second = identifier.CommandSubscription;

        Assert.Same(first, second);
    }

    [Fact]
    public void NotificationTopic_CachedOnSecondAccess()
    {
        var options = Options.Create(new MessagingOptions());
        var identifier = new MessagingIdentifier(options);

        var first = identifier.NotificationTopic;
        var second = identifier.NotificationTopic;

        Assert.Same(first, second);
    }
}
