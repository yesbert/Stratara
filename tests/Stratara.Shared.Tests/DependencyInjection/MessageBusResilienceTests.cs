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

    /// <summary>
    /// **Characterization test — it pins a defect, not a guarantee.**
    ///
    /// The message-bus policy's circuit breaker cannot open under the policy's own backoff. The
    /// breaker needs ten actions inside a sixty-second sampling window; the retry's delay is capped
    /// at sixty seconds, so once the backoff has grown at most one failure lands per window. Over a
    /// simulated hour of continuous failure the breaker records no open at all.
    ///
    /// Nothing observable is wrong today: the spec's guarantee is that the operation succeeds once
    /// the broker recovers, and the retry delivers that. What the breaker was documented to deliver
    /// — bounding the duty cycle of a permanent failure — is delivered by the sixty-second delay
    /// cap instead. The breaker is inert configuration whose stated purpose is served elsewhere.
    ///
    /// When that is fixed, this test fails. That is the point: it is here so the next person to
    /// touch the policy learns this in one test run rather than by reading the factory.
    /// </summary>
    [Fact]
    public async Task TheCircuitBreakerNeverOpens_BecauseTheBackoffOutrunsItsSamplingWindow()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new RecordingTelemetry();
        var pipeline = MessageBusPipelineOn(clock, telemetry);
        var attempts = 0;

        using var stop = new CancellationTokenSource();

        var execution = pipeline.ExecuteAsync(
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("the broker is unreachable");
            },
            stop.Token).AsTask();

        // One simulated hour of uninterrupted failure.
        await PumpAsync(clock, () => telemetry.Events.Contains("OnCircuitOpened"));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => execution);

        Assert.DoesNotContain("OnCircuitOpened", telemetry.Events);

        // The duty cycle is bounded all the same — by the delay cap, at roughly one attempt a
        // minute, which is what the breaker was credited with.
        Assert.InRange(Volatile.Read(ref attempts), 55, 70);
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
