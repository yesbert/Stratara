using Stratara.Abstractions.Mediator;

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

    /// <summary>
    /// Topic carrying serialised <c>CommandEnvelope</c> messages for commands marked
    /// <see cref="IHeavyCommand"/>. Drained by a dedicated heavy-command worker so long-running
    /// commands cannot starve the interactive lane.
    /// </summary>
    string HeavyCommandTopic { get; }

    /// <summary>Subscription read by the dedicated heavy-command worker over the heavy-command topic.</summary>
    string HeavyCommandSubscription { get; }

    /// <summary>Topic used for fan-out client notifications (SignalR bridge).</summary>
    string NotificationTopic { get; }

    /// <summary>Subscription read by the saga worker over the event-bundle topic.</summary>
    string EventBundleSagaSubscription { get; }

    /// <summary>
    /// Returns the command topic a command of <paramref name="commandType"/> must be published to:
    /// <see cref="HeavyCommandTopic"/> when the type implements <see cref="IHeavyCommand"/>,
    /// otherwise <see cref="CommandTopic"/>.
    /// </summary>
    /// <param name="commandType">The runtime command type being dispatched.</param>
    /// <returns>The resolved command topic name.</returns>
    string GetCommandTopic(Type commandType) =>
        IsHeavy(commandType) ? HeavyCommandTopic : CommandTopic;

    /// <summary>
    /// Returns the command subscription that owns a command of <paramref name="commandType"/>:
    /// <see cref="HeavyCommandSubscription"/> when the type implements <see cref="IHeavyCommand"/>,
    /// otherwise <see cref="CommandSubscription"/>.
    /// </summary>
    /// <param name="commandType">The runtime command type being dispatched.</param>
    /// <returns>The resolved command subscription name.</returns>
    string GetCommandSubscription(Type commandType) =>
        IsHeavy(commandType) ? HeavyCommandSubscription : CommandSubscription;

    /// <summary>Whether <paramref name="commandType"/> is a heavy command routed to the dedicated lane.</summary>
    /// <param name="commandType">The runtime command type.</param>
    /// <returns><see langword="true"/> if the type implements <see cref="IHeavyCommand"/>.</returns>
    static bool IsHeavy(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        return typeof(IHeavyCommand).IsAssignableFrom(commandType);
    }
}
