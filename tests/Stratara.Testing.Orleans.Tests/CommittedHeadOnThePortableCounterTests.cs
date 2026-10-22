using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The portable reader on SQLite does not hold its head back, so the committed head a mark is taken from is its head, in
/// every partition, empty ones included.
/// </summary>
public sealed class CommittedHeadOnThePortableCounterTests
{
    [Fact]
    public async Task The_committed_head_is_the_head_in_every_partition()
    {
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services.AddAggregatesFromAssemblyContaining<Account>());
        for (var i = 0; i < 6; i++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            var accountId = Guid.NewGuid();
            await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, ExecutionModelTestHost.DefaultTenantId, 1m));
            await events.SaveChangesAsync();
        }

        await using var readScope = host.Services.CreateAsyncScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
        for (var partition = 0; partition < 4; partition++)
        {
            Assert.Equal(
                await reader.HeadAsync(partition, TestContext.Current.CancellationToken),
                await reader.CommittedHeadAsync(partition, TestContext.Current.CancellationToken));
        }
    }
}
