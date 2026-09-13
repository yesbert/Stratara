using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.IntegrationTests.Store;

/// <summary>
/// The proof of concept's read store: the shipped read model, the projection checkpoint table, and
/// one probe view the projection tests write to.
/// </summary>
public sealed class PocReadDbContext(DbContextOptions<PocReadDbContext> options) : ReadDbContext<PocReadDbContext>(options)
{
    public DbSet<CounterView> CounterViews => Set<CounterView>();

    public DbSet<CounterAudit> CounterAudits => Set<CounterAudit>();

    public DbSet<CounterTotals> CounterTotals => Set<CounterTotals>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ProjectionCheckpointModel.Apply(modelBuilder);
        modelBuilder.Entity<CounterView>(view =>
        {
            view.ToTable("poc_counter_view");
            view.HasKey(v => v.StreamId);
        });
        modelBuilder.Entity<CounterAudit>(audit =>
        {
            audit.ToTable("poc_counter_audit");
            audit.HasKey(a => new { a.StreamId, a.Version });
            audit.HasIndex(a => a.AppliedAt);
        });
        modelBuilder.Entity<CounterTotals>(totals =>
        {
            totals.ToTable("poc_counter_totals");
            totals.HasKey(t => t.Id);
            totals.Property(t => t.Id).ValueGeneratedNever();
        });
    }
}

public sealed class CounterAudit
{
    public Guid StreamId { get; set; }

    public long Version { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class CounterTotals
{
    public int Id { get; set; }

    public long Created { get; set; }

    public long Incremented { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>One row per counter: its value, the last event version applied, and who applied it.</summary>
public sealed class CounterView
{
    public Guid StreamId { get; set; }

    public int Value { get; set; }

    public long LastVersion { get; set; }

    public Guid AppliedByTenant { get; set; }

    public int Applications { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
