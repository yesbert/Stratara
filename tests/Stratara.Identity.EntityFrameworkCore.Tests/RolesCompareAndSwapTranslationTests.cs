using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class RolesCompareAndSwapTranslationTests
{
    [Fact]
    public async Task An_update_filtered_by_the_roles_it_read_affects_the_row_only_while_they_are_unchanged()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new TestDirectoryDbContext(
            new DbContextOptionsBuilder<TestDirectoryDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        context.Add(new TenantMembershipEntry { UserId = userId, TenantId = tenantId, Roles = ["a"], Status = MembershipStatus.Active });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        List<string> read = ["a"];
        List<string> stale = ["b"];
        List<string> next = ["a", "c"];

        var staleRows = await context.Set<TenantMembershipEntry>()
            .Where(e => e.UserId == userId && e.TenantId == tenantId && e.Roles == stale)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Roles, next), TestContext.Current.CancellationToken);
        var rows = await context.Set<TenantMembershipEntry>()
            .Where(e => e.UserId == userId && e.TenantId == tenantId && e.Roles == read)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Roles, next), TestContext.Current.CancellationToken);

        Assert.Equal(0, staleRows);
        Assert.Equal(1, rows);
    }
}
