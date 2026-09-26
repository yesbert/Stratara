using System.Text.Json.Nodes;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Infrastructure.EventSourcing;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>aggregate-rehydration</c> → <em>An unhandled event is skipped rather than rejected</em>: the
/// selection of the entries a rebuild reads, one rule per test, against a real resolver and upcaster
/// pipeline.
/// </summary>
public class AggregateEventSelectorTests
{
    private readonly TrustedTypeResolver _resolver = new();

    public sealed record Opened(string Name);

    public sealed record Renamed(string Name);

    public sealed record Ignored(string Note);

    public sealed record LegacyOpened(string Title);

    public interface IShared;

    public sealed record SharedFact : IShared;

    public record OpenFact;

    private sealed class Handles
    {
        public void Apply(Opened @event)
        {
        }

        public void Apply(IEvent<Renamed> @event)
        {
        }
    }

    private sealed class HandlesStatically
    {
        public static void Apply(Opened @event)
        {
        }
    }

    private sealed class HandlesAnInterface
    {
        public void Apply(Opened @event)
        {
        }

        public void Apply(IShared @event)
        {
        }
    }

    private sealed class HandlesAnUnsealedRecord
    {
        public void Apply(Opened @event)
        {
        }

        public void Apply(OpenFact @event)
        {
        }
    }

    private sealed class HandlesGenerically
    {
        public void Apply(Opened @event)
        {
        }

        public void Apply<TEvent>(TEvent @event)
        {
        }
    }

    private sealed class LegacyUpcaster : IEventUpcaster
    {
        public string SourceEventTypeName => typeof(LegacyOpened).AssemblyQualifiedName!;

        public string TargetEventTypeName => typeof(Opened).AssemblyQualifiedName!;

        public JsonNode Upcast(JsonNode payload) => new JsonObject { ["Name"] = payload["Title"]?.GetValue<string>() };
    }

    private AggregateEventSelector CreateSelector(params IEventUpcaster[] upcasters) =>
        new(_resolver, new EventUpcasterPipeline(upcasters));

    private void Register(params Type[] types)
    {
        foreach (var type in types)
        {
            _resolver.Register(type);
        }
    }

    private static EventStreamEntry Entry(string eventTypeName, string dataJson = "{}") => new()
    {
        StreamId = Guid.Empty,
        Version = 0,
        EventTypeName = eventTypeName,
        AggregateTypeName = "Aggregate",
        DataJson = dataJson,
        BucketId = 0,
        TenantId = Guid.Empty,
        ActorTenantId = Guid.Empty,
        ActorUserId = Guid.Empty
    };

    private static EventStreamEntry Entry(Type eventType) => Entry(eventType.AssemblyQualifiedName!);

    [Fact]
    public void A_registered_unhandled_entry_is_dropped()
    {
        Register(typeof(Opened), typeof(Renamed), typeof(Ignored));
        var opened = Entry(typeof(Opened));
        var ignored = Entry(typeof(Ignored));

        var selected = CreateSelector().Select(typeof(Handles), [opened, ignored]);

        Assert.Equal([opened], selected);
    }

    [Fact]
    public void An_unregistered_unhandled_entry_is_dropped()
    {
        Register(typeof(Opened), typeof(Renamed));
        var opened = Entry(typeof(Opened));
        var ignored = Entry(typeof(Ignored));
        var retired = Entry("Retired.Namespace.GoneEvent, Retired.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

        var selected = CreateSelector().Select(typeof(Handles), [ignored, opened, retired]);

        Assert.Equal([opened], selected);
    }

    [Fact]
    public void Handled_entries_are_kept_in_order_and_the_list_is_returned_as_is()
    {
        Register(typeof(Opened), typeof(Renamed));
        IReadOnlyList<EventStreamEntry> entries = [Entry(typeof(Opened)), Entry(typeof(Renamed)), Entry(typeof(Opened))];

        var selected = CreateSelector().Select(typeof(Handles), entries);

        Assert.Same(entries, selected);
    }

    [Fact]
    public void A_handler_taking_the_enveloped_event_handles_its_payload()
    {
        Register(typeof(Opened), typeof(Renamed));
        var renamed = Entry(typeof(Renamed));

        var selected = CreateSelector().Select(typeof(Handles), [renamed, Entry(typeof(Ignored))]);

        Assert.Equal([renamed], selected);
    }

    [Fact]
    public void An_entry_upcast_into_a_handled_type_is_kept()
    {
        Register(typeof(Opened), typeof(Renamed));
        var legacy = Entry(typeof(LegacyOpened).AssemblyQualifiedName!, """{"Title":"Ada"}""");

        var selected = CreateSelector(new LegacyUpcaster()).Select(typeof(Handles), [legacy, Entry(typeof(Ignored))]);

        Assert.Equal([legacy], selected);
    }

    [Fact]
    public void An_unresolvable_entry_with_the_full_name_of_a_handled_type_is_kept()
    {
        Register(typeof(Opened), typeof(Renamed));
        var moved = Entry($"{typeof(Opened).FullName}, Some.Former.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

        var selected = CreateSelector().Select(typeof(Handles), [moved, Entry(typeof(Ignored))]);

        Assert.Equal([moved], selected);
    }

    [Fact]
    public void A_static_handler_counts()
    {
        Register(typeof(Opened));
        var opened = Entry(typeof(Opened));

        var selected = CreateSelector().Select(typeof(HandlesStatically), [opened, Entry(typeof(Ignored))]);

        Assert.Equal([opened], selected);
    }

    [Fact]
    public void A_handler_taking_an_interface_makes_every_entry_read()
    {
        Register(typeof(Opened), typeof(IShared), typeof(SharedFact));
        IReadOnlyList<EventStreamEntry> entries = [Entry(typeof(Opened)), Entry(typeof(SharedFact)), Entry(typeof(Ignored))];

        var selected = CreateSelector().Select(typeof(HandlesAnInterface), entries);

        Assert.Same(entries, selected);
    }

    [Fact]
    public void A_handler_taking_an_unsealed_record_makes_every_entry_read()
    {
        Register(typeof(Opened), typeof(OpenFact));
        IReadOnlyList<EventStreamEntry> entries = [Entry(typeof(Opened)), Entry(typeof(Ignored))];

        var selected = CreateSelector().Select(typeof(HandlesAnUnsealedRecord), entries);

        Assert.Same(entries, selected);
    }

    [Fact]
    public void A_generic_handler_makes_every_entry_read()
    {
        Register(typeof(Opened));
        IReadOnlyList<EventStreamEntry> entries = [Entry(typeof(Opened)), Entry(typeof(Ignored))];

        var selected = CreateSelector().Select(typeof(HandlesGenerically), entries);

        Assert.Same(entries, selected);
    }

    [Fact]
    public void A_handled_type_missing_from_the_resolver_makes_every_entry_read()
    {
        Register(typeof(Opened));
        IReadOnlyList<EventStreamEntry> entries = [Entry(typeof(Opened)), Entry(typeof(Ignored))];

        var selected = CreateSelector().Select(typeof(Handles), entries);

        Assert.Same(entries, selected);
    }
}
