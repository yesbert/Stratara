using Microsoft.Extensions.Logging;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Who holds a store reader paused, and until when. Each pause belongs to the pauser that took it and lasts until that
/// pauser resumes it or stops renewing it: the expiry is taken from the reader's own clock when a pause or a renewal
/// arrives, so no clock is compared across silos. Releasing a pauser the reader does not hold does nothing, which makes
/// a repeated resume harmless. A released pauser is remembered for one lease, so a pause or a renewal of it that
/// arrives after its resume — delivered late — does not pause the reader again; a pauser that lapsed is not
/// remembered, so its renewal pauses the reader again. A pause taken through the parameterless methods of an older
/// silo belongs to an anonymous pauser, and an anonymous resume releases the oldest of them.
/// </summary>
/// <remarks>Every caller runs on the activation's scheduler, so the collections need no lock.</remarks>
internal sealed class StoreReaderPausers(TimeProvider clock)
{
    private readonly Dictionary<Guid, (DateTimeOffset Until, TimeSpan Lease)> _held = [];
    private readonly Dictionary<Guid, DateTimeOffset> _released = [];
    private readonly List<Guid> _anonymous = [];

    /// <summary>Whether any pauser holds the reader.</summary>
    public bool Any => _held.Count > 0;

    /// <summary>Whether <paramref name="pauser"/> holds the reader.</summary>
    public bool Holds(Guid pauser) => _held.ContainsKey(pauser);

    /// <summary>
    /// Holds the reader for <paramref name="pauser"/> until <paramref name="lease"/> from now — or longer, where the
    /// pauser already holds it for longer. A pauser the reader does not hold, its pause lapsed or its activation moved,
    /// holds it again; a pauser released within the last lease does not.
    /// </summary>
    /// <returns><see langword="false"/> where the pauser was released within the last lease and holds nothing.</returns>
    public bool Hold(Guid pauser, TimeSpan lease)
    {
        var now = clock.GetUtcNow();
        if (_released.TryGetValue(pauser, out var remembered))
        {
            if (remembered > now)
            {
                return false;
            }

            _released.Remove(pauser);
        }

        var until = now + lease;
        _held[pauser] = _held.TryGetValue(pauser, out var held) && held.Until > until ? held : (until, lease);
        return true;
    }

    /// <summary>Holds the reader for a new anonymous pauser, one whose caller cannot renew it.</summary>
    public void HoldAnonymously(TimeSpan lease)
    {
        var pauser = Guid.NewGuid();
        _anonymous.Add(pauser);
        Hold(pauser, lease);
    }

    /// <summary>Releases <paramref name="pauser"/>'s pause, and says whether that left the reader without a pauser.</summary>
    /// <returns><see langword="true"/> where the pauser was held and was the last.</returns>
    public bool Release(Guid pauser)
    {
        _anonymous.Remove(pauser);
        if (!_held.Remove(pauser, out var held))
        {
            return false;
        }

        _released[pauser] = clock.GetUtcNow() + held.Lease;
        return _held.Count == 0;
    }

    /// <summary>Releases the oldest anonymous pause, and says whether that left the reader without a pauser.</summary>
    /// <returns><see langword="true"/> where an anonymous pause was held and was the last.</returns>
    public bool ReleaseOldestAnonymous() => _anonymous.Count > 0 && Release(_anonymous[0]);

    /// <summary>
    /// Drops every pause whose lease has passed and logs each, and says whether that left the reader without a pauser —
    /// the reader then restarts as a resume would restart it.
    /// </summary>
    /// <returns><see langword="true"/> where a pause lapsed and none is left.</returns>
    public bool Lapse(ILogger logger, string consumer, int partition)
    {
        var now = clock.GetUtcNow();
        foreach (var forgotten in _released.Where(release => release.Value <= now).Select(release => release.Key).ToList())
        {
            _released.Remove(forgotten);
        }

        if (_held.Count == 0)
        {
            return false;
        }

        var lapsed = _held.Where(pause => pause.Value.Until <= now).Select(pause => pause.Key).ToList();
        foreach (var pauser in lapsed)
        {
            _held.Remove(pauser);
            _anonymous.Remove(pauser);
            logger.LogStoreReaderPauseLapsed(consumer, partition, pauser, _held.Count);
        }

        return lapsed.Count > 0 && _held.Count == 0;
    }
}
