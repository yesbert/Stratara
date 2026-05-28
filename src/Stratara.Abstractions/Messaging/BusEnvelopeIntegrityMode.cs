namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Controls how <see cref="IBusEnvelopeSigner"/>-based integrity protection is enforced on
/// inbound bus envelopes. See <see cref="BusEnvelopeIntegrityOptions"/> for binding.
/// </summary>
public enum BusEnvelopeIntegrityMode
{
    /// <summary>
    /// No signing on publish, no verification on consume. Default in 3.x. Hosts opt in by
    /// calling <c>AddBusEnvelopeIntegrity(...)</c> and setting a non-<c>Off</c> mode.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Publishers attach a signature to every outbound envelope, but consumers accept
    /// unsigned envelopes for a rolling deployment window. Signature failures are logged
    /// at warning level and otherwise tolerated. Recommended bootstrap mode when retrofitting
    /// integrity to an existing fleet.
    /// </summary>
    Permissive = 1,

    /// <summary>
    /// Publishers attach a signature and consumers reject any envelope whose signature is
    /// missing or invalid. The dispatch throws <see cref="System.InvalidOperationException"/>;
    /// the message-bus implementation routes the rejection through its standard error path
    /// (dead-letter on Azure Service Bus, NACK-discard on RabbitMQ).
    /// </summary>
    Strict = 2,
}
