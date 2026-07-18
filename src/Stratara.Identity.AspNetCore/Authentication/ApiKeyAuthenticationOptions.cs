using Microsoft.AspNetCore.Authentication;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// Options for the Stratara API-key authentication scheme.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The default scheme name — <c>StrataraApiKey</c>.</summary>
    public const string SchemeName = "StrataraApiKey";

    /// <summary>The default request header carrying the raw key — <c>X-Api-Key</c>.</summary>
    public const string DefaultHeaderName = "X-Api-Key";

    /// <summary>The query parameter consulted when <see cref="AllowQueryStringKey"/> is enabled — <c>access_token</c>.</summary>
    public const string QueryParameterName = "access_token";

    /// <summary>The request header carrying the raw key. Defaults to <see cref="DefaultHeaderName"/>.</summary>
    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>
    /// Also accept the key via the <c>access_token</c> query parameter. Off by default — query
    /// strings leak into logs and referrers; enable only for transports that cannot send custom
    /// headers (for example SignalR WebSocket negotiation).
    /// </summary>
    public bool AllowQueryStringKey { get; set; }
}
