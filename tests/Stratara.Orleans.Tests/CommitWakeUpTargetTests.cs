using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Scenarios <em>A commit of an event one projection handles</em>, <em>A client host wakes the readers through the
/// cluster</em> and <em>A host of the earlier version commits during a rolling deployment</em>, at the silo's wake-up
/// targets: a commit's event types wake only the projections that apply one of them, whether the commit was made on
/// the silo or named to it by a wake-up through the cluster, and a wake-up without types wakes every projection.
/// </summary>
public sealed class CommitWakeUpTargetTests
{
    private static readonly string Created = typeof(ProbeCreated).AssemblyQualifiedName ?? nameof(ProbeCreated);
    private static readonly string Incremented = typeof(ProbeIncremented).AssemblyQualifiedName ?? nameof(ProbeIncremented);
    private static readonly string Noted = typeof(ProbeNoted).AssemblyQualifiedName ?? nameof(ProbeNoted);

    [Fact]
    public async Task A_commit_wakes_only_the_projections_that_apply_one_of_its_events()
    {
        var (target, grains, woken) = Target();

        await target.NudgeAsync(grains, partition: 4, [Created]);

        Assert.Equal([StoreReaderGrainKey.Of("creation", 4)], woken);
    }

    [Fact]
    public async Task A_commit_of_events_nobody_applies_wakes_nobody_and_one_the_silo_cannot_tell_wakes_everyone()
    {
        var (target, grains, woken) = Target();

        await target.NudgeAsync(grains, partition: 4, [Noted]);
        Assert.Empty(woken);

        await target.NudgeAsync(grains, partition: 4, ["Elsewhere.Unknown, Elsewhere"]);
        Assert.Equal([StoreReaderGrainKey.Of("creation", 4), StoreReaderGrainKey.Of("increment", 4)], woken.Order());
    }

    [Fact]
    public async Task A_wake_up_through_the_cluster_wakes_the_projections_its_types_concern_and_every_one_without_types()
    {
        var (target, grains, woken) = Target();

        await StoreWakeUpGrain<ProjectionNudgeTarget>.WakeLocalAsync([target], grains, [4], partitionCount: 16, NullLogger.Instance, [Incremented]);
        Assert.Equal([StoreReaderGrainKey.Of("increment", 4)], woken);

        woken.Clear();
        await StoreWakeUpGrain<ProjectionNudgeTarget>.WakeLocalAsync([target], grains, [4], partitionCount: 16, NullLogger.Instance);
        Assert.Equal([StoreReaderGrainKey.Of("creation", 4), StoreReaderGrainKey.Of("increment", 4)], woken.Order());
    }

    /// <summary>
    /// Scenario <em>A stateful saga process and a fact it did not declare</em>: a process decides from the event itself,
    /// so every commit wakes it, while a stateless saga is woken only by the events it declares.
    /// </summary>
    [Fact]
    public async Task A_stateful_process_is_woken_by_every_commit_and_a_stateless_saga_only_by_its_events()
    {
        var handler = new Mock<ISagaHandler>();
        var stateless = new Mock<ISaga>().Object;
        var process = new Mock<ISagaProcess>().Object;
        handler.Setup(h => h.GetRelevantEventTypes(stateless)).Returns([typeof(ProbeCreated)]);
        handler.Setup(h => h.GetRelevantEventTypes(process)).Returns([typeof(ProbeCreated)]);
        var services = new ServiceCollection()
            .AddSingleton(handler.Object)
            .AddSingleton(stateless)
            .AddSingleton<ISaga>(process)
            .BuildServiceProvider();
        var registrations = new SagaRegistrations([stateless.GetType(), process.GetType()]);
        var target = new SagaNudgeTarget(
            registrations,
            new StoreReaderLease(StoreReaderLease.DefaultDuration, TimeProvider.System),
            new CommitRelevance(Resolver(), new EventUpcasterPipeline([]), []),
            new SagaRelevances(services.GetRequiredService<IServiceScopeFactory>(), registrations));
        var woken = new List<string>();
        var grains = new Mock<IGrainFactory>();
        grains.Setup(f => f.GetGrain<ISagaReaderGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) =>
        {
            var grain = new Mock<ISagaReaderGrain>();
            grain.Setup(g => g.NudgeAsync()).Callback(() => woken.Add(key)).Returns(Task.CompletedTask);
            return grain.Object;
        });

        await target.NudgeAsync(grains.Object, partition: 2, [Incremented]);
        Assert.Equal([StoreReaderGrainKey.Of(SagaReaderGrain.ConsumerOf(process.GetType().Name), 2)], woken);

        woken.Clear();
        await target.NudgeAsync(grains.Object, partition: 2, [Created]);
        Assert.Equal(2, woken.Count);
    }

    private static TrustedTypeResolver Resolver()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(ProbeCreated));
        resolver.Register(typeof(ProbeIncremented));
        resolver.Register(typeof(ProbeNoted));
        return resolver;
    }

    private static (ProjectionNudgeTarget Target, IGrainFactory Grains, List<string> Woken) Target()
    {
        var handler = new Mock<IProjectionHandler>();
        var services = new ServiceCollection()
            .AddSingleton(handler.Object)
            .AddSingleton(Projection(handler, "creation", typeof(ProbeCreated)))
            .AddSingleton(Projection(handler, "increment", typeof(ProbeIncremented)));
        var registrations = new ProjectionRegistrations(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var relevance = new CommitRelevance(Resolver(), new EventUpcasterPipeline([]), []);
        var target = new ProjectionNudgeTarget(registrations, new StoreReaderLease(StoreReaderLease.DefaultDuration, TimeProvider.System), relevance);

        var woken = new List<string>();
        var grains = new Mock<IGrainFactory>();
        grains.Setup(f => f.GetGrain<IProjectionGrain>(It.IsAny<string>(), null)).Returns((string key, string? _) =>
        {
            var grain = new Mock<IProjectionGrain>();
            grain.Setup(g => g.NudgeAsync()).Callback(() => woken.Add(key)).Returns(Task.CompletedTask);
            return grain.Object;
        });
        return (target, grains.Object, woken);
    }

    private static IProjection Projection(Mock<IProjectionHandler> handler, string name, Type applies)
    {
        var projection = new Mock<IProjection>();
        handler.Setup(h => h.GetProjectionName(projection.Object)).Returns(name);
        handler.Setup(h => h.GetRelevantEventTypes(projection.Object)).Returns([applies]);
        return projection.Object;
    }

    private sealed record ProbeCreated(Guid Id);

    private sealed record ProbeIncremented(Guid Id);

    private sealed record ProbeNoted(Guid Id);
}
