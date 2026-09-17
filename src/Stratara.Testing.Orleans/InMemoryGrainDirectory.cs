using System.Collections.Concurrent;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Stratara.Testing.Orleans;

/// <summary>
/// A grain directory in memory, for the one silo of <see cref="ExecutionModelTestHost"/>: one address per grain, a
/// registration that returns the address already registered where there is one, and an unregistration that removes
/// only the address it names. It is not shared with anything outside the process, so it serves exactly one silo.
/// </summary>
internal sealed class InMemoryGrainDirectory : IGrainDirectory
{
    private readonly ConcurrentDictionary<GrainId, GrainAddress> _entries = new();

    public int Count => _entries.Count;

    public Task<GrainAddress?> Register(GrainAddress address) => Register(address, previousAddress: null);

    public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        ArgumentNullException.ThrowIfNull(address);
        while (true)
        {
            if (_entries.TryAdd(address.GrainId, address))
            {
                return Task.FromResult<GrainAddress?>(address);
            }

            if (!_entries.TryGetValue(address.GrainId, out var existing))
            {
                continue;
            }

            if (previousAddress is null || !existing.Matches(previousAddress))
            {
                return Task.FromResult<GrainAddress?>(existing);
            }

            if (_entries.TryUpdate(address.GrainId, address, existing))
            {
                return Task.FromResult<GrainAddress?>(address);
            }
        }
    }

    public Task Unregister(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_entries.TryGetValue(address.GrainId, out var existing) && existing.Matches(address))
        {
            _entries.TryRemove(new KeyValuePair<GrainId, GrainAddress>(address.GrainId, existing));
        }

        return Task.CompletedTask;
    }

    public Task<GrainAddress?> Lookup(GrainId grainId) =>
        Task.FromResult(_entries.TryGetValue(grainId, out var address) ? address : null);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        ArgumentNullException.ThrowIfNull(siloAddresses);
        foreach (var entry in _entries.Where(entry => entry.Value.SiloAddress is { } silo && siloAddresses.Contains(silo)))
        {
            _entries.TryRemove(entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>Removes every entry and returns how many there were.</summary>
    public long Clear()
    {
        var count = _entries.Count;
        _entries.Clear();
        return count;
    }
}
