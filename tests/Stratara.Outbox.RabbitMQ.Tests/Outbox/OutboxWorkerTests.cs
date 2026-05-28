using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Contracts.Messages;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;

namespace Stratara.Outbox.RabbitMQ.Tests.Outbox;

public class OutboxWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_LockNotAcquired_SkipsDrainAndDoesNotInvokeDispatchers()
    {
        var harness = new Harness(grantLock: false);

        await harness.RunOneCycleAsync();

        harness.OutboxLock.Verify(
            l => l.TryAcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        harness.CommandDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(It.IsAny<IEnumerable<OutboxEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.EventDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(It.IsAny<IEnumerable<OutboxEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_LockAcquiredEmptyBatches_DoesNotInvokeDispatchers()
    {
        var harness = new Harness(grantLock: true);
        harness.OutboxRepository
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        harness.OutboxRepository
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await harness.RunOneCycleAsync();

        harness.CommandDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(It.IsAny<IEnumerable<OutboxEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.EventDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(It.IsAny<IEnumerable<OutboxEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_LockAcquiredWithBatches_DispatchesAndLoopsUntilEmpty()
    {
        var harness = new Harness(grantLock: true);

        var commandEntry = NewOutboxEntry();
        var eventEntry = NewOutboxEntry();

        var commandSeq = new Queue<IReadOnlyList<OutboxEntry>>([[commandEntry], []]);
        var eventSeq = new Queue<IReadOnlyList<OutboxEntry>>([[eventEntry], []]);

        harness.OutboxRepository
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandSeq.Dequeue);
        harness.OutboxRepository
            .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventSeq.Dequeue);

        await harness.RunOneCycleAsync();

        harness.CommandDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(
                It.Is<IEnumerable<OutboxEntry>>(entries => entries.Single().Id == commandEntry.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.EventDispatcher.Verify(
            d => d.EnqueueOutboxEntriesAsync(
                It.Is<IEnumerable<OutboxEntry>>(entries => entries.Single().Id == eventEntry.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_LockHandleIsDisposedAfterDrain()
    {
        var handle = new RecordingLockHandle();
        var harness = new Harness(grantLock: true, handleOverride: handle);

        await harness.RunOneCycleAsync();

        Assert.True(handle.Disposed);
    }

    private static OutboxEntry NewOutboxEntry() => new()
    {
        Id = Guid.NewGuid(),
        DataJson = "{}",
        DataTypeName = "T",
        BucketId = 0,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingLockHandle : IOutboxLockHandle
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public Mock<IOutboxLock> OutboxLock { get; } = new();
        public Mock<IWriteUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ITransaction> Transaction { get; } = new();
        public Mock<IOutboxRepository> OutboxRepository { get; } = new();
        public Mock<ICommandOutboxDispatcher> CommandDispatcher { get; } = new();
        public Mock<IEventBundleOutboxDispatcher> EventDispatcher { get; } = new();

        private readonly OutboxWorker _sut;
        private readonly TaskCompletionSource _lockProbed = new();

        public Harness(bool grantLock, IOutboxLockHandle? handleOverride = null)
        {
            var handle = handleOverride ?? Mock.Of<IOutboxLockHandle>();
            OutboxLock
                .Setup(l => l.TryAcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<TimeSpan, CancellationToken>((_, _) =>
                {
                    _lockProbed.TrySetResult();
                    return Task.FromResult(grantLock ? handle : null);
                });

            UnitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(u => u.CreateOutboxRepository(It.IsAny<ITransaction>())).Returns(OutboxRepository.Object);

            OutboxRepository
                .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            OutboxRepository
                .Setup(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var services = new ServiceCollection();
            services.AddSingleton(UnitOfWork.Object);
            services.AddSingleton(CommandDispatcher.Object);
            services.AddSingleton(EventDispatcher.Object);
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var options = Options.Create(new OutboxOptions
            {
                PollingIntervalSeconds = 60,
                BatchSize = 100,
                LockLeaseSeconds = 30,
            });

            _sut = new OutboxWorker(
                NullLogger<OutboxWorker>.Instance,
                scopeFactory,
                OutboxLock.Object,
                options);
        }

        public async Task RunOneCycleAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _sut.StartAsync(cts.Token);

            await _lockProbed.Task.WaitAsync(cts.Token);
            await Task.Delay(50, cts.Token);

            await _sut.StopAsync(CancellationToken.None);
        }
    }
}
