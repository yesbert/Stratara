namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Why an inbound envelope did not verify, reported alongside the
/// <see cref="BusEnvelopeIntegrityResult"/> that decides whether it is dispatched.
/// </summary>
/// <remarks>
/// The two failure cases mean different things to an operator. An absent signature is what a
/// rolling deployment produces between the first consumer that verifies and the last publisher
/// that signs — and, after the roll, the signal that a publisher was missed. A present signature
/// that does not verify means the publisher holds a different key, or the message was altered in
/// transit. The result alone cannot tell them apart; this value can.
/// </remarks>
public enum BusEnvelopeIntegrityFailure
{
    /// <summary>The envelope verified, or verification was skipped. Nothing failed.</summary>
    None = 0,

    /// <summary>The envelope carried no signature at all.</summary>
    Absent = 1,

    /// <summary>The envelope carried a signature and it did not verify against the canonical form.</summary>
    Invalid = 2,
}
