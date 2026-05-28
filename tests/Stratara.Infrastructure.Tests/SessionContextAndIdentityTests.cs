using System.Diagnostics;
using Stratara.Contracts.Session;
using Stratara.Infrastructure.Multitenancy;
using Stratara.Sessions.Session;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Tests;

public class SessionContextAndIdentityTests
{
    [Fact]
    public void SessionContextProvider_Set_Updates_Current_And_Activity_Tags()
    {
        // Arrange
        var provider = new SessionContextProvider();
        var actorTenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectTenantId = Guid.NewGuid();
        var ctx = new SessionContext(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            null,
            actorTenantId,
            actorUserId,
            subjectTenantId,
            null);

        var activity = new Activity("test-session");
        activity.Start();

        // Act
        provider.Set(ctx);

        // Assert
        Assert.Equal(ctx, provider.Current);
        Assert.Equal(ctx.CorrelationId, activity.GetTagItem(ApplicationDiagnostics.CorrelationIdTagName));
        Assert.Equal(ctx.CausationId, activity.GetTagItem(ApplicationDiagnostics.CausationIdTagName));
        // Activity tags surface TenantId (data owner) + ActorUserId (who-did-it).
        Assert.Equal(subjectTenantId, activity.GetTagItem(ApplicationDiagnostics.TenantIdTagName));
        Assert.Equal(actorUserId, activity.GetTagItem(ApplicationDiagnostics.UserIdTagName));
    }

    [Fact]
    public void CurrentUserService_And_TenantService_Read_From_Session_Or_Return_Empty()
    {
        // Arrange
        var provider = new SessionContextProvider();
        var currentUser = new CurrentUserService(provider);
        var tenantService = new TenantService(provider);

        // No session set -> empty
        Assert.Equal(Guid.Empty, currentUser.GetId());
        Assert.Equal(Guid.Empty, tenantService.GetTenantId());

        // Set session
        var actorTenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectTenantId = Guid.NewGuid();
        var ctx = new SessionContext(
            "c",
            null,
            null,
            actorTenantId,
            actorUserId,
            subjectTenantId,
            null);
        provider.Set(ctx);

        // Assert: CurrentUserService returns Actor (who is acting); TenantService returns Subject (data owner).
        Assert.Equal(actorUserId, currentUser.GetId());
        Assert.Equal(subjectTenantId, tenantService.GetTenantId());
    }
}
