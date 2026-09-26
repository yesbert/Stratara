namespace Stratara.Abstractions.Erasure;

/// <summary>
/// Erases a subject across every plane the framework holds its data in, in an order that leaves
/// nothing unreachable before it has been removed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it covers.</strong> API keys, scoped settings, directory memberships and their
/// active-tenant selections, and key material — the last of these making any data encrypted under
/// the subject's keys unrecoverable. A key is named by level, tenant and user together, so a
/// subject's key material is every user-level or tenant-level key that names it: alone, or together
/// with each tenant it belongs to or each member it has.
/// </para>
/// <para>
/// <strong>What it does not cover, and why it matters.</strong> Read models a consumer's own
/// projections built are unknown to the framework and remain the consumer's responsibility. Data in
/// the event stream that is not protected by a scoped key is not shredded by removing a key,
/// because there is no key to remove. The command audit log and the outbox both carry a session
/// context naming the subject and are deliberately left alone: the audit log is the evidence that
/// the erasure happened, and retaining it is a decision only the consumer can take.
/// System-wide (<c>Confidential</c>) key material is never subject-scoped and is never erased. A key
/// naming a tenant together with a user who left it before the erasure is found by neither erasure,
/// because the memberships are what name the other half. A snapshot is encrypted under its stream's
/// tenant alone, so a user's erasure does not reach the snapshot of an aggregate that user owns.
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
