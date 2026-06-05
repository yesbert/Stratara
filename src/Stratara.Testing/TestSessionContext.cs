using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

namespace Stratara.Testing;

/// <summary>
/// Builds ready-made <see cref="SessionContext"/> values for tests, with fresh correlation +
/// causation ids (so the context can drive event-store writes) and the Actor (who triggered)
/// defaulted to the Subject (data owner) unless overridden.
/// </summary>
public static class TestSessionContext
{
    /// <summary>
    /// A context owned by <paramref name="tenantId"/> where the Actor equals the Subject — the common
    /// "a user acting within their own tenant" case.
    /// </summary>
    /// <param name="tenantId">The Subject (data-owner) tenant id, also used as the Actor tenant id.</param>
    /// <param name="userId">The Subject/Actor user id, or <see langword="null"/> for a tenant-only context.</param>
    /// <returns>A populated <see cref="SessionContext"/>.</returns>
    public static SessionContext ForTenant(Guid tenantId, Guid? userId = null) =>
        new(
            Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7().ToString("N"),
            null,
            tenantId,
            userId ?? Guid.Empty,
            tenantId,
            userId);

    /// <summary>
    /// A context with an explicit Actor / Subject split — for cross-tenant flows (e.g. a platform
    /// admin acting on behalf of a tenant) where audit (Actor) and ownership (Subject) differ.
    /// </summary>
    /// <param name="actorTenantId">The tenant that triggered the operation (audit dimension).</param>
    /// <param name="actorUserId">The user that triggered the operation (audit dimension).</param>
    /// <param name="tenantId">The Subject (data-owner) tenant id (routing, encryption AAD, query filter).</param>
    /// <param name="userId">The Subject (data-owner) user id, or <see langword="null"/>.</param>
    /// <returns>A populated <see cref="SessionContext"/>.</returns>
    public static SessionContext ForActorAndSubject(Guid actorTenantId, Guid actorUserId, Guid tenantId, Guid? userId = null) =>
        new(
            Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7().ToString("N"),
            null,
            actorTenantId,
            actorUserId,
            tenantId,
            userId);
}

/// <summary>
/// An <see cref="ISessionContextProvider"/> test double whose <see cref="Current"/> is set directly,
/// so code under test that reads the ambient session (repositories, encryption AAD, tenant guards)
/// runs without HTTP middleware or a worker dispatch loop.
/// </summary>
public sealed class TestSessionContextProvider : ISessionContextProvider
{
    /// <summary>Create a provider with an optional initial context.</summary>
    /// <param name="current">The initial session context, or <see langword="null"/> for none.</param>
    public TestSessionContextProvider(SessionContext? current = null) => Current = current;

    /// <inheritdoc />
    public SessionContext? Current { get; private set; }

    /// <summary>Create a provider preset to a <see cref="TestSessionContext.ForTenant"/> context.</summary>
    /// <param name="tenantId">The Subject (data-owner) tenant id.</param>
    /// <param name="userId">The Subject/Actor user id, or <see langword="null"/>.</param>
    /// <returns>A provider whose <see cref="Current"/> is owned by <paramref name="tenantId"/>.</returns>
    public static TestSessionContextProvider ForTenant(Guid tenantId, Guid? userId = null) =>
        new(TestSessionContext.ForTenant(tenantId, userId));

    /// <inheritdoc />
    public void Set(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Current = context;
    }

    /// <inheritdoc />
    public void Clear() => Current = null;
}
