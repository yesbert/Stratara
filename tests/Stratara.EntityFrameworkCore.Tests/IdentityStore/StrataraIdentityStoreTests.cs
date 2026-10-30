using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Settings;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.IdentityStore;

/// <summary>
/// <c>identity-store</c> → the shared identity store's model, its shipped migrations and its registration, without a
/// database: the model has nothing the latest migration lacks, every migration after the first only adds, everything
/// lives in the fixed schema, and the registration resolves the store and the directory's stores on the configured
/// connection.
/// </summary>
public class StrataraIdentityStoreTests
{
    private const string DefaultConnection = "Host=default-host;Database=defaultdb";
    private const string IdentityConnection = "Host=identity-host;Database=identitydb";

    private static readonly Type[] DestructiveOperations =
    [
        typeof(DropTableOperation), typeof(DropColumnOperation), typeof(DropIndexOperation),
        typeof(DropPrimaryKeyOperation), typeof(DropForeignKeyOperation), typeof(DropUniqueConstraintOperation),
        typeof(DropSchemaOperation), typeof(DropSequenceOperation), typeof(DropCheckConstraintOperation),
        typeof(RenameTableOperation), typeof(RenameColumnOperation), typeof(RenameIndexOperation),
        typeof(RenameSequenceOperation), typeof(AlterColumnOperation), typeof(AlterTableOperation),
        typeof(SqlOperation), typeof(DeleteDataOperation), typeof(UpdateDataOperation),
    ];

    private static bool BreaksAnEarlierHost(MigrationOperation operation) =>
        DestructiveOperations.Contains(operation.GetType())
        || operation is AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null, ComputedColumnSql: null };

    private static StrataraIdentityStore DesignTimeStore() => new StrataraIdentityStoreDesignTimeFactory().CreateDbContext([]);

    private static ServiceProvider Provider(params (string Key, string Value)[] connectionStrings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(connectionStrings.Select(c => new KeyValuePair<string, string?>($"ConnectionStrings:{c.Key}", c.Value)))
            .Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddStrataraIdentityStore()
            .BuildServiceProvider();
    }

    [Fact]
    public void The_model_has_no_change_the_latest_shipped_migration_lacks()
    {
        using var store = DesignTimeStore();

        Assert.False(store.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Every_migration_after_the_first_only_adds()
    {
        using var store = DesignTimeStore();
        var migrations = store.GetService<IMigrationsAssembly>();
        var later = migrations.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal).Skip(1);

        var destructive = later
            .Select(m => (Id: m.Key, Migration: migrations.CreateMigration(m.Value, store.Database.ProviderName!)))
            .SelectMany(m => m.Migration.UpOperations
                .Where(BreaksAnEarlierHost)
                .Select(operation => $"{m.Id}: {operation.GetType().Name}"))
            .ToList();

        Assert.Empty(destructive);
    }

    [Fact]
    public void Every_table_and_the_migration_history_live_in_the_fixed_schema()
    {
        using var store = DesignTimeStore();

        Assert.All(store.Model.GetEntityTypes().Where(t => t.GetTableName() is not null),
            t => Assert.Equal(StrataraIdentityStore.Schema, t.GetSchema()));
        var history = store.GetService<IHistoryRepository>().GetCreateScript();
        Assert.Contains($"{StrataraIdentityStore.Schema}.{StrataraIdentityStore.MigrationsHistoryTable}", history.Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_holds_the_accounts_and_the_directory()
    {
        using var store = DesignTimeStore();
        var tables = store.Model.GetEntityTypes().Select(t => t.GetTableName()).ToHashSet();

        Assert.Superset(
            new HashSet<string?>
            {
                "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetUserRoles",
                "AspNetRoleClaims", "AspNetUserPasskeys", "tenant_membership", "active_tenant", "setting_entry", "api_key",
            },
            tables);
        Assert.NotNull(store.Model.FindEntityType(typeof(IdentityUser)));
    }

    [Fact]
    public void The_store_uses_its_own_connection_where_one_is_given()
    {
        using var provider = Provider(("defaultdb", DefaultConnection), (StrataraIdentityStore.ConnectionStringName, IdentityConnection));
        using var scope = provider.CreateScope();

        var connection = scope.ServiceProvider.GetRequiredService<StrataraIdentityStore>().Database.GetConnectionString();

        Assert.Contains("identity-host", connection, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_falls_back_to_the_default_connection()
    {
        using var provider = Provider(("defaultdb", DefaultConnection));
        using var scope = provider.CreateScope();

        var connection = scope.ServiceProvider.GetRequiredService<StrataraIdentityStore>().Database.GetConnectionString();

        Assert.Contains("default-host", connection, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registration_makes_the_directory_stores_available_on_the_store()
    {
        using var provider = Provider(("defaultdb", DefaultConnection));
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IApiKeyStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISettingStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraIdentityStore>>());
    }

    [Fact]
    public void A_column_added_without_null_or_default_breaks_an_earlier_host()
    {
        Assert.True(BreaksAnEarlierHost(new AddColumnOperation { Name = "c", Table = "t", ClrType = typeof(int), IsNullable = false }));
        Assert.False(BreaksAnEarlierHost(new AddColumnOperation { Name = "c", Table = "t", ClrType = typeof(int), IsNullable = true }));
        Assert.False(BreaksAnEarlierHost(new AddColumnOperation { Name = "c", Table = "t", ClrType = typeof(int), IsNullable = false, DefaultValue = 0 }));
        Assert.True(BreaksAnEarlierHost(new SqlOperation { Sql = "DELETE FROM t" }));
    }

    [Fact]
    public void A_host_that_protects_personal_data_is_refused()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddStrataraIdentityStore()
            .Configure<IdentityOptions>(options => options.Stores.ProtectPersonalData = true)
            .BuildServiceProvider();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value);
    }

    [Fact]
    public void Without_either_connection_string_the_store_says_which_it_needs()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var refusal = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<StrataraIdentityStore>());
        Assert.Contains(StrataraIdentityStore.ConnectionStringName, refusal.Message, StringComparison.Ordinal);
    }
}
