using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

internal sealed record AccountOpened(Guid AccountId, Guid TenantId, string Owner, decimal InitialBalance)
    : IAggregateCreationEvent;

internal sealed record AmountDeposited(decimal Amount);

internal sealed record AmountWithdrawn(decimal Amount);

internal sealed class Account : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Owner { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        TenantId = @event.TenantId;
        Owner = @event.Owner;
        Balance = @event.InitialBalance;
    }

    public void Apply(AmountDeposited @event) => Balance += @event.Amount;

    public void Apply(AmountWithdrawn @event) => Balance -= @event.Amount;
}

internal sealed record MetricsProbeCreated(Guid ProbeId, Guid TenantId) : IAggregateCreationEvent;

internal sealed record MetricsProbeTouched;

/// <summary>
/// Dedicated aggregate used only by the observability-metrics tests so their event-append
/// measurements can be isolated by the <c>aggregate.type</c> tag from events appended by other
/// concurrently-running test classes on the process-global meter.
/// </summary>
internal sealed class MetricsProbe : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public int Touches { get; set; }

    public void Apply(MetricsProbeCreated @event)
    {
        Id = @event.ProbeId;
        TenantId = @event.TenantId;
    }

    public void Apply(MetricsProbeTouched @event) => Touches++;
}
