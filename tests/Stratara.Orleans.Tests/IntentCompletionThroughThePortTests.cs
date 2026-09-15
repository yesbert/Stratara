using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The completion queue removes completed intents through the outbox repository port and never
/// sees a database context. A repository written before the batch removal existed — one that
/// implements only the single removal — still removes every completed intent.
/// </summary>
public sealed class IntentCompletionThroughThePortTests
{
    [Fact]
    public async Task A_repository_without_the_batch_override_removes_every_completed_intent()
    {
        var repository = new SingleRemovalRepository();
        var transaction = new Mock<ITransaction>();
        var unitOfWork = new Mock<IWriteUnitOfWork>();
        unitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        unitOfWork.Setup(u => u.CreateOutboxRepository(transaction.Object)).Returns(repository);

        var services = new ServiceCollection()
            .AddScoped(_ => unitOfWork.Object)
            .BuildServiceProvider();
        var queue = new IntentCompletionQueue(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrleansDispatchOptions { CompletionWindow = TimeSpan.FromMilliseconds(20), CompletionBatchSize = 8 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IntentCompletionQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            await queue.CompleteAsync(id);
        }

        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(ids.Order(), repository.Removed.Order());
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private sealed class SingleRemovalRepository : IOutboxRepository
    {
        public ConcurrentBag<Guid> Removed { get; } = [];

        public Task AddAsync<T>(T outboxData, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            Removed.Add(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> GetManyAsync<T>(int batchSizes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxEntry>>([]);
    }
}
