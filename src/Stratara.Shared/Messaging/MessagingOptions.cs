namespace Stratara.Shared.Messaging;

/// <summary>
/// Strongly-typed options object bound to the <c>Messaging</c> configuration section. Holds the
/// per-environment topic and subscription names used by the outbox / message bus, looked up via
/// case-insensitive name matching.
/// </summary>
public sealed class MessagingOptions
{
    /// <summary>Configuration section name (<c>Messaging</c>) that hosts these options.</summary>
    public const string SectionName = "Messaging";

    /// <summary>Configured topics, each carrying its own resolved <c>Value</c> plus per-subscription overrides.</summary>
    public TopicOptions[] Topics { get; set; } = [];

    /// <summary>
    /// Returns the <see cref="TopicOptions"/> entry whose <see cref="TopicOptions.Name"/> matches
    /// <paramref name="name"/> case-insensitively, or <see langword="null"/> if absent.
    /// </summary>
    /// <param name="name">Logical topic name (e.g. <c>"Command"</c>, <c>"EventBundle"</c>).</param>
    /// <returns>The matching topic entry or <see langword="null"/>.</returns>
    public TopicOptions? GetTopic(string name) =>
        Topics.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the resolved topic value for <paramref name="name"/>, or <see langword="null"/> if
    /// the topic is missing or its value is blank.
    /// </summary>
    /// <param name="name">Logical topic name.</param>
    /// <returns>The configured topic value or <see langword="null"/>.</returns>
    public string? GetTopicValue(string name)
    {
        var value = GetTopic(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Returns the <see cref="TopicOptions.SubscriptionOptions"/> for a named subscription on a
    /// named topic, or <see langword="null"/> if either is absent.
    /// </summary>
    /// <param name="topicName">Logical topic name.</param>
    /// <param name="subscriptionName">Logical subscription name.</param>
    /// <returns>The matching subscription entry or <see langword="null"/>.</returns>
    public TopicOptions.SubscriptionOptions? GetSubscription(string topicName, string subscriptionName) =>
        GetTopic(topicName)?
            .Subscriptions.FirstOrDefault(s => s.Name.Equals(subscriptionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the resolved subscription value for the given topic / subscription pair, or
    /// <see langword="null"/> if either is missing or the value is blank.
    /// </summary>
    /// <param name="topicName">Logical topic name.</param>
    /// <param name="subscriptionName">Logical subscription name.</param>
    /// <returns>The configured subscription value or <see langword="null"/>.</returns>
    public string? GetSubscriptionValue(string topicName, string subscriptionName)
    {
        var value = GetSubscription(topicName, subscriptionName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Configuration node describing a logical topic: its display name, resolved transport value,
    /// and per-subscription overrides.
    /// </summary>
    public sealed class TopicOptions
    {
        /// <summary>Logical topic name as referenced from <see cref="MessagingOptions.GetTopic(string)"/>.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Transport-level topic value (e.g. RabbitMQ exchange / queue name).</summary>
        public string? Value { get; set; } = string.Empty;

        /// <summary>Per-subscription overrides scoped to this topic.</summary>
        public SubscriptionOptions[] Subscriptions { get; set; } = [];

        /// <summary>Configuration node for a logical subscription beneath a <see cref="TopicOptions"/>.</summary>
        public sealed class SubscriptionOptions
        {
            /// <summary>Logical subscription name as referenced from <see cref="MessagingOptions.GetSubscription"/>.</summary>
            public string Name { get; set; } = string.Empty;

            /// <summary>Transport-level subscription value.</summary>
            public string Value { get; set; } = string.Empty;
        }
    }
}
