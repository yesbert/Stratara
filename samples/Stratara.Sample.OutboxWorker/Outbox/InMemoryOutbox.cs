using System.Collections.Concurrent;

namespace Stratara.Sample.OutboxWorker.Outbox;

public sealed class InMemoryOutbox
{
    private readonly ConcurrentQueue<OutboxEntry> _entries = new();

    public void Enqueue(OutboxEntry entry) => _entries.Enqueue(entry);

    public bool TryDequeue(out OutboxEntry entry) => _entries.TryDequeue(out entry!);

    public int PendingCount => _entries.Count;
}
