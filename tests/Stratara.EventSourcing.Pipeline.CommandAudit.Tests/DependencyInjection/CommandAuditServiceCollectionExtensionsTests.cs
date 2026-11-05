using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;
using Stratara.EventSourcing.Pipeline.CommandAudit;

namespace Stratara.EventSourcing.Pipeline.CommandAudit.Tests.DependencyInjection;

public class CommandAuditServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCommandAuditing_RegistersBothBehaviorAritiesAsScoped()
    {
        var services = new ServiceCollection();

        services.AddCommandAuditing();

        var twoArity = Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<,>));
        Assert.Equal(typeof(CommandAuditBehavior<,>), twoArity.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, twoArity.Lifetime);

        var oneArity = Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<>));
        Assert.Equal(typeof(CommandAuditBehavior<>), oneArity.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, oneArity.Lifetime);
    }

    [Fact]
    public void AddCommandAuditing_CalledTwice_InstallsEachBehaviorOnce()
    {
        var services = new ServiceCollection();

        services.AddCommandAuditing();
        services.AddCommandAuditing();

        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<,>));
        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<>));
    }

    [Fact]
    public void The_behaviours_resolve_where_no_session_provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<IWriteUnitOfWork>());
        services.AddCommandAuditing();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<CommandAuditBehavior<StubCommand>>(scope.ServiceProvider.GetRequiredService<IPipelineBehavior<StubCommand>>());
        Assert.IsType<CommandAuditBehavior<StubCommandWithResult, int>>(
            scope.ServiceProvider.GetRequiredService<IPipelineBehavior<StubCommandWithResult, int>>());
    }

    public sealed record StubCommand : ICommand;

    public sealed record StubCommandWithResult : ICommand<int>;

    [Fact]
    public void AddCommandAuditing_ReturnsSameServiceCollectionForChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddCommandAuditing();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddCommandAuditing_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddCommandAuditing());
    }
}
