using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.HealthChecks;

/// <summary>
/// Readiness health check that reports the depth of the Stratara outbox backlog
/// (<c>outbox_entry</c>). A persistently growing backlog signals that the outbox worker is not
/// draining as fast as commands and event bundles are enqueued.
/// </summary>
/// <remarks>
/// The check always exposes the current pending count in the <see cref="HealthCheckResult.Data"/>
/// dictionary under <c>pending</c>. When <paramref name="degradedThreshold"/> /
/// <paramref name="unhealthyThreshold"/> are supplied, the pending count is compared against them to
/// escalate to <see cref="HealthStatus.Degraded"/> / <see cref="HealthStatus.Unhealthy"/>; without
/// thresholds the check is healthy whenever the store is reachable. A failed query (store unreachable)
/// reports <see cref="HealthStatus.Unhealthy"/>. Registered via <c>AddOutboxHealthCheck()</c>.
/// </remarks>
/// <param name="dbContext">The write-store DbContext that hosts the outbox table.</param>
/// <param name="degradedThreshold">
/// Pending-count threshold at or above which the check reports <see cref="HealthStatus.Degraded"/>,
/// or <see langword="null"/> to never degrade on backlog depth.
/// </param>
/// <param name="unhealthyThreshold">
/// Pending-count threshold at or above which the check reports <see cref="HealthStatus.Unhealthy"/>,
/// or <see langword="null"/> to never fail on backlog depth.
/// </param>
internal sealed class OutboxBacklogHealthCheck(
    IWriteDbContext dbContext,
    int? degradedThreshold,
    int? unhealthyThreshold) : IHealthCheck
{
    private const string PendingDataKey = "pending";

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        long pending;
        try
        {
            pending = await dbContext.Set<OutboxEntry>().LongCountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Outbox backlog probe failed.", ex);
        }

        var data = new Dictionary<string, object> { [PendingDataKey] = pending };
        var description = $"Outbox backlog: {pending} pending.";

        if (unhealthyThreshold is { } unhealthy && pending >= unhealthy)
        {
            return HealthCheckResult.Unhealthy(description, data: data);
        }

        if (degradedThreshold is { } degraded && pending >= degraded)
        {
            return HealthCheckResult.Degraded(description, data: data);
        }

        return HealthCheckResult.Healthy(description, data);
    }
}
