using Stratara.Abstractions.Persistence;
using Stratara.Projections;
using Xunit;

namespace Stratara.Projections.Tests;

public class ProjectionTransactionExtensionsTests
{
    private sealed class StubTransaction(Func<Task<int>> onSave) : ITransaction
    {
        public int SaveAttempts { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return onSave();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static StubTransaction Conflicting() =>
        new(() => throw new ConcurrencyConflictException());

    private static StubTransaction Committing(int rows) => new(() => Task.FromResult(rows));

    [Fact]
    public async Task CommitsNormally_WhenThereIsNoConflict()
    {
        var transaction = Committing(3);
        var probed = false;

        var rows = await transaction.SaveChangesIdempotentAsync(_ =>
        {
            probed = true;
            return Task.FromResult(true);
        });

        Assert.Equal(3, rows);
        Assert.False(probed);
    }

    [Fact]
    public async Task AConflictOnAVanishedRow_IsSatisfied()
    {
        var transaction = Conflicting();

        var rows = await transaction.SaveChangesIdempotentAsync(_ => Task.FromResult(false));

        Assert.Equal(0, rows);
        Assert.Equal(1, transaction.SaveAttempts);
    }

    [Fact]
    public async Task AConflictOnARowThatStillExists_IsNotSuppressed()
    {
        var transaction = Conflicting();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            transaction.SaveChangesIdempotentAsync(_ => Task.FromResult(true)));
    }

    [Fact]
    public async Task TheProbeRunsOnlyAfterAConflict()
    {
        var probes = 0;
        var transaction = Conflicting();

        await transaction.SaveChangesIdempotentAsync(_ =>
        {
            probes++;
            return Task.FromResult(false);
        });

        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task AFailureThatIsNotAConflict_IsNotTouched()
    {
        var transaction = new StubTransaction(() => throw new InvalidOperationException("the database is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.SaveChangesIdempotentAsync(_ => Task.FromResult(false)));
    }
}
