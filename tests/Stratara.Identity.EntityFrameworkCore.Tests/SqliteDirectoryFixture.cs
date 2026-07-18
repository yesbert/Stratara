using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

internal sealed class TestDirectoryDbContext(DbContextOptions<TestDirectoryDbContext> options)
    : IdentityDirectoryDbContext<TestDirectoryDbContext>(options);

internal sealed class SqliteDirectoryFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public SqliteDirectoryFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<TestDirectoryDbContext>(o => o.UseSqlite(_connection));
        services.AddTenantMembershipStore<TestDirectoryDbContext>();
        services.AddSettingCatalog(_ => { });
        services.AddSettingStore<TestDirectoryDbContext>();
        _provider = services.BuildServiceProvider();

        _scope = _provider.CreateScope();
        _scope.ServiceProvider.GetRequiredService<TestDirectoryDbContext>().Database.EnsureCreated();
        Store = _scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>();
        SettingStore = _scope.ServiceProvider.GetRequiredService<Stratara.Abstractions.Settings.ISettingStore>();
    }

    public ITenantMembershipStore Store { get; }

    public Stratara.Abstractions.Settings.ISettingStore SettingStore { get; }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }
}
