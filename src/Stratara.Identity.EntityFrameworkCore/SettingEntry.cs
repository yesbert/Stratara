using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core row shape for the <c>setting_entry</c> table — one setting value in one exact scope.
/// The scope columns use the empty string (never <c>NULL</c>) for the unset dimension, so the
/// composite primary key stays portable across providers with differing NULL-uniqueness rules.
/// </summary>
[ExcludeFromCodeCoverage]
public class SettingEntry
{
    /// <summary>The declared setting name (composite-key part 1).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The owning tenant id, or the empty string for user-global/global scopes (composite-key part 2).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The owning user id, or the empty string for tenant/global scopes (composite-key part 3).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The stored value (plaintext, or the encrypted envelope for encrypted definitions).</summary>
    public string Value { get; set; } = string.Empty;
}
