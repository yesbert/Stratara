using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

public class UnitOfWorkTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IDbContext;

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContextFactory(options);
    }

    [Fact]
    public async Task StartAsync_ReturnsTransaction()
    {
        var uow = new UnitOfWork<TestDbContext>(CreateFactory());

        var transaction = await uow.StartAsync();

        Assert.NotNull(transaction);
    }

    [Fact]
    public async Task Transaction_SaveChangesAsync_ReturnsCount()
    {
        var uow = new UnitOfWork<TestDbContext>(CreateFactory());
        var transaction = await uow.StartAsync();

        var result = await transaction.SaveChangesAsync();

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Transaction_DisposeAsync_DoesNotThrow()
    {
        var uow = new UnitOfWork<TestDbContext>(CreateFactory());
        var transaction = await uow.StartAsync();

        var exception = await Record.ExceptionAsync(async () => await transaction.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_CreatesNewTransactionEachCall()
    {
        var uow = new UnitOfWork<TestDbContext>(CreateFactory());

        var transaction1 = await uow.StartAsync();
        var transaction2 = await uow.StartAsync();

        Assert.NotSame(transaction1, transaction2);
    }
}
