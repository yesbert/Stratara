using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Diagnostics;
using Stratara.Shared.EventSourcing.Mapping;

namespace Stratara.Shared.Tests;

/// <summary>
/// <c>projections</c> / <c>sagas</c> → an event no handler in the host takes is not read: the mapper resolves and
/// decrypts only what the reader's relevance accepts, one rule per test.
/// </summary>
public class EventMapperFactoryRelevanceTests
{
    public sealed record Opened(string Name);

    public sealed record Ignored(string Note);

    public sealed record LegacyOpened(string Title);

    private readonly TrustedTypeResolver _resolver = new();
    private readonly Mock<ISecureJsonSerializer> _serializer = new();
    private readonly Mock<ILogger<EventMapperFactory>> _logger = new();

    public EventMapperFactoryRelevanceTests()
    {
        _serializer
            .Setup(s => s.DeserializeAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string json, Type type, Guid? _, Guid? _, CancellationToken _) => JsonSerializer.Deserialize(json, type));
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    private sealed class LegacyUpcaster : IEventUpcaster
    {
        public string SourceEventTypeName => typeof(LegacyOpened).AssemblyQualifiedName!;

        public string TargetEventTypeName => typeof(Opened).AssemblyQualifiedName!;

        public JsonNode Upcast(JsonNode payload) => new JsonObject { ["Name"] = payload["Title"]?.GetValue<string>() };
    }

    private EventMapperFactory CreateMapper(params IEventUpcaster[] upcasters) =>
        new(_serializer.Object, _resolver, new EventUpcasterPipeline(upcasters), _logger.Object);

    private static EventRelevance OpenedOnly => EventRelevance.ForTypes([typeof(Opened)]);

    private static EventStreamEntry Entry(string typeName, string json) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ActorTenantId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        StreamId = Guid.NewGuid(),
        Version = 1,
        EventTypeName = typeName,
        AggregateTypeName = "Agg",
        DataJson = json,
        BucketId = 0
    };

    private static EventMessage Message(string typeName, string json) => new(
        Id: Guid.NewGuid(),
        Version: 1,
        DataJson: json,
        StreamId: Guid.NewGuid(),
        EventTypeName: typeName,
        AggregateTypeName: "Agg",
        ActorTenantId: Guid.NewGuid(),
        ActorUserId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        UserId: null);

    private void VerifyDeserialized(Type type, Times times) =>
        _serializer.Verify(s => s.DeserializeAsync(It.IsAny<string>(), type, It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), times);

    private void VerifySkipLogged(Times times) =>
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.Is<EventId>(e => e.Id == LogEvents.EventStore.UnresolvableEventSkippedForAnyResolvable),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    [Fact]
    public async Task A_relevant_event_is_mapped_and_an_irrelevant_one_is_not_deserialized()
    {
        _resolver.Register(typeof(Opened));
        _resolver.Register(typeof(Ignored));

        var events = await CreateMapper().MapToEventsAsync(
            [Entry(typeof(Opened).AssemblyQualifiedName!, """{"Name":"Ada"}"""), Entry(typeof(Ignored).AssemblyQualifiedName!, """{"Note":"x"}""")],
            OpenedOnly);

        var opened = Assert.Single(events);
        Assert.Equal(new Opened("Ada"), opened.Data);
        VerifyDeserialized(typeof(Ignored), Times.Never());
    }

    [Fact]
    public async Task An_irrelevant_event_of_a_type_never_registered_is_skipped()
    {
        _resolver.Register(typeof(Opened));

        var events = await CreateMapper().MapToEventsAsync(
            [Entry("Retired.Namespace.Archived, Retired.Assembly", "{}"), Entry(typeof(Opened).AssemblyQualifiedName!, """{"Name":"Ada"}""")],
            OpenedOnly);

        Assert.Single(events);
    }

    [Fact]
    public async Task An_unregistered_event_with_a_relevant_types_name_fails_as_unregistered()
    {
        const string moved = "Former.Namespace.Opened, Former.Assembly";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMapper().MapToEventsAsync([Entry(moved, """{"Name":"Ada"}""")], OpenedOnly));

        Assert.Contains(moved, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_event_upcast_into_a_relevant_type_is_mapped()
    {
        _resolver.Register(typeof(Opened));

        var events = await CreateMapper(new LegacyUpcaster()).MapToEventsAsync(
            [Entry(typeof(LegacyOpened).AssemblyQualifiedName!, """{"Title":"Ada"}""")], OpenedOnly);

        Assert.Equal(new Opened("Ada"), Assert.Single(events).Data);
    }

    [Fact]
    public async Task Any_resolvable_maps_every_resolvable_event_and_skips_an_unresolvable_one_with_one_warning()
    {
        _resolver.Register(typeof(Opened));
        _resolver.Register(typeof(Ignored));
        var mapper = CreateMapper();

        var events = await mapper.MapToEventsAsync(
        [
            Entry(typeof(Opened).AssemblyQualifiedName!, """{"Name":"Ada"}"""),
            Entry(typeof(Ignored).AssemblyQualifiedName!, """{"Note":"x"}"""),
            Entry("Retired.Namespace.Archived, Retired.Assembly", "{}"),
            Entry("Retired.Namespace.Archived, Retired.Assembly", "{}")
        ], EventRelevance.AnyResolvable);
        await mapper.MapToEventsAsync([Entry("Retired.Namespace.Archived, Retired.Assembly", "{}")], EventRelevance.AnyResolvable);

        Assert.Equal(2, events.Count);
        VerifySkipLogged(Times.Once());
    }

    [Fact]
    public async Task Any_resolvable_with_declared_types_keeps_an_unresolvable_event_named_like_one_loud()
    {
        const string moved = "Former.Namespace.Opened, Former.Assembly";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMapper().MapToEventsAsync([Entry(moved, "{}")], EventRelevance.AnyResolvableWith([typeof(Opened)])));

        Assert.Contains(moved, failure.Message, StringComparison.Ordinal);
        VerifySkipLogged(Times.Never());
    }

    private sealed class PayloadDependentPipeline : IEventUpcasterPipeline
    {
        public UpcastedEvent Upcast(string eventTypeName, string dataJson) =>
            eventTypeName == "Legacy.Namespace.Fact, Legacy.Assembly"
                ? new UpcastedEvent((dataJson.Contains("opened", StringComparison.Ordinal) ? typeof(Opened) : typeof(Ignored)).AssemblyQualifiedName!,
                    dataJson.Contains("opened", StringComparison.Ordinal) ? """{"Name":"Ada"}""" : """{"Note":"x"}""")
                : new UpcastedEvent(eventTypeName, dataJson);
    }

    [Fact]
    public async Task A_pipeline_of_the_hosts_own_is_asked_for_every_entry()
    {
        _resolver.Register(typeof(Opened));
        _resolver.Register(typeof(Ignored));
        var mapper = new EventMapperFactory(_serializer.Object, _resolver, new PayloadDependentPipeline(), _logger.Object);
        const string legacy = "Legacy.Namespace.Fact, Legacy.Assembly";

        var events = await mapper.MapToEventsAsync(
            [Entry(legacy, """{"kind":"closed"}"""), Entry(legacy, """{"kind":"opened"}""")], OpenedOnly);

        Assert.Equal(new Opened("Ada"), Assert.Single(events).Data);
    }

    [Fact]
    public void Relevant_types_must_not_contain_null()
    {
        Assert.Throws<ArgumentException>(() => EventRelevance.ForTypes([typeof(Opened), null!]));
        Assert.Throws<ArgumentException>(() => EventRelevance.AnyResolvableWith([null!]));
    }

    [Fact]
    public async Task Messages_are_selected_like_entries()
    {
        _resolver.Register(typeof(Opened));

        var events = await CreateMapper().MapToEventsAsync(
            [Message("Retired.Namespace.Archived, Retired.Assembly", "{}"), Message(typeof(Opened).AssemblyQualifiedName!, """{"Name":"Ada"}""")],
            OpenedOnly);

        Assert.Equal(new Opened("Ada"), Assert.Single(events).Data);
    }

    [Fact]
    public async Task A_mapper_without_the_overloads_keeps_mapping_everything_and_filters_afterwards()
    {
        var own = new Mock<IEventMapperFactory> { CallBase = true };
        IEvent opened = new Stratara.Shared.EventSourcing.Event<Opened>(Guid.NewGuid(), 1, new Opened("Ada"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        IEvent ignored = new Stratara.Shared.EventSourcing.Event<Ignored>(Guid.NewGuid(), 1, new Ignored("x"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        own.Setup(m => m.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([opened, ignored]);

        var events = await own.Object.MapToEventsAsync([Entry("any", "{}")], OpenedOnly);

        Assert.Equal([opened], events);
    }
}
