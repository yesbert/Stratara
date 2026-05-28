using System.Text.Json;
using Stratara.Abstractions.Messaging;

namespace Stratara.Shared.Tests.Messaging;

public class BusEnvelopeJsonGuardTests
{
    [Fact]
    public void EnsureWithinSizeLimit_BelowLimit_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            BusEnvelopeJsonGuard.EnsureWithinSizeLimit(byteLength: 512, maxBytes: 1024, source: "test-topic"));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureWithinSizeLimit_AtLimit_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            BusEnvelopeJsonGuard.EnsureWithinSizeLimit(byteLength: 1024, maxBytes: 1024, source: "test-topic"));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureWithinSizeLimit_AboveLimit_ThrowsJsonException()
    {
        var ex = Assert.Throws<JsonException>(() =>
            BusEnvelopeJsonGuard.EnsureWithinSizeLimit(byteLength: 1025, maxBytes: 1024, source: "test-topic"));

        Assert.Contains("test-topic", ex.Message);
        Assert.Contains("1025", ex.Message);
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public void CreateOptions_AppliesMaxDepth()
    {
        var options = BusEnvelopeJsonGuard.CreateOptions(maxDepth: 16);

        Assert.Equal(16, options.MaxDepth);
    }

    [Fact]
    public void CreateOptions_KeepsSystemTextJsonDefaultsBeyondMaxDepth()
    {
        var options = BusEnvelopeJsonGuard.CreateOptions(maxDepth: 32);

        Assert.Null(options.PropertyNamingPolicy);
        Assert.False(options.PropertyNameCaseInsensitive);
    }
}

public class BusEnvelopeJsonOptionsTests
{
    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var options = new BusEnvelopeJsonOptions();

        Assert.Equal(32, options.MaxDepth);
        Assert.Equal(1_048_576, options.MaxBodyBytes);
        Assert.Equal("BusEnvelopeJson", BusEnvelopeJsonOptions.SectionName);
    }
}
