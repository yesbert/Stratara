using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;

namespace Stratara.Orleans.EntityFrameworkCore.CommitOrder;

/// <summary>
/// Refuses to let the host start while the store holds an entry the portable reader cannot see: one
/// without a position, written before the counter was adopted. Starting anyway would let every reader
/// skip those entries for good.
/// </summary>
/// <typeparam name="TWriteContext">A write context derived from the framework's write context.</typeparam>
internal sealed class PortableCounterStartupCheck<TWriteContext>(IServiceScopeFactory scopeFactory) : IHostedService
    where TWriteContext : DbContext, IWriteDbContext
{
    /// <exception cref="InvalidOperationException">The store holds entries without a position.</exception>
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
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
