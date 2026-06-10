using Stratara.Contracts.Session;

namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Authorizes a privileged cross-tenant operation — one where the acting principal's tenant
/// (<see cref="SessionContext.ActorTenantId"/>) differs from the data-owner tenant
/// (<see cref="SessionContext.TenantId"/>). Consulted by the tenant-isolation pipeline behavior
/// when it runs in strict mode and observes such a divergence.
/// </summary>
/// <remarks>
/// <para>
/// Stratara ships a deny-all default, so strict mode forbids every cross-tenant operation until a
/// consumer registers its own implementation. A typical implementation grants the divergence only
/// to a platform-administrator principal (e.g. a role check). Because the in-process query path
/// runs at the HTTP endpoint, an implementation there can read the current <c>ClaimsPrincipal</c>;
/// the worker-side command path (outbox → worker) has no HTTP context, so an implementation that
/// must run there should base its decision on data carried by the <see cref="SessionContext"/>
/// rather than ambient request state.
/// </para>
/// <para>
/// Register the implementation with the DI container; the default deny-all is added via
/// <c>TryAdd</c> so any consumer registration wins.
/// </para>
/// </remarks>
public interface ICrossTenantAuthorizer
{
    /// <summary>
    /// Decide whether the current principal may operate across tenants for the given session.
    /// </summary>
    /// <param name="session">The ambient session whose actor and data-owner tenants diverge.</param>
    /// <param name="cancellationToken">Token to observe while authorizing.</param>
    /// <returns><c>true</c> to permit the cross-tenant operation; <c>false</c> to reject it.</returns>
    ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken cancellationToken = default);
}
