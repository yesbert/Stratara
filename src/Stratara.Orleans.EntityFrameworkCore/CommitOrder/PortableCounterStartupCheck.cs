using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Refuses to let the host start while the store holds an entry the portable reader cannot see — one
/// without a position, written before the counter was adopted — or counter rows for partitions the configured
/// partition count does not have. Starting anyway would let every reader skip those entries for good, or read
/// merged partitions whose positions overlap.
/// </summary>
/// <typeparam name="TWriteContext">A write context derived from the framework's write context.</typeparam>
internal sealed class PortableCounterStartupCheck<TWriteContext>(IServiceScopeFactory scopeFactory, IOptions<CommitOrderOptions> options) : IHostedService
    where TWriteContext : DbContext, IWriteDbContext
{
    /// <exception cref="InvalidOperationException">The store holds entries without a position, or counter rows beyond the partition count.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TWriteContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);

        var unpositioned = await context.Set<EventStreamEntry>()
            .AnyAsync(e => EF.Property<long?>(e, CommitOrderSchema.PartitionPositionColumn) == null, cancellationToken);
        if (unpositioned)
        {
            throw new InvalidOperationException(
                $"The event store holds entries without a partition position, which the portable commit-order reader cannot see. Run {nameof(PartitionCounterBackfill)}.{nameof(PartitionCounterBackfill.RunAsync)} once against the store before starting a host that reads with it.");
        }

        var partitionCount = options.Value.PartitionCount;
        var highest = await context.Set<PartitionPosition>().MaxAsync(counter => (int?)counter.Partition, cancellationToken);
        if (highest >= partitionCount)
        {
            throw new InvalidOperationException(
                $"The store holds a partition counter for partition {highest}, but the host reads with a partition count of {partitionCount}. The partition count of a store read by the portable commit-order reader cannot be lowered: partitions would merge whose positions overlap, and entries would be skipped. Configure the count the store was counted with ({highest + 1} or more).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
