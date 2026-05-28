using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Shared.Messaging;

/// <summary>
/// Default <see cref="IMessagingIdentifier"/> implementation. Resolves topic and subscription names
/// from the bound <see cref="MessagingOptions"/>; if a name is missing in configuration, a stable
/// fallback (e.g. <c>"command"</c>, <c>"event-bundle-subscription"</c>) is returned. Resolved names
/// are memoized on first access to avoid repeated dictionary lookups during hot-path publishing.
/// </summary>
public sealed class MessagingIdentifier(IOptions<MessagingOptions> options) : IMessagingIdentifier
{
    private readonly MessagingOptions _config = options.Value;
    private string _commandSubscription = string.Empty;
    private string _commandTopic = string.Empty;
    private string _eventBundleSubscription = string.Empty;
    private string _eventBundleSagaSubscription = string.Empty;
    private string _eventStreamTopic = string.Empty;
    private string _notificationTopic = string.Empty;


    /// <inheritdoc/>
    public string EventBundleTopic
    {
        get
        {
            if (!string.IsNullOrEmpty(_eventStreamTopic))
            {
                return _eventStreamTopic;
            }

            return _eventStreamTopic = _config.GetTopicValue("EventBundle") ?? "event-bundle";
        }
    }

    /// <inheritdoc/>
    public string EventBundleSubscription
    {
        get
        {
            if (!string.IsNullOrEmpty(_eventBundleSubscription))
            {
                return _eventBundleSubscription;
            }

            return _eventBundleSubscription = _config
                .GetSubscriptionValue("EventBundle", "EventBundleSubscription") ?? "event-bundle-subscription";
        }
    }

    /// <inheritdoc/>
    public string EventBundleSagaSubscription
    {
        get
        {
            if (!string.IsNullOrEmpty(_eventBundleSagaSubscription))
            {
                return _eventBundleSagaSubscription;
            }

            return _eventBundleSagaSubscription = _config
                .GetSubscriptionValue("EventBundle", "EventBundleSagaSubscription") ?? "event-bundle-saga-subscription";
        }
    }

    /// <inheritdoc/>
    public string CommandTopic
    {
        get
        {
            if (!string.IsNullOrEmpty(_commandTopic))
            {
                return _commandTopic;
            }

            return _commandTopic = _config.GetTopicValue("Command") ?? "command";
        }
    }

    /// <inheritdoc/>
    public string CommandSubscription
    {
        get
        {
            if (!string.IsNullOrEmpty(_commandSubscription))
            {
                return _commandSubscription;
            }

            return _commandSubscription =
                _config.GetSubscriptionValue("Command", "CommandSubscription") ?? "command-subscription";
        }
    }

    /// <inheritdoc/>
    public string NotificationTopic
    {
        get
        {
            if (!string.IsNullOrEmpty(_notificationTopic))
            {
                return _notificationTopic;
            }

            return _notificationTopic = _config.GetTopicValue("Notification") ?? "notifications";
        }
    }
}
