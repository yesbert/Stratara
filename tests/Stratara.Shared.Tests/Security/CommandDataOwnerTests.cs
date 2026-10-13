using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Shared.Security;

namespace Stratara.Shared.Tests.Security;

/// <summary>
/// <c>mediator-dispatch</c> → a command can name the owner of its protected fields. Every place that
/// serializes or deserializes a command asks this one resolution, so what it answers is what the copies
/// of a command are written and read for.
/// </summary>
public class CommandDataOwnerTests
{
    private static readonly Guid SessionTenant = Guid.CreateVersion7();
    private static readonly Guid SessionUser = Guid.CreateVersion7();
    private static readonly Guid ActorTenant = Guid.CreateVersion7();
    private static readonly Guid ActorUser = Guid.CreateVersion7();
    private static readonly Guid OwnerTenant = Guid.CreateVersion7();
    private static readonly Guid OwnerUser = Guid.CreateVersion7();

    private static readonly SessionContext Session =
        new("correlation", "causation", null, ActorTenant, ActorUser, SessionTenant, SessionUser);

    private sealed record Plain(string Text) : ICommand;

    private sealed record Owned(Guid Tenant, string Text) : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;
    }

    private sealed record OwnedByAUser(Guid Tenant, Guid User, string Text) : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;

        Guid? INamesDataOwner.DataOwnerUserId => User;
    }

    [Fact]
    public void ForTransit_CommandNamingAnOwner_ReturnsThatOwner()
    {
        var owner = CommandDataOwner.ForTransit(new OwnedByAUser(OwnerTenant, OwnerUser, "x"), Session);

        Assert.Equal(new CommandDataOwner(OwnerTenant, OwnerUser, Named: true), owner);
    }

    [Fact]
    public void ForTransit_CommandNamingATenantAlone_NamesNoUser()
    {
        var owner = CommandDataOwner.ForTransit(new Owned(OwnerTenant, "x"), Session);

        Assert.Equal(new CommandDataOwner(OwnerTenant, null, Named: true), owner);
    }

    [Fact]
    public void ForTransit_CommandNamingNone_ReturnsSessionTenantAndActorUser()
    {
        var owner = CommandDataOwner.ForTransit(new Plain("x"), Session);

        Assert.Equal(new CommandDataOwner(SessionTenant, ActorUser, Named: false), owner);
    }

    [Fact]
    public void ForRecord_CommandNamingAnOwner_ReturnsThatOwner()
    {
        var owner = CommandDataOwner.ForRecord(new OwnedByAUser(OwnerTenant, OwnerUser, "x"), Session);

        Assert.Equal(new CommandDataOwner(OwnerTenant, OwnerUser, Named: true), owner);
    }

    [Fact]
    public void ForRecord_CommandNamingNone_ReturnsSessionTenantAndUser()
    {
        var owner = CommandDataOwner.ForRecord(new Plain("x"), Session);

        Assert.Equal(new CommandDataOwner(SessionTenant, SessionUser, Named: false), owner);
    }

    /// <summary>The two copies of a command that names an owner cannot disagree.</summary>
    [Fact]
    public void ForTransit_AndForRecord_AgreeForACommandNamingAnOwner()
    {
        var command = new OwnedByAUser(OwnerTenant, OwnerUser, "x");

        Assert.Equal(CommandDataOwner.ForTransit(command, Session), CommandDataOwner.ForRecord(command, Session));
    }

    [Fact]
    public void ForTransit_CommandNamingAnEmptyTenant_ThrowsNamingTheCommandType()
    {
        var exception = Assert.Throws<ArgumentException>(() => CommandDataOwner.ForTransit(new Owned(Guid.Empty, "x"), Session));

        Assert.Contains(typeof(Owned).FullName!, exception.Message);
        Assert.Contains(nameof(INamesDataOwner), exception.Message);
    }

    [Fact]
    public void ForRecord_CommandNamingAnEmptyTenant_ThrowsNamingTheCommandType()
    {
        var exception = Assert.Throws<ArgumentException>(() => CommandDataOwner.ForRecord(new Owned(Guid.Empty, "x"), Session));

        Assert.Contains(typeof(Owned).FullName!, exception.Message);
    }

    [Fact]
    public void ForRead_EnvelopeNamingAnOwner_ReturnsIt()
    {
        var envelope = new CommandEnvelope(Guid.CreateVersion7(), "{}", "type", "{}") { DataOwnerTenantId = OwnerTenant, DataOwnerUserId = OwnerUser };

        Assert.Equal(new CommandDataOwner(OwnerTenant, OwnerUser, Named: true), CommandDataOwner.ForRead(envelope, Session));
    }

    [Fact]
    public void ForRead_EnvelopeNamingNone_ReturnsSessionTenantAndActorUser()
    {
        var envelope = new CommandEnvelope(Guid.CreateVersion7(), "{}", "type", "{}");

        Assert.Equal(new CommandDataOwner(SessionTenant, ActorUser, Named: false), CommandDataOwner.ForRead(envelope, Session));
    }

    /// <summary>A user without a tenant names no owner: the tenant is what says an owner was named.</summary>
    [Fact]
    public void ForRead_AUserWithoutATenant_IsReadForTheSession()
    {
        Assert.Equal(
            new CommandDataOwner(SessionTenant, ActorUser, Named: false),
            CommandDataOwner.ForRead(null, OwnerUser, Session));
    }

    /// <summary>What a carrier records beside the session: the owner a command named, and nothing for one taken from the session.</summary>
    [Fact]
    public void NamedTenantIdAndUserId_AreTheOwnerOnlyWhereTheCommandNamedIt()
    {
        var named = CommandDataOwner.ForTransit(new OwnedByAUser(OwnerTenant, OwnerUser, "x"), Session);
        var fromTheSession = CommandDataOwner.ForTransit(new Plain("x"), Session);

        Assert.Equal(OwnerTenant, named.NamedTenantId);
        Assert.Equal(OwnerUser, named.NamedUserId);
        Assert.Null(fromTheSession.NamedTenantId);
        Assert.Null(fromTheSession.NamedUserId);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForTransit(null!, Session));
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForTransit(new Plain("x"), null!));
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForRecord(null!, Session));
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForRecord(new Plain("x"), null!));
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForRead(null!, Session));
        Assert.Throws<ArgumentNullException>(() => CommandDataOwner.ForRead(OwnerTenant, null, null!));
    }
}
