namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Static helper that resolves the integrity-verification decision for a single inbound bus envelope
/// (<c>CommandEnvelope</c> or <c>EventBundle</c>). Centralises the rules so the three consuming
/// workers (Mediator-command, projection, saga) share identical Off / Permissive / Strict semantics.
/// </summary>
/// <remarks>
/// The helper is logger-free on purpose — each worker owns its own structured-logging vocabulary
/// (command vs event bundle) and decides which extension method to invoke on the
/// <see cref="BusEnvelopeIntegrityResult"/> the helper returns.
/// </remarks>
public static class BusEnvelopeIntegrityVerifier
{
    /// <summary>
    /// Returns the verification outcome for an envelope given the configured mode and the
    /// supplied signer / canonical payload / signature.
    /// </summary>
    /// <param name="signer">The signer to delegate to, or <c>null</c> when no signer is registered.</param>
    /// <param name="mode">The current <see cref="BusEnvelopeIntegrityOptions.Mode"/>.</param>
    /// <param name="canonical">The canonical projection of the envelope as produced by <see cref="BusEnvelopeCanonical"/>.</param>
    /// <param name="signature">The signature read off the envelope, or <c>null</c> for an unsigned envelope.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><see cref="BusEnvelopeIntegrityResult.Skipped"/> when mode is <see cref="BusEnvelopeIntegrityMode.Off"/> or no signer is registered.</item>
    /// <item><see cref="BusEnvelopeIntegrityResult.Verified"/> when the signature matches.</item>
    /// <item><see cref="BusEnvelopeIntegrityResult.RejectedPermissive"/> when the signature mismatches under <see cref="BusEnvelopeIntegrityMode.Permissive"/>.</item>
    /// <item><see cref="BusEnvelopeIntegrityResult.RejectedStrict"/> when the signature mismatches under <see cref="BusEnvelopeIntegrityMode.Strict"/>.</item>
    /// </list>
    /// </returns>
    public static BusEnvelopeIntegrityResult Verify(
        IBusEnvelopeSigner? signer,
        BusEnvelopeIntegrityMode mode,
        string canonical,
        string? signature)
    {
        if (mode == BusEnvelopeIntegrityMode.Off || signer is null)
        {
            return BusEnvelopeIntegrityResult.Skipped;
        }

        if (signer.Verify(canonical, signature))
        {
            return BusEnvelopeIntegrityResult.Verified;
        }

        return mode == BusEnvelopeIntegrityMode.Strict
            ? BusEnvelopeIntegrityResult.RejectedStrict
            : BusEnvelopeIntegrityResult.RejectedPermissive;
    }
}
