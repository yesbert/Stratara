using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Infrastructure.Authorization;

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
