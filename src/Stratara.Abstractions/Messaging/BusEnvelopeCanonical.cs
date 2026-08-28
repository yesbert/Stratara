using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stratara.Contracts.Messages;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Canonical payload projection helpers used by both publishers (when signing) and consumers
/// (when verifying) so the two sides hash identical bytes. The projection covers every field of
/// the message except the signature itself.
/// </summary>
/// <remarks>
/// <para>
/// Two properties make this projection safe, and both are load-bearing.
/// </para>
/// <para>
/// <b>Every field is length-prefixed.</b> Joining fields with a separator they are allowed to
/// contain would let content be shifted across a field boundary without changing the projection —
/// so an attacker could alter which command type is dispatched while the signature still verified,
/// defeating the very guard the type name is signed for.
/// </para>
/// <para>
/// <b>The projection is built from field values, never by re-serializing.</b> The payloads survive
/// the envelope's own deserialization as strings, and every other field is a scalar with a
/// canonical text form, so a message that has been on the wire projects to exactly what its
/// publisher signed. Re-serializing would make the projection depend on property order, escaping
/// and culture, and the resulting failures would be intermittent rather than immediate — the worst
/// shape a signature check can have.
/// </para>
/// <para>
/// Changing this projection invalidates signatures produced by an older publisher. Fleets move
/// through permissive mode: publishers first, consumers after, then strict again.
/// </para>
/// </remarks>
public static class BusEnvelopeCanonical
{
    /// <summary>
    /// Canonical projection of a <see cref="CommandEnvelope"/> — covers the envelope id, the command
    /// type, the session context, the heavy-lane flag and a digest of the command payload.
    /// </summary>
    /// <param name="envelope">The envelope to project.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <c>null</c>.</exception>
    public static string Of(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var canonical = new StringBuilder();
        Append(canonical, envelope.Id.ToString());
        Append(canonical, envelope.CommandTypeName);
        Append(canonical, envelope.SessionContextJson);
        Append(canonical, envelope.Heavy ? "1" : "0");
        Append(canonical, Digest(envelope.CommandJson));
        return canonical.ToString();
    }

    /// <summary>
    /// Canonical projection of an <see cref="EventBundle"/> — covers the session context and a
    /// digest over every field of every event it carries.
    /// </summary>
    /// <param name="bundle">The bundle to project.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is <c>null</c>.</exception>
    public static string Of(EventBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var canonical = new StringBuilder();
        Append(canonical, bundle.SessionContextJson);
        Append(canonical, Digest(EventsProjection(bundle.Events)));
        return canonical.ToString();
    }

    private static string EventsProjection(IReadOnlyList<EventMessage> events)
    {
        var projection = new StringBuilder();
        Append(projection, events.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var message in events)
        {
            Append(projection, message.Id.ToString());
            Append(projection, message.Version.ToString(CultureInfo.InvariantCulture));
            Append(projection, message.DataJson);
            Append(projection, message.StreamId.ToString());
            Append(projection, message.EventTypeName);
            Append(projection, message.AggregateTypeName);
            Append(projection, message.ActorTenantId.ToString());
            Append(projection, message.ActorUserId.ToString());
            Append(projection, message.TenantId.ToString());
            Append(projection, message.UserId?.ToString() ?? string.Empty);
        }

        return projection.ToString();
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        var text = value ?? string.Empty;
        canonical.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
