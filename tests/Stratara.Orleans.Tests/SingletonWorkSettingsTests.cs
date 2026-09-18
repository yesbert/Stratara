using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The settings given with a work's registration are that work's alone; the host's settings for singleton work as a
/// whole are every other work's, and an invalid setting of one work fails the start naming it and the work.
/// </summary>
public sealed class SingletonWorkSettingsTests
{
    [Fact]
    public async Task Two_works_registered_with_different_keep_alive_periods_keep_their_own()
    {
        var services = BaseServices()
            .AddStrataraSingletonWork<FirstWork>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(2))
            .AddStrataraSingletonWork<SecondWork>(SecondWork.WorkName, o => o.KeepAlivePeriod = TimeSpan.FromMinutes(5));
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(2), KeepAliveOf<FirstWork>(provider));
        Assert.Equal(TimeSpan.FromMinutes(5), KeepAliveOf<SecondWork>(provider));
    }

    [Fact]
    public async Task A_work_registered_twice_runs_with_the_later_registrations_settings()
    {
        var services = BaseServices()
            .AddStrataraSingletonWork<FirstWork>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(2))
            .AddStrataraSingletonWork<FirstWork>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(5));
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(5), KeepAliveOf<FirstWork>(provider));
        Assert.Single(provider.GetServices<ISingletonWork>());
    }

    [Fact]
    public async Task The_hosts_settings_for_every_work_are_the_default_of_a_work_without_its_own()
    {
        var services = BaseServices()
            .Configure<SingletonWorkOptions>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(3))
            .AddStrataraSingletonWork<FirstWork>()
            .AddStrataraSingletonWork<SecondWork>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(2));
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(3), KeepAliveOf<FirstWork>(provider));
        Assert.Equal(TimeSpan.FromMinutes(2), KeepAliveOf<SecondWork>(provider));
    }

    [Fact]
    public async Task An_invalid_setting_of_one_work_fails_the_start_naming_the_setting_and_the_work()
    {
        var services = BaseServices()
            .AddStrataraSingletonWork<FirstWork>()
            .AddStrataraSingletonWork<SecondWork>(o => o.KeepAlivePeriod = TimeSpan.FromSeconds(1));
        await using var provider = services.BuildServiceProvider();

        var failure = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("SingletonWorkOptions.KeepAlivePeriod", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondWork), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FirstWork), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_settings_of_each_work_start()
    {
        var services = BaseServices()
            .AddStrataraSingletonWork<FirstWork>(o => o.KeepAlivePeriod = TimeSpan.FromMinutes(2))
            .AddStrataraSingletonWork<SecondWork>();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static TimeSpan KeepAliveOf<TWork>(IServiceProvider provider) =>
        provider.GetRequiredService<SingletonWorkRegistrations>()
            .SettingsOf(typeof(TWork), provider.GetRequiredService<IOptions<SingletonWorkOptions>>().Value)
            .KeepAlivePeriod;

    private static IServiceCollection BaseServices() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(new Mock<IGrainFactory>().Object);

    public sealed class FirstWork : ISingletonWork
    {
        public string Name => "first";

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class SecondWork : ISingletonWork
    {
        public const string WorkName = "second";

        public string Name => WorkName;

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
