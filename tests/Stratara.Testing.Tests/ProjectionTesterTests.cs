using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Testing.Tests;

public class ProjectionTesterTests
{
    private sealed record ItemAdded(string Name);

    private sealed record ItemRemoved(string Name);

    private sealed record NeverHandled(string Value);

    private sealed class CounterProjection
    {
        public int WrappedHits { get; private set; }

        public long LastVersion { get; private set; }

        public int BareHits { get; private set; }

        public string? LastBareName { get; private set; }

        private Task HandleAsync(IEvent<ItemAdded> @event, CancellationToken cancellationToken)
        {
            WrappedHits++;
            LastVersion = @event.Version;
            return Task.CompletedTask;
        }

        private Task HandleAsync(ItemRemoved @event, CancellationToken cancellationToken)
        {
            BareHits++;
            LastBareName = @event.Name;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TestEvent_Create_populates_metadata()
    {
        var streamId = Guid.CreateVersion7();

        var @event = TestEvent.Create(new ItemAdded("a"), streamId, version: 7);

        Assert.Equal(streamId, @event.StreamId);
        Assert.Equal(7, @event.Version);
        Assert.Equal("a", @event.Data.Name);
        Assert.NotEqual(Guid.Empty, @event.Id);
    }

    [Fact]
    public async Task Invokes_the_wrapped_ievent_handler()
    {
        var projection = new CounterProjection();

        await ProjectionTester.HandleAsync(projection, TestEvent.Create(new ItemAdded("x"), version: 3));

        Assert.Equal(1, projection.WrappedHits);
        Assert.Equal(3, projection.LastVersion);
    }

    [Fact]
    public async Task Invokes_the_bare_payload_handler()
    {
        var projection = new CounterProjection();

        await ProjectionTester.HandleAsync(projection, TestEvent.Create(new ItemRemoved("gone")));

        Assert.Equal(1, projection.BareHits);
        Assert.Equal("gone", projection.LastBareName);
    }

    private sealed class BothOverloadsProjection
    {
        public List<string> Handled { get; } = [];

        private Task HandleAsync(IEvent<ItemAdded> @event, CancellationToken cancellationToken)
        {
            Handled.Add("wrapped");
            return Task.CompletedTask;
        }

        private Task HandleAsync(ItemAdded @event, CancellationToken cancellationToken)
        {
            Handled.Add("bare");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_projection_declaring_both_overloads_has_its_payload_handler_run_as_in_production()
    {
        var projection = new BothOverloadsProjection();

        await ProjectionTester.HandleAsync(projection, TestEvent.Create(new ItemAdded("x")), TestContext.Current.CancellationToken);

        Assert.Equal(["bare"], projection.Handled);
    }

    [Fact]
    public async Task Throws_when_no_handler_matches()
    {
        var projection = new CounterProjection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectionTester.HandleAsync(projection, TestEvent.Create(new NeverHandled("nope"))));

        Assert.Contains("NeverHandled", ex.Message, StringComparison.Ordinal);
    }
}
