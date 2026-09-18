using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A command dispatched through the execution model with no session context set fails with the
/// missing-identity failure, before anything is recorded or forwarded — on the durable-intent path and
/// on the synchronous grain path alike.
/// </summary>
public sealed class NoSessionDispatchTests
{
    public sealed record ScopedCommand(Guid AggregateId) : ICommand, IAggregateScopedCommand;

    [Fact]
    public async Task The_durable_intent_dispatcher_refuses_a_command_without_a_session_before_recording_it()
    {
        var intents = new Mock<ICommandIntentStore>();
        var sessions = new Mock<ISessionContextProvider>();
        sessions.SetupGet(s => s.Current).Returns((SessionContext?)null);
        var services = new ServiceCollection().AddLogging();
        services.AddStrataraOrleansCommandDispatcher();
        services
            .AddScoped(_ => intents.Object)
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddSingleton(new Mock<ISecureJsonSerializer>().Object)
            .AddSingleton(sessions.Object)
            .AddSingleton(new Mock<IProjectionReplayState>().Object);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();

        var exception = await Assert.ThrowsAsync<SessionRequiredException>(
            () => dispatcher.EnqueueCommandAsync(new ScopedCommand(Guid.NewGuid())));

        Assert.Equal("Session context is not set", exception.Message);
        intents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_grain_behaviour_refuses_a_command_without_a_session_before_forwarding_it()
    {
        var grains = new Mock<IGrainFactory>(MockBehavior.Strict);
        var sessions = new Mock<ISessionContextProvider>();
        sessions.SetupGet(s => s.Current).Returns((SessionContext?)null);
        var behaviour = new AggregateGrainBehavior<ScopedCommand>(
            grains.Object, sessions.Object, new Mock<ISecureJsonSerializer>().Object, new AggregateSendLane());
        var reachedHandler = false;

        var exception = await Assert.ThrowsAsync<SessionRequiredException>(() => behaviour.HandleAsync(
            new ScopedCommand(Guid.NewGuid()),
            () =>
            {
                reachedHandler = true;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal("Session context is not set", exception.Message);
        Assert.False(reachedHandler);
    }
}
