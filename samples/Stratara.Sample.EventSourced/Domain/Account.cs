using Stratara.Abstractions.Domain;

namespace Stratara.Sample.EventSourced.Domain;

public sealed class Account : IAggregate
{
    public Guid Id { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public DateTimeOffset OpenedAt { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        OwnerName = @event.OwnerName;
        Balance = @event.InitialBalance;
        OpenedAt = @event.OpenedAt;
    }

    public void Apply(AmountDeposited @event)
    {
        Balance += @event.Amount;
    }

    public void Apply(AmountWithdrawn @event)
    {
        Balance -= @event.Amount;
    }
}
