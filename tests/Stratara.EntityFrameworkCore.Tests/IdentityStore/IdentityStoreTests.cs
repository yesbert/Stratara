using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;
using Stratara.Projections.Multitenancy.Models;
using Stratara.Shared.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.IdentityStore;

public class IdentityStoreTests
{
    [Fact]
    public void OnModelCreating_RegistersIdentityUserAndRoleEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.Contains(typeof(IdentityUser), entityClrTypes);
        Assert.Contains(typeof(IdentityRole), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_RegistersAspNetUserPasskeysTable()
    {
        using var ctx = BuildContext();

        var passkey = ctx.Model.FindEntityType(typeof(IdentityUserPasskey<string>));

        Assert.NotNull(passkey);
        Assert.Equal("AspNetUserPasskeys", passkey.GetTableName());
    }

    [Fact]
    public void OnModelCreating_PasskeyEntity_UsesCredentialIdAsPrimaryKey()
    {
        using var ctx = BuildContext();

        var passkey = ctx.Model.FindEntityType(typeof(IdentityUserPasskey<string>));
        var primaryKey = passkey!.FindPrimaryKey();

        Assert.NotNull(primaryKey);
        Assert.Single(primaryKey.Properties);
        Assert.Equal(nameof(IdentityUserPasskey<string>.CredentialId), primaryKey.Properties[0].Name);
    }

    [Fact]
    public void OnModelCreating_DoesNotRegisterWriteStoreEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.DoesNotContain(typeof(EventStreamEntry), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_DoesNotRegisterReadStoreEntityTypes()
    {
        using var ctx = BuildContext();

        var entityClrTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).ToHashSet();

        Assert.DoesNotContain(typeof(TenantView), entityClrTypes);
    }

    [Fact]
    public void OnModelCreating_DoesNotThrowOnEnsureCreated()
    {
        using var ctx = BuildContext();

        var exception = Record.Exception(() => ctx.Database.EnsureCreated());

        Assert.Null(exception);
    }

    private static TestIdentityStore BuildContext()
    {
        var options = new DbContextOptionsBuilder<TestIdentityStore>()
            .UseInMemoryDatabase($"identity-store-{Guid.NewGuid():N}")
            .Options;
        return new TestIdentityStore(options);
    }

    private sealed class TestIdentityStore(DbContextOptions<TestIdentityStore> options)
        : IdentityStore<TestIdentityStore, IdentityUser>(options);
}
