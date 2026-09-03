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
    /// <item><see cref="BusEnvelopeIntegrityResult.RejectedPermissive"/> when the signature is absent or does not verify under <see cref="BusEnvelopeIntegrityMode.Permissive"/>.</item>
    /// <item><see cref="BusEnvelopeIntegrityResult.RejectedStrict"/> when the signature is absent or does not verify under <see cref="BusEnvelopeIntegrityMode.Strict"/>.</item>
    /// </list>
    /// Use the overload with a <see cref="BusEnvelopeIntegrityFailure"/> parameter to learn which of
    /// the two rejection reasons applied.
    /// </returns>
    public static BusEnvelopeIntegrityResult Verify(
        IBusEnvelopeSigner? signer,
        BusEnvelopeIntegrityMode mode,
        string canonical,
        string? signature) =>
        Verify(signer, mode, canonical, signature, out _);

    /// <summary>
    /// Returns the verification outcome for an envelope and reports why it did not verify, so that
    /// a caller can tell an unsigned envelope from one whose signature is present but wrong.
    /// </summary>
    /// <param name="signer">The signer to delegate to, or <c>null</c> when no signer is registered.</param>
    /// <param name="mode">The current <see cref="BusEnvelopeIntegrityOptions.Mode"/>.</param>
    /// <param name="canonical">The canonical projection of the envelope as produced by <see cref="BusEnvelopeCanonical"/>.</param>
    /// <param name="signature">The signature read off the envelope, or <c>null</c> for an unsigned envelope.</param>
    /// <param name="failure">
    /// <see cref="BusEnvelopeIntegrityFailure.Absent"/> when the envelope carried no signature,
    /// <see cref="BusEnvelopeIntegrityFailure.Invalid"/> when it carried one the signer refused,
    /// and <see cref="BusEnvelopeIntegrityFailure.None"/> when the result is
    /// <see cref="BusEnvelopeIntegrityResult.Skipped"/> or <see cref="BusEnvelopeIntegrityResult.Verified"/>.
    /// </param>
    /// <returns>The same value the four-parameter overload returns.</returns>
    /// <remarks>
    /// An absent signature is decided here and never reaches the signer. A present signature of any
    /// content — including whitespace — is handed to the signer, and refusal is reported as
    /// <see cref="BusEnvelopeIntegrityFailure.Invalid"/>: a publisher that emits a malformed
    /// signature is not one that has not started signing yet.
    /// </remarks>
    public static BusEnvelopeIntegrityResult Verify(
        IBusEnvelopeSigner? signer,
        BusEnvelopeIntegrityMode mode,
        string canonical,
        string? signature,
        out BusEnvelopeIntegrityFailure failure)
    {
        failure = BusEnvelopeIntegrityFailure.None;

        if (mode == BusEnvelopeIntegrityMode.Off || signer is null)
        {
            return BusEnvelopeIntegrityResult.Skipped;
        }

        if (string.IsNullOrEmpty(signature))
        {
            failure = BusEnvelopeIntegrityFailure.Absent;
        }
        else if (signer.Verify(canonical, signature))
        {
            return BusEnvelopeIntegrityResult.Verified;
        }
        else
        {
            failure = BusEnvelopeIntegrityFailure.Invalid;
        }

        return mode == BusEnvelopeIntegrityMode.Strict
            ? BusEnvelopeIntegrityResult.RejectedStrict
            : BusEnvelopeIntegrityResult.RejectedPermissive;
    }
}
