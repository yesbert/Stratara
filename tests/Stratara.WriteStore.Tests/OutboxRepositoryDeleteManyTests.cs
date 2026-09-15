using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

/// <summary>
/// The framework's repository removes a batch of stored messages with one statement rather than the
/// port's default of one removal per message.
/// </summary>
public sealed class OutboxRepositoryDeleteManyTests
{
    [Fact]
    public async Task DeleteManyAsync_removes_the_batch_in_one_statement_and_keeps_the_rest()
    {
        var counter = new DeleteCounter();
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var context = new TestWriteDbContext(new DbContextOptionsBuilder<TestWriteDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options);
        await context.Database.EnsureCreatedAsync();

        var repository = new OutboxRepository(context);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var id in ids)
        {
            await repository.AddAsync(id, new Payload(id.ToString()), CancellationToken.None);
        }

        await context.SaveChangesAsync();
        counter.Deletes = 0;

        await repository.DeleteManyAsync(ids[..3], CancellationToken.None);

        Assert.Equal(1, counter.Deletes);
        var remaining = await context.Set<OutboxEntry>().AsNoTracking().Select(e => e.Id).ToListAsync();
        Assert.Equal(ids[3..].Order(), remaining.Order());
    }

    [Fact]
    public async Task DeleteManyAsync_with_no_ids_issues_no_statement()
    {
        var counter = new DeleteCounter();
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var context = new TestWriteDbContext(new DbContextOptionsBuilder<TestWriteDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options);
        await context.Database.EnsureCreatedAsync();

        await new OutboxRepository(context).DeleteManyAsync([], CancellationToken.None);

        Assert.Equal(0, counter.Deletes);
    }

    private sealed record Payload(string Value);

    private sealed class DeleteCounter : DbCommandInterceptor
    {
        public int Deletes { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Deletes++;
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
