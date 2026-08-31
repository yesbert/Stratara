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
    public async Task Established_subscription_receives_what_was_published_before_its_handler_attached()
    {
        var bus = new InMemoryMessageBus();
        await bus.EnsureSubscriptionAsync("orders", "projections");

        var first = new OrderPlaced(Guid.CreateVersion7());
        var second = new OrderPlaced(Guid.CreateVersion7());
        await bus.PublishAsync("orders", first);
        await bus.PublishAsync("orders", second);

        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("orders", "projections", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        Assert.Equal([first, second], received);
    }

    [Fact]
    public async Task Establishing_a_subscription_twice_keeps_what_is_already_held()
    {
        var bus = new InMemoryMessageBus();
        await bus.EnsureSubscriptionAsync("orders", "projections");

        var placed = new OrderPlaced(Guid.CreateVersion7());
        await bus.PublishAsync("orders", placed);
        await bus.EnsureSubscriptionAsync("orders", "projections");

        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("orders", "projections", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        Assert.Equal(placed, Assert.Single(received));
    }

    [Fact]
    public async Task A_subscription_that_was_never_established_still_misses_what_came_before_it()
    {
        var bus = new InMemoryMessageBus();
        await bus.PublishAsync("orders", new OrderPlaced(Guid.CreateVersion7()));

        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("orders", "late", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        Assert.Empty(received);
    }

    [Fact]
    public async Task An_established_subscription_receives_nothing_twice()
    {
        var bus = new InMemoryMessageBus();
        await bus.EnsureSubscriptionAsync("orders", "projections");

        var held = new OrderPlaced(Guid.CreateVersion7());
        await bus.PublishAsync("orders", held);

        var received = new List<OrderPlaced>();
        await bus.SubscribeAsync<OrderPlaced>("orders", "projections", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        var afterAttach = new OrderPlaced(Guid.CreateVersion7());
        await bus.PublishAsync("orders", afterAttach);

        Assert.Equal([held, afterAttach], received);
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
