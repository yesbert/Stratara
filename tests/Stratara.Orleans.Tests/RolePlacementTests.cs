using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Orleans.Runtime;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Placement by role: a role's grains go to the silos that publish the role, a cluster without one fails naming
/// the role and its registration, and the timer role is published only where an owner check is registered.
/// </summary>
public sealed class RolePlacementTests
{
    private static readonly SiloAddress Commands = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Readers = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 1);
    private static readonly SiloAddress Neither = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11113), 1);

    [Fact]
    public void A_role_is_placed_on_the_silos_that_publish_it()
    {
        var published = new Dictionary<SiloAddress, string[]>
        {
            [Commands] = [RolePlacement.MetadataKeyOf(ExecutionRole.Commands)],
            [Readers] = [RolePlacement.MetadataKeyOf(ExecutionRole.Projections), RolePlacement.MetadataKeyOf(ExecutionRole.Sagas)],
            [Neither] = [],
        };

        Assert.Equal([Commands], RolePlacement.Select(ExecutionRole.Commands, published.Keys, silo => published[silo].Contains(RolePlacement.MetadataKeyOf(ExecutionRole.Commands))));
        Assert.Equal([Readers], RolePlacement.Select(ExecutionRole.Sagas, published.Keys, silo => published[silo].Contains(RolePlacement.MetadataKeyOf(ExecutionRole.Sagas))));
    }

    [Fact]
    public void A_cluster_without_the_role_fails_naming_the_role_and_its_registration()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => RolePlacement.Select(ExecutionRole.Projections, [Commands, Neither], _ => false));

        Assert.Contains("projections role", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddStrataraProjectionGrains", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_role_has_a_distinct_key_and_a_registration()
    {
        var roles = Enum.GetValues<ExecutionRole>();

        Assert.Equal(roles.Length, roles.Select(RolePlacement.MetadataKeyOf).Distinct(StringComparer.Ordinal).Count());
        Assert.All(roles, role => Assert.StartsWith("AddStratara", RolePlacement.RegistrationOf(role), StringComparison.Ordinal));
    }

    [Fact]
    public void Published_roles_are_collected_across_registrations_and_written_to_the_metadata()
    {
        var services = new ServiceCollection();
        RolePlacement.Publish(services, ExecutionRole.Commands);
        RolePlacement.Publish(services, ExecutionRole.Projections);
        RolePlacement.Publish(services, ExecutionRole.Commands);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        RolePlacement.Fill(entries, services.BuildServiceProvider());

        Assert.Equal(
            [RolePlacement.MetadataKeyOf(ExecutionRole.Commands), RolePlacement.MetadataKeyOf(ExecutionRole.Projections)],
            entries.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_timer_role_is_published_only_where_an_owner_check_is_registered()
    {
        var withoutOwners = new ServiceCollection();
        RolePlacement.Publish(withoutOwners, ExecutionRole.Timers);
        var withOwners = new ServiceCollection().AddSingleton(Mock.Of<ITimerOwners>());
        RolePlacement.Publish(withOwners, ExecutionRole.Timers);
        var silent = new Dictionary<string, string>(StringComparer.Ordinal);
        var publishing = new Dictionary<string, string>(StringComparer.Ordinal);

        RolePlacement.Fill(silent, withoutOwners.BuildServiceProvider());
        RolePlacement.Fill(publishing, withOwners.BuildServiceProvider());

        Assert.Empty(silent);
        Assert.Equal([RolePlacement.MetadataKeyOf(ExecutionRole.Timers)], publishing.Keys);
    }

    [Fact]
    public void A_composition_that_publishes_nothing_fills_nothing()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        RolePlacement.Fill(entries, new ServiceCollection().BuildServiceProvider());

        Assert.Empty(entries);
    }
}
