using JetBrains.Annotations;
using Stratara.Abstractions.Multitenancy;
using Stratara.Contracts.Session;

namespace Stratara.Mediator.Multitenancy;

/// <summary>
/// Default <see cref="ICrossTenantAuthorizer"/> that denies every cross-tenant operation. Registered
/// via <c>TryAdd</c> so a consumer's own authorizer wins; ensures strict mode is safe by default.
/// </summary>
[UsedImplicitly]
internal sealed class DenyAllCrossTenantAuthorizer : ICrossTenantAuthorizer
{
    public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
