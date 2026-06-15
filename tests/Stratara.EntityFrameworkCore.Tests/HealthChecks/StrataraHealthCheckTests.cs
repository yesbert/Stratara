using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.HealthChecks;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.EntityFrameworkCore.Tests.HealthChecks;

/// <summary>
/// Exercises the Stratara outbox-backlog and event-store health checks against the real EF Core write
/// stack on in-memory SQLite (via <see cref="EventStoreTestHost"/>).
/// </summary>
public class StrataraHealthCheckTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("test", _ => null!, failureStatus: null, tags: null)
    };

    [Fact]
    public async Task EventStore_check_is_healthy_when_the_store_is_reachable()
    {
        await using var host = EventStoreTestHost.Create();
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IWriteDbContext>();

        var result = await new EventStoreHealthCheck(dbContext).CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Outbox_check_is_healthy_with_an_empty_backlog()
    {
        await using var host = EventStoreTestHost.Create();
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IWriteDbContext>();

        var check = new OutboxBacklogHealthCheck(dbContext, degradedThreshold: 10, unhealthyThreshold: 100);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0L, Assert.Contains("pending", result.Data));
    }

    [Theory]
    [InlineData(5, 3, 100, HealthStatus.Degraded)]
    [InlineData(5, 1, 3, HealthStatus.Unhealthy)]
    [InlineData(2, 10, 100, HealthStatus.Healthy)]
    public async Task Outbox_check_escalates_status_by_pending_count(
        int pending, int degradedThreshold, int unhealthyThreshold, HealthStatus expected)
    {
        await using var host = EventStoreTestHost.Create();

        // Seed and check through the same scope/context so the shared in-memory SQLite connection
        // is not torn down by an intermediate scope disposal.
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IWriteDbContext>();
        SeedOutbox(dbContext, pending);
        await dbContext.SaveChangesAsync();

        var check = new OutboxBacklogHealthCheck(dbContext, degradedThreshold, unhealthyThreshold);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(expected, result.Status);
        Assert.Equal((long)pending, Assert.Contains("pending", result.Data));
    }

    private static void SeedOutbox(IWriteDbContext dbContext, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            dbContext.Set<OutboxEntry>().Add(new OutboxEntry
            {
                Id = id,
                BucketId = 0,
                DataJson = "{}",
                DataTypeName = "Test.Payload",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        }
    }
}
