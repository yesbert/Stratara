using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Orleans.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Store;

/// <summary>
/// The shipped write model plus the commit-order columns and the partition counter — what a
/// consumer would migrate to if the readers shipped. The counter is maintained by the interceptor
/// when the options say so, which is how the benchmarks switch its cost on and off.
/// </summary>
public sealed class PocCommitOrderWriteDbContext(
    DbContextOptions<PocCommitOrderWriteDbContext> options,
    IOptions<CommitOrderOptions> commitOrder)
    : WriteDbContext<PocCommitOrderWriteDbContext>(options)
{
    private readonly CommitOrderOptions _commitOrder = commitOrder.Value;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (_commitOrder.MaintainPartitionCounter)
        {
            optionsBuilder.AddInterceptors(new PartitionCounterInterceptor(_commitOrder.PartitionCount));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        CommitOrderModel.Apply(modelBuilder, postgres: Database.IsNpgsql());
    }
}
