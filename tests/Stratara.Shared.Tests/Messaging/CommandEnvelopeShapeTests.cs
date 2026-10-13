using System.Text.Json;
using Stratara.Contracts.Messages;

namespace Stratara.Shared.Tests.Messaging;

/// <summary>
/// <c>outbox-and-messaging</c> → a command that names no owner is stored and published in the form it
/// had before a command could name one. The envelope is what the outbox row, the intent row and the bus
/// hold, so its serialized form is that guarantee.
/// </summary>
public class CommandEnvelopeShapeTests
{
    private static readonly Guid Id = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Tenant = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid User = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private const string FormBeforeTheOwner =
        "{\"Id\":\"66666666-6666-6666-6666-666666666666\",\"CommandJson\":\"{}\",\"CommandTypeName\":\"Stratara.Test.Cmd\"," +
        "\"SessionContextJson\":\"session-json\",\"Signature\":null,\"Heavy\":false}";

    private static CommandEnvelope AnEnvelope() => new(Id, "{}", "Stratara.Test.Cmd", "session-json");

    [Fact]
    public void Serialize_EnvelopeNamingNoOwner_IsByteIdenticalToTheFormBeforeTheOwner()
    {
        Assert.Equal(FormBeforeTheOwner, JsonSerializer.Serialize(AnEnvelope()));
    }

    [Fact]
    public void Deserialize_EnvelopeWrittenBeforeTheOwner_NamesNone()
    {
        var envelope = JsonSerializer.Deserialize<CommandEnvelope>(FormBeforeTheOwner)!;

        Assert.Null(envelope.DataOwnerTenantId);
        Assert.Null(envelope.DataOwnerUserId);
        Assert.Equal(AnEnvelope(), envelope);
    }

    [Fact]
    public void RoundTrip_EnvelopeNamingAnOwner_KeepsTenantAndUser()
    {
        var envelope = AnEnvelope() with { DataOwnerTenantId = Tenant, DataOwnerUserId = User };

        var roundTripped = JsonSerializer.Deserialize<CommandEnvelope>(JsonSerializer.Serialize(envelope))!;

        Assert.Equal(Tenant, roundTripped.DataOwnerTenantId);
        Assert.Equal(User, roundTripped.DataOwnerUserId);
        Assert.Equal(envelope, roundTripped);
    }

    [Fact]
    public void Serialize_EnvelopeNamingATenantAlone_WritesNoUser()
    {
        var json = JsonSerializer.Serialize(AnEnvelope() with { DataOwnerTenantId = Tenant });

        Assert.Contains("\"DataOwnerTenantId\":\"77777777-7777-7777-7777-777777777777\"", json);
        Assert.DoesNotContain("DataOwnerUserId", json);
    }

    /// <summary>
    /// The owner is carried by properties, not by constructor parameters: a consumer that constructs or
    /// deconstructs an envelope compiled against the six-parameter form keeps working.
    /// </summary>
    [Fact]
    public void Constructor_AndDeconstruct_KeepTheirSixParameters()
    {
        var constructor = Assert.Single(typeof(CommandEnvelope).GetConstructors(), c => c.GetParameters().Length > 1);
        var deconstruct = typeof(CommandEnvelope).GetMethod("Deconstruct")!;

        Assert.Equal(
            ["Id", "CommandJson", "CommandTypeName", "SessionContextJson", "Signature", "Heavy"],
            constructor.GetParameters().Select(p => p.Name));
        Assert.Equal(6, deconstruct.GetParameters().Length);
    }
}
