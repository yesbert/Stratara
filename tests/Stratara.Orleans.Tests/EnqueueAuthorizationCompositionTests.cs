using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The enqueue-time authorizer and the execution model's dispatcher compose in either registration order:
/// the resolved dispatcher is the authorizer, the one it wraps is the execution model's, and a command the
/// caller may not issue is refused before anything is recorded.
/// </summary>
/// <remarks>
/// This runs against the real registrations rather than a silo: a command type carrying a role attribute in
/// the integration assembly would fail the start of every host there that registers the plain mediator.
/// </remarks>
public sealed class EnqueueAuthorizationCompositionTests
{
    [RequireRole("Operator")]
    public sealed record GuardedCommand(Guid AggregateId) : ICommand, IAggregateScopedCommand;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_the_caller_may_not_issue_is_refused_before_it_is_recorded(bool authorizerFirst)
    {
        var intents = new Mock<ICommandIntentStore>();
        var services = new ServiceCollection().AddLogging();
        if (authorizerFirst)
        {
            services.AddAuthorizingCommandOutboxDispatcher().AddStrataraOrleansCommandDispatcher();
        }
        else
        {
            services.AddStrataraOrleansCommandDispatcher().AddAuthorizingCommandOutboxDispatcher();
        }

        var authorization = new Mock<IAuthorizationProvider>();
        authorization.Setup(a => a.IsInRoleAsync("Operator", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        services
            .AddScoped(_ => authorization.Object)
            .AddScoped(_ => intents.Object)
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddSingleton(new Mock<ISecureJsonSerializer>().Object)
            .AddSingleton(new Mock<ISessionContextProvider>().Object)
            .AddSingleton(new Mock<IProjectionReplayState>().Object);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();

        Assert.Equal("AuthorizingCommandOutboxDispatcher", dispatcher.GetType().Name);
        Assert.IsType<OrleansCommandDispatcher>(scope.ServiceProvider.GetRequiredKeyedService<ICommandOutboxDispatcher>(typeof(ICommandOutboxDispatcher)));
        await Assert.ThrowsAsync<AuthorizationException>(() => dispatcher.EnqueueCommandAsync(new GuardedCommand(Guid.NewGuid())));
        intents.VerifyNoOtherCalls();
    }
}
