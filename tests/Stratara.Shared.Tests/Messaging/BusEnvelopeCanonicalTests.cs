using Stratara.Abstractions.Messaging;
using Stratara.Contracts.Messages;

namespace Stratara.Shared.Tests.Messaging;

public class BusEnvelopeCanonicalTests
{
    [Fact]
    public void Of_CommandEnvelope_ConcatsTypeAndSessionJson()
    {
        var envelope = new CommandEnvelope(Guid.NewGuid(), "{}", "Stratara.Test.Cmd", "{\"TenantId\":\"00000000-0000-0000-0000-000000000001\"}");

        var canonical = BusEnvelopeCanonical.Of(envelope);

        Assert.Equal("Stratara.Test.Cmd|{\"TenantId\":\"00000000-0000-0000-0000-000000000001\"}", canonical);
    }

    [Fact]
    public void Of_CommandEnvelope_TamperingSessionContextProducesDifferentCanonical()
    {
        var a = new CommandEnvelope(Guid.NewGuid(), "{}", "T", "session-a");
        var b = a with { SessionContextJson = "session-b" };

        Assert.NotEqual(BusEnvelopeCanonical.Of(a), BusEnvelopeCanonical.Of(b));
    }

    [Fact]
    public void Of_EventBundle_ReturnsSessionContextJson()
    {
        var bundle = new EventBundle(new List<EventMessage>(), "session-json");

        Assert.Equal("session-json", BusEnvelopeCanonical.Of(bundle));
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
