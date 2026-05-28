using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Configuration for opt-in HMAC integrity protection on bus envelopes. Mitigates tenant /
/// actor spoofing on a compromised message bus by signing the trust-relevant slice of every
/// outbound <c>CommandEnvelope</c> and <c>EventBundle</c>, and verifying it on the consumer side.
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
/// <c>BusEnvelopeCanonical</c>, which is intentionally <i>identity-only</i>: for
/// <c>CommandEnvelope</c> it covers <c>CommandTypeName + "|" + SessionContextJson</c>; for
/// <c>EventBundle</c> it covers <c>SessionContextJson</c>. The signature therefore prevents
/// tenant / actor spoofing and command-type substitution but does <b>NOT</b> bind the payload
/// body (the <c>CommandJson</c> string on <c>CommandEnvelope</c>, the <c>Events[]</c> list on
/// <c>EventBundle</c>). Payload tamper resistance comes from a separate layer: fields marked
/// <c>[EncryptData]</c> are AES-GCM-encrypted with a tenant-bound AAD and refuse to decrypt
/// after any tamper, while unencrypted fields are not authenticated. Adopters that need
/// payload-tamper protection on non-encrypted fields should add an additional integrity check
/// at the application layer (e.g., command-handler-side schema validation + signed external
/// references) or mark sensitive fields with <c>[EncryptData]</c>.
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
