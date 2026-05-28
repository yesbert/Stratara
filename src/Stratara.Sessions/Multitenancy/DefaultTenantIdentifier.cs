namespace Stratara.Sessions.Multitenancy;

/// <summary>
/// Sentinel tenant id used by the session-context middleware when no
/// <see cref="StrataraClaimTypes.TenantId"/> claim or <c>X-Tenant-Id</c> header is
/// present on the incoming request.
/// </summary>
/// <remarks>
/// Implementations of <c>ITenantService</c> may rewrite this to the actual default
/// tenant of the host application before consumers see it. Treat the value as opaque.
/// </remarks>
public static class DefaultTenantIdentifier
{
    /// <summary>The sentinel <see cref="Guid"/> value.</summary>
    public static readonly Guid Value = Guid.Parse("DB5DB794-EDF0-4E50-9B50-D0105F694B52");
}
