namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Command whose dispatch must be serialised against the same aggregate to avoid concurrent
/// writes on the underlying event stream.
/// </summary>
/// <remarks>
/// The message-bus consumer hashes <see cref="AggregateId"/> into a bucket and acquires a
/// per-bucket semaphore before invoking the handler. Commands targeting different aggregate
/// ids still run in parallel; commands sharing the same id are queued. The bucket count is
/// fixed at 4096 — collisions are tolerated and just reduce throughput within a bucket.
/// </remarks>
public interface IAggregateScopedCommand : ICommandBase
{
    /// <summary>The id of the aggregate the command writes to. Used as the bucket-lock key.</summary>
    Guid AggregateId { get; }
}
