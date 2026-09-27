using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The registrations that add to what the silo publishes or runs — the roles it takes and the works it runs once —
/// refuse once the host was built from the service collection, naming themselves, and leave the running host's
/// record as it was. The host builder makes the collection read-only when it builds; a registration made before that
/// still adds to what earlier ones registered.
/// </summary>
public sealed class RegistrationAfterBuildTests
{
    public static TheoryData<string> Registrations => [.. Registration.Keys];

    private static readonly Dictionary<string, Action<IServiceCollection>> Registration = new(StringComparer.Ordinal)
    {
        ["AddStrataraAggregateGrains"] = services => services.AddStrataraAggregateGrains(),
        ["AddStrataraProjectionGrains"] = services => services.AddStrataraProjectionGrains(),
        ["AddStrataraSagaGrains"] = services => services.AddStrataraSagaGrains(),
        ["AddStrataraDurableTimers"] = services => services.AddStrataraDurableTimers(),
        ["AddStrataraSingletonWork"] = services => services.AddStrataraSingletonWork<DurableDirectoryCheckTests.IdleWork>(),
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_registration_after_the_host_was_built_is_refused_and_names_itself(string registration)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        using var host = builder.Build();

        var refused = Assert.Throws<InvalidOperationException>(() => Registration[registration](builder.Services));

        Assert.Contains(registration, refused.Message, StringComparison.Ordinal);
        Assert.Contains("while the host is being configured", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_singleton_work_registered_after_the_host_was_built_is_not_run()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddStrataraSingletonWork<DurableDirectoryCheckTests.IdleWork>();
        using var host = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Services.AddStrataraSingletonWork<OtherWork>());

        Assert.Equal([typeof(DurableDirectoryCheckTests.IdleWork)], host.Services.GetRequiredService<SingletonWorkRegistrations>().Works.Select(work => work.WorkType));
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_registration_before_the_host_is_built_succeeds(string registration)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        Registration[registration](builder.Services);
        Registration[registration](builder.Services);

        using var host = builder.Build();
    }

    private sealed class OtherWork : Stratara.Abstractions.Singleton.ISingletonWork
    {
        public string Name => "other";

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
