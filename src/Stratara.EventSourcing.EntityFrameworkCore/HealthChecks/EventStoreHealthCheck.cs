using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.HealthChecks;

/// <summary>
/// Readiness health check that verifies the Stratara event store (write-side database) is reachable
/// by opening a lightweight connectivity probe against the configured provider.
/// </summary>
/// <remarks>
/// Returns <see cref="HealthStatus.Unhealthy"/> when the database cannot be reached (connection
/// refused, authentication failure, network partition) so orchestrators stop routing readiness
/// traffic to a host whose event store is offline. Registered via
/// <c>AddEventStoreHealthCheck()</c>.
/// </remarks>
/// <param name="dbContext">The write-store DbContext whose connection is probed.</param>
internal sealed class EventStoreHealthCheck(IWriteDbContext dbContext) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Event store is reachable.")
                : HealthCheckResult.Unhealthy("Event store is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Event store connectivity probe failed.", ex);
        }
    }
}
