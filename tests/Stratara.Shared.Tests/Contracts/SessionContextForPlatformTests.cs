using Stratara.Contracts.Session;

namespace Stratara.Shared.Tests.Contracts;

public class SessionContextForPlatformTests
{
    [Fact]
    public void ForPlatform_names_the_platform_as_the_actor_and_the_tenant_as_the_data_owner()
    {
        var tenantId = Guid.CreateVersion7();

        var session = SessionContext.ForPlatform(tenantId);

        Assert.Equal(SessionContext.SystemActorTenantId, session.ActorTenantId);
        Assert.Equal(SessionContext.SystemActorUserId, session.ActorUserId);
        Assert.Equal(tenantId, session.TenantId);
        Assert.Null(session.UserId);
    }

    [Fact]
    public void ForPlatform_carries_a_causation_id_so_the_work_can_append()
    {
        var session = SessionContext.ForPlatform(Guid.CreateVersion7());

        Assert.False(string.IsNullOrWhiteSpace(session.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(session.CausationId));
    }

    [Fact]
    public void ForPlatform_mints_fresh_identities_per_call()
    {
        var tenantId = Guid.CreateVersion7();

        var first = SessionContext.ForPlatform(tenantId);
        var second = SessionContext.ForPlatform(tenantId);

        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
        Assert.NotEqual(first.CausationId, second.CausationId);
    }

    [Fact]
    public void ForPlatform_takes_a_subject_user_where_the_work_concerns_one()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var session = SessionContext.ForPlatform(tenantId, userId);

        Assert.Equal(userId, session.UserId);
        Assert.Equal(SessionContext.SystemActorUserId, session.ActorUserId);
    }
}
