using System.Diagnostics;
using Stratara.Contracts.Session;
using Stratara.Sessions.Session;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Tests;

public class SessionContextProviderTests
{
    private static SessionContext NewContext(string correlationId = "corr-1", string? causationId = null,
        Guid? actorTenantId = null, Guid? actorUserId = null,
        Guid? subjectTenantId = null, Guid? subjectUserId = null, Guid? clientId = null) =>
        new(
            correlationId,
            causationId,
            null,
            actorTenantId ?? Guid.NewGuid(),
            actorUserId ?? Guid.NewGuid(),
            subjectTenantId ?? actorTenantId ?? Guid.NewGuid(),
            subjectUserId,
            clientId);

    [Fact]
    public void Current_IsNull_ByDefault()
    {
        var provider = new SessionContextProvider();

        Assert.Null(provider.Current);
    }

    [Fact]
    public void Set_StoresContext()
    {
        var provider = new SessionContextProvider();
        var context = NewContext(Guid.NewGuid().ToString("N"));

        provider.Set(context);

        Assert.Equal(context, provider.Current);
    }

    [Fact]
    public void Clear_RemovesContext()
    {
        var provider = new SessionContextProvider();
        var context = NewContext(Guid.NewGuid().ToString("N"));

        provider.Set(context);
        provider.Clear();

        Assert.Null(provider.Current);
    }

    [Fact]
    public void Set_OverwritesPreviousContext()
    {
        var provider = new SessionContextProvider();
        var first = NewContext("first");
        var second = NewContext("second");

        provider.Set(first);
        provider.Set(second);

        Assert.Equal(second, provider.Current);
        Assert.Equal("second", provider.Current!.CorrelationId);
    }

    [Fact]
    public void Set_UpdatesActivityTags()
    {
        var provider = new SessionContextProvider();
        var actorTenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectTenantId = Guid.NewGuid();
        var context = NewContext("corr-123", "cause-456",
            actorTenantId, actorUserId, subjectTenantId);

        using var activity = new Activity("test-tags");
        activity.Start();

        provider.Set(context);

        Assert.Equal("corr-123", activity.GetTagItem(ApplicationDiagnostics.CorrelationIdTagName));
        Assert.Equal("cause-456", activity.GetTagItem(ApplicationDiagnostics.CausationIdTagName));
        // Tags surface TenantId (data owner) + ActorUserId (who-did-it).
        Assert.Equal(subjectTenantId, activity.GetTagItem(ApplicationDiagnostics.TenantIdTagName));
        Assert.Equal(actorUserId, activity.GetTagItem(ApplicationDiagnostics.UserIdTagName));
    }

    [Fact]
    public void Clear_ClearsActivityTags()
    {
        var provider = new SessionContextProvider();
        var context = NewContext("corr-123", "cause-456");

        using var activity = new Activity("test-clear-tags");
        activity.Start();

        provider.Set(context);
        provider.Clear();

        Assert.Null(activity.GetTagItem(ApplicationDiagnostics.CorrelationIdTagName));
        Assert.Null(activity.GetTagItem(ApplicationDiagnostics.CausationIdTagName));
        Assert.Null(activity.GetTagItem(ApplicationDiagnostics.TenantIdTagName));
        Assert.Null(activity.GetTagItem(ApplicationDiagnostics.UserIdTagName));
    }

    [Fact]
    public void Set_WithClientId_StoresFullContext()
    {
        var provider = new SessionContextProvider();
        var clientId = Guid.NewGuid();
        var context = new SessionContext(
            "corr",
            null,
            "conn-123",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            clientId);

        provider.Set(context);

        Assert.Equal(clientId, provider.Current!.ClientId);
        Assert.Equal("conn-123", provider.Current.ClientConnectionId);
    }
}
