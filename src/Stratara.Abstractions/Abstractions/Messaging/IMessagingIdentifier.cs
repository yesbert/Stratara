namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Centralises topic + subscription names so every producer/consumer agrees on the
/// wire-level identifiers. Names typically include a per-environment prefix to keep
/// shared infrastructure isolated between dev / test / prod.
/// </summary>
public interface IMessagingIdentifier
{
    /// <summary>Topic carrying serialised <c>EventBundle</c> messages.</summary>
    string EventBundleTopic { get; }

    /// <summary>Subscription read by the projection worker.</summary>
    string EventBundleSubscription { get; }

    /// <summary>Topic carrying serialised <c>CommandEnvelope</c> messages.</summary>
    string CommandTopic { get; }

    /// <summary>Subscription read by the command-handling worker.</summary>
    string CommandSubscription { get; }

    /// <summary>Topic used for fan-out client notifications (SignalR bridge).</summary>
    string NotificationTopic { get; }

    /// <summary>Subscription read by the saga worker over the event-bundle topic.</summary>
    string EventBundleSagaSubscription { get; }
}
