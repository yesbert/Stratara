using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A permit ledger admits no new unit for one lease after it was created and takes back every running unit that
/// registers again, so the units a lost keeper had admitted count again before the bound applies to new units; after
/// the grace a unit registering again is refused only at the bound.
/// </summary>
public sealed class PermitLedgerTests
{
    private const int Limit = 2;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(4);
    private static readonly SiloAddress Holder = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
    private static readonly ClusterMembershipSnapshot NobodyDead = new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, new MembershipVersion(1));

    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void An_acquisition_within_the_grace_is_refused()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease - TimeSpan.FromMilliseconds(1));

        Assert.False(ledger.TryAcquire(Guid.NewGuid(), Holder));
        Assert.Equal(0, ledger.InUse);
    }

    [Fact]
    public void A_reclaim_within_the_grace_is_taken_even_beyond_the_bound()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);

        for (var i = 0; i <= Limit; i++)
        {
            Assert.True(ledger.Reclaim(Guid.NewGuid(), Holder));
        }

        Assert.Equal(Limit + 1, ledger.InUse);
    }

    [Fact]
    public void An_acquisition_after_the_grace_is_taken_within_the_bound()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);

        Assert.True(ledger.TryAcquire(Guid.NewGuid(), Holder));
        Assert.True(ledger.TryAcquire(Guid.NewGuid(), Holder));
        Assert.False(ledger.TryAcquire(Guid.NewGuid(), Holder));
        Assert.Equal(Limit, ledger.InUse);
    }

    [Fact]
    public void A_reclaim_after_the_grace_at_the_bound_is_refused()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);
        ledger.TryAcquire(Guid.NewGuid(), Holder);
        ledger.TryAcquire(Guid.NewGuid(), Holder);

        Assert.False(ledger.Reclaim(Guid.NewGuid(), Holder));
        Assert.Equal(Limit, ledger.InUse);
    }

    [Fact]
    public void A_reclaim_after_the_grace_with_room_is_taken_and_renews()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);
        var unit = Guid.NewGuid();

        Assert.True(ledger.Reclaim(unit, Holder));
        Assert.True(ledger.Renew(unit));
        _clock.Advance(Lease - TimeSpan.FromMilliseconds(1));
        Assert.Empty(ledger.Reconcile(NobodyDead));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var released = Assert.Single(ledger.Reconcile(NobodyDead));
        Assert.Equal(unit, released.UnitId);
        Assert.False(released.HolderDead);
        Assert.Equal(0, ledger.InUse);
    }

    [Fact]
    public void A_permit_that_falls_free_goes_to_the_refused_unit_and_not_to_a_new_one()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);
        var running = Guid.NewGuid();
        var held = Enumerable.Range(0, Limit).Select(_ => Guid.NewGuid()).ToList();
        foreach (var unit in held)
        {
            Assert.True(ledger.TryAcquire(unit, Holder));
        }

        Assert.False(ledger.Reclaim(running, Holder));
        ledger.Release(held[0]);

        Assert.False(ledger.TryAcquire(Guid.NewGuid(), Holder));
        Assert.True(ledger.Reclaim(running, Holder));
        Assert.Equal(Limit, ledger.InUse);
    }

    [Fact]
    public void A_reservation_a_unit_stops_refreshing_frees_the_permit_for_a_new_unit()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);
        var held = Enumerable.Range(0, Limit).Select(_ => Guid.NewGuid()).ToList();
        foreach (var unit in held)
        {
            ledger.TryAcquire(unit, Holder);
        }

        Assert.False(ledger.Reclaim(Guid.NewGuid(), Holder));
        ledger.Release(held[0]);
        _clock.Advance(Lease);

        Assert.True(ledger.TryAcquire(Guid.NewGuid(), Holder));
    }

    [Fact]
    public void A_unit_that_holds_its_permit_again_keeps_no_reservation()
    {
        var ledger = new PermitLedger(Limit, Lease, _clock);
        _clock.Advance(Lease);
        var first = Guid.NewGuid();
        Assert.True(ledger.TryAcquire(first, Holder));
        var running = Guid.NewGuid();
        Assert.True(ledger.TryAcquire(Guid.NewGuid(), Holder));

        Assert.False(ledger.Reclaim(running, Holder));
        ledger.Release(first);
        Assert.True(ledger.Reclaim(running, Holder));
        ledger.Release(running);

        Assert.True(ledger.TryAcquire(Guid.NewGuid(), Holder));
    }
}
