namespace Stratara.Abstractions.Projections;

/// <summary>
/// Rebuilds one projection from the beginning of the store while every other projection keeps
/// applying. Only a projection that declares how to empty its own read model can be rebuilt alone.
/// </summary>
public interface IProjectionRebuilder
{
    /// <summary>
    /// Pauses the projection's readers, returns their checkpoints to the beginning, empties the
    /// projection's read model and resumes the readers, which then read the store from the start in
    /// parallel, one per partition. Returns once the readers are resumed, not once they have caught up.
    /// </summary>
    /// <param name="projectionName">The projection, by the name the framework gives it.</param>
    /// <param name="cancellationToken">Propagated to the checkpoint store and the truncation.</param>
    /// <returns>A task that completes when the readers have been resumed.</returns>
    /// <exception cref="InvalidOperationException">No projection of that name is registered, or it cannot be rebuilt alone.</exception>
    Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default);
}
