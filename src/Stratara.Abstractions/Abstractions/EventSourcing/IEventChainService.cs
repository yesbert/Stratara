namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Decides whether the global event-chain anchor needs to be advanced and adds a new
/// anchor row when required. Typically invoked from the event-stream-hash worker on
/// each batch.
/// </summary>
public interface IEventChainService
{
    /// <summary>Add a new anchor if the configured threshold has been crossed; no-op otherwise.</summary>
    Task AddAnchorIfNeededAsync(CancellationToken cancellationToken = default);
}
