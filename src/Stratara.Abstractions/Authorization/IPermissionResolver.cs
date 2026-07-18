namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Resolves the effective permission set a user holds within a tenant — the lookup behind
/// <see cref="RequirePermissionAttribute"/> enforcement (authorizing mediator, authorizing
/// outbox dispatcher, HTTP permission policies).
/// </summary>
/// <remarks>
/// <para>
/// The framework's default implementation maps the user's roles (tenant-scoped membership roles,
/// optionally global identity roles) through the application's <see cref="PermissionCatalog"/>
/// role grants. Custom implementations may resolve from a database grant store instead — the
/// contract does not prescribe the source.
/// </para>
/// <para>
/// Implementations should be cheap to call repeatedly within one request scope (memoize per
/// <c>(userId, tenantId)</c>); permission sets are deliberately <em>not</em> carried in the
/// session context, cookies, or tokens.
/// </para>
/// </remarks>
public interface IPermissionResolver
{
    /// <summary>
    /// Resolves every permission the user effectively holds within the tenant.
    /// </summary>
    /// <param name="userId">The user whose permissions to resolve (typically the session's actor).</param>
    /// <param name="tenantId">The tenant scope (typically the session's data-owner tenant).</param>
    /// <param name="cancellationToken">Token to observe while resolving.</param>
    /// <returns>The effective permission set; empty when the user holds none.</returns>
    ValueTask<IReadOnlySet<string>> ResolvePermissionsAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default);
}
