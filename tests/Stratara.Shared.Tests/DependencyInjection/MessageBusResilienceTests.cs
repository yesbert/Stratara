using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.Telemetry;
using Stratara.Resilience;
using Xunit;

namespace Stratara.Shared.Tests.DependencyInjection;

/// <summary>
/// The message-bus policy retries indefinitely with a ten-second base delay, so it cannot be
/// exercised in real time — which is why nothing covered it. With a controlled clock the circuit
/// breaker is observable: while the circuit is open the inner operation stops being invoked, so the
/// attempt count plateaus and then resumes once the break elapses.
/// </summary>
public class MessageBusResilienceTests
{
    private static readonly TimeSpan PastAnyRetryDelay = TimeSpan.FromSeconds(61);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(60);

    /// <summary>Records the resilience events Polly emits, so the breaker is observed rather than inferred.</summary>
    private sealed class RecordingTelemetry : TelemetryListener
    {
        private readonly List<string> events = [];

        public IReadOnlyList<string> Events
        {
            get { lock (events) { return [.. events]; } }
        }

        public override void Write<TResult, TArgs>(in TelemetryEventArguments<TResult, TArgs> args)
        {
            lock (events)
            {
                events.Add(args.Event.EventName);
            }
        }
    }

    private static ResiliencePipeline MessageBusPipelineOn(TimeProvider clock, TelemetryListener? telemetry = null)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = clock, TelemetryListener = telemetry };
        ResilienceFactory.CreateMessageBusPipeline(builder);
        return builder.Build();
    }

    /// <summary>
    /// Advances the clock in one-second increments until <paramref name="until"/> holds or the budget
    /// runs out. Small increments matter: advancing past the whole backoff in one step would space
    /// even the early, short retries a full minute apart and hide how many failures actually land
    /// inside the breaker's sampling window.
    /// </summary>
    private static async Task<bool> PumpAsync(FakeTimeProvider clock, Func<bool> until, int seconds = 3_600)
    {
        for (var i = 0; i < seconds; i++)
        {
            if (until())
            {
                return true;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        return until();
    }

    [Fact]
    public void TheSamplingWindowIsWideEnoughForTheRetryToFillIt()
    {
        // The defect this replaces: three settings chosen without reference to one another. At steady
        // state the retry produces one action per MaxDelay, so a window that does not span
        // MaxDelay × MinimumThroughput can never see the throughput the breaker demands.
        var minimumUsableWindow = ResilienceFactory.MessageBusMaxDelay
                                  * ResilienceFactory.MessageBusMinimumThroughput;

        Assert.True(
            ResilienceFactory.MessageBusSamplingDuration >= minimumUsableWindow,
            $"the sampling window ({ResilienceFactory.MessageBusSamplingDuration}) must span at least " +
            $"MaxDelay × MinimumThroughput ({minimumUsableWindow}), otherwise the breaker cannot open");
    }

    [Fact]
    public async Task TheCircuitOpensUnderSustainedFailure()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new RecordingTelemetry();
        var pipeline = MessageBusPipelineOn(clock, telemetry);

        using var stop = new CancellationTokenSource();

        var execution = pipeline.ExecuteAsync(
            _ => throw new InvalidOperationException("the broker is unreachable"),
            stop.Token).AsTask();

        var opened = await PumpAsync(clock, () => telemetry.Events.Contains("OnCircuitOpened"));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => execution);

        Assert.True(opened, "the circuit did not open within a simulated hour of uninterrupted failure");
    }

    [Fact]
    public async Task TheCircuitClosesAgainOnceTheOperationSucceeds()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new RecordingTelemetry();
        var pipeline = MessageBusPipelineOn(clock, telemetry);
        var brokerIsDown = true;

        using var stop = new CancellationTokenSource();

        var execution = pipeline.ExecuteAsync(
            _ =>
            {
                if (Volatile.Read(ref brokerIsDown))
                {
                    throw new InvalidOperationException("the broker is unreachable");
                }

                return ValueTask.CompletedTask;
            },
            stop.Token).AsTask();

        Assert.True(
            await PumpAsync(clock, () => telemetry.Events.Contains("OnCircuitOpened")),
            "the circuit did not open, so its recovery cannot be observed");

        Volatile.Write(ref brokerIsDown, false);

        var closed = await PumpAsync(clock, () => telemetry.Events.Contains("OnCircuitClosed"));

        await stop.CancelAsync();

        Assert.True(closed, "the circuit stayed open after the broker recovered");
    }

    [Fact]
    public async Task ItRetriesRatherThanGivingUp()
    {
        var clock = new FakeTimeProvider();
        var pipeline = MessageBusPipelineOn(clock);
        var attempts = 0;

        var result = pipeline.ExecuteAsync(
            _ =>
            {
                if (Interlocked.Increment(ref attempts) < 4)
                {
                    throw new InvalidOperationException("transient");
                }

                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await PumpAsync(clock, () => result.IsCompleted);

        await result;
        Assert.Equal(4, attempts);
    }
}
