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
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddAsync(new DummyCommand(), CancellationToken.None));
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

    private class DummyCommand : ICommandBase
    {
    }
}