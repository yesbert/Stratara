using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>Whether a timer registration waits once it is stored — the switch a test flips before the step it kills.</summary>
public sealed class TimerRegistrationHold
{
    public bool Active { get; set; }
}

/// <summary>
/// The durable timers with a pause after each registration while <see cref="TimerRegistrationHold"/> is active:
/// the timer is stored and the step that registered it does not reach its append, so a kill lands in between.
/// </summary>
public sealed class HoldingTimers(IDurableTimers inner, TimerRegistrationHold hold) : IDurableTimers
{
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);

    public static void Decorate(IServiceCollection services)
    {
        services.AddSingleton<TimerRegistrationHold>();
        services.RemoveAll<IDurableTimers>();
        services.AddSingleton<IDurableTimers>(sp => new HoldingTimers(ActivatorUtilities.CreateInstance<DurableTimers>(sp), sp.GetRequiredService<TimerRegistrationHold>()));
    }

    public async Task RegisterAsync(TimerRegistration registration, CancellationToken cancellationToken = default)
    {
        await inner.RegisterAsync(registration, cancellationToken);
        if (hold.Active)
        {
            await Task.Delay(Hold, cancellationToken);
        }
    }

    public Task CancelAsync(string ownerId, string purpose, CancellationToken cancellationToken = default) => inner.CancelAsync(ownerId, purpose, cancellationToken);

    public Task CancelAllAsync(string ownerId, CancellationToken cancellationToken = default) => inner.CancelAllAsync(ownerId, cancellationToken);

    public Task<IReadOnlyList<TimerRegistration>> ListAsync(string ownerId, CancellationToken cancellationToken = default) => inner.ListAsync(ownerId, cancellationToken);
}
