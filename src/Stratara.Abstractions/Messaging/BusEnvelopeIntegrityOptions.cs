using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Configuration for opt-in HMAC integrity protection on bus envelopes. Mitigates tenant /
/// actor spoofing and payload tampering on a compromised message bus by signing every outbound
/// <c>CommandEnvelope</c> and <c>EventBundle</c>, and verifying it on the consumer side.
/// </summary>
/// <remarks>
/// <para>
/// Bind from configuration via section <see cref="SectionName"/> (<c>"BusEnvelopeIntegrity"</c>)
/// or configure programmatically through <c>services.AddBusEnvelopeIntegrity(o =&gt; ...)</c>.
/// <see cref="Mode"/> defaults to <see cref="BusEnvelopeIntegrityMode.Off"/>: no signing, no
/// verification — the framework behaves exactly as it did before this option was introduced
/// unless the host opts in.
/// </para>
/// <para>
/// <b>Signature scope (threat model).</b> The HMAC covers the canonical projection produced by
/// <c>BusEnvelopeCanonical</c>, which covers every field of the message except the signature
/// itself: for <c>CommandEnvelope</c> the envelope id, the command type name, the session
/// context, the heavy-lane flag and a SHA-256 digest of <c>CommandJson</c>; for
/// <c>EventBundle</c> the session context and a SHA-256 digest over every field of every event
/// in <c>Events[]</c>. Every field is length-prefixed, so content cannot be shifted across a
/// field boundary without changing the projection. The signature therefore prevents tenant /
/// actor spoofing, command-type substitution and payload tampering alike: a signature captured
/// from one message does not verify when presented with a different body.
/// </para>
/// <para>
/// Fields marked <c>[EncryptData]</c> are additionally AES-GCM-encrypted with a tenant-bound AAD
/// and refuse to decrypt after any tamper. That protection is independent of this option and
/// still applies when <see cref="Mode"/> is <see cref="BusEnvelopeIntegrityMode.Off"/>.
/// </para>
/// <para>
/// The projection changed in 3.4.0 — before that release it covered identity only. Signatures
/// produced by a pre-3.4.0 publisher do not verify against a 3.4.0 consumer; move a fleet across
/// through <see cref="BusEnvelopeIntegrityMode.Permissive"/>.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class BusEnvelopeIntegrityOptions
{
    /// <summary>Configuration section name (<c>"BusEnvelopeIntegrity"</c>) used to bind these options.</summary>
    public const string SectionName = "BusEnvelopeIntegrity";

    /// <summary>
    /// Enforcement mode. Defaults to <see cref="BusEnvelopeIntegrityMode.Off"/>. Must match
    /// across the publisher and consumer fleets; switching from <see cref="BusEnvelopeIntegrityMode.Off"/>
    /// to <see cref="BusEnvelopeIntegrityMode.Strict"/> in a single step rejects in-flight
    /// envelopes — use <see cref="BusEnvelopeIntegrityMode.Permissive"/> as a rolling step.
    /// </summary>
    public BusEnvelopeIntegrityMode Mode { get; set; } = BusEnvelopeIntegrityMode.Off;

    /// <summary>
    /// HMAC shared secret used to compute and verify envelope signatures. Must be at least
    /// 32 bytes (256 bit) and identical across every host that participates in the bus.
    /// </summary>
    public byte[]? SharedKey { get; set; }
}
