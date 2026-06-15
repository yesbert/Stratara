using Stratara.Abstractions.Messaging;

namespace Stratara.Outbox.RabbitMQ.Mediator;

/// <summary>
/// Configures which command lane a <see cref="MediatorCommandWorker"/> instance drains. The
/// interactive lane consumes the default command topic/subscription; the heavy lane consumes the
/// dedicated heavy-command topic/subscription so long-running commands cannot starve interactive
/// ones. The degree of parallelism (number of concurrent subscriptions) is configurable per lane.
/// </summary>
/// <param name="Heavy">
/// <see langword="true"/> to drain the heavy-command lane (<see cref="IMessagingIdentifier.HeavyCommandTopic"/> /
/// <see cref="IMessagingIdentifier.HeavyCommandSubscription"/>); <see langword="false"/> for the
/// interactive lane (<see cref="IMessagingIdentifier.CommandTopic"/> / <see cref="IMessagingIdentifier.CommandSubscription"/>).
/// </param>
/// <param name="DegreeOfParallelism">
/// Number of concurrent subscriptions the worker opens; <see langword="null"/> defaults to
/// <see cref="Environment.ProcessorCount"/>. Cap the heavy lane low (e.g. 1–2) to bound the
/// resources slow commands can consume.
/// </param>
internal sealed record CommandWorkerLane(bool Heavy, int? DegreeOfParallelism = null)
{
    /// <summary>The default interactive lane (default command topic/subscription, processor-count parallelism).</summary>
    public static CommandWorkerLane Interactive { get; } = new(Heavy: false);

    /// <summary>Resolves the topic this lane consumes from the messaging identifier.</summary>
    /// <param name="messagingIdentifier">The configured messaging identifier.</param>
    /// <returns>The heavy or interactive command topic.</returns>
    public string ResolveTopic(IMessagingIdentifier messagingIdentifier) =>
        Heavy ? messagingIdentifier.HeavyCommandTopic : messagingIdentifier.CommandTopic;

    /// <summary>Resolves the subscription this lane consumes from the messaging identifier.</summary>
    /// <param name="messagingIdentifier">The configured messaging identifier.</param>
    /// <returns>The heavy or interactive command subscription.</returns>
    public string ResolveSubscription(IMessagingIdentifier messagingIdentifier) =>
        Heavy ? messagingIdentifier.HeavyCommandSubscription : messagingIdentifier.CommandSubscription;

    /// <summary>The effective number of concurrent subscriptions for this lane.</summary>
    public int EffectiveDegreeOfParallelism => DegreeOfParallelism is > 0 ? DegreeOfParallelism.Value : Environment.ProcessorCount;
}
