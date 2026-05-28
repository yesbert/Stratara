using Stratara.Shared.Messaging;

namespace Stratara.Shared.Tests.Messaging;

public class MessagingOptionsTests
{
    [Fact]
    public void GetTopic_ExistingTopic_ReturnsTopic()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions { Name = "my-topic", Value = "my-value" }
            ]
        };

        var topic = options.GetTopic("my-topic");

        Assert.NotNull(topic);
        Assert.Equal("my-value", topic.Value);
    }

    [Fact]
    public void GetTopic_NonExistingTopic_ReturnsNull()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions { Name = "other-topic", Value = "value" }
            ]
        };

        var topic = options.GetTopic("non-existing");

        Assert.Null(topic);
    }

    [Fact]
    public void GetTopic_CaseInsensitive_ReturnsTopic()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions { Name = "My-Topic", Value = "found" }
            ]
        };

        var topic = options.GetTopic("my-topic");

        Assert.NotNull(topic);
        Assert.Equal("found", topic.Value);
    }

    [Fact]
    public void GetTopicValue_ExistingTopic_ReturnsValue()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions { Name = "topic", Value = "the-value" }
            ]
        };

        Assert.Equal("the-value", options.GetTopicValue("topic"));
    }

    [Fact]
    public void GetTopicValue_NonExistingTopic_ReturnsNull()
    {
        var options = new MessagingOptions();

        Assert.Null(options.GetTopicValue("non-existing"));
    }

    [Fact]
    public void GetTopicValue_WhitespaceValue_ReturnsNull()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions { Name = "topic", Value = "   " }
            ]
        };

        Assert.Null(options.GetTopicValue("topic"));
    }

    [Fact]
    public void GetSubscription_ExistingSubscription_ReturnsSubscription()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "topic",
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "sub",
                            Value = "sub-value"
                        }
                    ]
                }
            ]
        };

        var sub = options.GetSubscription("topic", "sub");

        Assert.NotNull(sub);
        Assert.Equal("sub-value", sub.Value);
    }

    [Fact]
    public void GetSubscription_NonExistingTopic_ReturnsNull()
    {
        var options = new MessagingOptions();

        Assert.Null(options.GetSubscription("no-topic", "sub"));
    }

    [Fact]
    public void GetSubscription_NonExistingSubscription_ReturnsNull()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "topic",
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "other-sub",
                            Value = "value"
                        }
                    ]
                }
            ]
        };

        Assert.Null(options.GetSubscription("topic", "non-existing"));
    }

    [Fact]
    public void GetSubscriptionValue_ExistingSubscription_ReturnsValue()
    {
        var options = new MessagingOptions
        {
            Topics =
            [
                new MessagingOptions.TopicOptions
                {
                    Name = "topic",
                    Subscriptions =
                    [
                        new MessagingOptions.TopicOptions.SubscriptionOptions
                        {
                            Name = "sub",
                            Value = "sub-value"
                        }
                    ]
                }
            ]
        };

        Assert.Equal("sub-value", options.GetSubscriptionValue("topic", "sub"));
    }

    [Fact]
    public void GetSubscriptionValue_NonExisting_ReturnsNull()
    {
        var options = new MessagingOptions();

        Assert.Null(options.GetSubscriptionValue("topic", "sub"));
    }

    [Fact]
    public void SectionName_IsMessaging()
    {
        Assert.Equal("Messaging", MessagingOptions.SectionName);
    }

    [Fact]
    public void DefaultTopics_IsEmptyArray()
    {
        var options = new MessagingOptions();

        Assert.Empty(options.Topics);
    }
}
