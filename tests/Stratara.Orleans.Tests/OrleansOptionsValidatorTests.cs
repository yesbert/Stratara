using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Every setting the execution model binds is validated when the host starts, and an invalid one fails the
/// start naming itself; the defaults start.
/// </summary>
public sealed class OrleansOptionsValidatorTests
{
    [Theory]
    [InlineData("OrleansDispatchOptions.IntentGrace")]
    [InlineData("OrleansDispatchOptions.CompletionWindow")]
    [InlineData("OrleansDispatchOptions.CompletionBatchSize")]
    [InlineData("HeavyWorkOptions.ClusterWideLimit")]
    [InlineData("HeavyWorkOptions.PermitRetry")]
    [InlineData("HeavyWorkOptions.PermitLease")]
    [InlineData("DurableTimerOptions.RetryPeriod")]
    [InlineData("DurableTimerOptions.DueTolerance")]
    [InlineData("ProjectionGrainOptions.BatchSize")]
    [InlineData("ProjectionGrainOptions.PollInterval")]
    [InlineData("ProjectionGrainOptions.KeepAlivePeriod")]
    [InlineData("SagaGrainOptions.BatchSize")]
    [InlineData("SagaGrainOptions.PollInterval")]
    [InlineData("SagaGrainOptions.KeepAlivePeriod")]
    [InlineData("CommitOrderOptions.PartitionCount")]
    [InlineData("SingletonWorkOptions.KeepAlivePeriod")]
    [InlineData("OutboxDrainOptions.PollingInterval")]
    [InlineData("OutboxDrainOptions.BatchSize")]
    public async Task An_invalid_setting_fails_the_start_naming_it(string setting)
    {
        var services = BaseServices();
        Invalidate(services, setting);
        await using var provider = services.BuildServiceProvider();

        var failure = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_settings_start()
    {
        var services = BaseServices()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraDurableTimers()
            .AddStrataraProjectionGrains()
            .AddStrataraSagaGrains()
            .AddStrataraSingletonWork<ProbeWork>();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public async Task A_completion_window_longer_than_the_queue_accepts_fails_the_start_naming_it()
    {
        var services = BaseServices().AddStrataraOrleansCommandDispatcher(o =>
        {
            o.CompletionWindow = TimeSpan.FromSeconds(15);
            o.IntentGrace = TimeSpan.FromMinutes(1);
        });
        await using var provider = services.BuildServiceProvider();

        var failure = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("OrleansDispatchOptions.CompletionWindow", failure.Message, StringComparison.Ordinal);
    }

    private static IServiceCollection BaseServices() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IGrainFactory>().Object);

    private static void Invalidate(IServiceCollection services, string setting)
    {
        var tooShortForAReminder = TimeSpan.FromSeconds(1);
        switch (setting)
        {
            case "OrleansDispatchOptions.IntentGrace":
                services.AddStrataraOrleansCommandDispatcher(o => o.IntentGrace = TimeSpan.FromMilliseconds(10));
                break;
            case "OrleansDispatchOptions.CompletionWindow":
                services.AddStrataraOrleansCommandDispatcher(o => o.CompletionWindow = TimeSpan.Zero);
                break;
            case "OrleansDispatchOptions.CompletionBatchSize":
                services.AddStrataraOrleansCommandDispatcher(o => o.CompletionBatchSize = 0);
                break;
            case "HeavyWorkOptions.ClusterWideLimit":
                services.AddStrataraOrleansCommandDispatcher().ConfigureStrataraHeavyWork(o => o.ClusterWideLimit = 0);
                break;
            case "HeavyWorkOptions.PermitRetry":
                services.AddStrataraOrleansCommandDispatcher().ConfigureStrataraHeavyWork(o => o.PermitRetry = TimeSpan.Zero);
                break;
            case "HeavyWorkOptions.PermitLease":
                services.AddStrataraOrleansCommandDispatcher().ConfigureStrataraHeavyWork(o => o.PermitLease = TimeSpan.Zero);
                break;
            case "DurableTimerOptions.RetryPeriod":
                services.AddStrataraDurableTimers(o => o.RetryPeriod = tooShortForAReminder);
                break;
            case "DurableTimerOptions.DueTolerance":
                services.AddStrataraDurableTimers(o => o.DueTolerance = TimeSpan.FromMinutes(2));
                break;
            case "ProjectionGrainOptions.BatchSize":
                services.AddStrataraProjectionGrains(o => o.BatchSize = 0);
                break;
            case "ProjectionGrainOptions.PollInterval":
                services.AddStrataraProjectionGrains(o => o.PollInterval = TimeSpan.Zero);
                break;
            case "ProjectionGrainOptions.KeepAlivePeriod":
                services.AddStrataraProjectionGrains(o => o.KeepAlivePeriod = tooShortForAReminder);
                break;
            case "SagaGrainOptions.BatchSize":
                services.AddStrataraSagaGrains(o => o.BatchSize = 0);
                break;
            case "SagaGrainOptions.PollInterval":
                services.AddStrataraSagaGrains(o => o.PollInterval = TimeSpan.Zero);
                break;
            case "SagaGrainOptions.KeepAlivePeriod":
                services.AddStrataraSagaGrains(o => o.KeepAlivePeriod = tooShortForAReminder);
                break;
            case "CommitOrderOptions.PartitionCount":
                services.AddStrataraProjectionGrains().Configure<CommitOrderOptions>(o => o.PartitionCount = 0);
                break;
            case "SingletonWorkOptions.KeepAlivePeriod":
                services.AddStrataraSingletonWork<ProbeWork>(o => o.KeepAlivePeriod = tooShortForAReminder);
                break;
            case "OutboxDrainOptions.PollingInterval":
                services.AddStrataraSingletonWork<ProbeWork>().Configure<OutboxDrainOptions>(o => o.PollingInterval = TimeSpan.Zero);
                break;
            case "OutboxDrainOptions.BatchSize":
                services.AddStrataraSingletonWork<ProbeWork>().Configure<OutboxDrainOptions>(o => o.BatchSize = 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, "No invalid value is defined for this setting.");
        }
    }

    private sealed class ProbeWork : ISingletonWork
    {
        public string Name => "probe";

        public TimeSpan Period => TimeSpan.FromSeconds(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
