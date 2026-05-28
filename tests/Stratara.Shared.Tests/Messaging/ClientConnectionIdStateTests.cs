using Stratara.Shared.Messaging;

namespace Stratara.Shared.Tests.Messaging;

public class ClientConnectionIdStateTests
{
    [Fact]
    public void ConnectionId_DefaultValue_StartsWithServer()
    {
        var state = new ClientConnectionIdState();

        Assert.StartsWith("server-", state.ConnectionId);
    }

    [Fact]
    public void ConnectionId_DefaultValue_ContainsGuid()
    {
        var state = new ClientConnectionIdState();
        var guidPart = state.ConnectionId["server-".Length..];

        Assert.True(Guid.TryParse(guidPart, out _));
    }

    [Fact]
    public void SetConnectionId_WithValue_SetsToProvidedValue()
    {
        var state = new ClientConnectionIdState();
        var customId = "custom-connection-id";

        state.SetConnectionId(customId);

        Assert.Equal(customId, state.ConnectionId);
    }

    [Fact]
    public void SetConnectionId_WithNull_GeneratesNewServerPrefixedId()
    {
        var state = new ClientConnectionIdState();
        var originalId = state.ConnectionId;

        state.SetConnectionId(null);

        Assert.NotEqual(originalId, state.ConnectionId);
        Assert.StartsWith("server-", state.ConnectionId);
    }

    [Fact]
    public void ConnectionId_MultipleInstances_GenerateUniqueIds()
    {
        var state1 = new ClientConnectionIdState();
        var state2 = new ClientConnectionIdState();

        Assert.NotEqual(state1.ConnectionId, state2.ConnectionId);
    }
}
