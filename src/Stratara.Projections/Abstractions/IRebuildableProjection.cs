namespace Stratara.Projections.Abstractions;

/// <summary>
/// A projection that can be rebuilt on its own because it knows how to empty what it wrote. A full
/// replay empties every read model at once; a projection that implements this interface can instead
/// be rebuilt alone while every other projection keeps applying.
/// </summary>
public interface IRebuildableProjection : IProjection
{
    /// <summary>Empties this projection's read model, and nothing else.</summary>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the read model is empty.</returns>
    Task TruncateAsync(CancellationToken cancellationToken);
}
