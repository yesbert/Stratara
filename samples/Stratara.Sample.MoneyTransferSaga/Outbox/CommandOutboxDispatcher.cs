using System.Text.Json;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.MoneyTransferSaga.Outbox;

public sealed class CommandOutboxDispatcher(InMemoryOutbox outbox, TimeProvider clock)
{
    public Guid Enqueue<T>(T command) where T : ICommand
    {
        var entry = new OutboxEntry(
            Id: Guid.NewGuid(),
            CommandTypeName: typeof(T).AssemblyQualifiedName ?? typeof(T).FullName!,
            PayloadJson: JsonSerializer.Serialize(command),
            EnqueuedAt: clock.GetUtcNow());

        outbox.Enqueue(entry);
        return entry.Id;
    }
}
