namespace Stratara.Sample.MoneyTransferSaga.Outbox;

public sealed record OutboxEntry(Guid Id, string CommandTypeName, string PayloadJson, DateTimeOffset EnqueuedAt);
