using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The send lane keeps calls to one aggregate in initiation order and forgets an aggregate once its
/// last call has completed, so a long-lived scope does not hold every aggregate it ever addressed.
/// </summary>
public sealed class AggregateSendLaneTests
{
    private static readonly AggregateCommandEnvelope Envelope = new("type", "{}", "{}");

    [Fact]
    public async Task Calls_to_one_aggregate_run_one_after_another_in_initiation_order()
    {
        var lane = new AggregateSendLane();
        var key = Guid.NewGuid();
        var order = new List<int>();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = lane.SendAsync(key, Task.FromResult(Envelope), async _ =>
        {
            await firstGate.Task;
            lock (order)
            {
                order.Add(1);
            }
        });
        var second = lane.SendAsync(key, Task.FromResult(Envelope), _ =>
        {
            lock (order)
            {
                order.Add(2);
            }

            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.Empty(order);

        firstGate.SetResult();
        await await first;
        await await second;

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task A_key_whose_calls_have_completed_is_released()
    {
        var lane = new AggregateSendLane();

        for (var i = 0; i < 100; i++)
        {
            await await lane.SendAsync(Guid.NewGuid(), Task.FromResult(Envelope), _ => Task.CompletedTask);
        }

        Assert.Equal(0, lane.InFlight);
    }

    [Fact]
    public async Task A_key_is_kept_while_a_later_call_is_still_queued()
    {
        var lane = new AggregateSendLane();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = lane.SendAsync(key, Task.FromResult(Envelope), _ => Task.CompletedTask);
        var second = lane.SendAsync(key, Task.FromResult(Envelope), _ => gate.Task);
        await await first;
        var secondCall = await second;

        Assert.Equal(1, lane.InFlight);

        gate.SetResult();
        await secondCall;
        await Task.Delay(10);
        Assert.Equal(0, lane.InFlight);
    }

    [Fact]
    public async Task A_call_that_could_not_be_prepared_releases_its_key()
    {
        var lane = new AggregateSendLane();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lane.SendAsync(Guid.NewGuid(), Task.FromException<AggregateCommandEnvelope>(new InvalidOperationException()), _ => Task.CompletedTask));

        Assert.Equal(0, lane.InFlight);
    }
}
