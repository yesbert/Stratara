using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;

namespace Stratara.EntityFrameworkCore.Tests.ReadStore;

/// <summary>Which tables a read context asks the read-model preservation to copy.</summary>
public class PreservedTablesTests
{
    private sealed class ProbeReadContext(DbContextOptions<ProbeReadContext> options) : ReadDbContext<ProbeReadContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasDefaultSchema("read_store");
            modelBuilder.Entity<OrderView>().ToTable("order_view").HasKey(view => view.Id);
            modelBuilder.Entity<OrderView>().OwnsOne(view => view.Address, address => address.ToTable("order_view"));
            modelBuilder.Entity<ArchivedView>().ToTable("archived_view", "archive").HasKey(view => view.Id);
            modelBuilder.Entity<ReportRow>().HasNoKey().ToView("report");
            modelBuilder.Entity<Summary>().HasKey(summary => summary.Id);
            modelBuilder.Entity<Summary>().ToSqlQuery("SELECT 1 AS id");
        }
    }

    private sealed class OrderView
    {
        public Guid Id { get; set; }
        public Address Address { get; set; } = new();
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";
    }

    private sealed class ArchivedView
    {
        public Guid Id { get; set; }
    }

    private sealed class ReportRow
    {
        public int Count { get; set; }
    }

    private sealed class Summary
    {
        public int Id { get; set; }
    }

    private static List<PreservedTable> TablesOf(ReadModelRestoreOptions options)
    {
        using var context = new ProbeReadContext(new DbContextOptionsBuilder<ProbeReadContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options);
        return PreservedTables.Of(context.Model, options);
    }

    [Fact]
    public void TheSet_IncludesTheFrameworksOwnTables()
    {
        var tables = TablesOf(new ReadModelRestoreOptions());

        Assert.Contains(new PreservedTable("read_store", "projection_checkpoint"), tables);
        Assert.Contains(new PreservedTable("read_store", "projection_forgotten_tenant"), tables);
    }

    [Fact]
    public void TheSet_SkipsViewsKeylessTypesAndSqlQueries_AndNamesASharedTableOnce()
    {
        var tables = TablesOf(new ReadModelRestoreOptions());

        Assert.Single(tables, table => table == new PreservedTable("read_store", "order_view"));
        Assert.Contains(new PreservedTable("archive", "archived_view"), tables);
        Assert.DoesNotContain(tables, table => table.Name is "report" or "Summary" or "summary");
    }

    [Fact]
    public void TheSet_HonoursExcludedAndAdditionalTables()
    {
        var tables = TablesOf(new ReadModelRestoreOptions
        {
            ExcludedTables = ["order_view", "archive.archived_view"],
            AdditionalTables = ["raw_counter", "other.raw_log"],
        });

        Assert.DoesNotContain(new PreservedTable("read_store", "order_view"), tables);
        Assert.DoesNotContain(new PreservedTable("archive", "archived_view"), tables);
        Assert.Contains(new PreservedTable("read_store", "raw_counter"), tables);
        Assert.Contains(new PreservedTable("other", "raw_log"), tables);
    }
}
