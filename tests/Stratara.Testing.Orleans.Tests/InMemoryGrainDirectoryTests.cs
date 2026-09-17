using System.Net;
using Orleans.Runtime;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>The in-memory directory keeps the directory's contract for one silo.</summary>
public sealed class InMemoryGrainDirectoryTests
{
    private static readonly SiloAddress First = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Second = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 1);

    [Fact]
    public async Task A_registration_returns_the_address_already_registered()
    {
        var directory = new InMemoryGrainDirectory();
        var grain = GrainId.Create("probe", Guid.NewGuid().ToString("N"));
        var registered = Address(grain, First);

        Assert.Same(registered, await directory.Register(registered));
        Assert.Same(registered, await directory.Register(Address(grain, Second)));
        Assert.Same(registered, await directory.Lookup(grain));
    }

    [Fact]
    public async Task A_registration_replaces_the_previous_address_it_names()
    {
        var directory = new InMemoryGrainDirectory();
        var grain = GrainId.Create("probe", Guid.NewGuid().ToString("N"));
        var previous = Address(grain, First);
        var next = Address(grain, Second);
        await directory.Register(previous);

        Assert.Same(next, await directory.Register(next, previous));
        Assert.Same(next, await directory.Lookup(grain));
    }

    [Fact]
    public async Task An_unregistration_removes_only_the_matching_address()
    {
        var directory = new InMemoryGrainDirectory();
        var grain = GrainId.Create("probe", Guid.NewGuid().ToString("N"));
        var registered = Address(grain, First);
        await directory.Register(registered);

        await directory.Unregister(Address(grain, Second));
        Assert.Same(registered, await directory.Lookup(grain));

        await directory.Unregister(registered);
        Assert.Null(await directory.Lookup(grain));
    }

    [Fact]
    public async Task Unregistering_silos_removes_every_address_on_them_and_the_clear_counts()
    {
        var directory = new InMemoryGrainDirectory();
        var onFirst = GrainId.Create("probe", "first");
        var onSecond = GrainId.Create("probe", "second");
        await directory.Register(Address(onFirst, First));
        await directory.Register(Address(onSecond, Second));

        await directory.UnregisterSilos([First]);

        Assert.Null(await directory.Lookup(onFirst));
        Assert.NotNull(await directory.Lookup(onSecond));
        Assert.Equal(1, directory.Clear());
        Assert.Null(await directory.Lookup(onSecond));
    }

    private static GrainAddress Address(GrainId grain, SiloAddress silo) => new()
    {
        GrainId = grain,
        SiloAddress = silo,
        ActivationId = ActivationId.NewId(),
    };
}
