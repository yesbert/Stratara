namespace Stratara.Sessions;

/// <summary>
/// Public constants for HTTP headers that the Stratara session-context middleware reads. Consumer
/// services that send requests to Stratara-backed hosts should reference these constants instead of
/// duplicating the literal string.
/// </summary>
public static class StrataraHeaderNames
{
    /// <summary>HTTP header carrying the subject (data-owner) <c>TenantId</c> when no JWT claim is present.</summary>
    public const string TenantId = "X-Tenant-Id";

    /// <summary>HTTP header carrying the per-connection <c>ClientId</c> (browser tab / phone call / SSR session).</summary>
    public const string ClientId = "X-Client-Id";
}
