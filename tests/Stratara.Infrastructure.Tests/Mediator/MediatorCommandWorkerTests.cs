using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Mediator;
using Stratara.Outbox.RabbitMQ.Mediator;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Shared.Partitioning;
using Stratara.Resilience;

namespace Stratara.Infrastructure.Tests.Mediator;

public sealed record TestScopedCommand(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed record TestPlainCommand(string Data) : ICommand;

public class MediatorCommandWorkerTests
{
    private readonly Mock<ILogger<MediatorCommandWorker>> _loggerMock = new();
    private readonly Mock<IMessageBus> _messageBusMock = new();
    private readonly Mock<IMessagingIdentifier> _messagingIdentifierMock = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();

    public MediatorCommandWorkerTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _messagingIdentifierMock.Setup(m => m.CommandTopic).Returns("command");
        _messagingIdentifierMock.Setup(m => m.CommandSubscription).Returns("command-subscription");
    }

    private static Stratara.Abstractions.Reflections.TrustedTypeResolver CreateResolverWithTestCommands()
    {
        var resolver = new Stratara.Abstractions.Reflections.TrustedTypeResolver();
        resolver.Register(typeof(TestScopedCommand));
        resolver.Register(typeof(TestPlainCommand));
        return resolver;
    }

    private static ISecureJsonSerializer CreatePassthroughSerializer()
    {
        var mock = new Mock<ISecureJsonSerializer>();
        mock.Setup(s => s.DeserializeAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns<string, Type, Guid?, Guid?, CancellationToken>((json, type, _, _, _) =>
                Task.FromResult(JsonSerializer.Deserialize(json, type)));
        return mock.Object;
    }

    private MediatorCommandWorker CreateWorker()
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(ResilienceNames.MessageBus, (builder, _) => { });

        return new MediatorCommandWorker(
            _loggerMock.Object,
            _messageBusMock.Object,
            _messagingIdentifierMock.Object,
            _scopeFactoryMock.Object,
            registry,
            CreateResolverWithTestCommands(),
            CreatePassthroughSerializer(),
            Options.Create(new BusEnvelopeJsonOptions()),
            Options.Create(new BusEnvelopeIntegrityOptions()));
    }

    private static MediatorCommandWorker CreateWorkerWithMediator(IMediator mediator, ISessionContextProvider sessionContextProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(mediator);
        services.AddSingleton(sessionContextProvider);
        var serviceProvider = services.BuildServiceProvider();

        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(ResilienceNames.MessageBus, (builder, _) => { });

        return new MediatorCommandWorker(
            new Mock<ILogger<MediatorCommandWorker>>().Object,
            new Mock<IMessageBus>().Object,
            new Mock<IMessagingIdentifier>().Object,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            CreateResolverWithTestCommands(),
            CreatePassthroughSerializer(),
            Options.Create(new BusEnvelopeJsonOptions()),
            Options.Create(new BusEnvelopeIntegrityOptions()));
    }

    private static CommandEnvelope CreateEnvelope(object command)
    {
        var tenantId = Guid.NewGuid();
        var sessionContext = new SessionContext("corr-1", "caus-1", null, tenantId, Guid.NewGuid(), tenantId, null);
        return new CommandEnvelope(
            Guid.CreateVersion7(),
            JsonSerializer.Serialize(command, command.GetType()),
            command.GetType().AssemblyQualifiedName!,
            JsonSerializer.Serialize(sessionContext));
    }

    private static Guid GenerateGuidWithDifferentBucket(Guid reference)
    {
        var referenceBucket = BucketCalculator.GetBucketId(reference);
        for (var i = 0; i < 100; i++)
        {
            var candidate = Guid.CreateVersion7();
            if (BucketCalculator.GetBucketId(candidate) != referenceBucket)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to find a Guid with a different bucket id within 100 attempts.");
    }

    [Fact]
    public async Task StartAsync_LogsStartMessage()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var worker = CreateWorker();
        await worker.StartAsync(cts.Token);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopAsync_LogsStopMessage()
    {
        var worker = CreateWorker();
        await worker.StopAsync(CancellationToken.None);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DispatchAsync_AggregateScopedCommandsWithSameAggregateId_AreSerialized()
    {
        var aggregateId = Guid.NewGuid();
        var mediator = new GatedTestMediator();
        var sessionContextProviderMock = new Mock<ISessionContextProvider>();
        var worker = CreateWorkerWithMediator(mediator, sessionContextProviderMock.Object);

        var firstDispatch = worker.DispatchAsync(CreateEnvelope(new TestScopedCommand(aggregateId)), TestContext.Current.CancellationToken);
        var secondDispatch = worker.DispatchAsync(CreateEnvelope(new TestScopedCommand(aggregateId)), TestContext.Current.CancellationToken);

        await mediator.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, mediator.CallCount);

        mediator.Gate.SetResult();

        await firstDispatch;
        await secondDispatch;

        Assert.Equal(2, mediator.CallCount);
    }

    [Fact]
    public async Task DispatchAsync_AggregateScopedCommandsWithDifferentAggregateIds_RunInParallel()
    {
        var aggregateA = Guid.NewGuid();
        var aggregateB = GenerateGuidWithDifferentBucket(aggregateA);
        var mediator = new GatedTestMediator();
        var sessionContextProviderMock = new Mock<ISessionContextProvider>();
        var worker = CreateWorkerWithMediator(mediator, sessionContextProviderMock.Object);

        var firstDispatch = worker.DispatchAsync(CreateEnvelope(new TestScopedCommand(aggregateA)), TestContext.Current.CancellationToken);
        var secondDispatch = worker.DispatchAsync(CreateEnvelope(new TestScopedCommand(aggregateB)), TestContext.Current.CancellationToken);

        await mediator.WaitForCallCountAsync(2, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, mediator.CallCount);

        mediator.Gate.SetResult();
        await Task.WhenAll(firstDispatch, secondDispatch);
    }

    [Fact]
    public async Task DispatchAsync_PlainCommands_RunInParallelWithoutLock()
    {
        var mediator = new GatedTestMediator();
        var sessionContextProviderMock = new Mock<ISessionContextProvider>();
        var worker = CreateWorkerWithMediator(mediator, sessionContextProviderMock.Object);

        var firstDispatch = worker.DispatchAsync(CreateEnvelope(new TestPlainCommand("a")), TestContext.Current.CancellationToken);
        var secondDispatch = worker.DispatchAsync(CreateEnvelope(new TestPlainCommand("b")), TestContext.Current.CancellationToken);

        await mediator.WaitForCallCountAsync(2, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, mediator.CallCount);

        mediator.Gate.SetResult();
        await Task.WhenAll(firstDispatch, secondDispatch);
    }

    private sealed class GatedTestMediator : IMediator
    {
        private int _callCount;

        public int CallCount => _callCount;
        public TaskCompletionSource Gate { get; } = new();
        public TaskCompletionSource FirstCallStarted { get; } = new();

        public Task<TResult> HandleAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Result-returning commands are not exercised by these tests.");

        public async Task HandleAsync<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : class, IRequest
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstCallStarted.TrySetResult();
            }

            await Gate.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForCallCountAsync(int expected, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            while (CallCount < expected)
            {
                await Task.Delay(10, cts.Token);
            }
        }
    }
}
