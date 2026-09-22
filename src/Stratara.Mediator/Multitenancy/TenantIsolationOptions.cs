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

    /// <summary>
    /// Whether work the platform starts on a tenant's behalf — a session carrying the reserved system
    /// actor identities, as <c>SessionContext.ForPlatform</c> builds it — is referred to the
    /// cross-tenant authorizer like any other cross-tenant operation.
    /// <para>
    /// <c>false</c> by default: the platform acting for a tenant is not one tenant acting on another,
    /// so strict mode permits it and records it under its own log event, and a host does not have to
    /// teach its authorizer about the framework's own timers, saga steps and sweeps. Set it to
    /// <c>true</c> to decide in the authorizer instead — with the shipped deny-everything default
    /// that refuses every platform-initiated request. The data-owner check applies either way.
    /// </para>
    /// </summary>
    public bool AuthorizePlatformActor { get; set; }
}
