using System.Text.Json.Nodes;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Transforms a persisted event's raw JSON payload from an older on-disk schema into the shape the
/// current event record expects, applied on read before the payload is deserialized into a runtime
/// type. This is the framework's <em>event upcasting</em> hook: it lets an event's structure evolve
/// (renamed / added / defaulted / moved fields, or a renamed event type) without rewriting the
/// immutable events already in the store.
/// </summary>
/// <remarks>
/// <para>
/// An upcaster is matched by <see cref="SourceEventTypeName"/> against the type name persisted in the
/// event row (compared in the version-independent form — assembly <c>Version</c> / <c>Culture</c> /
/// <c>PublicKeyToken</c> segments are ignored). When it matches, <see cref="Upcast"/> rewrites the JSON
/// and the payload's effective type name becomes <see cref="TargetEventTypeName"/>. Upcasters chain:
/// register one per schema hop (v1→v2, v2→v3, …) and the pipeline applies them in sequence until no
/// further upcaster matches, then resolves <see cref="TargetEventTypeName"/> and deserializes.
/// </para>
/// <para>
/// Because upcasting runs on the payload <em>as persisted</em>, values of fields marked
/// <c>[EncryptData]</c> are still ciphertext at this point — an upcaster can restructure them (rename /
/// move the field) but cannot read or produce their plaintext. Snapshots are not upcasted; a change to
/// an aggregate's shape should invalidate or rebuild its snapshots.
/// </para>
/// </remarks>
public interface IEventUpcaster
{
    /// <summary>
    /// The persisted event type name this upcaster reads from, in version-independent assembly-qualified
    /// form (for example <c>"MyApp.Events.OrderPlacedV1, MyApp"</c>). Matched against the name stored in
    /// the event row; a renamed or since-removed source type is fine because the source type is never
    /// resolved — only <see cref="TargetEventTypeName"/> is.
    /// </summary>
    string SourceEventTypeName { get; }

    /// <summary>
    /// The event type name the payload carries after this upcaster runs, in version-independent
    /// assembly-qualified form. Equal to <see cref="SourceEventTypeName"/> for an in-place shape change,
    /// or a different registered type name when the event was renamed. Must resolve through the
    /// trusted-type resolver once the chain completes.
    /// </summary>
    string TargetEventTypeName { get; }

    /// <summary>
    /// Rewrites the raw event payload from the source schema to the target schema.
    /// </summary>
    /// <param name="payload">The persisted payload parsed as a mutable JSON node.</param>
    /// <returns>The upcasted payload — the same node mutated in place, or a new node.</returns>
    JsonNode Upcast(JsonNode payload);
}
