namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Opt-in marker for mediator requests (commands or queries) whose payload names the tenant
/// whose data the request operates on. Implement it on a request to subject it to the
/// tenant-isolation pipeline behavior, which rejects the request before the handler runs
/// when <see cref="TenantId"/> does not match the current session's data-owner tenant.
/// </summary>
/// <remarks>
/// <para>
/// The behavior compares <see cref="TenantId"/> against the <em>data-owner</em> dimension of the
/// ambient session (<c>SessionContext.TenantId</c>), not the <em>actor</em> dimension
/// (<c>SessionContext.ActorTenantId</c>). A privileged cross-tenant operation — where the acting
/// principal's home tenant differs from the data-owner tenant — therefore still passes the default
/// check, because the calling endpoint is expected to have promoted the session's data-owner tenant
/// to the target before dispatch. The optional strict mode adds an explicit authorization gate for
/// that cross-tenant case (see the tenant-isolation behavior's options).
/// </para>
/// <para>
/// The marker is intentionally independent of <see cref="Stratara.Abstractions.Mediator.IRequest"/>:
/// a request implements both its CQRS contract (<c>ICommand</c>/<c>IQuery&lt;T&gt;</c>) and this
/// marker. The behavior is registered for every request shape and acts only on those that also
/// implement <see cref="ITenantScopedRequest"/>.
/// </para>
/// </remarks>
/// <example>
/// A tenant-scoped query that the isolation behavior guards:
/// <code>
/// public sealed record GetCustomerQuery(Guid CustomerId, Guid TenantId)
///     : IQuery&lt;CustomerDto&gt;, ITenantScopedRequest;
/// </code>
/// </example>
public interface ITenantScopedRequest
{
    /// <summary>
    /// The tenant that owns the data this request operates on. Must match the current session's
    /// data-owner tenant for the request to reach its handler.
    /// </summary>
    Guid TenantId { get; }
}
