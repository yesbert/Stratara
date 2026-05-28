namespace Stratara.Sessions.Multitenancy;

/// <summary>
/// Stable claim-name constants used by Stratara's session-context middleware to read
/// identity information from a JWT or cookie principal.
/// </summary>
public static class StrataraClaimTypes
{
    private const string Prefix = "stratara:";

    /// <summary>Claim name carrying the Subject tenant id — <c>stratara:tenant_id</c>.</summary>
    public const string TenantId = Prefix + "tenant_id";
}
