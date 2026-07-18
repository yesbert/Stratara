using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

namespace Stratara.Sample.IdentityDirectory;

public sealed class SampleSessionContextProvider : ISessionContextProvider
{
    public SessionContext? Current { get; private set; }

    public void Clear() => Current = null;

    public void Set(SessionContext context) => Current = context;

    public void SignIn(Guid userId, Guid tenantId) =>
        Set(new SessionContext(
            CorrelationId: Guid.CreateVersion7().ToString("N"),
            CausationId: Guid.CreateVersion7().ToString("N"),
            ClientConnectionId: null,
            ActorTenantId: tenantId,
            ActorUserId: userId,
            TenantId: tenantId,
            UserId: userId));
}
