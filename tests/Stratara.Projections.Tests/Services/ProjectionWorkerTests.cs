using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Resilience;
using Stratara.Shared.EventSourcing;

namespace Stratara.Projections.Tests.Services;

/// <summary>
/// The worker opens several consumers on one subscription, so two bundles about one aggregate used to
/// be applied at the same time and in whichever order finished first. These tests pin the guarantees
/// that replaced that: bundles about one aggregate serialise within the process, a handler can say
/// "not yet" and be retried with the lock released in between, and the consumer count is configurable.
/// </summary>
public class ProjectionWorkerTests
{
    private static readonly TimeSpan EarlyWindow = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SameStream_SecondBundleWaitsForFirst()
    {
        var stream = Guid.NewGuid();
        var gate = new Gate();
        var harness = new Harness(gate.Behaviour);

        var first = harness.Sut.HandleEventBundleAsync(Bundle("Created", stream), CancellationToken.None);
        await gate.FirstStarted.Task.WaitAsync(Generous);
        var second = harness.Sut.HandleEventBundleAsync(Bundle("Updated", stream), CancellationToken.None);

        var secondStartedEarly = await StartedWithin(gate.SecondStarted, EarlyWindow);
        gate.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Generous);

        Assert.False(secondStartedEarly);
        Assert.Equal(2, harness.Manager.Calls);
    }

    [Fact]
    public async Task DifferentStreams_BundlesApplyInParallel()
    {
        var gate = new Gate();
        var harness = new Harness(gate.Behaviour);

        var first = harness.Sut.HandleEventBundleAsync(Bundle("Created", Guid.NewGuid()), CancellationToken.None);
        await gate.FirstStarted.Task.WaitAsync(Generous);
        var second = harness.Sut.HandleEventBundleAsync(Bundle("Created", Guid.NewGuid()), CancellationToken.None);

        var secondStartedEarly = await StartedWithin(gate.SecondStarted, Generous);
        gate.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Generous);

        Assert.True(secondStartedEarly);
    }

    [Fact]
    public async Task BundleSpanningTwoStreams_SerialisesAgainstBundleOnEither()
    {
        var shared = Guid.NewGuid();
        var gate = new Gate();
        var harness = new Harness(gate.Behaviour);

        var first = harness.Sut.HandleEventBundleAsync(Bundle("Created", Guid.NewGuid(), shared), CancellationToken.None);
        await gate.FirstStarted.Task.WaitAsync(Generous);
        var second = harness.Sut.HandleEventBundleAsync(Bundle("Updated", shared), CancellationToken.None);

        var secondStartedEarly = await StartedWithin(gate.SecondStarted, EarlyWindow);
        gate.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Generous);

        Assert.False(secondStartedEarly);
    }

    [Fact]
    public async Task PrecedingFactMissing_IsRetriedAndSucceeds_LoggingOneWarning()
    {
        var stream = Guid.NewGuid();
        var calls = 0;
        var logger = new Mock<ILogger<ProjectionWorker>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var harness = new Harness((events, _) =>
            ++calls == 1 ? throw new PrecedingFactMissingException(stream, "Updated") : Task.CompletedTask, logger.Object);

        await harness.Sut.HandleEventBundleAsync(Bundle("Updated", stream), CancellationToken.None);

        Assert.Equal(2, calls);
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Id == Stratara.Diagnostics.LogEvents.Projection.PrecedingFactMissing),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<PrecedingFactMissingException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PrecedingFactMissing_OnEveryAttempt_FailsOnceRetriesAreExhausted()
    {
        var stream = Guid.NewGuid();
        var calls = 0;
        var harness = new Harness((_, _) =>
        {
            calls++;
            throw new PrecedingFactMissingException(stream, "Updated");
        });

        var ex = await Assert.ThrowsAsync<PrecedingFactMissingException>(
            () => harness.Sut.HandleEventBundleAsync(Bundle("Updated", stream), CancellationToken.None));

        Assert.Equal(Harness.RetryAttempts + 1, calls);
        Assert.Equal(stream, ex.StreamId);
        Assert.Equal("Updated", ex.EventTypeName);
    }

    [Fact]
    public async Task OtherFailure_IsNotRetried()
    {
        var calls = 0;
        var harness = new Harness((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Sut.HandleEventBundleAsync(Bundle("Updated", Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitingBundle_ReleasesTheLock_SoTheCreatingFactCanApply()
    {
        var stream = Guid.NewGuid();
        var created = false;
        var followUpFirstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpAttempts = 0;
        var harness = new Harness((events, _) =>
        {
            if (Kind(events) == "Created")
            {
                created = true;
                return Task.CompletedTask;
            }

            followUpAttempts++;
            followUpFirstAttempt.TrySetResult();
            return created ? Task.CompletedTask : throw new PrecedingFactMissingException(stream, "Updated");
        });

        var followUp = harness.Sut.HandleEventBundleAsync(Bundle("Updated", stream), CancellationToken.None);
        await followUpFirstAttempt.Task.WaitAsync(Generous);
        var beginning = harness.Sut.HandleEventBundleAsync(Bundle("Created", stream), CancellationToken.None);

        await Task.WhenAll(followUp, beginning).WaitAsync(Generous);

        Assert.True(created);
        Assert.InRange(followUpAttempts, 2, Harness.RetryAttempts + 1);
    }

    [Fact]
    public async Task EmptyBundle_TakesNoLockAndCompletes()
    {
        var harness = new Harness((_, _) => Task.CompletedTask);

        await harness.Sut.HandleEventBundleAsync(Bundle("Created"), CancellationToken.None);

        Assert.Equal(1, harness.Manager.Calls);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, -1)]
    [InlineData(-3, -1)]
    public async Task ExecuteAsync_OpensTheConfiguredNumberOfConsumers(int configured, int expected)
    {
        var harness = new Harness((_, _) => Task.CompletedTask, degreeOfParallelism: configured);

        await harness.Sut.StartAsync(CancellationToken.None);
        await harness.Sut.ExecuteTask!.WaitAsync(Generous);

        harness.MessageBus.Verify(
            b => b.SubscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<EventBundle, Task>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(expected < 0 ? Environment.ProcessorCount : expected));
    }

    private static async Task<bool> StartedWithin(TaskCompletionSource started, TimeSpan window) =>
        await Task.WhenAny(started.Task, Task.Delay(window)) == started.Task;

    private static string Kind(IReadOnlyList<IEvent> events) => (string)events[0].Data;

    private static EventBundle Bundle(string kind, params Guid[] streams)
    {
        var events = streams.Select(stream => new EventMessage(
            Id: Guid.CreateVersion7(),
            Version: 1,
            DataJson: "{}",
            StreamId: stream,
            EventTypeName: kind,
            AggregateTypeName: "TestAggregate",
            ActorTenantId: Guid.Empty,
            ActorUserId: Guid.Empty,
            TenantId: Guid.Empty,
            UserId: null)).ToList();
        return new EventBundle(events, JsonSerializer.Serialize(SessionContext.Empty()));
    }

    private sealed class Gate
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Behaviour(IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
        {
            if (FirstStarted.TrySetResult())
            {
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                return;
            }

            SecondStarted.TrySetResult();
        }
    }

    private sealed class ScriptedProjectionManager(Func<IReadOnlyList<IEvent>, CancellationToken, Task> behaviour) : IProjectionManager
    {
        private int _calls;

        public int Calls => _calls;

        public Task HandleAsync(IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return behaviour(events, cancellationToken);
        }
    }

    private sealed class Harness
    {
        public const int RetryAttempts = 5;

        public Mock<IMessageBus> MessageBus { get; } = new();
        public ScriptedProjectionManager Manager { get; }
        public ProjectionWorker Sut { get; }

        public Harness(Func<IReadOnlyList<IEvent>, CancellationToken, Task> behaviour, ILogger<ProjectionWorker>? logger = null, int? degreeOfParallelism = null)
        {
            Manager = new ScriptedProjectionManager(behaviour);

            var pipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
            pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
            pipelineProvider.Setup(p => p.GetPipeline(ResilienceNames.PrecedingFact)).Returns(FastPrecedingFactPipeline());

            var mapper = new Mock<IEventMapperFactory>();
            mapper.Setup(m => m.MapToEventsAsync(It.IsAny<IReadOnlyList<EventMessage>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<EventMessage> messages, CancellationToken _) => messages
                    .Select(m => (IEvent)new Event<string>(m.Id, m.Version, m.EventTypeName, m.StreamId, m.TenantId, m.UserId ?? Guid.Empty))
                    .ToList());

            MessageBus.Setup(b => b.SubscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<EventBundle, Task>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(new Mock<ISessionContextProvider>().Object);
            services.AddSingleton<IProjectionManager>(Manager);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Sut = new ProjectionWorker(
                logger ?? NullLogger<ProjectionWorker>.Instance,
                MessageBus.Object,
                new Mock<IMessagingIdentifier>().Object,
                scopeFactory,
                mapper.Object,
                pipelineProvider.Object,
                Options.Create(new BusEnvelopeJsonOptions()),
                Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off }),
                Options.Create(new ProjectionOptions { DegreeOfParallelism = degreeOfParallelism }));
        }

        private static ResiliencePipeline FastPrecedingFactPipeline() =>
            new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<PrecedingFactMissingException>(),
                    MaxRetryAttempts = RetryAttempts,
                    Delay = TimeSpan.FromMilliseconds(10),
                    BackoffType = DelayBackoffType.Constant,
                })
                .Build();
    }
}
