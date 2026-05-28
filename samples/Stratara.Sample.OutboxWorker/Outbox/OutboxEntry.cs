namespace Stratara.Sample.OutboxWorker.Outbox;

public sealed record OutboxEntry(Guid Id, string CommandTypeName, string PayloadJson, DateTimeOffset EnqueuedAt);
