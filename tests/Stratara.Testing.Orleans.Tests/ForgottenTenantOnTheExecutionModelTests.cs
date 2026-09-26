using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Domain;
using Stratara.Projections.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// <c>projections</c> → <em>A projection can forget a deleted tenant</em>, on the Orleans execution model: a
/// store-reading projection that declares it reads past a fact recorded after its tenant's deletion instead of
/// stalling its partition — verified on the in-process host over SQLite.
/// </summary>
public sealed class ForgottenTenantOnTheExecutionModelTests
{
    private static readonly Guid Tenant = ExecutionModelTestHost.DefaultTenantId;

    public sealed class Rows
    {
        public ConcurrentDictionary<Guid, Guid> Accounts { get; } = new();

        public ConcurrentBag<Guid> Deposits { get; } = [];
    }

    /// <summary>Keeps one row per account and removes a tenant's rows on either deletion fact.</summary>
    public sealed class AccountRowProjection(Rows rows) : IForgetsDeletedTenants
    {
        public Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken cancellationToken)
        {
            rows.Accounts[@event.StreamId] = @event.TenantId;
            return Task.CompletedTask;
        }

        public Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken cancellationToken)
        {
            if (!rows.Accounts.ContainsKey(@event.StreamId))
            {
                throw new PrecedingFactMissingException(@event.StreamId, nameof(AmountDeposited));
            }

            rows.Deposits.Add(@event.StreamId);
            return Task.CompletedTask;
        }

        public Task HandleAsync(CustomerTenantsDeleted @event, CancellationToken cancellationToken)
        {
            foreach (var row in rows.Accounts.Where(r => @event.TenantIds.Contains(r.Value)).ToList())
            {
                rows.Accounts.TryRemove(row.Key, out _);
            }

            return Task.CompletedTask;
        }

        public Task HandleAsync(IEvent<TenantDeleted> @event, CancellationToken cancellationToken)
        {
            foreach (var row in rows.Accounts.Where(r => r.Value == @event.StreamId).ToList())
            {
                rows.Accounts.TryRemove(row.Key, out _);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The account and the cascade live in different streams, and so perhaps in different partitions, each read at its
    /// own pace; the test lets the readers catch up after each step so the history is applied in the order it was
    /// written, as a replay would apply it.
    /// </summary>
    [Fact]
    public async Task A_store_reading_projection_reads_past_a_fact_recorded_after_its_tenants_deletion()
    {
        var rows = new Rows();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(rows)
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddTrustedType<CustomerTenantsDeleted>()
            .AddTrustedType<TenantDeleted>()
            .AddScoped<IProjection, AccountRowProjection>()
            .AddStrataraProjectionGrains());
        var account = Guid.NewGuid();
        var customer = Guid.NewGuid();

        await AppendAsync(host, async events =>
        {
            await events.CreateAsync<Account>(account, new AccountOpened(account, Tenant, 10m));
            await events.SaveChangesAsync();
        });
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);
        await AppendAsync(host, async events =>
        {
            await events.CreateAsync<Account>(customer, new CustomerTenantsDeleted(customer, [Tenant], DateTimeOffset.UtcNow));
            await events.SaveChangesAsync();
        });
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);
        await AppendAsync(host, async events =>
        {
            await events.AppendAsync<Account>(account, new AmountDeposited(account, 5m));
            await events.SaveChangesAsync();
        });

        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(rows.Accounts);
        Assert.Empty(rows.Deposits);
        await using var scope = host.Services.CreateAsyncScope();
        var name = scope.ServiceProvider.GetRequiredService<IProjectionHandler>().GetProjectionName(new AccountRowProjection(rows));
        Assert.True(await scope.ServiceProvider.GetRequiredService<IForgottenTenantStore>()
            .HasForgottenAsync(name, Tenant, TestContext.Current.CancellationToken));
    }

    private static async Task AppendAsync(ExecutionModelTestHost host, Func<IEventSource, Task> work)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<IEventSource>());
    }
}
