using Stratara.EventSourcing.Pipeline.CommandAudit;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.Pipeline.CommandAudit.Tests;

public class CommandAuditBehaviorTests
{
    public sealed record TestCommand(string Payload) : ICommand;

    public sealed record TestCommandWithResult(int Value) : ICommand<int>;

    public sealed record TestQuery : IQuery<string>;

    [Fact]
    public async Task HandleAsync_NoResult_CommandRequest_WritesAuditThenInvokesNext()
    {
        var harness = new Harness();
        var sut = new CommandAuditBehavior<TestCommand>(harness.UnitOfWork.Object);
        var nextCalled = false;

        await sut.HandleAsync(new TestCommand("hi"), () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(nextCalled);
        harness.AuditRepository.Verify(
            r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithResult_CommandRequest_WritesAuditThenInvokesNextAndReturnsResult()
    {
        var harness = new Harness();
        var sut = new CommandAuditBehavior<TestCommandWithResult, int>(harness.UnitOfWork.Object);

        var result = await sut.HandleAsync(
            new TestCommandWithResult(42),
            () => Task.FromResult(42),
            CancellationToken.None);

        Assert.Equal(42, result);
        harness.AuditRepository.Verify(
            r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithResult_NonCommandRequest_DoesNotWriteAuditButStillInvokesNext()
    {
        var harness = new Harness();
        var sut = new CommandAuditBehavior<TestQuery, string>(harness.UnitOfWork.Object);

        var result = await sut.HandleAsync(
            new TestQuery(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
        harness.AuditRepository.Verify(
            r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_AuditWriteRunsBeforeNext()
    {
        var harness = new Harness();
        var order = new List<string>();
        harness.AuditRepository
            .Setup(r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("audit"))
            .ReturnsAsync(Guid.NewGuid());

        var sut = new CommandAuditBehavior<TestCommand>(harness.UnitOfWork.Object);

        await sut.HandleAsync(new TestCommand("seq"), () =>
        {
            order.Add("next");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(["audit", "next"], order);
    }

    [Fact]
    public async Task HandleAsync_NoResult_NonCommandRequest_PassesThrough()
    {
        var harness = new Harness();
        // A non-ICommand request type that satisfies IRequest only.
        // Build a minimal stub.
        var sut = new CommandAuditBehavior<StubRequest>(harness.UnitOfWork.Object);
        var nextCalled = false;

        await sut.HandleAsync(new StubRequest(), () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(nextCalled);
        harness.AuditRepository.Verify(
            r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public sealed record StubRequest : IRequest;

    private sealed class Harness
    {
        public Mock<IWriteUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ITransaction> Transaction { get; } = new();
        public Mock<ICommandAuditRepository> AuditRepository { get; } = new();

        public Harness()
        {
            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateCommandAuditRepository(It.IsAny<ITransaction>())).Returns(AuditRepository.Object);
            AuditRepository
                .Setup(r => r.AddAsync(It.IsAny<ICommandBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
        }
    }
}
