using Orleans.Runtime;
using Stratara.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// The table behind the heavy-work permits and the rules it keeps. A ledger starts empty, and it cannot tell a first
/// start from the loss of the keeper before it, whose units may still be running: for one lease after it was created —
/// the grace — it admits no unit it does not hold and takes back every running unit that registers again, which each
/// does at its next renewal, at the latest half a lease after the ledger is reachable. After the grace a unit
/// registering again is taken only while the bound has room, because it had let its lease lapse with the lost keeper
/// too. A unit refused there keeps a reservation for one lease, refreshed by every further refusal, and a
/// reservation counts against the bound like a permit: the next permit that falls free goes to the unit that is
/// already running rather than to one waiting to start, so the bound is exceeded only until a running unit ends. A
/// permit is released by its unit, by its lease lapsing without renewal, or by its holder being declared dead.
/// </summary>
internal sealed class PermitLedger
{
    private readonly int _limit;
    private readonly TimeSpan _lease;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _graceEndsAt;
    private readonly Dictionary<Guid, Permit> _permits = new();
    private readonly Dictionary<Guid, DateTimeOffset> _reserved = new();

    public PermitLedger(int limit, TimeSpan lease, TimeProvider timeProvider)
    {
        _limit = limit;
        _lease = lease;
        _timeProvider = timeProvider;
        _graceEndsAt = timeProvider.GetUtcNow() + lease;
    }

    public int InUse => _permits.Count;

    /// <summary>
    /// Takes a permit for a new unit, unless the grace has not ended, the bound is reached, or a running unit that
    /// was refused holds a reservation on the room that is left; a unit already held keeps its permit.
    /// </summary>
    public bool TryAcquire(Guid unitId, SiloAddress holder)
    {
        if (_permits.ContainsKey(unitId))
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        if (now < _graceEndsAt || _permits.Count + Reservations(now) >= _limit)
        {
            return false;
        }

        Take(unitId, holder, now);
        return true;
    }

    /// <summary>Takes back a running unit whose permit was not held: always within the grace, afterwards only while the bound has room.</summary>
    public bool Reclaim(Guid unitId, SiloAddress holder)
    {
        var now = _timeProvider.GetUtcNow();
        if (_permits.TryGetValue(unitId, out var permit))
        {
            _reserved.Remove(unitId);
            _permits[unitId] = permit with { ExpiresAt = now + _lease };
            return true;
        }

        if (now >= _graceEndsAt && _permits.Count >= _limit)
        {
            _reserved[unitId] = now + _lease;
            return false;
        }

        _reserved.Remove(unitId);
        Take(unitId, holder, now);
        return true;
    }

    /// <summary>Extends a unit's lease; <see langword="false"/> when the permit is not held.</summary>
    public bool Renew(Guid unitId)
    {
        if (!_permits.TryGetValue(unitId, out var permit))
        {
            return false;
        }

        _permits[unitId] = permit with { ExpiresAt = _timeProvider.GetUtcNow() + _lease };
        return true;
    }

    public void Release(Guid unitId)
    {
        _reserved.Remove(unitId);
        if (_permits.Remove(unitId))
        {
            ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(-1);
        }
    }

    /// <summary>Releases every permit whose lease lapsed or whose holder <paramref name="snapshot"/> reports dead, and returns what it released.</summary>
    public IReadOnlyList<ReleasedPermit> Reconcile(ClusterMembershipSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow();
        var released = new List<ReleasedPermit>();
        foreach (var (unitId, permit) in _permits.ToList())
        {
            var holderDead = snapshot.GetSiloStatus(permit.Holder) == SiloStatus.Dead;
            if (!holderDead && permit.ExpiresAt > now)
            {
                continue;
            }

            _permits.Remove(unitId);
            ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(-1);
            released.Add(new ReleasedPermit(unitId, permit.Holder, holderDead));
        }

        return released;
    }

    /// <summary>
    /// How many running units that were refused are waiting for the next free permit; a reservation a unit stopped
    /// refreshing — it ended, or its silo died — is dropped once its lease has passed.
    /// </summary>
    private int Reservations(DateTimeOffset now)
    {
        foreach (var (unitId, expiresAt) in _reserved.ToList())
        {
            if (expiresAt <= now)
            {
                _reserved.Remove(unitId);
            }
        }

        return _reserved.Count;
    }

    private void Take(Guid unitId, SiloAddress holder, DateTimeOffset now)
    {
        _permits[unitId] = new Permit(holder, now + _lease);
        ApplicationDiagnostics.Metrics.OrleansHeavyPermitsInUse.Add(1);
    }

    private sealed record Permit(SiloAddress Holder, DateTimeOffset ExpiresAt);
}

/// <summary>A permit the ledger released on reconciliation, and whether its holder was declared dead or its lease lapsed.</summary>
internal sealed record ReleasedPermit(Guid UnitId, SiloAddress Holder, bool HolderDead);
