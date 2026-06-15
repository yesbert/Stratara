using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Outbox.RabbitMQ.Mediator;
using Stratara.Shared.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Mediator;

public class CommandWorkerLaneTests
{
    [Fact]
    public void Interactive_ResolvesDefaultCommandTopicAndSubscription()
    {
        IMessagingIdentifier identifier = new MessagingIdentifier(Options.Create(new MessagingOptions()));
        var lane = CommandWorkerLane.Interactive;

        Assert.False(lane.Heavy);
        Assert.Equal("command", lane.ResolveTopic(identifier));
        Assert.Equal("command-subscription", lane.ResolveSubscription(identifier));
        Assert.Equal(Environment.ProcessorCount, lane.EffectiveDegreeOfParallelism);
    }

    [Fact]
    public void Heavy_ResolvesHeavyTopicAndSubscriptionAndConfiguredParallelism()
    {
        IMessagingIdentifier identifier = new MessagingIdentifier(Options.Create(new MessagingOptions()));
        var lane = new CommandWorkerLane(Heavy: true, DegreeOfParallelism: 2);

        Assert.Equal("heavy-command", lane.ResolveTopic(identifier));
        Assert.Equal("heavy-command-subscription", lane.ResolveSubscription(identifier));
        Assert.Equal(2, lane.EffectiveDegreeOfParallelism);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public void EffectiveDegreeOfParallelism_NonPositive_FallsBackToProcessorCount(int? configured)
    {
        var lane = new CommandWorkerLane(Heavy: true, configured);

        Assert.Equal(Environment.ProcessorCount, lane.EffectiveDegreeOfParallelism);
    }

    [Fact]
    public async Task Worker_HeavyLane_SubscribesToHeavyTopicAndSubscription()
    {
        var bus = new RecordingBus();
        var worker = CreateWorker(bus, new CommandWorkerLane(Heavy: true, DegreeOfParallelism: 1));

        await worker.StartAsync(CancellationToken.None);
        await bus.Recorded.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        var (topic, subscription) = Assert.Single(bus.Subscriptions);
        Assert.Equal("heavy-command", topic);
        Assert.Equal("heavy-command-subscription", subscription);
    }

    [Fact]
    public async Task Worker_DefaultLane_SubscribesToInteractiveTopicAndSubscription()
    {
        var bus = new RecordingBus();
        var worker = CreateWorker(bus, lane: null);

        await worker.StartAsync(CancellationToken.None);
        await bus.Recorded.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // The default (interactive) lane opens Environment.ProcessorCount subscriptions; only one
        // recorded entry is needed to confirm it binds the interactive topic/subscription.
        Assert.Contains(("command", "command-subscription"), bus.Subscriptions);
    }

    private static MediatorCommandWorker CreateWorker(RecordingBus bus, CommandWorkerLane? lane)
    {
        var pipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
        pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);

        IMessagingIdentifier identifier = new MessagingIdentifier(Options.Create(new MessagingOptions()));
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new MediatorCommandWorker(
            NullLogger<MediatorCommandWorker>.Instance,
            bus,
            identifier,
            scopeFactory,
            pipelineProvider.Object,
            new TrustedTypeResolver(),
            new Mock<ISecureJsonSerializer>().Object,
            Options.Create(new BusEnvelopeJsonOptions()),
            Options.Create(new BusEnvelopeIntegrityOptions()),
            signer: null,
            lane: lane);
    }

    private sealed class RecordingBus : IMessageBus
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();

        public List<(string Topic, string Subscription)> Subscriptions { get; } = [];

        public Task Recorded => _signal.Task;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Subscriptions.Add((topic, subscription));
            }

            _signal.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
