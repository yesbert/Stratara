using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Contracts.Messages;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;

namespace Stratara.Infrastructure.Tests.Outbox;

public class OutboxWorkerTests
{
    private readonly Mock<ILogger<OutboxWorker>> _loggerMock = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IWriteUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly Mock<IOutboxRepository> _outboxRepositoryMock = new();
    private readonly Mock<ICommandOutboxDispatcher> _commandDispatcherMock = new();
    private readonly Mock<IEventBundleOutboxDispatcher> _eventDispatcherMock = new();
    private readonly Mock<IOutboxLock> _outboxLockMock = new();
    private readonly Mock<IOutboxLockHandle> _outboxLockHandleMock = new();
    private readonly IOptions<OutboxOptions> _options = Options.Create(new OutboxOptions());

    public OutboxWorkerTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        _outboxLockHandleMock.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _outboxLockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_outboxLockHandleMock.Object);

        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);

        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IWriteUnitOfWork)))
            .Returns(_unitOfWorkMock.Object);

        _serviceProviderMock
            .Setup(p => p.GetService(typeof(ICommandOutboxDispatcher)))
            .Returns(_commandDispatcherMock.Object);

        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IEventBundleOutboxDispatcher)))
            .Returns(_eventDispatcherMock.Object);

        _unitOfWorkMock
            .Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_transactionMock.Object);

        _unitOfWorkMock
            .Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>()))
            .Returns(_outboxRepositoryMock.Object);
    }

    [Fact]
    public async Task StartAsync_LogsStartMessage()
    {
        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await worker.StartAsync(cts.Token);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopAsync_LogsStopMessage()
    {
        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StopAsync(CancellationToken.None);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesCommandsAndEvents()
    {
        var callCount = 0;

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxEntry>());

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callCount++;
            })
            .ReturnsAsync(new List<OutboxEntry>());

        using var cts = new CancellationTokenSource();

        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StartAsync(cts.Token);
        await Task.Delay(200);

        _outboxRepositoryMock.Verify(
            r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _outboxRepositoryMock.Verify(
            r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesOutboxEntriesInWhileLoop()
    {
        var commandCallCount = 0;
        var eventCallCount = 0;

        var commandEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = "{}",
            DataTypeName = typeof(CommandEnvelope).AssemblyQualifiedName!,
            BucketId = 0
        };

        var eventEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            DataJson = "{}",
            DataTypeName = typeof(EventBundle).AssemblyQualifiedName!,
            BucketId = 0
        };

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                commandCallCount++;
                return commandCallCount == 1
                    ? new List<OutboxEntry> { commandEntry }
                    : new List<OutboxEntry>();
            });

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                eventCallCount++;
                return eventCallCount == 1
                    ? new List<OutboxEntry> { eventEntry }
                    : new List<OutboxEntry>();
            });

        using var cts = new CancellationTokenSource();

        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StartAsync(cts.Token);
        await Task.Delay(300);

        _commandDispatcherMock.Verify(
            d => d.EnqueueOutboxEntriesAsync(
                It.Is<IReadOnlyList<OutboxEntry>>(l => l.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _eventDispatcherMock.Verify(
            d => d.EnqueueOutboxEntriesAsync(
                It.Is<IReadOnlyList<OutboxEntry>>(l => l.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_LockNotAcquired_SkipsDrainAndDoesNotInvokeDispatchers()
    {
        _outboxLockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IOutboxLockHandle?)null);

        using var cts = new CancellationTokenSource();

        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StartAsync(cts.Token);
        await Task.Delay(200);

        _outboxRepositoryMock.Verify(
            r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _outboxRepositoryMock.Verify(
            r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _commandDispatcherMock.Verify(
            d => d.EnqueueOutboxEntriesAsync(It.IsAny<IReadOnlyList<OutboxEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_LockAcquired_DisposesHandleAfterDrain()
    {
        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxEntry>());
        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxEntry>());

        using var cts = new CancellationTokenSource();

        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StartAsync(cts.Token);
        await Task.Delay(200);

        _outboxLockHandleMock.Verify(h => h.DisposeAsync(), Times.AtLeastOnce);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionInProcessing_ContinuesExecution()
    {
        var callCount = 0;

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("Test error");
                }
            })
            .ReturnsAsync(new List<OutboxEntry>());

        _outboxRepositoryMock
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxEntry>());

        using var cts = new CancellationTokenSource();

        var worker = new OutboxWorker(_loggerMock.Object, _scopeFactoryMock.Object, _outboxLockMock.Object, _options);

        await worker.StartAsync(cts.Token);
        await Task.Delay(500);

        Assert.True(callCount >= 1);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }
}
