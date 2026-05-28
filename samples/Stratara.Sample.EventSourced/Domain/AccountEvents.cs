namespace Stratara.Sample.EventSourced.Domain;

public sealed record AccountOpened(Guid AccountId, string OwnerName, decimal InitialBalance, DateTimeOffset OpenedAt);

public sealed record AmountDeposited(Guid AccountId, decimal Amount, DateTimeOffset OccurredAt);

public sealed record AmountWithdrawn(Guid AccountId, decimal Amount, DateTimeOffset OccurredAt);
