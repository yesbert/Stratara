using Stratara.Abstractions.Domain;

namespace Stratara.Infrastructure.Security.Serialization;

/// <summary>
/// The rule that what a stream stores is encrypted field by field: an event payload or an aggregate
/// marked for encryption as a whole has no field left to recover once its subject is erased, so the
/// event cannot be delivered and the snapshot cannot be restored, and a replay or a rebuild stops at
/// the erasure for good.
/// </summary>
internal static class WholeTypeStreamEncryption
{
    public static bool IsMarkedAsAWhole(Type type) => MetadataCache.GetOrCreateMetadata(type).ClassAttribute is not null;

    public static string Describe(Type type) =>
        typeof(IAggregate).IsAssignableFrom(type) ? $"aggregate {type.FullName}" : $"event {type.FullName}";

    public static InvalidOperationException Refusal(IEnumerable<Type> types) =>
        new(
            "These types are stored in an event stream and are marked for encryption as a whole. Once the subject " +
            "they are encrypted for is erased nothing of them can be read, so a replay or a rebuild stops at that " +
            "point for good. Remove [EncryptData] from the type and mark the properties that hold personal data " +
            "instead; payloads already written as a whole are still read: " +
            Environment.NewLine + string.Join(Environment.NewLine, types.Select(Describe).Order(StringComparer.Ordinal)));
}
