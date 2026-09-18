using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Every registration of the execution model is idempotent in itself: called twice, it leaves the composition one
/// call leaves — the same descriptors, each as often, and the same singleton works with the same settings — so no
/// projection is woken twice, no consumer is listed twice and no work is started twice or configured twice.
/// </summary>
public sealed class RegistrationIdempotencyTests
{
    public static TheoryData<string> Registrations => [.. Registration.Keys];

    private static readonly Dictionary<string, Action<IServiceCollection>> Registration = new(StringComparer.Ordinal)
    {
        ["AddStrataraAggregateGrains"] = services => services.AddStrataraAggregateGrains(),
        ["AddStrataraProjectionGrains"] = services => services.AddStrataraProjectionGrains(),
        ["AddStrataraSagaGrains"] = services => services.AddStrataraSagaGrains(),
        ["AddStrataraDurableTimers"] = services => services.AddStrataraDurableTimers(),
        ["AddStrataraOrleansCommandDispatcher"] = services => services.AddStrataraOrleansCommandDispatcher(),
        ["ConfigureStrataraHeavyWork"] = services => services.ConfigureStrataraHeavyWork(Limit),
        ["AddStrataraSingletonWork"] = services => services.AddStrataraSingletonWork<IdleWork>(),
        ["AddStrataraSingletonWork(name)"] = services => services.AddStrataraSingletonWork<IdleWork>(IdleWork.WorkName),
        ["AddStrataraSingletonWork(configure)"] = services => services.AddStrataraSingletonWork<IdleWork>(FiveMinutes),
        ["AddStrataraSingletonWork(name, configure)"] = services => services.AddStrataraSingletonWork<IdleWork>(IdleWork.WorkName, FiveMinutes),
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_registration_called_twice_leaves_the_composition_one_call_leaves(string registration)
    {
        var once = new ServiceCollection();
        Registration[registration](once);
        var twice = new ServiceCollection();
        Registration[registration](twice);
        Registration[registration](twice);

        Assert.Equal(Shape(once), Shape(twice));
    }

    [Fact]
    public void The_same_heavy_work_setting_applied_twice_is_the_value_applied_once()
    {
        var services = new ServiceCollection();
        services.ConfigureStrataraHeavyWork(Limit).ConfigureStrataraHeavyWork(Limit);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(3, provider.GetRequiredService<IOptions<HeavyWorkOptions>>().Value.ClusterWideLimit);
    }

    [Fact]
    public void A_work_registered_twice_with_different_settings_leaves_the_composition_of_the_later_registration()
    {
        var once = new ServiceCollection().AddStrataraSingletonWork<IdleWork>(FiveMinutes);
        var twice = new ServiceCollection().AddStrataraSingletonWork<IdleWork>(TwoMinutes).AddStrataraSingletonWork<IdleWork>(FiveMinutes);

        Assert.Equal(Shape(once), Shape(twice));
        using var provider = twice.BuildServiceProvider();
        var settings = provider.GetRequiredService<SingletonWorkRegistrations>()
            .SettingsOf(typeof(IdleWork), provider.GetRequiredService<IOptions<SingletonWorkOptions>>().Value);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.KeepAlivePeriod);
    }

    private static void Limit(HeavyWorkOptions options) => options.ClusterWideLimit = 3;

    private static void TwoMinutes(SingletonWorkOptions options) => options.KeepAlivePeriod = TimeSpan.FromMinutes(2);

    private static void FiveMinutes(SingletonWorkOptions options) => options.KeepAlivePeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every descriptor by service, key and implementation, with how often it is registered, and every singleton work
    /// with its name and the settings callback it runs with. A <c>Configure</c> of a setting is left out: two calls with
    /// the same delegate apply the same value, which is what configuring an option means everywhere in the framework.
    /// A singleton work's callback is not such a <c>Configure</c>; it is kept with the work and compared here.
    /// </summary>
    private static string[] Shape(IServiceCollection services) =>
    [
        .. services
            .Where(d => !IsConfigureOf(d.ServiceType))
            .GroupBy(d => $"{d.ServiceType} [{d.ServiceKey}] -> {Implementation(d)}")
            .Select(group => $"{group.Key} x{group.Count()}")
            .Order(StringComparer.Ordinal),
        .. SingletonWorks(services),
    ];

    private static IEnumerable<string> SingletonWorks(IServiceCollection services)
    {
        var registrations = services
            .Select(d => d.ImplementationInstance)
            .OfType<SingletonWorkRegistrations>()
            .SingleOrDefault();
        return registrations is null
            ? []
            : registrations.Works.Select(work => $"singleton work {work.WorkType} [{work.Name}] configure {registrations.ConfigureOf(work.WorkType)?.Method.Name ?? "none"}");
    }

    private static bool IsConfigureOf(Type serviceType) =>
        serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>);

    private static string Implementation(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType?.ToString() ?? descriptor.KeyedImplementationInstance?.GetType().ToString() ?? "factory"
            : descriptor.ImplementationType?.ToString() ?? descriptor.ImplementationInstance?.GetType().ToString() ?? "factory";

    public sealed class IdleWork : ISingletonWork
    {
        public const string WorkName = "idempotency-idle";

        public string Name => WorkName;

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
