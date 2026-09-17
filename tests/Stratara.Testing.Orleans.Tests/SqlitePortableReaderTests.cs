using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The portable commit-order reader and the partition counter on SQLite, through the host: entries appended across
/// partitions are read back in every partition in position order, without a gap, up to the head.
/// </summary>
public sealed class SqlitePortableReaderTests
{
    [Fact]
    public async Task Entries_appended_across_partitions_are_read_back_in_position_order()
    {
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services.AddAggregatesFromAssemblyContaining<Account>());
        var accounts = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()).ToList();
        foreach (var accountId in accounts)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, ExecutionModelTestHost.DefaultTenantId, 1m));
            await events.AppendAsync<Account>(accountId, new AmountDeposited(accountId, 2m));
            await events.SaveChangesAsync();
        }

        await using var readScope = host.Services.CreateAsyncScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
        var read = new List<CommittedEntry>();
        for (var partition = 0; partition < 4; partition++)
        {
            var positions = new List<long>();
            var after = 0L;
            while (true)
            {
                var batch = await reader.ReadAfterAsync(partition, after, batchSize: 5, TestContext.Current.CancellationToken);
                positions.AddRange(batch.Entries.Select(entry => entry.Position));
                read.AddRange(batch.Entries);
                after = batch.Position;
                if (!batch.HasMore)
                {
                    break;
                }
            }

            Assert.Equal(Enumerable.Range(1, positions.Count).Select(position => (long)position), positions);
            Assert.Equal(positions.Count == 0 ? 0 : positions[^1], await reader.HeadAsync(partition, TestContext.Current.CancellationToken));
        }

        Assert.Equal(accounts.Count * 2, read.Count);
        foreach (var stream in read.GroupBy(entry => entry.Entry.StreamId))
        {
            Assert.Equal([1L, 2L], stream.OrderBy(entry => entry.Position).Select(entry => entry.Entry.Version));
        }
    }
}
