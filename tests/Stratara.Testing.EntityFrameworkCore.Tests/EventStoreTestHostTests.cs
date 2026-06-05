using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

public class EventStoreTestHostTests
{
    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(s => s.AddAggregatesFromAssemblyContaining<Account>());

    [Fact]
    public async Task Creates_appends_and_rehydrates_through_the_real_stack()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 100m));
            await events.AppendAsync<Account>(id, new AmountWithdrawn(30m));
            await events.SaveChangesAsync();
        });

        var account = await host.AggregateAsync<Account>(id);

        Assert.NotNull(account);
        Assert.Equal("Ada", account!.Owner);
        Assert.Equal(70m, account.Balance);
        Assert.Equal(EventStoreTestHost.DefaultTenantId, account.TenantId);
    }

    [Fact]
    public async Task Appends_across_separate_save_calls_persist_via_shared_connection()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 0m));
            await events.SaveChangesAsync();
        });
        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Account>(id, new AmountDeposited(40m));
            await events.AppendAsync<Account>(id, new AmountDeposited(2m));
            await events.SaveChangesAsync();
        });

        var account = await host.AggregateAsync<Account>(id);
        Assert.Equal(42m, account!.Balance);
    }

    [Fact]
    public async Task SaveChanges_emits_a_recorded_outbox_bundle()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 5m));
            await events.SaveChangesAsync();
        });

        Assert.NotEmpty(host.Outbox.Bundles);
    }

    [Fact]
    public async Task Aggregating_an_unknown_stream_returns_null()
    {
        await using var host = CreateHost();

        var account = await host.AggregateAsync<Account>(Guid.CreateVersion7());

        Assert.Null(account);
    }

    [Fact]
    public async Task Recreating_an_existing_stream_throws()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 1m));
            await events.SaveChangesAsync();
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await host.ExecuteAsync(async events =>
            {
                await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Eve", 9m));
                await events.SaveChangesAsync();
            }));
    }

    [Fact]
    public async Task Rehydrates_correctly_past_the_snapshot_threshold()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, EventStoreTestHost.DefaultTenantId, "Ada", 0m));
            await events.SaveChangesAsync();
        });

        for (var i = 0; i < 60; i++)
        {
            await host.ExecuteAsync(async events =>
            {
                await events.AppendAsync<Account>(id, new AmountDeposited(1m));
                await events.SaveChangesAsync();
            });
        }

        var account = await host.AggregateAsync<Account>(id);
        Assert.Equal(60m, account!.Balance);
    }

    [Fact]
    public async Task Switching_the_session_tenant_is_honored()
    {
        var id = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        await using var host = CreateHost();
        host.Session.Set(TestSessionContext.ForTenant(otherTenant));

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, otherTenant, "Ada", 10m));
            await events.SaveChangesAsync();
        });

        var account = await host.AggregateAsync<Account>(id);
        Assert.Equal(otherTenant, account!.TenantId);
    }
}
