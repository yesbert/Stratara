using System.Text.Json.Nodes;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Tests;

public class EventUpcasterPipelineTests
{
    private sealed class DelegateUpcaster(string source, string target, Func<JsonNode, JsonNode> transform) : IEventUpcaster
    {
        public string SourceEventTypeName => source;
        public string TargetEventTypeName => target;
        public JsonNode Upcast(JsonNode payload) => transform(payload);
    }

    private static DelegateUpcaster Bump(string source, string target) =>
        new(source, target, node =>
        {
            var obj = node.AsObject();
            obj["v"] = (int)(obj["v"]?.GetValue<int>() ?? 0) + 1;
            return obj;
        });

    [Fact]
    public void Upcast_With_No_Upcasters_Returns_Input_Unchanged()
    {
        var pipeline = new EventUpcasterPipeline([]);

        var result = pipeline.Upcast("Some.Type, Asm", "{\"v\":1}");

        Assert.Equal("Some.Type, Asm", result.EventTypeName);
        Assert.Equal("{\"v\":1}", result.DataJson);
    }

    [Fact]
    public void Upcast_With_No_Matching_Source_Returns_Input_Unchanged()
    {
        var pipeline = new EventUpcasterPipeline([Bump("Other.Type, Asm", "Other.Type.V2, Asm")]);

        var result = pipeline.Upcast("Some.Type, Asm", "{\"v\":1}");

        Assert.Equal("Some.Type, Asm", result.EventTypeName);
        Assert.Equal("{\"v\":1}", result.DataJson);
    }

    [Fact]
    public void Upcast_Applies_Chain_To_Fixpoint()
    {
        var pipeline = new EventUpcasterPipeline(
        [
            Bump("T.V1, Asm", "T.V2, Asm"),
            Bump("T.V2, Asm", "T.V3, Asm")
        ]);

        var result = pipeline.Upcast("T.V1, Asm", "{\"v\":0}");

        Assert.Equal("T.V3, Asm", result.EventTypeName);
        Assert.Equal(2, JsonNode.Parse(result.DataJson)!["v"]!.GetValue<int>());
    }

    [Fact]
    public void Upcast_Matches_Source_Ignoring_Assembly_Version()
    {
        var pipeline = new EventUpcasterPipeline([Bump("T.V1, Asm", "T.V2, Asm")]);

        var result = pipeline.Upcast("T.V1, Asm, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null", "{\"v\":0}");

        Assert.Equal("T.V2, Asm", result.EventTypeName);
        Assert.Equal(1, JsonNode.Parse(result.DataJson)!["v"]!.GetValue<int>());
    }

    [Fact]
    public void Constructor_Throws_On_Duplicate_Source()
    {
        Assert.Throws<InvalidOperationException>(() => new EventUpcasterPipeline(
        [
            Bump("T.V1, Asm", "T.V2, Asm"),
            Bump("T.V1, Asm", "T.Other, Asm")
        ]));
    }

    [Fact]
    public void Upcast_Throws_On_Cyclic_Chain()
    {
        var pipeline = new EventUpcasterPipeline(
        [
            Bump("T.A, Asm", "T.B, Asm"),
            Bump("T.B, Asm", "T.A, Asm")
        ]);

        Assert.Throws<InvalidOperationException>(() => pipeline.Upcast("T.A, Asm", "{\"v\":0}"));
    }
}
