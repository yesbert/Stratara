using Xunit;

namespace Stratara.Testing.Tests;

public class TestSessionContextAndTenantsTests
{
    [Fact]
    public void ForTenant_sets_actor_equal_to_subject()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();

        var context = TestSessionContext.ForTenant(tenant, user);

        Assert.Equal(tenant, context.TenantId);
        Assert.Equal(tenant, context.ActorTenantId);
        Assert.Equal(user, context.UserId);
        Assert.Equal(user, context.ActorUserId);
        Assert.False(string.IsNullOrEmpty(context.CorrelationId));
    }

    [Fact]
    public void ForActorAndSubject_keeps_actor_and_subject_distinct()
    {
        var actorTenant = Guid.CreateVersion7();
        var actorUser = Guid.CreateVersion7();
        var subjectTenant = Guid.CreateVersion7();

        var context = TestSessionContext.ForActorAndSubject(actorTenant, actorUser, subjectTenant);

        Assert.Equal(actorTenant, context.ActorTenantId);
        Assert.Equal(subjectTenant, context.TenantId);
        Assert.NotEqual(context.ActorTenantId, context.TenantId);
    }

    [Fact]
    public void Provider_set_and_clear_updates_current()
    {
        var provider = TestSessionContextProvider.ForTenant(Guid.CreateVersion7());
        Assert.NotNull(provider.Current);

        var replacement = TestSessionContext.ForTenant(Guid.CreateVersion7());
        provider.Set(replacement);
        Assert.Same(replacement, provider.Current);

        provider.Clear();
        Assert.Null(provider.Current);
    }

    [Fact]
    public void Empty_provider_starts_with_no_context()
    {
        var provider = new TestSessionContextProvider();
        Assert.Null(provider.Current);
    }

    [Fact]
    public void TestTenants_is_deterministic_per_slug()
    {
        Assert.Equal(TestTenants.Of("acme"), TestTenants.Of("acme"));
        Assert.NotEqual(TestTenants.Of("acme"), TestTenants.Of("contoso"));
        Assert.NotEqual(Guid.Empty, TestTenants.Of("acme"));
    }

    [Fact]
    public void TestTenants_rejects_blank_slugs()
    {
        Assert.Throws<ArgumentException>(() => TestTenants.Of("  "));
    }
}
