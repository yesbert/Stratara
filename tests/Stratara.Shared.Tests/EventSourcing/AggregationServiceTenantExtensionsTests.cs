using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class AggregationServiceTenantExtensionsTests
{
    private sealed class TestAggregate : ITenantAggregate
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }
    }

    [Fact]
    public async Task AggregateOwnedByTenantAsync_ReturnsAggregate_WhenTenantMatches()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var aggregate = new TestAggregate { Id = streamId, TenantId = tenantId };

        var service = new Mock<IAggregationService>();
        service.Setup(s => s.AggregateAsync<TestAggregate>(streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);

        var result = await service.Object.AggregateOwnedByTenantAsync<TestAggregate>(streamId, tenantId);

        Assert.Same(aggregate, result);
    }

    [Fact]
    public async Task AggregateOwnedByTenantAsync_ReturnsNull_WhenTenantMismatch()
    {
        var streamId = Guid.NewGuid();
        var aggregate = new TestAggregate { Id = streamId, TenantId = Guid.NewGuid() };
        var requestingTenantId = Guid.NewGuid();

        var service = new Mock<IAggregationService>();
        service.Setup(s => s.AggregateAsync<TestAggregate>(streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);

        var result = await service.Object.AggregateOwnedByTenantAsync<TestAggregate>(streamId, requestingTenantId);

        Assert.Null(result);
    }

    [Fact]
    public async Task AggregateOwnedByTenantAsync_ReturnsNull_WhenAggregateNotFound()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var service = new Mock<IAggregationService>();
        service.Setup(s => s.AggregateAsync<TestAggregate>(streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAggregate?)null);

        var result = await service.Object.AggregateOwnedByTenantAsync<TestAggregate>(streamId, tenantId);

        Assert.Null(result);
    }

    [Fact]
    public async Task AggregateOwnedByTenantAsync_PropagatesCancellationToken()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var service = new Mock<IAggregationService>();
        service.Setup(s => s.AggregateAsync<TestAggregate>(streamId, null, null, cts.Token))
            .ReturnsAsync(new TestAggregate { Id = streamId, TenantId = tenantId });

        await service.Object.AggregateOwnedByTenantAsync<TestAggregate>(streamId, tenantId, cts.Token);

        service.Verify(s => s.AggregateAsync<TestAggregate>(streamId, null, null, cts.Token), Times.Once);
    }
}
