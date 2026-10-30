using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Settings;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

namespace Stratara.Identity.IntegrationTests;

/// <summary>
/// <c>identity-store</c> on PostgreSQL: hosts that share the identity store migrate it with the shipped migrations —
/// one after the other, at once, or on two releases — and read and write the same accounts and memberships, wherever
/// their own databases are.
/// </summary>
public sealed class SharedIdentityStoreTests(IdentityStoreDatabase database) : IClassFixture<IdentityStoreDatabase>
{
    private static string NewDatabase() => $"identity_{Guid.NewGuid():N}";

    private static async Task<int> ShippedMigrationCountAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<StrataraIdentityStore>().Database.GetMigrations().Count();
    }

    private Task<List<string>> AppliedMigrationsAsync(string identityDatabase) =>
        database.ReadAsync(identityDatabase,
            $"SELECT migration_id FROM {StrataraIdentityStore.Schema}.\"{StrataraIdentityStore.MigrationsHistoryTable}\"");

    private static async Task<IdentityUser> CreateAccountAsync(IHost host, string email)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { Id = Guid.CreateVersion7().ToString(), UserName = email, Email = email };
        var created = await users.CreateAsync(user, "Correct-horse-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }

    [Fact]
    public async Task A_host_migrates_an_empty_database_and_uses_every_part_of_the_store()
    {
        var identityDatabase = NewDatabase();
        using var host = database.Host(identityDatabase);

        await host.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        var schemas = await database.ReadAsync(identityDatabase,
            "SELECT DISTINCT table_schema FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')");
        Assert.Equal([StrataraIdentityStore.Schema], schemas);
        var tables = await database.ReadAsync(identityDatabase,
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{StrataraIdentityStore.Schema}'");
        Assert.Contains(StrataraIdentityStore.MigrationsHistoryTable, tables);
        Assert.Contains("AspNetUserPasskeys", tables);
        Assert.Contains("tenant_membership", tables);

        var user = await CreateAccountAsync(host, "ada@example.com");
        var userId = Guid.Parse(user.Id);
        var tenantId = Guid.CreateVersion7();
        var credentialId = Guid.NewGuid().ToByteArray();

        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var stored = (await users.FindByEmailAsync("ada@example.com"))!;
        Assert.True(await users.CheckPasswordAsync(stored, "Correct-horse-1"));
        var passkey = await users.AddOrUpdatePasskeyAsync(stored, new UserPasskeyInfo(
            credentialId, [1, 2, 3], DateTimeOffset.UtcNow, 0, ["internal"], true, false, false, [4], [5]));
        Assert.True(passkey.Succeeded, string.Join("; ", passkey.Errors.Select(e => e.Description)));
        Assert.Equal(user.Id, (await users.FindByPasskeyIdAsync(credentialId))!.Id);

        var memberships = services.GetRequiredService<ITenantMembershipStore>();
        await memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, ["tenant.admin"]));
        await memberships.SetActiveTenantAsync(userId, tenantId);
        await services.GetRequiredService<ISettingStore>().SetAsync("theme", "dark", SettingScope.ForUser(userId));
        var key = await services.GetRequiredService<IApiKeyStore>().IssueAsync(new ApiKeyIssueRequest(tenantId, "pat", [], userId));

        Assert.Equal(tenantId, await memberships.GetActiveTenantAsync(userId));
        Assert.Equal("dark", await services.GetRequiredService<ISettingStore>().GetOrNullAsync("theme", SettingScope.ForUser(userId)));
        Assert.Equal(userId, (await services.GetRequiredService<IApiKeyStore>().ValidateAsync(key.RawKey))!.UserId);
    }

    [Fact]
    public async Task A_second_host_on_a_migrated_store_applies_nothing_and_uses_it()
    {
        var identityDatabase = NewDatabase();
        using var first = database.Host(identityDatabase);
        using var second = database.Host(identityDatabase);
        await first.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);
        var applied = await AppliedMigrationsAsync(identityDatabase);

        await second.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(applied, await AppliedMigrationsAsync(identityDatabase));
        var user = await CreateAccountAsync(first, "grace@example.com");
        await using var scope = second.Services.CreateAsyncScope();
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByIdAsync(user.Id));
    }

    [Fact]
    public async Task Two_hosts_migrating_an_empty_store_at_once_both_complete_and_record_each_migration_once()
    {
        var identityDatabase = NewDatabase();
        using var first = database.Host(identityDatabase);
        using var second = database.Host(identityDatabase);

        await Task.WhenAll(
            first.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken),
            second.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken));

        var applied = await AppliedMigrationsAsync(identityDatabase);
        Assert.Equal(await ShippedMigrationCountAsync(first), applied.Count);
        Assert.Equal(applied.Count, applied.Distinct().Count());
    }

    [Fact]
    public async Task A_host_on_an_earlier_release_keeps_working_on_a_store_a_later_release_migrated()
    {
        var identityDatabase = NewDatabase();
        using var later = database.Host(identityDatabase);
        await later.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);
        await database.ExecuteAsync(identityDatabase, $"""
            ALTER TABLE {StrataraIdentityStore.Schema}."AspNetUsers" ADD COLUMN preferred_name text NULL;
            ALTER TABLE {StrataraIdentityStore.Schema}.tenant_membership ADD COLUMN ended_at timestamptz NULL;
            CREATE TABLE {StrataraIdentityStore.Schema}.later_release_table (id uuid PRIMARY KEY);
            INSERT INTO {StrataraIdentityStore.Schema}."{StrataraIdentityStore.MigrationsHistoryTable}" (migration_id, product_version)
                VALUES ('29991231000000_LaterReleaseAddition', '10.0.0');
            """);

        using var earlier = database.Host(identityDatabase);
        await earlier.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        var user = await CreateAccountAsync(earlier, "edsger@example.com");
        var userId = Guid.Parse(user.Id);
        var tenantId = Guid.CreateVersion7();
        await using var scope = earlier.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(user.Id))!, "Correct-horse-1"));
        var memberships = scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>();
        await memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, []));
        Assert.Single(await memberships.GetMembershipsAsync(userId));
    }

    [Fact]
    public async Task Hosts_with_their_own_databases_share_the_accounts_and_memberships_of_one_identity_store()
    {
        var identityDatabase = NewDatabase();
        using var first = database.Host(identityDatabase, defaultDatabase: $"first_{Guid.NewGuid():N}");
        using var second = database.Host(identityDatabase, defaultDatabase: $"second_{Guid.NewGuid():N}");
        await first.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);
        await second.Services.MigrateStrataraIdentityStoreAsync(TestContext.Current.CancellationToken);

        var user = await CreateAccountAsync(first, "barbara@example.com");
        var userId = Guid.Parse(user.Id);
        var tenantId = Guid.CreateVersion7();
        await using (var firstScope = first.Services.CreateAsyncScope())
        {
            await firstScope.ServiceProvider.GetRequiredService<ITenantMembershipStore>()
                .SetMembershipAsync(new TenantMembership(userId, tenantId, ["tenant.member"]));
        }

        await using var secondScope = second.Services.CreateAsyncScope();
        Assert.NotNull(await secondScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync("barbara@example.com"));
        var membership = await secondScope.ServiceProvider.GetRequiredService<ITenantMembershipStore>().GetMembershipAsync(userId, tenantId);
        Assert.Equal(["tenant.member"], membership!.Roles);
    }
}
