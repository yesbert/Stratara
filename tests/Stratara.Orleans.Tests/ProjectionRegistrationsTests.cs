using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    private static ProjectionRegistrations RegistrationsOf(Action<IServiceCollection> registerProjections)
    {
        var handler = new Mock<IProjectionHandler>();
        handler.Setup(h => h.GetProjectionName(Orders)).Returns("Orders");
        var services = new ServiceCollection().AddScoped(_ => handler.Object);
        registerProjections(services);
        return new ProjectionRegistrations(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }
}
