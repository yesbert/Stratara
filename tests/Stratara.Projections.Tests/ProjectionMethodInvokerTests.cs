using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Abstractions.EventSourcing;
using Xunit;

namespace Stratara.Projections.Tests;

public class ProjectionMethodInvokerTests
{
    private sealed class TestEvent {}

    private sealed class ProjectionWithDirectEvent : IProjection
    {
        public List<string> Calls { get; } = new();
        public Task HandleAsync(TestEvent e, CancellationToken token)
        {
            Calls.Add("Direct:" + e.GetType().Name);
            return Task.CompletedTask;
        }
    }

    private sealed class ProjectionWithWrappedEvent : IProjection
    {
        public List<string> Calls { get; } = new();
        public Task HandleAsync(IEvent<TestEvent> e, CancellationToken token)
        {
            Calls.Add("Wrapped:" + (e.Data?.GetType().Name ?? "null"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_Finds_Direct_And_Wrapped_Types()
    {
        var invoker = new ProjectionMethodInvoker();
        var p1 = new ProjectionWithDirectEvent();
        var p2 = new ProjectionWithWrappedEvent();

        var types1 = invoker.GetOrCreateRelevantEventTypes(p1);
        var types2 = invoker.GetOrCreateRelevantEventTypes(p2);

        Assert.Contains(typeof(TestEvent), types1);
        Assert.Contains(typeof(TestEvent), types2);
    }

    [Fact]
    public async Task GetOrCreateDelegate_Invokes_Correct_Method()
    {
        var invoker = new ProjectionMethodInvoker();
        var p = new ProjectionWithDirectEvent();
        var del = invoker.GetOrCreateDelegate(p, typeof(TestEvent));

        await del(p, new TestEvent(), CancellationToken.None);

        Assert.Single(p.Calls);
        Assert.Equal("Direct:TestEvent", p.Calls[0]);
        Assert.False(invoker.IsNoOp(del));
    }

    [Fact]
    public void GetOrCreateDelegate_Returns_NoOp_When_Method_Not_Found()
    {
        var invoker = new ProjectionMethodInvoker();
        var p = new ProjectionWithDirectEvent();
        var del = invoker.GetOrCreateDelegate(p, typeof(string));

        Assert.True(invoker.IsNoOp(del));
    }

    [Fact]
    public void GetOrCreateRelevantEventTypes_Returns_Cached_On_Second_Call()
    {
        var invoker = new ProjectionMethodInvoker();
        var p = new ProjectionWithDirectEvent();

        var first = invoker.GetOrCreateRelevantEventTypes(p);
        var second = invoker.GetOrCreateRelevantEventTypes(p);

        Assert.Same(first, second);
        Assert.Contains(typeof(TestEvent), first);
    }
}
