using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Stratara.Projections.Multitenancy.Models;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.Shared.EventSourcing;
using Stratara.Abstractions.Outbox;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.ReadStore;

public class ReadDbContextTests
{
    [Fact]
    public void OnModelCreating_RegistersReadStoreEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.Contains(typeof(TenantView), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_DoesNotRegisterWriteStoreEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.DoesNotContain(typeof(EventStreamEntry), entityClrTypes);
        Assert.DoesNotContain(typeof(OutboxEntry), entityClrTypes);
        Assert.DoesNotContain(typeof(Snapshot), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_DoesNotRegisterIdentityStoreEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.DoesNotContain(typeof(IdentityUser), entityClrTypes);
        Assert.DoesNotContain(typeof(IdentityRole), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_DoesNotThrowOnEnsureCreated()
    {
        using var ctx = BuildContext();

        var exception = Record.Exception(() => ctx.Database.EnsureCreated());

        Assert.Null(exception);
    }

    private static TestReadDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<TestReadDbContext>()
            .UseInMemoryDatabase($"read-store-{Guid.NewGuid():N}")
            .Options;
        return new TestReadDbContext(options);
    }

    private sealed class TestReadDbContext(DbContextOptions<TestReadDbContext> options)
        : ReadDbContext<TestReadDbContext>(options);
}
