using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Store;

/// <summary>
/// A write store wired the way a consumer wires it — through <c>AddNpgsqlWriteDbContextFactory</c>
/// and the <c>defaultdb</c> connection string — against a database of its own, with its schema
/// created. Tests that only need the store and not a host use this instead of building one.
/// </summary>
public sealed class PocStore<TContext> : IAsyncDisposable
    where TContext : DbContext, Stratara.EventSourcing.EntityFrameworkCore.Abstractions.IWriteDbContext
{
    private readonly ServiceProvider _provider;

    private PocStore(ServiceProvider provider, CommitOrderOptions options)
    {
        _provider = provider;
        Options = options;
    }

    public CommitOrderOptions Options { get; }

    public IDbContextFactory<TContext> ContextFactory => _provider.GetRequiredService<IDbContextFactory<TContext>>();

    public IServiceProvider Services => _provider;

    public static async Task<PocStore<TContext>> CreateAsync(string connectionString, Action<CommitOrderOptions>? configure = null)
    {
        var options = new CommitOrderOptions();
        configure?.Invoke(options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:defaultdb"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddNpgsqlWriteDbContextFactory<TContext>();

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await SeedPartitionCountersAsync(context, options.PartitionCount);
        }

        return new PocStore<TContext>(provider, options);
    }

    /// <summary>
    /// One counter row per partition, the way a migration would seed them. A store whose model has no
    /// counter table is left alone.
    /// </summary>
    private static async Task SeedPartitionCountersAsync(TContext context, int partitionCount)
    {
        if (context.Model.FindEntityType(typeof(PartitionPosition)) is null)
        {
            return;
        }

        var existing = await context.Set<PartitionPosition>().Select(counter => counter.Partition).ToHashSetAsync();
        for (var partition = 0; partition < partitionCount; partition++)
        {
            if (!existing.Contains(partition))
            {
                context.Set<PartitionPosition>().Add(new PartitionPosition { Partition = partition, Position = 0 });
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A context from the root factory. The factory is registered scoped, so a context minted inside
    /// a short-lived scope would outlive the provider it resolves its services from.
    /// </summary>
    public Task<TContext> CreateContextAsync() => ContextFactory.CreateDbContextAsync();

    public static EventStreamEntry NewEntry(Guid streamId, long version, int bucketId, Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(),
        StreamId = streamId,
        Version = version,
        EventTypeName = "Stratara.Orleans.IntegrationTests.Probe",
        AggregateTypeName = "Stratara.Orleans.IntegrationTests.ProbeAggregate",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Guid.CreateVersion7().ToString("N"),
        CausationId = Guid.CreateVersion7().ToString("N"),
        BucketId = bucketId,
        TenantId = tenantId,
        UserId = null,
        ActorTenantId = tenantId,
        ActorUserId = tenantId,
    };

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
    }
}
