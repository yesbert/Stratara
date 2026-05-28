using Stratara.Abstractions.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Outbox;

/// <summary>
/// Durable outbox row — persists a serialised payload (command envelope or event
/// bundle) for later delivery by the outbox worker. Written by dispatchers when the
/// direct bus publish fails.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OutboxEntry : IEntity, IBucket, IHasRowVersion
{
    /// <summary>The serialised payload as JSON.</summary>
    public required string DataJson { get; set; }

    /// <summary>Fully-qualified, version-independent type name of the payload — used by the worker to deserialise.</summary>
    public required string DataTypeName { get; set; }

    /// <summary>When the entry was queued.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc/>
    public required int BucketId { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }

    /// <inheritdoc/>
    public uint RowVersion { get; set; }
}
