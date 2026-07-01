using System.Text.Json.Nodes;
using Stratara.Abstractions.Reflections;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Default <see cref="IEventUpcasterPipeline"/>. Indexes the registered <see cref="IEventUpcaster"/>
/// instances by their version-independent <see cref="IEventUpcaster.SourceEventTypeName"/> and applies
/// them in a chain on read. With no upcasters registered it is a transparent pass-through and never
/// parses the payload.
/// </summary>
/// <remarks>
/// Registered as a singleton by the event-mapping DI wiring; the set of upcasters is captured once at
/// construction. Two upcasters claiming the same source type name is a configuration error and throws.
/// </remarks>
public sealed class EventUpcasterPipeline : IEventUpcasterPipeline
{
    private readonly Dictionary<string, IEventUpcaster> _bySource;

    /// <summary>
    /// Initializes the pipeline from the registered upcasters.
    /// </summary>
    /// <param name="upcasters">Every <see cref="IEventUpcaster"/> registered in the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="upcasters"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Two upcasters declare the same source event type name.</exception>
    public EventUpcasterPipeline(IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        _bySource = new Dictionary<string, IEventUpcaster>(StringComparer.Ordinal);
        foreach (var upcaster in upcasters)
        {
            var key = TypeNameNormalization.ToVersionIndependent(upcaster.SourceEventTypeName);
            if (!_bySource.TryAdd(key, upcaster))
            {
                throw new InvalidOperationException(
                    $"Two event upcasters declare the same source event type name '{upcaster.SourceEventTypeName}'. " +
                    "Each schema hop must have exactly one upcaster.");
            }
        }
    }

    /// <inheritdoc/>
    public UpcastedEvent Upcast(string eventTypeName, string dataJson)
    {
        ArgumentNullException.ThrowIfNull(eventTypeName);
        ArgumentNullException.ThrowIfNull(dataJson);

        if (_bySource.Count == 0)
        {
            return new UpcastedEvent(eventTypeName, dataJson);
        }

        var currentName = eventTypeName;
        var key = TypeNameNormalization.ToVersionIndependent(currentName);
        if (!_bySource.ContainsKey(key))
        {
            return new UpcastedEvent(eventTypeName, dataJson);
        }

        var node = JsonNode.Parse(dataJson);
        if (node is null)
        {
            return new UpcastedEvent(eventTypeName, dataJson);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (_bySource.TryGetValue(key, out var upcaster))
        {
            if (!visited.Add(key))
            {
                throw new InvalidOperationException(
                    $"Cyclic event-upcaster chain detected at source event type name '{currentName}'.");
            }

            node = upcaster.Upcast(node)
                ?? throw new InvalidOperationException(
                    $"Event upcaster for '{upcaster.SourceEventTypeName}' returned a null payload.");
            currentName = upcaster.TargetEventTypeName;
            key = TypeNameNormalization.ToVersionIndependent(currentName);
        }

        return new UpcastedEvent(currentName, node.ToJsonString());
    }
}
