namespace Stratara.Orleans.Hosting;

/// <summary>
/// Seeds the checkpoints of the store-reading projections and sagas the host registers at the store's current head,
/// so that a host whose read models are already current when it adopts the execution model applies only what
/// commits afterwards. A consumer and partition that already has a checkpoint is left as it is; a consumer
/// registered later without one still starts at the beginning of the store, as a new projection needs.
/// </summary>
/// <remarks>
/// Run it once, while no silo of the cluster runs, after migrating the schema and before the host's first start on a
/// populated store. Resolve it from the host's own composition — the one that calls <c>AddStrataraProjectionGrains</c>
/// or <c>AddStrataraSagaGrains</c> — and from a scope, like the store readers whose names it reads. A checkpoint at
/// the beginning counts as absent and is seeded, so a reset followed by a seeding re-reads nothing: start without
/// seeding where a full re-read is wanted, and rebuild single read models with <c>IProjectionRebuilder</c> instead.
/// </remarks>
public interface IStoreReaderSeeding
{
    /// <summary>Writes a checkpoint at the head for every registered consumer and partition that has none.</summary>
    /// <param name="cancellationToken">Propagated to the reader and the checkpoint store.</param>
    /// <returns>How many checkpoints were seeded and how many already existed.</returns>
    /// <exception cref="InvalidOperationException">A checkpoint exists that was written under a different reader.</exception>
    Task<StoreReaderSeedingReport> SeedAtHeadAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a seeding did.</summary>
/// <param name="Seeded">The checkpoints written at the head.</param>
/// <param name="Existing">The checkpoints that already existed and were left as they were.</param>
public sealed record StoreReaderSeedingReport(int Seeded, int Existing);
