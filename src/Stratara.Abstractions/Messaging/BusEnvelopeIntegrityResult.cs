namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Outcome of <see cref="BusEnvelopeIntegrityVerifier.Verify"/> for a single inbound envelope.
/// Callers translate the value into the appropriate logger call and dispatch decision.
/// </summary>
public enum BusEnvelopeIntegrityResult
{
    /// <summary>
    /// Integrity verification was skipped — the mode is <see cref="BusEnvelopeIntegrityMode.Off"/>
    /// or no <see cref="IBusEnvelopeSigner"/> is registered. The envelope dispatches without
    /// any signature check.
    /// </summary>
    Skipped = 0,

    /// <summary>The signature on the envelope matched the expected value. The envelope dispatches.</summary>
    Verified = 1,

    /// <summary>
    /// The signature mismatched under <see cref="BusEnvelopeIntegrityMode.Permissive"/>. The
    /// caller logs a warning and dispatches the envelope anyway.
    /// </summary>
    RejectedPermissive = 2,

    /// <summary>
    /// The signature mismatched under <see cref="BusEnvelopeIntegrityMode.Strict"/>. The
    /// caller logs an error and rejects the envelope by throwing
    /// <see cref="System.InvalidOperationException"/>.
    /// </summary>
    RejectedStrict = 3,
}
