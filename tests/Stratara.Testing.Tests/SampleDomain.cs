using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.Tests;

internal sealed record AccountOpened(Guid AccountId, string Owner, decimal InitialBalance);

internal sealed record AmountDeposited(decimal Amount);

internal sealed record AmountWithdrawn(decimal Amount);

internal sealed class Account : IAggregate
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        Owner = @event.Owner;
        Balance = @event.InitialBalance;
    }

    public void Apply(AmountDeposited @event) => Balance += @event.Amount;

    public void Apply(AmountWithdrawn @event) => Balance -= @event.Amount;
}

internal sealed record ProjectRenamed(string Name);

internal sealed class Project : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public void Apply(ProjectRenamed @event) => Name = @event.Name;
}

internal sealed record EntryPosted(decimal Amount);

// Aggregate that applies the wrapped IEvent<T> form rather than the bare payload.
internal sealed class Ledger : IAggregate
{
    public Guid Id { get; set; }

    public decimal Total { get; set; }

    public long LastVersion { get; set; }

    public void Apply(IEvent<EntryPosted> @event)
    {
        Total += @event.Data.Amount;
        LastVersion = @event.Version;
    }
}
