namespace Stratara.Abstractions.Erasure;

/// <summary>
/// One plane of a composed erasure, and the scopes that were swept in it.
/// </summary>
/// <param name="Plane">The plane that was swept.</param>
/// <param name="Scopes">
/// A description of each scope the sweep covered — a tenant, a user, or a user within a tenant.
/// Empty where the plane's sweep takes no scope of its own.
/// </param>
public sealed record ErasedPlane(ErasurePlane Plane, IReadOnlyList<string> Scopes);

/// <summary>
/// What a composed erasure covered. Returned only when every plane succeeded; a failure part-way
/// raises <see cref="ErasureIncompleteException"/> instead, carrying the planes completed until then.
/// </summary>
/// <param name="Planes">The planes swept, in the order they ran.</param>
public sealed record ErasureReport(IReadOnlyList<ErasedPlane> Planes);
