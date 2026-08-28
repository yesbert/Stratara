using System.Text.Json;
using Stratara.Abstractions.Messaging;
using Stratara.Contracts.Messages;

namespace Stratara.Shared.Tests.Messaging;

public class BusEnvelopeCanonicalTests
{
    private static EventMessage AnEvent(string dataJson = "{\"a\":1}") =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Version: 7,
            DataJson: dataJson,
            StreamId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            EventTypeName: "Stratara.Test.Evt",
            AggregateTypeName: "Stratara.Test.Agg",
            ActorTenantId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ActorUserId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TenantId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            UserId: null);

    private static CommandEnvelope ACommand(string commandJson = "{\"a\":1}") =>
        new(Guid.Parse("66666666-6666-6666-6666-666666666666"), commandJson, "Stratara.Test.Cmd", "session-json");

    [Fact]
    public void Of_CommandEnvelope_CoversTheCommandPayload()
    {
        var a = ACommand();
        var b = a with { CommandJson = "{\"a\":2}" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_CommandEnvelope_CoversTheEnvelopeId()
    {
        var a = ACommand();
        var b = a with { Id = Guid.Parse("77777777-7777-7777-7777-777777777777") };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_CommandEnvelope_CoversTheHeavyLaneFlag()
    {
        var a = ACommand();
        var b = a with { Heavy = true };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_CommandEnvelope_TamperingSessionContextProducesDifferentCanonical()
    {
        var a = ACommand();
        var b = a with { SessionContextJson = "session-b" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_CommandEnvelope_TamperingTypeNameProducesDifferentCanonical()
    {
        var a = ACommand();
        var b = a with { CommandTypeName = "Stratara.Test.Other" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    /// <summary>
    /// Content must not be shiftable across a field boundary. Joining fields with a separator they
    /// are allowed to contain lets an attacker move the boundary — here, changing which command type
    /// is dispatched while the canonical string, and therefore the signature, stays identical.
    /// </summary>
    [Fact]
    public void Of_CommandEnvelope_ContentCannotBeShiftedAcrossAFieldBoundary()
    {
        var a = ACommand() with { CommandTypeName = "Type", SessionContextJson = "|session" };
        var b = ACommand() with { CommandTypeName = "Type|", SessionContextJson = "session" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_EventBundle_CoversTheEvents()
    {
        var a = new EventBundle([AnEvent()], "session-json");
        var b = new EventBundle([AnEvent("{\"a\":2}")], "session-json");

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_EventBundle_CoversEachEventField()
    {
        var baseline = BusEnvelopeCanonical.Of(new EventBundle([AnEvent()], "session-json"));

        EventMessage[] mutations =
        [
            AnEvent() with { Id = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            AnEvent() with { Version = 8 },
            AnEvent() with { StreamId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            AnEvent() with { EventTypeName = "Other" },
            AnEvent() with { AggregateTypeName = "Other" },
            AnEvent() with { ActorTenantId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            AnEvent() with { ActorUserId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            AnEvent() with { TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            AnEvent() with { UserId = Guid.Empty },
        ];

        foreach (var mutated in mutations)
        {
            Assert.NotEqual(baseline, BusEnvelopeCanonical.Of(new EventBundle([mutated], "session-json")));
        }
    }

    [Fact]
    public void Of_EventBundle_TamperingSessionContextProducesDifferentCanonical()
    {
        var a = new EventBundle([AnEvent()], "session-a");
        var b = a with { SessionContextJson = "session-b" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    /// <summary>
    /// The canonical form is built from field values, never by re-serializing the deserialized
    /// record — so a message that has been on the wire projects to exactly what its publisher signed,
    /// whatever the serializer did with property order or escaping.
    /// </summary>
    [Fact]
    public void Of_SurvivesAJsonRoundTrip()
    {
        var bundle = new EventBundle([AnEvent()], "session-json");
        var command = ACommand();

        var bundleRoundTripped = JsonSerializer.Deserialize<EventBundle>(JsonSerializer.Serialize(bundle))!;
        var commandRoundTripped = JsonSerializer.Deserialize<CommandEnvelope>(JsonSerializer.Serialize(command))!;

        Assert.Equal(BusEnvelopeCanonical.Of(bundle), BusEnvelopeCanonical.Of(bundleRoundTripped));
        Assert.Equal(BusEnvelopeCanonical.Of(command), BusEnvelopeCanonical.Of(commandRoundTripped));
    }

    [Fact]
    public void Of_SignatureItselfIsNotCovered()
    {
        var command = ACommand();
        var bundle = new EventBundle([AnEvent()], "session-json");

        Assert.Equal(
            BusEnvelopeCanonical.Of(command),
            BusEnvelopeCanonical.Of(command with { Signature = "anything" }));
        Assert.Equal(
            BusEnvelopeCanonical.Of(bundle),
            BusEnvelopeCanonical.Of(bundle with { Signature = "anything" }));
    }

    /// <summary>
    /// A field added to one of these records and not added to the canonical form is silently
    /// unsigned, and nothing else would catch it. This test fails at the point of the edit.
    /// </summary>
    [Theory]
    [InlineData(typeof(CommandEnvelope), "Id,CommandJson,CommandTypeName,SessionContextJson,Signature,Heavy")]
    [InlineData(typeof(EventBundle), "Events,SessionContextJson,Signature")]
    [InlineData(typeof(EventMessage), "Id,Version,DataJson,StreamId,EventTypeName,AggregateTypeName,ActorTenantId,ActorUserId,TenantId,UserId")]
    public void TheSignedRecordsHaveTheFieldsTheCanonicalFormAccountsFor(Type recordType, string expected)
    {
        var actual = string.Join(
            ',',
            recordType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First()
                .GetParameters().Select(p => p.Name));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Of_CommandEnvelope_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BusEnvelopeCanonical.Of((CommandEnvelope)null!));
    }

    [Fact]
    public void Of_EventBundle_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BusEnvelopeCanonical.Of((EventBundle)null!));
    }
}
