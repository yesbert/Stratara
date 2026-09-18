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

    /// <summary>When the entry was queued; recorded commands due together are resumed in the order of this time.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc/>
    public required int BucketId { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }

    /// <inheritdoc/>
    public uint RowVersion { get; set; }

    /// <summary>
    /// How many times the entry has been handed over for execution by a resumption since it was stored or returned, less
    /// the attempts given back for a concurrency conflict or a stop.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>How many of the entry's attempts since it was stored or returned ended in a concurrency conflict.</summary>
    public int ConflictCount { get; set; }

    /// <summary>When the entry was last handed over for execution; <see langword="null"/> until it first is.</summary>
    public DateTimeOffset? LastHandedOverAt { get; set; }

    /// <summary>When the entry was kept for an operator after its attempts ran out; <see langword="null"/> while it is still resumed.</summary>
    public DateTimeOffset? KeptAt { get; set; }

    /// <summary>The failure of the last attempt, recorded when the entry is kept.</summary>
    public string? LastFailure { get; set; }

    /// <summary>The aggregate the stored command names, recorded with it so resuming does not read the payload; <see langword="null"/> for a command that names none and for a bundle.</summary>
    public Guid? AggregateId { get; set; }

    /// <summary>Whether the stored command declared itself long-running.</summary>
    public bool Heavy { get; set; }
}
