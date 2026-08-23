using System.Security.Cryptography;
using System.Text;
using Stratara.Abstractions.EventSourcing;
using Stratara.Infrastructure.EventSourcing;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// Pins the chain-hash input. Without this, the field order, the separator or the encoding could
/// change and the whole suite would still pass — while every event chained under the old format
/// became unverifiable. Tamper evidence would be gone and nothing would say so.
/// </summary>
public class EventStreamHashInputTests
{
    private static readonly byte[] PreviousHash =
        [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

    private const long SequenceNumber = 42;
    private const long Version = 7;
    private const string EventTypeName = "Stratara.Domain.TenantCreated";
    private const string DataJson = """{"Name":"Acme","Umlaut":"Grüße"}""";

    private static readonly DateTimeOffset Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);

    private static EventStreamEntry Entry() => new()
    {
        StreamId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Version = Version,
        EventTypeName = EventTypeName,
        AggregateTypeName = "Stratara.Domain.Tenant",
        DataJson = DataJson,
        Timestamp = Timestamp,
        PreviousHash = PreviousHash,
        SequenceNumber = SequenceNumber,
        BucketId = 3,
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ActorTenantId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        ActorUserId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    };

    [Fact]
    public void TheHashInput_IsPipeSeparatedInADefinedOrder()
    {
        var expectedInput =
            $"{Convert.ToHexString(PreviousHash)}|{SequenceNumber}|{Version}|" +
            $"{Timestamp.ToUnixTimeMilliseconds()}|{EventTypeName}|{DataJson}";
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput));

        Assert.Equal(expected, EventStreamHashService.ComputeHash(Entry()));
    }

    [Fact]
    public void TheHash_MatchesItsRecordedValue()
    {
        Assert.Equal(
            "ADF43C90F42BC9DE7734FE77D8C1E2AB980CDD6ACB22FD11B13D0BC0585EA78E",
            Convert.ToHexString(EventStreamHashService.ComputeHash(Entry())));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("version")]
    [InlineData("timestamp")]
    [InlineData("type")]
    [InlineData("payload")]
    [InlineData("previous")]
    public void EveryFieldParticipates(string field)
    {
        var baseline = EventStreamHashService.ComputeHash(Entry());

        var mutated = Entry();
        switch (field)
        {
            case "sequence": mutated.SequenceNumber = SequenceNumber + 1; break;
            case "version": mutated.Version = Version + 1; break;
            case "timestamp": mutated.Timestamp = Timestamp.AddMilliseconds(1); break;
            case "type": mutated.EventTypeName = EventTypeName + "V2"; break;
            case "payload": mutated.DataJson = DataJson.Replace("Acme", "Other", StringComparison.Ordinal); break;
            case "previous": mutated.PreviousHash = [.. PreviousHash.Reverse()]; break;
        }

        Assert.NotEqual(baseline, EventStreamHashService.ComputeHash(mutated));
    }

    [Fact]
    public void FieldsAreSeparated_SoTheirBoundariesCannotBeShifted()
    {
        var left = Entry();
        left.EventTypeName = "AB";
        left.DataJson = "CD";

        var right = Entry();
        right.EventTypeName = "A";
        right.DataJson = "BCD";

        Assert.NotEqual(EventStreamHashService.ComputeHash(left), EventStreamHashService.ComputeHash(right));
    }

    [Fact]
    public void ThePayloadIsHashedAsUtf8()
    {
        var entry = Entry();
        entry.DataJson = "Grüße";

        var expectedInput =
            $"{Convert.ToHexString(PreviousHash)}|{SequenceNumber}|{Version}|" +
            $"{Timestamp.ToUnixTimeMilliseconds()}|{EventTypeName}|Grüße";

        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput)),
            EventStreamHashService.ComputeHash(entry));
    }
}
