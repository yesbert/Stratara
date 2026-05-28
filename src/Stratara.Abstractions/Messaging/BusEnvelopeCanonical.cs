using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Canonical payload projection helpers used by both publishers (when signing) and consumers
/// (when verifying) so the two sides hash identical bytes. The projection covers the
/// trust-relevant fields only: identity that drives routing / AAD on the consumer side.
/// </summary>
public static class BusEnvelopeCanonical
{
    private const char FieldSeparator = '|';

    /// <summary>
    /// Canonical projection of a <see cref="CommandEnvelope"/> — covers
    /// <see cref="CommandEnvelope.CommandTypeName"/> (type-confusion guard) and
    /// <see cref="CommandEnvelope.SessionContextJson"/> (tenant / actor identity).
    /// </summary>
    /// <param name="envelope">The envelope to project.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <c>null</c>.</exception>
    public static string Of(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return string.Concat(envelope.CommandTypeName, FieldSeparator, envelope.SessionContextJson);
    }

    /// <summary>
    /// Canonical projection of an <see cref="EventBundle"/> — covers
    /// <see cref="EventBundle.SessionContextJson"/>. Event payloads are out of scope because
    /// they are protected by the per-event AES-GCM tag bound to the tenant id; tampering with
    /// the session context (and therefore the tenant id) is the spoofing vector this signature
    /// closes.
    /// </summary>
    /// <param name="bundle">The bundle to project.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is <c>null</c>.</exception>
    public static string Of(EventBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return bundle.SessionContextJson;
    }
}
