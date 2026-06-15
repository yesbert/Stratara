using Microsoft.Extensions.Diagnostics.HealthChecks;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.HealthChecks;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Health-check registration extensions for the Stratara event-sourcing write store: outbox-backlog
/// depth and event-store connectivity. Both are opt-in — a consumer adds them to its existing
/// <see cref="IHealthChecksBuilder"/> so no health dependency is forced on hosts that do not want one.
/// </summary>
public static class StrataraHealthCheckExtensions
{
    /// <summary>Default registration name for the outbox-backlog check — <c>outbox</c>.</summary>
    public const string OutboxCheckName = "outbox";

    /// <summary>Default registration name for the event-store connectivity check — <c>eventstore</c>.</summary>
    public const string EventStoreCheckName = "eventstore";

    /// <summary>
    /// Default tag applied to Stratara readiness checks — <c>ready</c>. Distinguishes them from
    /// liveness (<c>live</c>) checks so a host can map them to a separate readiness endpoint.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Registers the <see cref="OutboxBacklogHealthCheck"/>, reporting the depth and age of the
    /// Stratara outbox backlog. Requires the write store to be registered (the check resolves
    /// <see cref="IWriteDbContext"/> from DI).
    /// </summary>
    /// <param name="builder">The health-checks builder to register on.</param>
    /// <param name="degradedThreshold">
    /// Pending-entry count at or above which the check reports <see cref="HealthStatus.Degraded"/>,
    /// or <see langword="null"/> to never degrade on backlog depth.
    /// </param>
    /// <param name="unhealthyThreshold">
    /// Pending-entry count at or above which the check reports <see cref="HealthStatus.Unhealthy"/>,
    /// or <see langword="null"/> to never fail on backlog depth.
    /// </param>
    /// <param name="name">The registration name. Defaults to <see cref="OutboxCheckName"/>.</param>
    /// <param name="failureStatus">
    /// The status reported when the check fails, or <see langword="null"/> to use the default
    /// (<see cref="HealthStatus.Unhealthy"/>).
    /// </param>
    /// <param name="tags">
    /// Tags applied to the registration, or <see langword="null"/> to apply <see cref="ReadyTag"/>.
    /// </param>
    /// <returns>The same <see cref="IHealthChecksBuilder"/> for chaining.</returns>
    public static IHealthChecksBuilder AddOutboxHealthCheck(
        this IHealthChecksBuilder builder,
        int? degradedThreshold = null,
        int? unhealthyThreshold = null,
        string name = OutboxCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new OutboxBacklogHealthCheck(
                sp.GetRequiredService<IWriteDbContext>(),
                degradedThreshold,
                unhealthyThreshold),
            failureStatus,
            tags ?? [ReadyTag]));
    }

    /// <summary>
    /// Registers the <see cref="EventStoreHealthCheck"/>, verifying that the event-store (write-side)
    /// database is reachable. Requires the write store to be registered (the check resolves
    /// <see cref="IWriteDbContext"/> from DI).
    /// </summary>
    /// <param name="builder">The health-checks builder to register on.</param>
    /// <param name="name">The registration name. Defaults to <see cref="EventStoreCheckName"/>.</param>
    /// <param name="failureStatus">
    /// The status reported when the check fails, or <see langword="null"/> to use the default
    /// (<see cref="HealthStatus.Unhealthy"/>).
    /// </param>
    /// <param name="tags">
    /// Tags applied to the registration, or <see langword="null"/> to apply <see cref="ReadyTag"/>.
    /// </param>
    /// <returns>The same <see cref="IHealthChecksBuilder"/> for chaining.</returns>
    public static IHealthChecksBuilder AddEventStoreHealthCheck(
        this IHealthChecksBuilder builder,
        string name = EventStoreCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new EventStoreHealthCheck(sp.GetRequiredService<IWriteDbContext>()),
            failureStatus,
            tags ?? [ReadyTag]));
    }
}
