using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Every registration of the execution model is idempotent in itself: called twice, it leaves the composition one
/// call leaves — the same descriptors, each as often — so no projection is woken twice, no consumer is listed twice
/// and no work is started twice.
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

    private static void Limit(HeavyWorkOptions options) => options.ClusterWideLimit = 3;

    /// <summary>
    /// Every descriptor by service, key and implementation, with how often it is registered. A <c>Configure</c> of a
    /// setting is left out: two calls with the same delegate apply the same value, which is what configuring an option
    /// means everywhere in the framework.
    /// </summary>
    private static string[] Shape(IServiceCollection services) =>
    [
        .. services
            .Where(d => !IsConfigureOf(d.ServiceType))
            .GroupBy(d => $"{d.ServiceType} [{d.ServiceKey}] -> {Implementation(d)}")
            .Select(group => $"{group.Key} x{group.Count()}")
            .Order(StringComparer.Ordinal),
    ];

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
