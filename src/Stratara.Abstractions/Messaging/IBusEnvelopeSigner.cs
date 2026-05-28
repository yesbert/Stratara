namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Computes and verifies opaque signatures over the trust-relevant slice of a bus envelope.
/// Implementations are registered via <c>services.AddBusEnvelopeIntegrity(...)</c>; the default
/// is HMAC-SHA256 backed by <see cref="BusEnvelopeIntegrityOptions.SharedKey"/>.
/// </summary>
/// <remarks>
/// The payload supplied to <see cref="Sign"/> and <see cref="Verify"/> is a framework-defined
/// canonical projection of the envelope (for example <c>CommandTypeName + "|" + SessionContextJson</c>
/// for <c>CommandEnvelope</c>) — implementations must not interpret it.
/// </remarks>
public interface IBusEnvelopeSigner
{
    /// <summary>Returns an opaque signature string over <paramref name="payload"/>.</summary>
    /// <param name="payload">Canonical projection of the envelope as provided by the framework.</param>
    /// <returns>A string-encoded signature suitable for embedding in the envelope's <c>Signature</c> field.</returns>
    string Sign(string payload);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="signature"/> matches the expected signature for
    /// <paramref name="payload"/>. A <c>null</c> or empty <paramref name="signature"/> must return
    /// <c>false</c>. Implementations must use a constant-time comparison.
    /// </summary>
    /// <param name="payload">Canonical projection of the envelope as provided by the framework.</param>
    /// <param name="signature">The signature read off the envelope, or <c>null</c> for an unsigned envelope.</param>
    /// <returns><c>true</c> if the signature is valid; otherwise <c>false</c>.</returns>
    bool Verify(string payload, string? signature);
}
