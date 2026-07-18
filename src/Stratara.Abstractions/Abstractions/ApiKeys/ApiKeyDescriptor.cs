using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// The stored, non-secret description of an issued API key. The raw key itself is never
/// persisted — only its hash — so a descriptor can be listed and audited freely.
/// </summary>
/// <remarks>
/// Two kinds share this shape: <em>machine keys</em> (<see cref="UserId"/> is <c>null</c>) act
/// as their own principal — the key id is the actor — and carry tenant-scoped
/// <see cref="Roles"/>; <em>personal access tokens</em> (<see cref="UserId"/> set) act as the
/// bound user, whose own memberships and roles apply, so <see cref="Roles"/> is empty for them.
/// </remarks>
/// <param name="Id">The key's stable identity (also the machine key's actor id).</param>
/// <param name="TenantId">The tenant the key is bound to.</param>
/// <param name="UserId">The bound user for personal access tokens; <c>null</c> for machine keys.</param>
/// <param name="Name">Display name for administration ("CI deploy key").</param>
/// <param name="Roles">Tenant-scoped roles for machine keys; empty for personal access tokens.</param>
/// <param name="CreatedAt">Issuance timestamp.</param>
/// <param name="ExpiresAt">Optional expiry; the key stops validating once passed.</param>
/// <param name="RevokedAt">Revocation timestamp; a revoked key never validates again.</param>
[ExcludeFromCodeCoverage]
public sealed record ApiKeyDescriptor(
    Guid Id,
    Guid TenantId,
    Guid? UserId,
    string Name,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RevokedAt = null);
