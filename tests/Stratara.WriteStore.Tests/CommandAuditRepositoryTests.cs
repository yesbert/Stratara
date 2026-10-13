using Microsoft.EntityFrameworkCore;
using Stratara.Contracts.Session;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class CommandAuditRepositoryTests
{
    private static TestWriteDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestWriteDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestWriteDbContext(options);
    }

    [Fact]
    public async Task AddAsync_Throws_When_SessionContext_Is_Null()
    {
        await using var ctx = CreateContext(nameof(AddAsync_Throws_When_SessionContext_Is_Null));
        var sessionProvider = new Mock<ISessionContextProvider>(MockBehavior.Strict);
        sessionProvider.SetupGet(s => s.Current).Returns((SessionContext?)null);

        var serializer = new Mock<ISecureJsonSerializer>(MockBehavior.Strict);

        var repo = new CommandAuditRepository(ctx, sessionProvider.Object, serializer.Object);
        var exception = await Assert.ThrowsAsync<SessionRequiredException>(() => repo.AddAsync(new DummyCommand(), CancellationToken.None));
        Assert.Equal("Session context is null", exception.Message);
    }

    [Fact]
    public async Task AddAsync_Persists_LogEntry_And_Sets_CausationId_And_Calls_Serializer()
    {
        await using var ctx = CreateContext(nameof(AddAsync_Persists_LogEntry_And_Sets_CausationId_And_Calls_Serializer));

        var actorTenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectTenantId = Guid.NewGuid();
        var initialSession = new SessionContext(
            Guid.NewGuid().ToString("N"),
            null,
            null,
            actorTenantId,
            actorUserId,
            subjectTenantId,
            null);

        var sessionProvider = new Mock<ISessionContextProvider>(MockBehavior.Strict);
        // Current returns initial first, then after Set we still allow reading (not required here)
        sessionProvider.SetupGet(s => s.Current).Returns(() => initialSession);
        sessionProvider.Setup(s => s.Set(It.IsAny<SessionContext>()));

        var serializer = new Mock<ISecureJsonSerializer>(MockBehavior.Strict);
        serializer
            .Setup(s => s.SerializeAsync<object>(It.IsAny<object>(), subjectTenantId, (Guid?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"ok\":true}");

        var repo = new CommandAuditRepository(ctx, sessionProvider.Object, serializer.Object);

        var command = new DummyCommand();
        var id = await repo.AddAsync(command, CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Verify serializer was called with the Subject identifiers (encryption AAD uses Subject).
        serializer.Verify(s => s.SerializeAsync<object>(command, subjectTenantId, (Guid?)null, It.IsAny<CancellationToken>()), Times.Once);
        // Verify causation Set() was called changing causation id to created id (N format)
        sessionProvider.Verify(s => s.Set(It.Is<SessionContext>(sc => sc.CausationId == id.ToString("N"))), Times.Once);

        var saved = await ctx.Set<CommandLogEntry>().SingleAsync();
        Assert.Equal(id, saved.Id);
        Assert.Equal(initialSession.CorrelationId, saved.CorrelationId);
        Assert.Equal(subjectTenantId, saved.TenantId);
        Assert.Equal(actorTenantId, saved.ActorTenantId);
        Assert.Equal(actorUserId, saved.ActorUserId);
        Assert.Equal("{\"ok\":true}", saved.CommandJson);
        Assert.False(string.IsNullOrWhiteSpace(saved.CommandTypeName));
        Assert.True(saved.BucketId >= 0);
        Assert.True(saved.Timestamp <= DateTime.UtcNow);
    }

    /// <summary>
    /// <c>mediator-dispatch</c> → the record of a command that names an owner is written for that owner
    /// and names it as its data owner, with the session's actor as its actor.
    /// </summary>
    [Fact]
    public async Task AddAsync_CommandNamingAnOwner_EncryptsForAndAttributesToThatOwner()
    {
        await using var ctx = CreateContext(nameof(AddAsync_CommandNamingAnOwner_EncryptsForAndAttributesToThatOwner));
        var ownerTenantId = Guid.NewGuid();
        var session = ASessionOfAnotherTenant();
        var serializer = ASerializerExpecting(ownerTenantId, null);

        var repo = new CommandAuditRepository(ctx, AProviderFor(session), serializer.Object);
        await repo.AddAsync(new OwnedCommand(ownerTenantId, null), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Set<CommandLogEntry>().SingleAsync();
        Assert.Equal(ownerTenantId, saved.TenantId);
        Assert.Null(saved.UserId);
        serializer.VerifyAll();
    }

    [Fact]
    public async Task AddAsync_CommandNamingAnOwner_KeepsTheSessionsActor()
    {
        await using var ctx = CreateContext(nameof(AddAsync_CommandNamingAnOwner_KeepsTheSessionsActor));
        var ownerTenantId = Guid.NewGuid();
        var session = ASessionOfAnotherTenant();

        var repo = new CommandAuditRepository(ctx, AProviderFor(session), ASerializerExpecting(ownerTenantId, null).Object);
        await repo.AddAsync(new OwnedCommand(ownerTenantId, null), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Set<CommandLogEntry>().SingleAsync();
        Assert.Equal(session.ActorTenantId, saved.ActorTenantId);
        Assert.Equal(session.ActorUserId, saved.ActorUserId);
        Assert.Equal(session.CorrelationId, saved.CorrelationId);
    }

    [Fact]
    public async Task AddAsync_CommandNamingAnOwnerAndUser_WritesForBoth()
    {
        await using var ctx = CreateContext(nameof(AddAsync_CommandNamingAnOwnerAndUser_WritesForBoth));
        var ownerTenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var serializer = ASerializerExpecting(ownerTenantId, ownerUserId);

        var repo = new CommandAuditRepository(ctx, AProviderFor(ASessionOfAnotherTenant()), serializer.Object);
        await repo.AddAsync(new OwnedCommand(ownerTenantId, ownerUserId), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Set<CommandLogEntry>().SingleAsync();
        Assert.Equal(ownerTenantId, saved.TenantId);
        Assert.Equal(ownerUserId, saved.UserId);
        serializer.VerifyAll();
    }

    [Fact]
    public async Task AddAsync_CommandNamingAnEmptyTenant_ThrowsAndAddsNoRow()
    {
        await using var ctx = CreateContext(nameof(AddAsync_CommandNamingAnEmptyTenant_ThrowsAndAddsNoRow));
        var sessionProvider = new Mock<ISessionContextProvider>(MockBehavior.Strict);
        sessionProvider.SetupGet(s => s.Current).Returns(ASessionOfAnotherTenant());
        var serializer = new Mock<ISecureJsonSerializer>(MockBehavior.Strict);

        var repo = new CommandAuditRepository(ctx, sessionProvider.Object, serializer.Object);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => repo.AddAsync(new OwnedCommand(Guid.Empty, null), CancellationToken.None));
        await ctx.SaveChangesAsync();

        Assert.Contains(nameof(OwnedCommand), exception.Message);
        Assert.Empty(await ctx.Set<CommandLogEntry>().ToListAsync());
    }

    /// <summary>
    /// A command that names no owner is recorded as before: for the session's data-owner tenant and
    /// data-owner user, whichever tenant its data in fact belongs to.
    /// </summary>
    [Fact]
    public async Task AddAsync_CommandNamingNone_RecordsForTheSessionsTenantAndUser()
    {
        await using var ctx = CreateContext(nameof(AddAsync_CommandNamingNone_RecordsForTheSessionsTenantAndUser));
        var session = ASessionOfAnotherTenant() with { UserId = Guid.NewGuid() };
        var serializer = ASerializerExpecting(session.TenantId, session.UserId);

        var repo = new CommandAuditRepository(ctx, AProviderFor(session), serializer.Object);
        await repo.AddAsync(new DummyCommand(), CancellationToken.None);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Set<CommandLogEntry>().SingleAsync();
        Assert.Equal(session.TenantId, saved.TenantId);
        Assert.Equal(session.UserId, saved.UserId);
        serializer.VerifyAll();
    }

    private static SessionContext ASessionOfAnotherTenant() =>
        new(Guid.NewGuid().ToString("N"), null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);

    private static ISessionContextProvider AProviderFor(SessionContext session)
    {
        var provider = new Mock<ISessionContextProvider>(MockBehavior.Strict);
        provider.SetupGet(s => s.Current).Returns(session);
        provider.Setup(s => s.Set(It.IsAny<SessionContext>()));
        return provider.Object;
    }

    private static Mock<ISecureJsonSerializer> ASerializerExpecting(Guid tenantId, Guid? userId)
    {
        var serializer = new Mock<ISecureJsonSerializer>(MockBehavior.Strict);
        serializer
            .Setup(s => s.SerializeAsync<object>(It.IsAny<object>(), tenantId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");
        return serializer;
    }

    private class DummyCommand : ICommandBase
    {
    }

    private sealed class OwnedCommand(Guid tenantId, Guid? userId) : ICommandBase, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => tenantId;

        Guid? INamesDataOwner.DataOwnerUserId => userId;
    }
}