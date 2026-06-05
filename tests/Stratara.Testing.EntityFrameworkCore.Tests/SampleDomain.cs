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
