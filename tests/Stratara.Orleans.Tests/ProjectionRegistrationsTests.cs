using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The silo's projection names are the projection handler's names for the registered projections — the names the
/// store readers are started under — worked out once, and worked out again only where working them out failed.
/// </summary>
public sealed class ProjectionRegistrationsTests
{
    private static readonly IProjection Orders = new Mock<IProjection>().Object;

    [Fact]
    public void The_names_are_the_ones_the_projection_handler_gives()
    {
        var registrations = RegistrationsOf(services => services.AddScoped(_ => Orders));

        Assert.True(registrations.Contains("Orders"));
        Assert.False(registrations.Contains(Orders.GetType().Name));
        Assert.False(registrations.Contains("Totals"));
    }

    [Fact]
    public void A_second_question_constructs_no_projection()
    {
        var constructed = 0;
        var registrations = RegistrationsOf(services => services.AddScoped(_ =>
        {
            constructed++;
            return Orders;
        }));

        Assert.True(registrations.Contains("Orders"));
        Assert.False(registrations.Contains("Totals"));
        Assert.Equal(1, constructed);
    }

    [Fact]
    public void A_failure_to_work_out_the_names_is_asked_again()
    {
        var attempts = 0;
        var registrations = RegistrationsOf(services => services.AddScoped(_ =>
            ++attempts == 1 ? throw new InvalidOperationException("not yet") : Orders));

        Assert.Throws<InvalidOperationException>(() => registrations.Contains("Orders"));
        Assert.True(registrations.Contains("Orders"));
    }

    [Fact]
    public void The_names_keep_registration_order_and_drop_a_second_projection_of_the_same_name()
    {
        var totals = new TotalsProjection();
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(Orders)).Returns("Orders");
        handler.Setup(h => h.GetProjectionName(totals)).Returns("Totals");
        var services = new ServiceCollection()
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ => totals)
            .AddScoped(_ => Orders)
            .AddScoped<IProjection>(_ => totals);
        var registrations = new ProjectionRegistrations(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(["Totals", "Orders"], registrations.Names);
        Assert.Equal("Totals", registrations.NameOf(typeof(TotalsProjection)));
        Assert.Null(registrations.NameOf(typeof(ProjectionRegistrationsTests)));
    }

    [Fact]
    public async Task Commits_construct_each_projection_once_per_silo_to_learn_the_readers_they_wake()
    {
        var constructed = 0;
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(It.IsAny<IProjection>())).Returns<IProjection>(p => p.GetType().Name);
        var grains = new Mock<IGrainFactory>();
        var nudged = 0;
        grains.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns(new IdleGrain(() => Interlocked.Increment(ref nudged)));
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(grains.Object)
            .AddSingleton(new Mock<IProjectionReplayState>().Object)
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ =>
            {
                Interlocked.Increment(ref constructed);
                return new TotalsProjection();
            })
            .AddScoped<IProjection>(_ =>
            {
                Interlocked.Increment(ref constructed);
                return new OrdersProjection();
            })
            .AddStrataraProjectionGrains();
        await using var provider = services.BuildServiceProvider();

        for (var commit = 0; commit < 3; commit++)
        {
            await using var scope = provider.CreateAsyncScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<OrleansEventBundleDispatcher>());
            foreach (var target in scope.ServiceProvider.GetServices<INudgeTarget>())
            {
                await target.NudgeAsync(grains.Object, 0);
            }
        }

        Assert.Equal(2, Volatile.Read(ref constructed));
        Assert.Equal(6, Volatile.Read(ref nudged));
    }

    private sealed class OrdersProjection : IProjection;

    /// <summary>A store reader that counts its wake-ups and answers every other call at once.</summary>
    private sealed class IdleGrain(Action onNudge) : IProjectionGrain
    {
        public Task EnsureRunningAsync() => Task.CompletedTask;

        public Task NudgeAsync()
        {
            onNudge();
            return Task.CompletedTask;
        }

        public Task<int> CatchUpAsync() => Task.FromResult(0);

        public Task<long> PositionAsync() => Task.FromResult(0L);

        public Task PauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync(Guid pauser) => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public Task RenewPauseAsync(Guid pauser, TimeSpan lease) => Task.CompletedTask;

        public Task<StoreReaderProgress> WaitForAsync(long position, TimeSpan budget) => Task.FromResult(new StoreReaderProgress(true, StoreReaderState.Advancing));
    }

    private sealed class TotalsProjection : IProjection;

    private static ProjectionRegistrations RegistrationsOf(Action<IServiceCollection> registerProjections)
    {
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(Orders)).Returns("Orders");
        var services = new ServiceCollection().AddScoped(_ => handler.Object);
        registerProjections(services);
        return new ProjectionRegistrations(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }
}
