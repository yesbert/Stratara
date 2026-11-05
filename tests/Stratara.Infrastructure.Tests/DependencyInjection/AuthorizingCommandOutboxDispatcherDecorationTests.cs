using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Infrastructure.Authorization;
using Stratara.Outbox.RabbitMQ.Outbox;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// The enqueue-time authorizer decorates whichever dispatcher is registered, not one concrete type, and a
/// dispatcher that later takes the slot it decorates stays authorized.
/// </summary>
public sealed class AuthorizingCommandOutboxDispatcherDecorationTests
{
    [RequireRole("Operator")]
    private sealed record GuardedCommand : ICommand;

    [Fact]
    public async Task The_dispatcher_registered_before_it_is_decorated()
    {
        var dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection()
            .AddScoped<ICommandOutboxDispatcher>(_ => dispatcher)
            .AddAuthorizingCommandOutboxDispatcher();
        await using var provider = WithRole(services, granted: false).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var resolved = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();

        Assert.IsType<AuthorizingCommandOutboxDispatcher>(resolved);
        Assert.Same(dispatcher, scope.ServiceProvider.GetRequiredKeyedService<ICommandOutboxDispatcher>(typeof(ICommandOutboxDispatcher)));
        await Assert.ThrowsAsync<AuthorizationException>(() => resolved.EnqueueCommandAsync(new GuardedCommand()));
        Assert.Equal(0, dispatcher.Enqueued);
    }

    [Fact]
    public async Task An_authorized_command_reaches_the_decorated_dispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection()
            .AddScoped<ICommandOutboxDispatcher>(_ => dispatcher)
            .AddAuthorizingCommandOutboxDispatcher();
        await using var provider = WithRole(services, granted: true).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new GuardedCommand());

        Assert.Equal(1, dispatcher.Enqueued);
    }

    [Fact]
    public async Task A_dispatcher_that_takes_the_decorated_slot_afterwards_stays_authorized()
    {
        var dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection().AddAuthorizingCommandOutboxDispatcher();
        services.Remove(services.Single(d => d.ServiceType == typeof(ICommandOutboxDispatcher) && d.IsKeyedService));
        services.AddKeyedScoped<ICommandOutboxDispatcher>(typeof(ICommandOutboxDispatcher), (_, _) => dispatcher);
        await using var provider = WithRole(services, granted: false).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.ThrowsAsync<AuthorizationException>(() =>
            scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new GuardedCommand()));
        Assert.Equal(0, dispatcher.Enqueued);
    }

    [Fact]
    public void Registering_it_twice_decorates_once()
    {
        var services = new ServiceCollection()
            .AddScoped<ICommandOutboxDispatcher, RecordingDispatcher>()
            .AddAuthorizingCommandOutboxDispatcher()
            .AddAuthorizingCommandOutboxDispatcher();

        Assert.Single(services, d => d.ServiceType == typeof(ICommandOutboxDispatcher) && !d.IsKeyedService);
        var slot = Assert.Single(services, d => d.ServiceType == typeof(ICommandOutboxDispatcher) && d.IsKeyedService);
        Assert.Equal(typeof(RecordingDispatcher), slot.KeyedImplementationType);
    }

    /// <summary>
    /// <c>authorization</c> → asynchronous command dispatch is guarded identically, whichever of the outbox
    /// dispatcher and its authorizer a host registers first — the worker composites register the dispatcher, so a
    /// host that adds the authorizer before a composite must not end up with an unguarded dispatcher.
    /// </summary>
    [Fact]
    public void The_outbox_dispatcher_registered_after_it_is_still_decorated()
    {
        var services = WithDispatcherDependencies(new ServiceCollection()
            .AddAuthorizingCommandOutboxDispatcher()
            .AddOutboxDispatcher());

        AssertDecoratesTheOutboxDispatcher(services);
    }

    [Fact]
    public void The_outbox_dispatcher_registered_before_it_is_decorated()
    {
        var services = WithDispatcherDependencies(new ServiceCollection()
            .AddOutboxDispatcher()
            .AddAuthorizingCommandOutboxDispatcher());

        AssertDecoratesTheOutboxDispatcher(services);
    }

    [Fact]
    public void The_outbox_dispatcher_registered_twice_after_it_is_still_decorated_once()
    {
        var services = WithDispatcherDependencies(new ServiceCollection()
            .AddAuthorizingCommandOutboxDispatcher()
            .AddOutboxDispatcher()
            .AddOutboxDispatcher());

        Assert.Single(services, d => d.ServiceType == typeof(ICommandOutboxDispatcher) && !d.IsKeyedService);
        AssertDecoratesTheOutboxDispatcher(services);
    }

    [Fact]
    public async Task A_guarded_command_is_refused_when_the_outbox_dispatcher_is_registered_after_the_authorizer()
    {
        var bus = new Mock<IMessageBus>();
        var services = WithDispatcherDependencies(new ServiceCollection()
            .AddAuthorizingCommandOutboxDispatcher()
            .AddOutboxDispatcher(), bus.Object);
        await using var provider = WithRole(services, granted: false).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.ThrowsAsync<AuthorizationException>(() =>
            scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new GuardedCommand(), TestContext.Current.CancellationToken));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_dispatcher_of_the_hosts_own_registered_after_the_outbox_dispatcher_is_what_is_resolved()
    {
        var dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection()
            .AddOutboxDispatcher()
            .AddScoped<ICommandOutboxDispatcher>(_ => dispatcher);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(dispatcher, scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>());
    }

    private static void AssertDecoratesTheOutboxDispatcher(IServiceCollection services)
    {
        using var provider = WithRole(services, granted: true).BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<AuthorizingCommandOutboxDispatcher>(scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>());
        Assert.IsType<CommandOutboxDispatcher>(scope.ServiceProvider.GetRequiredKeyedService<ICommandOutboxDispatcher>(typeof(ICommandOutboxDispatcher)));
    }

    private static IServiceCollection WithDispatcherDependencies(IServiceCollection services, IMessageBus? bus = null)
    {
        var pipelines = new Mock<ResiliencePipelineProvider<string>>();
        pipelines.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IWriteUnitOfWork>());
        services.AddSingleton(bus ?? Mock.Of<IMessageBus>());
        services.AddSingleton(Mock.Of<IMessagingIdentifier>());
        services.AddSingleton(Mock.Of<ISessionContextProvider>());
        services.AddSingleton(pipelines.Object);
        services.AddSingleton(Mock.Of<ISecureJsonSerializer>());
        return services;
    }

    private static IServiceCollection WithRole(IServiceCollection services, bool granted)
    {
        var provider = new Mock<IAuthorizationProvider>();
        provider.Setup(p => p.IsInRoleAsync("Operator", It.IsAny<CancellationToken>())).ReturnsAsync(granted);
        return services.AddScoped(_ => provider.Object);
    }

    private sealed class RecordingDispatcher : ICommandOutboxDispatcher
    {
        public int Enqueued { get; private set; }

        public Task<Guid> EnqueueCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : ICommand
        {
            Enqueued++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
