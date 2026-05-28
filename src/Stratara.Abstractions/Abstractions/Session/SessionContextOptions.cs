using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Session;

/// <summary>
/// Configuration for Stratara's <c>SessionContextMiddleware</c> — controls how the ambient
/// <see cref="ISessionContextProvider"/> resolves identity from inbound requests.
/// </summary>
/// <remarks>
/// Bind from configuration via section <see cref="SectionName"/> (<c>"SessionContext"</c>)
/// or programmatically via <c>services.Configure&lt;SessionContextOptions&gt;(o =&gt; ...)</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class SessionContextOptions
{
    /// <summary>Configuration section name (<c>"SessionContext"</c>) used to bind these options.</summary>
    public const string SectionName = "SessionContext";

    /// <summary>
    /// Allows the <c>X-Tenant-Id</c> HTTP header to substitute for a missing
    /// <c>stratara:tenant_id</c> JWT claim when resolving the subject tenant. Defaults to
    /// <see langword="false"/> since 3.0.10 — opt-in required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the default is fail-closed:</strong> in earlier versions the middleware
    /// silently fell back to the header whenever the claim was missing or unparsable, which
    /// allowed any authenticated principal to choose the tenant their request operated against
    /// (cross-tenant read in hosts whose identity provider does not embed a tenant claim in
    /// the JWT). Round-3-Audit Finding KI-04.
    /// </para>
    /// <para>
    /// <strong>When to opt in (<c>true</c>):</strong> when the consumer guarantees the header
    /// is gated upstream by a platform-admin role check, or when service-to-service calls
    /// carry the header as part of an internal trusted contract. The preferred alternative
    /// is to embed the tenant id directly into the JWT claim set so no header fallback is
    /// needed.
    /// </para>
    /// </remarks>
    public bool AllowTenantHeader { get; set; }
}
