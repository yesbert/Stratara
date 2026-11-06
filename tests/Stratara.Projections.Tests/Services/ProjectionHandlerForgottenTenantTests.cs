using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Diagnostics;
using Stratara.Domain;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Projections.Tests.Services;

/// <summary>
/// <c>projections</c> → the requirements on forgetting a deleted tenant: the default handler hands a
/// declaring projection the tenant-deletion facts, records the tenants it deleted, and passes a fact of such a
/// tenant over before handling it, or on a missing-prerequisite report that races the deletion — and changes
/// nothing else.
/// </summary>
public class ProjectionHandlerForgottenTenantTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();

    private readonly InMemoryForgottenTenantStore _store = new();
    private readonly Mock<ILogger<ProjectionHandler>> _logger = new();

    public ProjectionHandlerForgottenTenantTests()
    {
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    private sealed record EntryCreated;

    private sealed record EntryIndexed;

    private sealed record EntryWithdrawn(Guid OtherEntry);

    /// <summary>Keeps one row per entry and removes a tenant's rows when the tenant is deleted.</summary>
    private sealed class EntryProjection : IForgetsDeletedTenants
    {
        public Dictionary<Guid, Guid> Rows { get; } = [];

        public List<Guid> Indexed { get; } = [];

        public List<Type> Called { get; } = [];

        private Task HandleAsync(IEvent<EntryCreated> @event, CancellationToken cancellationToken)
        {
            Called.Add(typeof(EntryCreated));
            Rows[@event.StreamId] = @event.TenantId;
            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<EntryWithdrawn> @event, CancellationToken cancellationToken)
        {
            Called.Add(typeof(EntryWithdrawn));
            Rows.Remove(@event.Data.OtherEntry);
            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<EntryIndexed> @event, CancellationToken cancellationToken)
        {
            if (!Rows.ContainsKey(@event.StreamId))
            {
                throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryIndexed));
            }

            Indexed.Add(@event.StreamId);
            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<CustomerTenantsDeleted> @event, CancellationToken cancellationToken)
        {
            Called.Add(typeof(CustomerTenantsDeleted));
            foreach (var row in Rows.Where(r => @event.Data.TenantIds.Contains(r.Value)).ToList())
            {
                Rows.Remove(row.Key);
            }

            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<TenantDeleted> @event, CancellationToken cancellationToken)
        {
            Called.Add(typeof(TenantDeleted));
            foreach (var row in Rows.Where(r => r.Value == @event.StreamId).ToList())
            {
                Rows.Remove(row.Key);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Declares itself but handles no deletion fact; a fact of an entry it never saw fails.</summary>
    private sealed class IndexOnlyProjection : IForgetsDeletedTenants
    {
        private Task HandleAsync(IEvent<EntryIndexed> @event, CancellationToken cancellationToken) =>
            throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryIndexed));
    }

    /// <summary>The same projection without the declaration.</summary>
    private sealed class UndeclaredProjection : IProjection
    {
        public List<Guid> Rows { get; } = [];

        private Task HandleAsync(IEvent<EntryCreated> @event, CancellationToken cancellationToken)
        {
            Rows.Add(@event.StreamId);
            return Task.CompletedTask;
        }

        private Task HandleAsync(IEvent<EntryIndexed> @event, CancellationToken cancellationToken) =>
            throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryIndexed));

        private Task HandleAsync(CustomerTenantsDeleted @event, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Declares itself, and its own deletion handler fails.</summary>
    private sealed class FailingDeletionProjection : IForgetsDeletedTenants
    {
        private Task HandleAsync(CustomerTenantsDeleted @event, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the read store refused");
    }

    private sealed class InMemoryForgottenTenantStore : IForgottenTenantStore
    {
        public HashSet<(string Projection, Guid TenantId)> Forgotten { get; } = [];

        public Task ForgetAsync(string projection, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken = default)
        {
            foreach (var tenantId in tenantIds)
            {
                Forgotten.Add((projection, tenantId));
            }

            return Task.CompletedTask;
        }

        public Task<bool> HasForgottenAsync(string projection, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Forgotten.Contains((projection, tenantId)));

        public Task ClearAsync(string projection, CancellationToken cancellationToken = default)
        {
            Forgotten.RemoveWhere(f => f.Projection == projection);
            return Task.CompletedTask;
        }

    }

    private ProjectionHandler CreateHandler() => new(new ProjectionMethodInvoker(), _logger.Object, _store);

    private static IEvent Fact<TEvent>(TEvent data, Guid streamId, Guid tenantId) where TEvent : notnull =>
        new Event<TEvent>(Guid.CreateVersion7(), 1, data, streamId, tenantId, Guid.Empty);

    private static IEvent Cascade(params Guid[] tenantIds) =>
        Fact(new CustomerTenantsDeleted(Guid.CreateVersion7(), tenantIds, DateTimeOffset.UtcNow), Guid.CreateVersion7(), OtherTenant);

    private void VerifyPassOverLogged(Times times) =>
        _logger.Verify(l => l.Log(
            LogLevel.Information,
            It.Is<EventId>(e => e.Id == LogEvents.Projection.ProjectionForgottenTenantFactPassedOver),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    [Fact]
    public async Task A_late_fact_after_the_cascade_is_passed_over_and_the_next_event_still_runs()
    {
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();
        var nextEntry = Guid.CreateVersion7();

        await CreateHandler().ProjectAsync(projection,
        [
            Fact(new EntryCreated(), entry, Tenant),
            Cascade(Tenant),
            Fact(new EntryIndexed(), entry, Tenant),
            Fact(new EntryCreated(), nextEntry, OtherTenant)
        ]);

        Assert.DoesNotContain(entry, projection.Rows.Keys);
        Assert.Contains(nextEntry, projection.Rows.Keys);
        Assert.Contains((nameof(EntryProjection), Tenant), _store.Forgotten);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_tenant_deletion_records_the_tenant_stream()
    {
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();

        await CreateHandler().ProjectAsync(projection,
        [
            Fact(new EntryCreated(), entry, Tenant),
            Fact(new TenantDeleted(DateTimeOffset.UtcNow), Tenant, Tenant),
            Fact(new EntryIndexed(), entry, Tenant)
        ]);

        Assert.Contains((nameof(EntryProjection), Tenant), _store.Forgotten);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task Every_tenant_of_the_cascade_is_recorded()
    {
        var tenants = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };

        await CreateHandler().ProjectAsync(new EntryProjection(), [Cascade(tenants)]);

        Assert.All(tenants, tenant => Assert.Contains((nameof(EntryProjection), tenant), _store.Forgotten));
    }

    [Fact]
    public async Task A_declaring_projection_without_deletion_handlers_is_still_handed_the_deletion_facts()
    {
        var projection = new IndexOnlyProjection();
        var handler = CreateHandler();

        var relevant = handler.GetRelevantEventTypeNames(projection);
        await handler.ProjectAsync(projection, [Cascade(Tenant), Fact(new EntryIndexed(), Guid.CreateVersion7(), Tenant)]);

        Assert.Contains(typeof(CustomerTenantsDeleted).GetQualifiedTypeName(), relevant);
        Assert.Contains(typeof(TenantDeleted).GetQualifiedTypeName(), relevant);
        Assert.Contains((nameof(IndexOnlyProjection), Tenant), _store.Forgotten);
    }

    [Fact]
    public async Task A_missing_prerequisite_for_a_tenant_not_recorded_propagates()
    {
        var projection = new EntryProjection();

        await Assert.ThrowsAsync<PrecedingFactMissingException>(() => CreateHandler().ProjectAsync(projection,
            [Cascade(OtherTenant), Fact(new EntryIndexed(), Guid.CreateVersion7(), Tenant)]));

        VerifyPassOverLogged(Times.Never());
    }

    [Fact]
    public async Task A_projection_that_does_not_declare_itself_is_untouched()
    {
        var projection = new UndeclaredProjection();
        var handler = CreateHandler();

        var relevant = handler.GetRelevantEventTypeNames(projection);

        Assert.DoesNotContain(typeof(TenantDeleted).GetQualifiedTypeName(), relevant);
        await Assert.ThrowsAsync<PrecedingFactMissingException>(() => handler.ProjectAsync(projection,
            [Cascade(Tenant), Fact(new EntryIndexed(), Guid.CreateVersion7(), Tenant)]));
        Assert.Empty(_store.Forgotten);
    }

    [Fact]
    public async Task A_declaring_projection_without_a_store_fails_naming_the_registrations()
    {
        var handler = new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ProjectAsync(new EntryProjection(), [Fact(new EntryCreated(), Guid.CreateVersion7(), Tenant)]));

        Assert.Contains("AddNpgsqlReadDbContextFactory", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddStrataraForgottenTenants", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_that_fails_is_reported_as_the_missing_prerequisite()
    {
        var store = new Mock<IForgottenTenantStore>();
        store.Setup(s => s.HasForgottenAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the read store is unreachable"));
        var handler = new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object);
        var entry = Guid.CreateVersion7();

        var failure = await Assert.ThrowsAsync<PrecedingFactMissingException>(() =>
            handler.ProjectAsync(new EntryProjection(), [Fact(new EntryIndexed(), entry, Tenant)]));

        Assert.Equal(entry, failure.StreamId);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task A_deletion_whose_own_handler_fails_records_nothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateHandler().ProjectAsync(new FailingDeletionProjection(), [Cascade(Tenant)]));

        Assert.Empty(_store.Forgotten);
    }

    [Fact]
    public async Task A_row_adding_fact_after_the_tenant_deletion_leaves_no_row_and_the_projection_is_not_called()
    {
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();

        await CreateHandler().ProjectAsync(projection,
        [
            Fact(new TenantDeleted(DateTimeOffset.UtcNow), Tenant, Tenant),
            Fact(new EntryCreated(), entry, Tenant)
        ]);

        Assert.Empty(projection.Rows);
        Assert.DoesNotContain(typeof(EntryCreated), projection.Called);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_row_adding_fact_after_the_cascade_leaves_no_row_and_the_projection_is_not_called()
    {
        var projection = new EntryProjection();

        await CreateHandler().ProjectAsync(projection, [Cascade(Tenant), Fact(new EntryCreated(), Guid.CreateVersion7(), Tenant)]);

        Assert.Empty(projection.Rows);
        Assert.DoesNotContain(typeof(EntryCreated), projection.Called);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_row_adding_fact_in_a_later_call_than_the_deletion_is_passed_over()
    {
        var projection = new EntryProjection();
        var handler = CreateHandler();

        await handler.ProjectAsync(projection, [Fact(new TenantDeleted(DateTimeOffset.UtcNow), Tenant, Tenant)]);
        await handler.ProjectAsync(projection, [Fact(new EntryCreated(), Guid.CreateVersion7(), Tenant)]);

        Assert.Empty(projection.Rows);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_deletion_fact_of_a_recorded_tenant_is_still_handled_and_removes_what_is_there()
    {
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();
        projection.Rows[entry] = Tenant;
        await _store.ForgetAsync(nameof(EntryProjection), [Tenant]);
        var alsoDeleted = Guid.CreateVersion7();

        await CreateHandler().ProjectAsync(projection,
        [
            Fact(new TenantDeleted(DateTimeOffset.UtcNow), Tenant, Tenant),
            Fact(new CustomerTenantsDeleted(Guid.CreateVersion7(), [alsoDeleted], DateTimeOffset.UtcNow), Guid.CreateVersion7(), Tenant)
        ]);

        Assert.Empty(projection.Rows);
        Assert.Equal([typeof(TenantDeleted), typeof(CustomerTenantsDeleted)], projection.Called);
        Assert.Contains((nameof(EntryProjection), alsoDeleted), _store.Forgotten);
        VerifyPassOverLogged(Times.Never());
    }

    [Fact]
    public async Task A_fact_with_an_empty_tenant_is_handled_without_asking_the_store()
    {
        var store = new Mock<IForgottenTenantStore>(MockBehavior.Strict);
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();

        await new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object)
            .ProjectAsync(projection, [Fact(new EntryCreated(), entry, Guid.Empty)]);

        Assert.Contains(entry, projection.Rows.Keys);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Each_fact_asks_the_store_once_and_a_deletion_between_two_facts_is_seen_by_the_later_one()
    {
        var store = new Mock<IForgottenTenantStore>();
        var deleted = false;
        store.Setup(s => s.HasForgottenAsync(nameof(EntryProjection), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => deleted);
        var handler = new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object);
        var projection = new EntryProjection();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        await handler.ProjectAsync(projection, [Fact(new EntryCreated(), first, Tenant), Fact(new EntryCreated(), second, Tenant)]);
        deleted = true;
        await handler.ProjectAsync(projection, [Fact(new EntryCreated(), third, Tenant)]);

        Assert.Equal([first, second], projection.Rows.Keys);
        store.Verify(s => s.HasForgottenAsync(nameof(EntryProjection), Tenant, It.IsAny<CancellationToken>()), Times.Exactly(3));
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_fact_of_a_recorded_tenant_that_would_remove_another_tenants_row_is_passed_over()
    {
        var projection = new EntryProjection();
        var othersEntry = Guid.CreateVersion7();
        projection.Rows[othersEntry] = OtherTenant;

        await CreateHandler().ProjectAsync(projection,
        [
            Fact(new TenantDeleted(DateTimeOffset.UtcNow), Tenant, Tenant),
            Fact(new EntryWithdrawn(othersEntry), Guid.CreateVersion7(), Tenant)
        ]);

        Assert.Contains(othersEntry, projection.Rows.Keys);
        Assert.DoesNotContain(typeof(EntryWithdrawn), projection.Called);
        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_report_that_races_the_deletion_is_still_passed_over()
    {
        var store = new Mock<IForgottenTenantStore>();
        store.SetupSequence(s => s.HasForgottenAsync(nameof(EntryProjection), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object)
            .ProjectAsync(new EntryProjection(), [Fact(new EntryIndexed(), Guid.CreateVersion7(), Tenant)]);

        VerifyPassOverLogged(Times.Once());
    }

    [Fact]
    public async Task A_store_that_fails_before_handling_keeps_the_projection_from_being_called()
    {
        var store = new Mock<IForgottenTenantStore>();
        store.Setup(s => s.HasForgottenAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the read store is unreachable"));
        var projection = new EntryProjection();
        var entry = Guid.CreateVersion7();

        var failure = await Assert.ThrowsAsync<PrecedingFactMissingException>(() =>
            new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object)
                .ProjectAsync(projection, [Fact(new EntryCreated(), entry, Tenant)]));

        Assert.Equal(entry, failure.StreamId);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Empty(projection.Rows);
        Assert.Empty(projection.Called);
    }

    [Fact]
    public async Task A_cancellation_while_asking_the_store_stays_a_cancellation()
    {
        var store = new Mock<IForgottenTenantStore>();
        store.Setup(s => s.HasForgottenAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object)
                .ProjectAsync(new EntryProjection(), [Fact(new EntryCreated(), Guid.CreateVersion7(), Tenant)]));
    }

    [Fact]
    public async Task A_projection_that_does_not_declare_itself_applies_a_deleted_tenants_fact_without_asking_the_store()
    {
        var store = new Mock<IForgottenTenantStore>(MockBehavior.Strict);
        var projection = new UndeclaredProjection();
        var entry = Guid.CreateVersion7();

        await new ProjectionHandler(new ProjectionMethodInvoker(), _logger.Object, store.Object)
            .ProjectAsync(projection, [Cascade(Tenant), Fact(new EntryCreated(), entry, Tenant)]);

        Assert.Equal([entry], projection.Rows);
        store.VerifyNoOtherCalls();
    }
}
