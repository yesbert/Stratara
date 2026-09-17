using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Store;

/// <summary>
/// The shipped write model, which declares the commit-order columns and the partition counter. The counter is maintained by the interceptor
/// when <see cref="PocCounterOptions"/> say so, which is how the tests stand for a process without it and the benchmarks switch its cost on and off.
/// </summary>
public sealed class PocCommitOrderWriteDbContext(
    DbContextOptions<PocCommitOrderWriteDbContext> options,
    IOptions<CommitOrderOptions> commitOrder,
    IOptions<PocCounterOptions> counter)
    : WriteDbContext<PocCommitOrderWriteDbContext>(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (counter.Value.MaintainPartitionCounter)
        {
            optionsBuilder.AddInterceptors(new PartitionCounterInterceptor(commitOrder));
        }
    }
}

/// <summary>Whether the test store's write context adds the partition counter interceptor — the decision a consumer's write context makes.</summary>
public sealed class PocCounterOptions
{
    public bool MaintainPartitionCounter { get; set; } = true;
}
