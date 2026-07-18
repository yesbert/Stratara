namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// Issues, validates, and revokes API keys — the machine-to-machine authentication plane.
/// Keys are stored hashed (never in plaintext) and flow through the same membership/permission
/// plane as human sign-ins: a machine key acts as its own actor with tenant-scoped roles, a
/// personal access token acts as its bound user.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed validation: unknown, revoked, and expired keys all return <c>null</c>. The
/// erasure sweeps (<see cref="RemoveAllForTenantAsync"/>, <see cref="RemoveAllForUserAsync"/>)
/// exist so tenant- and user-erasure flows can clear the key plane in one call.
/// </para>
/// </remarks>
public interface IApiKeyStore
{
    /// <summary>
    /// Issues a new key. The returned <see cref="IssuedApiKey.RawKey"/> is shown once and never
    /// persisted. Personal access tokens (a bound user) require the user to hold an active
    /// membership in the target tenant.
    /// </summary>
    /// <param name="request">The issuance parameters.</param>
    /// <param name="cancellationToken">Token to observe while issuing.</param>
    /// <returns>The raw secret plus the stored descriptor.</returns>
    /// <exception cref="InvalidOperationException">
    /// A personal access token was requested for a user without an active membership in the
    /// tenant, or roles were supplied for a personal access token.
    /// </exception>
    Task<IssuedApiKey> IssueAsync(ApiKeyIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a presented raw key: hash lookup, then revocation and expiry checks.
    /// </summary>
    /// <param name="rawKey">The presented plaintext key.</param>
    /// <param name="cancellationToken">Token to observe while validating.</param>
    /// <returns>The key's descriptor, or <c>null</c> for unknown/revoked/expired keys.</returns>
    Task<ApiKeyDescriptor?> ValidateAsync(string rawKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a key; it never validates again.
    /// </summary>
    /// <param name="apiKeyId">The key to revoke.</param>
    /// <param name="cancellationToken">Token to observe while revoking.</param>
    Task RevokeAsync(Guid apiKeyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tenant's keys (descriptors only — raw keys are unrecoverable).
    /// </summary>
    /// <param name="tenantId">The tenant whose keys to list.</param>
    /// <param name="cancellationToken">Token to observe while listing.</param>
    /// <returns>The tenant's keys; empty when none exist.</returns>
    Task<IReadOnlyList<ApiKeyDescriptor>> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every key bound to the tenant — the key-plane step of a tenant-erasure sweep.
    /// </summary>
    /// <param name="tenantId">The tenant whose keys to remove.</param>
    /// <param name="cancellationToken">Token to observe while removing.</param>
    Task RemoveAllForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every personal access token bound to the user — the key-plane step of a
    /// user-erasure sweep.
    /// </summary>
    /// <param name="userId">The user whose personal access tokens to remove.</param>
    /// <param name="cancellationToken">Token to observe while removing.</param>
    Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
