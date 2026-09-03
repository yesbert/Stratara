using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Projections;
using Stratara.Testing;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.DependencyInjection;

public class NpgsqlDbContextServiceCollectionExtensionsTests
{
    public sealed class TestWriteDbContext(DbContextOptions<TestWriteDbContext> options)
        : WriteDbContext<TestWriteDbContext>(options);

    public sealed class TestReadDbContext(DbContextOptions<TestReadDbContext> options)
        : ReadDbContext<TestReadDbContext>(options);

    private static ServiceCollection CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:defaultdb"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ISessionContextProvider>(new TestSessionContextProvider(TestSessionContext.ForTenant(Guid.NewGuid())));
        services.AddSingleton(Mock.Of<ISecureJsonSerializer>());
        return services;
    }

    [Fact]
    public void AddNpgsqlWriteDbContextFactory_Alone_ResolvesWriteUnitOfWorkOverThatContext()
    {
        var services = CreateServices();
        services.AddNpgsqlWriteDbContextFactory<TestWriteDbContext>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();

        Assert.IsType<WriteUnitOfWork<TestWriteDbContext>>(unitOfWork);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IWriteUnitOfWork)).Lifetime);
    }

    [Fact]
    public void AddNpgsqlWriteDbContextFactory_ConsumerUnitOfWorkRegisteredBefore_Wins()
    {
        var services = CreateServices();
        var own = Mock.Of<IWriteUnitOfWork>();
        services.AddScoped<IWriteUnitOfWork>(_ => own);
        services.AddNpgsqlWriteDbContextFactory<TestWriteDbContext>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>());
    }

    [Fact]
    public void AddNpgsqlWriteDbContextFactory_ConsumerUnitOfWorkRegisteredAfter_Wins()
    {
        var services = CreateServices();
        var own = Mock.Of<IWriteUnitOfWork>();
        services.AddNpgsqlWriteDbContextFactory<TestWriteDbContext>();
        services.AddScoped<IWriteUnitOfWork>(_ => own);
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>());
    }

    [Fact]
    public void AddNpgsqlWriteDbContextFactory_CalledTwice_RegistersOneUnitOfWork()
    {
        var services = CreateServices();
        services.AddNpgsqlWriteDbContextFactory<TestWriteDbContext>();
        services.AddNpgsqlWriteDbContextFactory<TestWriteDbContext>();

        Assert.Single(services, d => d.ServiceType == typeof(IWriteUnitOfWork));
    }

    [Fact]
    public void AddNpgsqlReadDbContextFactory_Alone_ResolvesOneProjectionsUnitOfWorkUnderBothContracts()
    {
        var services = CreateServices();
        services.AddNpgsqlReadDbContextFactory<TestReadDbContext>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionsUnitOfWork>();
        var read = scope.ServiceProvider.GetRequiredService<IReadUnitOfWork>();

        Assert.IsType<ProjectionsUnitOfWork<TestReadDbContext>>(projections);
        Assert.Same(projections, read);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IProjectionsUnitOfWork)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IReadUnitOfWork)).Lifetime);
    }

    [Fact]
    public void AddNpgsqlReadDbContextFactory_Alone_YieldsADifferentInstancePerScope()
    {
        var services = CreateServices();
        services.AddNpgsqlReadDbContextFactory<TestReadDbContext>();
        var sp = services.BuildServiceProvider();

        using var first = sp.CreateScope();
        using var second = sp.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IProjectionsUnitOfWork>(),
            second.ServiceProvider.GetRequiredService<IProjectionsUnitOfWork>());
    }

    [Fact]
    public void AddNpgsqlReadDbContextFactory_ConsumerUnitOfWorkRegisteredBefore_Wins()
    {
        var services = CreateServices();
        var own = Mock.Of<IProjectionsUnitOfWork>();
        services.AddScoped<IProjectionsUnitOfWork>(_ => own);
        services.AddNpgsqlReadDbContextFactory<TestReadDbContext>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IProjectionsUnitOfWork>());
        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IReadUnitOfWork>());
    }

    [Fact]
    public void AddNpgsqlReadDbContextFactory_ConsumerUnitOfWorkRegisteredAfter_Wins()
    {
        var services = CreateServices();
        var own = Mock.Of<IProjectionsUnitOfWork>();
        services.AddNpgsqlReadDbContextFactory<TestReadDbContext>();
        services.AddScoped<IProjectionsUnitOfWork>(_ => own);
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IProjectionsUnitOfWork>());
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IReadUnitOfWork)).Lifetime);
    }
}
