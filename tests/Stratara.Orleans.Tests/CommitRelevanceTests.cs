using System.Text.Json.Nodes;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Which readers a commit wakes, decided from its event type names as the reader decides relevance when it maps the
/// entries: a known type the consumer applies wakes it, a known type it does not apply leaves it asleep, and a name the
/// silo cannot tell without the payload wakes everyone.
/// </summary>
public sealed class CommitRelevanceTests
{
    private static readonly EventRelevance Created = EventRelevance.ForTypes([typeof(ProbeCreated)]);

    [Fact]
    public void A_commit_of_an_event_the_consumer_applies_concerns_it()
    {
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([]));

        Assert.True(relevance.Concerns(Created, [NameOf<ProbeIncremented>(), NameOf<ProbeCreated>()]));
    }

    [Fact]
    public void A_commit_of_events_the_consumer_does_not_apply_does_not_concern_it()
    {
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([]));

        Assert.False(relevance.Concerns(Created, [NameOf<ProbeIncremented>()]));
    }

    [Fact]
    public void A_consumer_that_takes_any_resolvable_event_or_whose_events_are_unknown_is_concerned_by_every_commit()
    {
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([]));

        Assert.True(relevance.Concerns(EventRelevance.AnyResolvableWith([typeof(ProbeCreated)]), [NameOf<ProbeIncremented>()]));
        Assert.True(relevance.Concerns(null, [NameOf<ProbeIncremented>()]));
    }

    [Fact]
    public void A_name_the_silo_does_not_resolve_concerns_every_consumer()
    {
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([]));

        Assert.True(relevance.Concerns(Created, ["Somewhere.Else.Unknown, Somewhere.Else"]));
    }

    [Fact]
    public void A_name_an_upcaster_renames_is_decided_by_the_type_it_is_upcast_to()
    {
        var upcaster = new RenamingUpcaster(NameOf<ProbeIncremented>(), NameOf<ProbeCreated>());
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([upcaster]));

        Assert.True(relevance.Concerns(Created, [NameOf<ProbeIncremented>()]));
        Assert.False(relevance.Concerns(EventRelevance.ForTypes([typeof(ProbeIncremented)]), [NameOf<ProbeIncremented>()]));
    }

    [Fact]
    public void A_name_whose_upcaster_refuses_an_empty_payload_concerns_every_consumer()
    {
        var upcaster = new RefusingUpcaster(NameOf<ProbeIncremented>());
        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([upcaster]));

        Assert.True(relevance.Concerns(EventRelevance.ForTypes([typeof(ProbeNoted)]), [NameOf<ProbeIncremented>()]));
    }

    [Fact]
    public void A_pipeline_of_the_hosts_own_or_no_resolver_wakes_every_consumer()
    {
        Assert.True(new CommitRelevance(Resolver(), new PayloadPipeline()).Concerns(Created, [NameOf<ProbeIncremented>()]));
        Assert.True(new CommitRelevance().Concerns(Created, [NameOf<ProbeIncremented>()]));
    }

    private static string NameOf<T>() => typeof(T).AssemblyQualifiedName ?? throw new InvalidOperationException($"{typeof(T)} has no assembly-qualified name.");

    private static TrustedTypeResolver Resolver()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(ProbeCreated));
        resolver.Register(typeof(ProbeIncremented));
        resolver.Register(typeof(ProbeNoted));
        return resolver;
    }

    private sealed record ProbeCreated(Guid Id);

    private sealed record ProbeIncremented(Guid Id);

    private sealed record ProbeNoted(Guid Id);

    private sealed class RefusingUpcaster(string source) : IEventUpcaster
    {
        public string SourceEventTypeName => source;

        public string TargetEventTypeName => source + "V2";

        public JsonNode Upcast(JsonNode payload) => throw new InvalidOperationException("the payload lacks the field the upcaster moves");
    }

    private sealed class RenamingUpcaster(string source, string target) : IEventUpcaster
    {
        public string SourceEventTypeName => source;

        public string TargetEventTypeName => target;

        public JsonNode Upcast(JsonNode payload) => payload;
    }

    private sealed class PayloadPipeline : IEventUpcasterPipeline
    {
        public UpcastedEvent Upcast(string eventTypeName, string dataJson) => new(eventTypeName, dataJson);
    }
}
