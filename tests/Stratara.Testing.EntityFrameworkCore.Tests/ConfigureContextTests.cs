using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// The overload that takes a connection string opens a connection per context to a shared database and applies the
/// caller's options after the provider, so an interceptor the caller adds runs on every save of the write stack.
/// </summary>
public class ConfigureContextTests
{
    [Fact]
    public async Task An_interceptor_added_through_the_overload_runs_on_the_write_stacks_save()
    {
        var connectionString = $"Data Source=configure-context-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync(TestContext.Current.CancellationToken);
        var interceptor = new CountingInterceptor();
        var services = new ServiceCollection();
        services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(
            connectionString,
            EventStoreTestHost.DefaultTenantId,
            options => options.AddInterceptors(interceptor));
        services.AddAggregatesFromAssemblyContaining<Account>();
        await using var provider = services.BuildServiceProvider();
        await using (var context = await provider.CreateAsyncScope().ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        var id = Guid.CreateVersion7();
        await using (var scope = provider.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 5m));
            await events.SaveChangesAsync();
        }

        Assert.True(interceptor.Saves > 0, "the interceptor did not run");
        await using var readScope = provider.CreateAsyncScope();
        Assert.Equal(5m, (await readScope.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Account>(id))!.Balance);
    }

    private sealed class CountingInterceptor : SaveChangesInterceptor
    {
        private int _saves;

        public int Saves => _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return ValueTask.FromResult(result);
        }
    }
}
