using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Limits applied when deserialising bus envelopes (<c>CommandEnvelope</c>, <c>EventBundle</c>)
/// and their embedded JSON strings (for example <c>SessionContextJson</c>). Mitigates denial of
/// service via deeply-nested or oversized JSON payloads from a hostile publisher.
/// </summary>
/// <remarks>
/// Bind from configuration via section <see cref="SectionName"/> (<c>"BusEnvelopeJson"</c>) or
/// override programmatically. The framework calls
/// <see cref="BusEnvelopeJsonGuard.EnsureWithinSizeLimit(int, int, string)"/> on the raw
/// payload length before invoking <see cref="System.Text.Json.JsonSerializer"/>, and threads
/// <see cref="MaxDepth"/> into the deserialiser via
/// <see cref="BusEnvelopeJsonGuard.CreateOptions(int)"/>.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class BusEnvelopeJsonOptions
{
    /// <summary>Configuration section name (<c>"BusEnvelopeJson"</c>) used to bind these options.</summary>
    public const string SectionName = "BusEnvelopeJson";

    /// <summary>
    /// Maximum allowed nesting depth for incoming JSON envelopes. Defaults to 32. The
    /// <see cref="System.Text.Json.JsonSerializerOptions"/> default is 64; the lower value
    /// protects against deeply-nested payloads designed to exhaust the stack or trigger
    /// pathological serialiser behaviour.
    /// </summary>
    public int MaxDepth { get; set; } = 32;

    /// <summary>
    /// Maximum allowed byte length of a single bus message payload. Defaults to 1 048 576
    /// (1 MiB). Larger payloads are rejected with <see cref="System.Text.Json.JsonException"/>
    /// before deserialisation, preventing OOM / Gen2 stalls caused by a hostile publisher.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;
}
