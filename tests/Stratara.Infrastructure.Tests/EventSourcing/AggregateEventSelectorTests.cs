using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Diagnostics;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Shared.EventSourcing.Mapping;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>aggregate-rehydration</c> → <em>An unhandled event is skipped rather than rejected</em>: the
/// selection of the entries a rebuild reads, one rule per test, against a real resolver, upcaster
/// pipeline and mapper.
/// </summary>
public class AggregateEventSelectorTests
{
    private readonly TrustedTypeResolver _resolver = new();
    private readonly Mock<ILogger<AggregateEventSelector>> _logger = new();

    public AggregateEventSelectorTests()
    {
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    public sealed record Opened(string Name);

    public sealed record Renamed(string Name);

    public sealed record Ignored(string Note);

    public sealed record LegacyOpened(string Title);

    public interface IShared;

    public sealed record SharedFact : IShared;

    public record BaseFact;

    public sealed record DerivedFact : BaseFact;

    public static class Nested
    {
        public sealed record Opened(string Name);
    }

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
        public void Apply(BaseFact @event)
        {
        }
    }

    private sealed class HandlesGenerically
    {
        public void Apply<TEvent>(TEvent @event)
        {
        }
    }

    private sealed class LegacyUpcaster : IEventUpcaster
    {
        public int Calls { get; private set; }

        public string SourceEventTypeName => typeof(LegacyOpened).AssemblyQualifiedName!;

        public string TargetEventTypeName => typeof(Opened).AssemblyQualifiedName!;

        public JsonNode Upcast(JsonNode payload)
        {
            Calls++;
            return new JsonObject { ["Name"] = payload["Title"]?.GetValue<string>() };
        }
    }

    /// <summary>A selector that reads every entry, as it does beside a replaced mapper.</summary>
    internal static AggregateEventSelector PassThrough() =>
        new(new TrustedTypeResolver(), new EventUpcasterPipeline([]), Mock.Of<IEventMapperFactory>(),
            NullLogger<AggregateEventSelector>.Instance);

    /// <summary>A selector beside the framework's mapper, over <paramref name="resolver"/>.</summary>
    internal static AggregateEventSelector Selecting(ITrustedTypeResolver resolver, params IEventUpcaster[] upcasters)
    {
        var pipeline = new EventUpcasterPipeline(upcasters);
        return new AggregateEventSelector(resolver, pipeline,
            new EventMapperFactory(Mock.Of<ISecureJsonSerializer>(), resolver, pipeline),
            NullLogger<AggregateEventSelector>.Instance);
    }

    private AggregateEventSelector CreateSelector(params IEventUpcaster[] upcasters)
    {
        var pipeline = new EventUpcasterPipeline(upcasters);
        return new AggregateEventSelector(_resolver, pipeline,
            new EventMapperFactory(Mock.Of<ISecureJsonSerializer>(), _resolver, pipeline), _logger.Object);
    }

    private void Register(params Type[] types)
    {
        foreach (var type in types)
        {
            _resolver.Register(type);
        }
    }

    private void VerifySkipLogged(Times times) =>
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.Is<EventId>(e => e.Id == LogEvents.EventStore.UnresolvableEventSkipped),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

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
    public void A_registered_unhandled_entry_is_dropped_without_a_warning()
    {
        Register(typeof(Opened), typeof(Renamed), typeof(Ignored));
        var opened = Entry(typeof(Opened));

        var selected = CreateSelector().Select(typeof(Handles), [opened, Entry(typeof(Ignored))]);

        Assert.Equal([opened], selected);
        VerifySkipLogged(Times.Never());
    }

    [Fact]
    public void An_unregistered_unhandled_entry_is_dropped_and_logged_once()
    {
        Register(typeof(Opened), typeof(Renamed));
        var opened = Entry(typeof(Opened));
        var selector = CreateSelector();

        var selected = selector.Select(typeof(Handles), [Entry(typeof(Ignored)), opened, Entry(typeof(Ignored))]);
        selector.Select(typeof(Handles), [Entry(typeof(Ignored))]);

        Assert.Equal([opened], selected);
        VerifySkipLogged(Times.Once());
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
        Register(typeof(Opened), typeof(Renamed), typeof(Ignored));
        var renamed = Entry(typeof(Renamed));

        var selected = CreateSelector().Select(typeof(Handles), [renamed, Entry(typeof(Ignored))]);

        Assert.Equal([renamed], selected);
    }

    [Fact]
    public void An_entry_upcast_into_a_handled_type_is_kept()
    {
        Register(typeof(Opened), typeof(Renamed), typeof(Ignored));
        var legacy = Entry(typeof(LegacyOpened).AssemblyQualifiedName!, """{"Title":"Ada"}""");

        var selected = CreateSelector(new LegacyUpcaster()).Select(typeof(Handles), [legacy, Entry(typeof(Ignored))]);

        Assert.Equal([legacy], selected);
    }

    [Fact]
    public void A_recorded_name_is_decided_once()
    {
        Register(typeof(Opened), typeof(Renamed));
        var upcaster = new LegacyUpcaster();
        var selector = CreateSelector(upcaster);
        var legacyName = typeof(LegacyOpened).AssemblyQualifiedName!;

        selector.Select(typeof(Handles), [Entry(legacyName, """{"Title":"Ada"}"""), Entry(legacyName, """{"Title":"Grace"}""")]);
        var selected = selector.Select(typeof(Handles), [Entry(legacyName, """{"Title":"Linus"}""")]);

        Assert.Single(selected);
        Assert.Equal(1, upcaster.Calls);
    }

    [Fact]
    public void An_unresolvable_entry_with_the_name_of_a_handled_type_in_another_namespace_is_kept()
    {
        Register(typeof(Opened), typeof(Renamed));
        var moved = Entry("Some.Former.Namespace.Opened, Some.Former.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

        var selected = CreateSelector().Select(typeof(Handles), [moved]);

        Assert.Equal([moved], selected);
        VerifySkipLogged(Times.Never());
    }

    [Fact]
    public void An_unresolvable_entry_with_the_name_of_a_handled_type_nested_elsewhere_is_kept()
    {
        Register(typeof(Opened), typeof(Renamed));
        var nested = Entry(typeof(Nested.Opened).AssemblyQualifiedName!);

        var selected = CreateSelector().Select(typeof(Handles), [nested]);

        Assert.Equal([nested], selected);
    }

    [Fact]
    public void A_handled_type_missing_from_the_resolver_is_kept_so_it_fails_in_the_mapper()
    {
        Register(typeof(Opened));
        var renamed = Entry(typeof(Renamed));

        var selected = CreateSelector().Select(typeof(Handles), [renamed]);

        Assert.Equal([renamed], selected);
    }

    [Fact]
    public void A_static_handler_counts()
    {
        Register(typeof(Opened), typeof(Ignored));
        var opened = Entry(typeof(Opened));

        var selected = CreateSelector().Select(typeof(HandlesStatically), [opened, Entry(typeof(Ignored))]);

        Assert.Equal([opened], selected);
    }

    [Fact]
    public void A_handler_taking_an_interface_takes_its_implementations_and_keeps_every_unresolvable_entry()
    {
        Register(typeof(Opened), typeof(IShared), typeof(SharedFact), typeof(Ignored));
        var shared = Entry(typeof(SharedFact));
        var unknown = Entry("Unknown.Namespace.Unknown, Unknown.Assembly");

        var selected = CreateSelector().Select(typeof(HandlesAnInterface), [shared, Entry(typeof(Ignored)), unknown]);

        Assert.Equal([shared, unknown], selected);
    }

    [Fact]
    public void A_handler_taking_an_unsealed_record_takes_its_registered_subtypes_only()
    {
        Register(typeof(BaseFact), typeof(DerivedFact), typeof(Ignored));
        var derived = Entry(typeof(DerivedFact));

        var selected = CreateSelector().Select(typeof(HandlesAnUnsealedRecord),
            [derived, Entry(typeof(Ignored)), Entry("Unknown.Namespace.Unknown, Unknown.Assembly")]);

        Assert.Equal([derived], selected);
    }

    [Fact]
    public void A_generic_handler_keeps_every_unresolvable_entry()
    {
        var unknown = Entry("Unknown.Namespace.Unknown, Unknown.Assembly");

        var selected = CreateSelector().Select(typeof(HandlesGenerically), [unknown]);

        Assert.Equal([unknown], selected);
    }

    [Fact]
    public void Beside_a_replaced_mapper_every_entry_is_read()
    {
        IReadOnlyList<EventStreamEntry> entries = [Entry("Unknown.Namespace.Unknown, Unknown.Assembly"), Entry(typeof(Ignored))];

        var selected = PassThrough().Select(typeof(Handles), entries);

        Assert.Same(entries, selected);
    }
}
