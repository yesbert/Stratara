using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

/// <summary>
/// Pins what each registration costs. Directory reads against SQLite complete faster than they can
/// be issued, so two calls made "at the same time" never actually overlap — every test here forces
/// the overlap with <see cref="CommandGate"/>, which holds the first command open until the second
/// has been issued. Without it these tests pass whatever the registration does, and prove nothing.
/// </summary>
public class DirectoryContextSharingTests
{
    [Fact]
    public async Task SharedContext_OverlappingReads_FailOnTheSecondOperation()
    {
        using var host = new DirectoryHost();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();

        var first = store.GetMembershipsAsync(userId);
        await host.Gate.FirstCommandInFlight;

        var second = await Record.ExceptionAsync(() => store.GetMembershipsAsync(userId));

        host.Gate.Release();
        await first;

        Assert.IsType<InvalidOperationException>(second);
    }


    [Fact]
    public async Task ContextPerOperation_OverlappingMembershipReads_BothComplete()
    {
        using var host = new FactoryDirectoryHost();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();

        var first = store.GetMembershipsAsync(userId);
        await host.Gate.FirstCommandInFlight;
        var second = store.GetMembershipsAsync(userId);

        host.Gate.Release();

        Assert.Empty(await first);
        Assert.Empty(await second);
    }

    [Fact]
    public async Task ContextPerOperation_OverlappingApiKeyReads_BothComplete()
    {
        using var host = new FactoryDirectoryHost();
        var store = host.Resolve<IApiKeyStore>();
        var tenantId = Guid.CreateVersion7();

        var first = store.GetForTenantAsync(tenantId);
        await host.Gate.FirstCommandInFlight;
        var second = store.GetForTenantAsync(tenantId);

        host.Gate.Release();

        Assert.Empty(await first);
        Assert.Empty(await second);
    }

    [Fact]
    public async Task ContextPerOperation_OverlappingSettingReads_BothComplete()
    {
        using var host = new FactoryDirectoryHost();
        var store = host.Resolve<ISettingStore>();
        var first = store.GetAllAsync(SettingScope.Global);
        await host.Gate.FirstCommandInFlight;
        var second = store.GetAllAsync(SettingScope.Global);

        host.Gate.Release();

        Assert.Empty(await first);
        Assert.Empty(await second);
    }

    [Fact]
    public void ContextPerOperation_SettingStore_StillGetsTheEncryptingWrapper()
    {
        using var host = new FactoryDirectoryHost(withEncryptedSetting: true);

        var store = host.Resolve<ISettingStore>();

        Assert.IsType<EncryptingSettingStore>(store);
    }

    [Fact]
    public async Task SharedContext_RoleChange_LeavesTheConsumersUnsavedChangesUnsaved()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var context = host.Resolve<TestDirectoryDbContext>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        var unsavedUser = Guid.CreateVersion7();
        context.Add(new TenantMembershipEntry { UserId = unsavedUser, TenantId = tenantId, Status = MembershipStatus.Active });

        await store.AddRolesAsync(userId, tenantId, ["Viewer"]);

        Assert.True(context.ChangeTracker.HasChanges());
        Assert.Null(await store.GetMembershipAsync(unsavedUser, tenantId));
        Assert.Equal(["Editor", "Viewer"], (await store.GetMembershipAsync(userId, tenantId))!.Roles);
    }

    [Fact]
    public async Task SharedContext_RemoveMembership_LetsTheSameScopeSetItAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.RemoveMembershipAsync(userId, tenantId);
        Assert.Null(await store.GetMembershipAsync(userId, tenantId));
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Reader"]));

        Assert.Equal(["Reader"], (await store.GetMembershipAsync(userId, tenantId))?.Roles);
    }

    [Fact]
    public async Task SharedContext_RemoveMembership_LetsTheSameScopeSelectTheTenantAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);
        await store.RemoveMembershipAsync(userId, tenantId);
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);

        Assert.Equal(tenantId, await store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task SharedContext_RemoveAllMemberships_LetsTheSameScopeSetThemAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);
        await store.RemoveAllMembershipsAsync(userId);
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Reader"]));
        await store.SetActiveTenantAsync(userId, tenantId);

        Assert.Equal(["Reader"], (await store.GetMembershipAsync(userId, tenantId))?.Roles);
        Assert.Equal(tenantId, await store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task SharedContext_RemoveAllMembers_LetsTheSameScopeSetThemAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);
        await store.RemoveAllMembersAsync(tenantId);
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Reader"]));
        await store.SetActiveTenantAsync(userId, tenantId);

        Assert.Equal(["Reader"], (await store.GetMembershipAsync(userId, tenantId))?.Roles);
        Assert.Equal(tenantId, await store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task SharedContext_EndingAMembership_LetsTheSameScopeSelectTheTenantAgainOnceActive()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"], MembershipStatus.Former));
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetActiveTenantAsync(userId, tenantId);

        Assert.Equal(tenantId, await store.GetActiveTenantAsync(userId));
    }

    [Fact]
    public async Task SharedContext_RemoveMembership_LeavesTheConsumersOtherPendingChangesPending()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ITenantMembershipStore>();
        var context = host.Resolve<TestDirectoryDbContext>();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var otherUserId = Guid.CreateVersion7();
        await store.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));
        await store.SetMembershipAsync(new TenantMembership(otherUserId, tenantId, ["Editor"]));

        var modified = await context.Set<TenantMembershipEntry>().SingleAsync(e => e.UserId == otherUserId);
        modified.Roles = ["Reader"];
        var added = new TenantMembershipEntry { UserId = Guid.CreateVersion7(), TenantId = tenantId, Roles = ["Editor"] };
        context.Set<TenantMembershipEntry>().Add(added);
        var readded = new TenantMembershipEntry { UserId = userId, TenantId = tenantId, Roles = ["Owner"] };

        await store.RemoveMembershipAsync(userId, tenantId);
        context.Set<TenantMembershipEntry>().Add(readded);
        await store.RemoveMembershipAsync(userId, tenantId);

        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, context.Entry(modified).State);
        Assert.Equal(EntityState.Added, context.Entry(added).State);
        Assert.Equal(EntityState.Added, context.Entry(readded).State);

        await context.SaveChangesAsync();
        Assert.Equal(["Reader"], (await store.GetMembershipAsync(otherUserId, tenantId))?.Roles);
        Assert.Equal(["Owner"], (await store.GetMembershipAsync(userId, tenantId))?.Roles);
    }

    [Fact]
    public async Task SharedContext_ClearingASetting_LetsTheSameScopeSetItAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ISettingStore>();

        await store.SetAsync("theme", "dark", SettingScope.Global);
        await store.SetAsync("theme", null, SettingScope.Global);
        Assert.Null(await store.GetOrNullAsync("theme", SettingScope.Global));
        await store.SetAsync("theme", "light", SettingScope.Global);

        Assert.Equal("light", await store.GetOrNullAsync("theme", SettingScope.Global));
    }

    public static TheoryData<string> Scopes => ["global", "tenant", "user", "tenant-user"];

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task SharedContext_DeletingASettingScope_LetsTheSameScopeSetItAgain(string kind)
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ISettingStore>();
        var scope = kind switch
        {
            "tenant" => SettingScope.ForTenant(Guid.CreateVersion7()),
            "user" => SettingScope.ForUser(Guid.CreateVersion7()),
            "tenant-user" => SettingScope.ForUserInTenant(Guid.CreateVersion7(), Guid.CreateVersion7()),
            _ => SettingScope.Global,
        };

        await store.SetAsync("theme", "dark", scope);
        await store.DeleteScopeAsync(scope);
        await store.SetAsync("theme", "light", scope);

        Assert.Equal("light", await store.GetOrNullAsync("theme", scope));
    }

    [Fact]
    public async Task SharedContext_DeletingATenantsSettings_LeavesAnotherTenantsTrackedChangePending()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<ISettingStore>();
        var context = host.Resolve<TestDirectoryDbContext>();
        var deleted = SettingScope.ForTenant(Guid.CreateVersion7());
        var kept = SettingScope.ForTenant(Guid.CreateVersion7());
        await store.SetAsync("theme", "dark", deleted);
        await store.SetAsync("theme", "dark", kept);
        var pending = await context.Set<SettingEntry>().SingleAsync(e => e.TenantId == kept.NormalizedTenantId);
        pending.Value = "light";

        await store.DeleteScopeAsync(deleted);

        context.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, context.Entry(pending).State);
    }

    [Fact]
    public async Task SharedContext_RemovingATenantsKeys_LetsTheSameScopeImportTheKeyAgain()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<IApiKeyStore>();
        var tenantId = Guid.CreateVersion7();
        var rawKey = ApiKeyFormat.CreateRawKey();

        await store.ImportAsync(new ApiKeyImportRequest(rawKey, tenantId, "CI key", ["Deployer"]));
        await store.RemoveAllForTenantAsync(tenantId);
        var imported = await store.ImportAsync(new ApiKeyImportRequest(rawKey, tenantId, "CI key", ["Deployer"]));

        Assert.Equal(imported.Id, (await store.ValidateAsync(rawKey))?.Id);
        await store.RevokeAsync(imported.Id);
        Assert.Null(await store.ValidateAsync(rawKey));
    }

    [Fact]
    public async Task SharedContext_RemovingATenantsKeys_LetsTheSameScopeIssueAgainAndSave()
    {
        using var host = new DirectoryHost();
        host.Gate.Release();
        var store = host.Resolve<IApiKeyStore>();
        var memberships = host.Resolve<ITenantMembershipStore>();
        var context = host.Resolve<TestDirectoryDbContext>();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await memberships.SetMembershipAsync(new TenantMembership(userId, tenantId, ["Editor"]));

        await store.IssueAsync(new ApiKeyIssueRequest(tenantId, "CI key", ["Deployer"]));
        var token = await store.IssueAsync(new ApiKeyIssueRequest(tenantId, "Token", [], userId));
        await store.RevokeAsync(token.Descriptor.Id);
        await store.RemoveAllForUserAsync(userId);
        await store.RemoveAllForTenantAsync(tenantId);
        var again = await store.IssueAsync(new ApiKeyIssueRequest(tenantId, "CI key", ["Deployer"]));
        await context.SaveChangesAsync();

        Assert.Equal(again.Descriptor.Id, Assert.Single(await store.GetForTenantAsync(tenantId)).Id);
    }

    [Fact]
    public void RegisteringBothVariants_LeavesTheFirstOneRegistered()
    {
        var services = new ServiceCollection();
        services.AddSettingCatalog(_ => { });
        services.AddTenantMembershipStore<TestDirectoryDbContext>();
        services.AddTenantMembershipStoreFromContextFactory<TestDirectoryDbContext>();

        var descriptor = Assert.Single(
            services, d => d.ServiceType == typeof(ITenantMembershipStore));

        Assert.Equal(typeof(EfTenantMembershipStore<TestDirectoryDbContext>), descriptor.ImplementationType);
    }

    private sealed class DirectoryHost : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public DirectoryHost()
        {
            var connectionString = $"DataSource=file:dir-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();

            var services = new ServiceCollection();
            services.AddSettingCatalog(_ => { });
            services.AddDbContext<TestDirectoryDbContext>(o => o.UseSqlite(connectionString).AddInterceptors(Gate));
            services.AddTenantMembershipStore<TestDirectoryDbContext>();
            services.AddApiKeyStore<TestDirectoryDbContext>();
            services.AddSettingStore<TestDirectoryDbContext>();

            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();
            _scope.ServiceProvider.GetRequiredService<TestDirectoryDbContext>().Database.EnsureCreated();
            Gate.Arm();
        }

        public CommandGate Gate { get; } = new();

        public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public void Dispose()
        {
            Gate.Release();
            _scope.Dispose();
            _provider.Dispose();
            _keepAlive.Dispose();
        }
    }


    private sealed class FactoryDirectoryHost : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public FactoryDirectoryHost(bool withEncryptedSetting = false)
        {
            var connectionString = $"DataSource=file:dir-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();

            var services = new ServiceCollection();
            services.AddSettingCatalog(catalog =>
            {
                if (withEncryptedSetting)
                {
                    catalog.Add(new SettingDefinition("Smtp.Password", IsEncrypted: true));
                }
            });
            services.AddSingleton<ISecureBlobEncryptor>(TestBlobEncryptor.CreateAesGcm());
            services.AddDbContextFactory<TestDirectoryDbContext>(
                o => o.UseSqlite(connectionString).AddInterceptors(Gate));
            services.AddTenantMembershipStoreFromContextFactory<TestDirectoryDbContext>();
            services.AddApiKeyStoreFromContextFactory<TestDirectoryDbContext>();
            services.AddSettingStoreFromContextFactory<TestDirectoryDbContext>();

            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            using (var context = _provider.GetRequiredService<IDbContextFactory<TestDirectoryDbContext>>()
                       .CreateDbContext())
            {
                context.Database.EnsureCreated();
            }

            Gate.Arm();
        }

        public CommandGate Gate { get; } = new();

        public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public void Dispose()
        {
            Gate.Release();
            _scope.Dispose();
            _provider.Dispose();
            _keepAlive.Dispose();
        }
    }

    /// <summary>
    /// Holds the first command that arrives after <see cref="Arm"/> until <see cref="Release"/>,
    /// so a second operation is guaranteed to be issued while the first is still in flight.
    /// </summary>
    private sealed class CommandGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _held;

        public Task FirstCommandInFlight => _inFlight.Task;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.Exchange(ref _held, 1) == 0)
            {
                _inFlight.TrySetResult();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}
