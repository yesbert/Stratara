namespace Stratara.Mediator.Multitenancy;

/// <summary>
/// Enforcement mode for the tenant-isolation pipeline behavior.
/// </summary>
public enum TenantIsolationMode
{
    /// <summary>
    /// Enforce only that a tenant-scoped request's <c>TenantId</c> matches the current session's
    /// data-owner tenant. A privileged cross-tenant operation — where the session's actor tenant
    /// differs from its data-owner tenant — is allowed, because the calling endpoint is expected to
    /// have promoted the data-owner tenant to the target before dispatch.
    /// </summary>
    Default = 0,

    /// <summary>
    /// In addition to the <see cref="Default"/> subject check, require that any cross-tenant
    /// operation (session actor tenant ≠ data-owner tenant) be explicitly permitted by the
    /// registered <c>ICrossTenantAuthorizer</c>. With the shipped deny-all default, strict mode
    /// rejects every cross-tenant operation until a consumer registers an authorizer that grants it.
    /// </summary>
    Strict = 1
}

/// <summary>
/// Options controlling the tenant-isolation pipeline behavior.
/// </summary>
public sealed class TenantIsolationOptions
{
    /// <summary>
    /// The enforcement mode. Defaults to <see cref="TenantIsolationMode.Default"/>.
    /// </summary>
    public TenantIsolationMode Mode { get; set; } = TenantIsolationMode.Default;
}
