using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core row shape for the <c>api_key</c> table — one issued API key. Only the SHA-256 hash
/// of the raw key is stored (lowercase hex, unique lookup index); the plaintext is shown once
/// at issuance and unrecoverable afterwards.
/// </summary>
[ExcludeFromCodeCoverage]
public class ApiKeyEntry
{
    /// <summary>The key's stable identity (primary key; also the machine key's actor id).</summary>
    public Guid Id { get; set; }

    /// <summary>Lowercase-hex SHA-256 digest of the raw key (unique lookup index).</summary>
    public string HashedKey { get; set; } = string.Empty;

    /// <summary>The tenant the key is bound to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The bound user for personal access tokens; <c>null</c> for machine keys.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Display name for administration.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tenant-scoped roles for machine keys (JSON column); empty for personal access tokens.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Issuance timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional expiry; the key stops validating once passed.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Revocation timestamp; a revoked key never validates again.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
