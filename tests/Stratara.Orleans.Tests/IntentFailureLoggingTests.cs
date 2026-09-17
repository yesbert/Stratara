using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A failing attempt of a recorded command and a failing hand-over are logged under the Orleans band with the
/// command's identity, so a handler that keeps failing is seen before the command is kept.
/// </summary>
public sealed class IntentFailureLoggingTests
{
    [Fact]
    public async Task A_failed_attempt_is_logged_with_the_intent_the_command_type_and_the_aggregate_before_it_is_recorded()
    {
        var intentId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(store => store.RecordFailureAsync(intentId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddSingleton(intents.Object)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromMinutes(1) }))
            .AddSingleton<ILogger<IntentLease>>(new TypedLogger<IntentLease>(logger))
            .BuildServiceProvider();
        await using var lease = await IntentLease.StartAsync(services, intentId);

        await lease.RecordFailureAsync(new InvalidOperationException("the handler failed"), "App.Commands.Approve", aggregateId);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentAttemptFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(intentId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("App.Commands.Approve", entry.Message, StringComparison.Ordinal);
        Assert.Contains(aggregateId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        intents.Verify(store => store.RecordFailureAsync(intentId, It.Is<string>(f => f.Contains("the handler failed")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_hand_over_that_faults_is_logged_with_the_intent_and_the_aggregate()
    {
        var intentId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var faulted = Task.FromException(new InvalidOperationException("the silo refused the call"));

        IntentHandOver.Observe(faulted, logger, intentId, aggregateId, heavy: false);
        await Task.Delay(50);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.HandOverFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(intentId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(aggregateId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task A_timeout_on_a_call_that_spans_a_heavy_unit_is_not_logged_but_any_other_fault_is()
    {
        var logger = new RecordingLogger();

        IntentHandOver.Observe(Task.FromException(new TimeoutException("the unit outlived the response timeout")), logger, Guid.NewGuid(), Guid.NewGuid(), heavy: true);
        IntentHandOver.Observe(Task.FromException(new TimeoutException("the runner outlived the response timeout")), logger, Guid.NewGuid(), aggregateId: null, heavy: false);
        IntentHandOver.Observe(Task.FromException(new InvalidOperationException("the pool refused the call")), logger, Guid.NewGuid(), Guid.NewGuid(), heavy: true);
        IntentHandOver.Observe(Task.CompletedTask, logger, Guid.NewGuid(), Guid.NewGuid(), heavy: false);
        await Task.Delay(50);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogEvents.Orleans.HandOverFailed, entry.EventId.Id);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }
}
