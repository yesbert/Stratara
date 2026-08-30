using Xunit;

namespace Stratara.Testing.Tests;

public class InMemoryMessageBusTests
{
    private sealed record OrderPlaced(Guid Id);

    private sealed record Unrelated(string Value);

    [Fact]
    public async Task Publish_dispatches_to_matching_subscriber()
    {
        var bus = new InMemoryMessageBus();
        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("orders", "worker", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        var placed = new OrderPlaced(Guid.CreateVersion7());
        await bus.PublishAsync("orders", placed);

        var message = Assert.Single(received);
        Assert.Equal(placed, message);
    }

    [Fact]
    public async Task Publish_records_every_message_for_assertions()
    {
        var bus = new InMemoryMessageBus();

        await bus.PublishAsync("orders", new OrderPlaced(Guid.CreateVersion7()));
        await bus.PublishAsync("orders", new OrderPlaced(Guid.CreateVersion7()));

        Assert.Equal(2, bus.Published.Count);
        Assert.All(bus.Published, p => Assert.Equal("orders", p.Topic));
    }

    [Fact]
    public async Task Publish_without_subscriber_is_dropped_but_recorded()
    {
        var bus = new InMemoryMessageBus();

        await bus.PublishAsync("orders", new OrderPlaced(Guid.CreateVersion7()));

        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task Subscriber_does_not_receive_a_different_message_type_on_the_topic()
    {
        var bus = new InMemoryMessageBus();
        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("mixed", "worker", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        await bus.PublishAsync("mixed", new Unrelated("noise"));

        Assert.Empty(received);
        Assert.Single(bus.Published);
    }
}
