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
