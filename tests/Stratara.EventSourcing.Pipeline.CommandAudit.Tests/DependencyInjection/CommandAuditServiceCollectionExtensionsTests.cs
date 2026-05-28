using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
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
