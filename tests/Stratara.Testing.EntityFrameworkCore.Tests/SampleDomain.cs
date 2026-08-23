using Stratara.Abstractions.Security;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

internal sealed record AccountOpened(Guid AccountId, Guid TenantId, string Owner, decimal InitialBalance)
    : IAggregateCreationEvent;

internal sealed record AmountDeposited(decimal Amount);

internal sealed record AmountWithdrawn(decimal Amount);

/// <summary>Carries an encrypted field, so an append exercises the resolved subject's key scope.</summary>
internal sealed record AccountNoteAdded([property: EncryptData] string Note);

internal sealed class Account : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Owner { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public string Note { get; set; } = string.Empty;

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        TenantId = @event.TenantId;
        Owner = @event.Owner;
        Balance = @event.InitialBalance;
    }

    public void Apply(AmountDeposited @event) => Balance += @event.Amount;

    public void Apply(AmountWithdrawn @event) => Balance -= @event.Amount;

    public void Apply(AccountNoteAdded @event) => Note = @event.Note;
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
