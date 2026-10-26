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
    public async Task A_commit_constructs_no_projection_to_learn_the_readers_it_wakes()
    {
        var constructed = 0;
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(It.IsAny<IProjection>())).Returns<IProjection>(p => p.GetType().Name);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IGrainFactory>().Object)
            .AddSingleton(new Mock<IProjectionReplayState>().Object)
            .AddScoped(_ => handler.Object)
            .AddScoped<IProjection>(_ =>
            {
                Interlocked.Increment(ref constructed);
                return new TotalsProjection();
            })
            .AddStrataraProjectionGrains();
        await using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ProjectionRegistrations>().Contains(nameof(TotalsProjection)));
        var before = Volatile.Read(ref constructed);

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrleansEventBundleDispatcher>();

        Assert.NotNull(dispatcher);
        Assert.Equal(before, Volatile.Read(ref constructed));
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
