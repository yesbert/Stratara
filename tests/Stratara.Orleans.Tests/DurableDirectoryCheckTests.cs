using Microsoft.Extensions.DependencyInjection;
using Moq;
using Orleans.GrainDirectory;
using Stratara.Abstractions.Singleton;
using Stratara.Diagnostics;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The start-up check fails a silo that registers a role or singleton work and publishes none of it — the silo that
/// registered the directory itself — naming what it found and the call that publishes it, and passes a silo that
/// hosts nothing placed by role or that publishes.
/// </summary>
public sealed class DurableDirectoryCheckTests
{
    [Fact]
    public async Task A_role_and_a_work_without_the_publication_fail_naming_both_and_the_call()
    {
        var logger = new RecordingLogger();
        var services = WithDirectory().AddStrataraProjectionGrains().AddStrataraSingletonWork<IdleWork>(IdleWork.WorkName);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(services, logger));

        Assert.Contains("AddStrataraProjectionGrains", failure.Message, StringComparison.Ordinal);
        Assert.Contains(IdleWork.WorkName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddStrataraOrleans", failure.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.RolesUnpublished);
    }

    [Fact]
    public async Task A_work_registered_without_its_name_is_named_by_its_type()
    {
        var services = WithDirectory().AddStrataraSingletonWork<IdleWork>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(services, new RecordingLogger()));

        Assert.Contains(nameof(IdleWork), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dispatcher_only_passes()
    {
        var services = WithDirectory().AddStrataraOrleansCommandDispatcher();

        await CheckAsync(services, new RecordingLogger());
    }

    [Fact]
    public async Task The_timers_without_an_owner_check_pass()
    {
        var services = WithDirectory().AddStrataraDurableTimers();

        await CheckAsync(services, new RecordingLogger());
    }

    [Fact]
    public async Task A_silo_that_publishes_passes()
    {
        var services = WithDirectory().AddStrataraAggregateGrains().AddStrataraSingletonWork<IdleWork>(IdleWork.WorkName);
        services.AddSingleton(new SingletonWorkPlacement.SingletonWorkSiloMetadata());

        await CheckAsync(services, new RecordingLogger());
    }

    private static IServiceCollection WithDirectory() =>
        new ServiceCollection().AddKeyedSingleton(GrainDirectories.Durable, new Mock<IGrainDirectory>().Object);

    private static async Task CheckAsync(IServiceCollection services, RecordingLogger logger)
    {
        await using var provider = services.BuildServiceProvider();
        await new DurableDirectoryCheck(provider, new TypedLogger<DurableDirectoryCheck>(logger)).CheckAsync(CancellationToken.None);
    }

    public sealed class IdleWork : ISingletonWork
    {
        public const string WorkName = "check-idle";

        public string Name => WorkName;

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
