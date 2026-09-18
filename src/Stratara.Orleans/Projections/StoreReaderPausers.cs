using Microsoft.Extensions.Logging;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Projections;

/// <summary>
/// Who holds a store reader paused, and until when. Each pause belongs to the pauser that took it and lasts until that
/// pauser resumes it or stops renewing it: the expiry is taken from the reader's own clock when a pause or a renewal
/// arrives, so no clock is compared across silos. Releasing a pauser the reader does not hold does nothing, which makes
/// a repeated resume harmless. A pause taken through the parameterless methods of an older silo belongs to an anonymous
/// pauser, and an anonymous resume releases the oldest of them.
/// </summary>
/// <remarks>Every caller runs on the activation's scheduler, so the collections need no lock.</remarks>
internal sealed class StoreReaderPausers(TimeProvider clock)
{
    private readonly Dictionary<Guid, DateTimeOffset> _held = [];
    private readonly List<Guid> _anonymous = [];

    /// <summary>Whether any pauser holds the reader.</summary>
    public bool Any => _held.Count > 0;

    /// <summary>Whether <paramref name="pauser"/> holds the reader.</summary>
    public bool Holds(Guid pauser) => _held.ContainsKey(pauser);

    /// <summary>
    /// Holds the reader for <paramref name="pauser"/> until <paramref name="lease"/> from now — or longer, where the
    /// pauser already holds it for longer. A pauser the reader does not hold, its pause lapsed or its activation moved,
    /// holds it again.
    /// </summary>
    public void Hold(Guid pauser, TimeSpan lease)
    {
        var until = clock.GetUtcNow() + lease;
        _held[pauser] = _held.TryGetValue(pauser, out var held) && held > until ? held : until;
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
        return _held.Remove(pauser) && _held.Count == 0;
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
        if (_held.Count == 0)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var lapsed = _held.Where(pause => pause.Value <= now).Select(pause => pause.Key).ToList();
        foreach (var pauser in lapsed)
        {
            _held.Remove(pauser);
            _anonymous.Remove(pauser);
            logger.LogStoreReaderPauseLapsed(consumer, partition, pauser, _held.Count);
        }

        return lapsed.Count > 0 && _held.Count == 0;
    }
}
