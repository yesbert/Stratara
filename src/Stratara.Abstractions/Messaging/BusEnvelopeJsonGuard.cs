using System.Text.Json;

namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Static helpers that enforce <see cref="BusEnvelopeJsonOptions"/> limits at the boundary
/// between the message-bus transport and the JSON deserialiser. Implementations of
/// <see cref="IMessageBus"/> and command-dispatch workers call into this guard before invoking
/// <see cref="JsonSerializer"/> so the deserialiser is never handed an over-large or
/// over-deeply-nested payload.
/// </summary>
public static class BusEnvelopeJsonGuard
{
    /// <summary>
    /// Throws <see cref="JsonException"/> when <paramref name="byteLength"/> exceeds
    /// <paramref name="maxBytes"/>; otherwise returns without effect.
    /// </summary>
    /// <param name="byteLength">Length of the raw payload, in bytes.</param>
    /// <param name="maxBytes">Configured maximum allowed payload size.</param>
    /// <param name="source">Short descriptor included in the exception message — typically a
    /// topic or queue name — to aid diagnosis when a payload is rejected.</param>
    /// <exception cref="JsonException">The payload exceeds the configured limit.</exception>
    public static void EnsureWithinSizeLimit(int byteLength, int maxBytes, string source)
    {
        if (byteLength > maxBytes)
        {
            throw new JsonException(
                $"Bus envelope from '{source}' is {byteLength} bytes, which exceeds the configured limit of {maxBytes} bytes (BusEnvelopeJsonOptions.MaxBodyBytes).");
        }
    }

    /// <summary>
    /// Builds a <see cref="JsonSerializerOptions"/> instance with <see cref="JsonSerializerOptions.MaxDepth"/>
    /// pinned to <paramref name="maxDepth"/>. Other options stay at the System.Text.Json defaults so
    /// the wire format remains compatible with payloads serialised via the framework's standard
    /// JSON path.
    /// </summary>
    /// <param name="maxDepth">Maximum allowed nesting depth.</param>
    /// <returns>A new <see cref="JsonSerializerOptions"/> instance with the configured depth limit.</returns>
    public static JsonSerializerOptions CreateOptions(int maxDepth) => new() { MaxDepth = maxDepth };
}
