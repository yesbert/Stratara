namespace Stratara.Abstractions.Erasure;

/// <summary>
/// Erases a subject across every plane the framework holds its data in, in an order that leaves
/// nothing unreachable before it has been removed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it covers.</strong> API keys, scoped settings, directory memberships and their
/// active-tenant selections, and key material — the last of these making any data encrypted under
/// the subject's keys unrecoverable. A key is named by level, tenant and user together, and the
/// level decides whose erasure a value dies with. A tenant's erasure shreds every key naming the
/// tenant, at every level, alone or together with each of its members. A user's erasure shreds the
/// user-level keys naming the user, alone or together with each tenant it belongs to. It asks the key
/// store for every key naming the subject as well, which reaches keys shared with someone no longer
/// in the directory.
/// </para>
/// <para>
/// <strong>What it does not cover, and why it matters.</strong> Read models a consumer's own
/// projections built are unknown to the framework and remain the consumer's responsibility. Data in
/// the event stream that is not protected by a scoped key is not shredded by removing a key,
/// because there is no key to remove. The command audit log and the outbox both carry a session
/// context naming the subject and are deliberately left alone: the audit log is the evidence that
/// the erasure happened, and retaining it is a decision only the consumer can take.
/// Tenant-level and confidential values written for a user belong to the tenant and survive that
/// user's erasure. A key store that cannot list its scopes leaves an erasure with the keys the
/// directory names; a key shared with a former member, or with an operator acting in a tenant from
/// outside it, is then not found. A snapshot written before 4.4.0 records no user and stays under its
/// tenant alone, so a user's erasure does not reach it unless the host removed such snapshots on
/// upgrading.
/// </para>
/// </remarks>
public interface ISubjectEraser
{
    /// <summary>Erases one user across every plane, in every tenant it is a member of.</summary>
    /// <param name="userId">The user to erase.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>What each plane covered, once every plane has succeeded.</returns>
    /// <exception cref="ErasureIncompleteException">Thrown when one plane's sweep fails; the erasure stops there.</exception>
    Task<ErasureReport> EraseUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Erases one tenant across every plane, including its members' tenant-scoped data.</summary>
    /// <param name="tenantId">The tenant to erase.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>What each plane covered, once every plane has succeeded.</returns>
    /// <exception cref="ErasureIncompleteException">Thrown when one plane's sweep fails; the erasure stops there.</exception>
    Task<ErasureReport> EraseTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
