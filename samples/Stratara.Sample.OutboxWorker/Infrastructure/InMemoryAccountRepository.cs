using System.Collections.Concurrent;
using Stratara.Sample.OutboxWorker.Domain;

namespace Stratara.Sample.OutboxWorker.Infrastructure;

public sealed class InMemoryAccountRepository
{
    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    public void Save(Account account) => _accounts[account.Id] = account;

    public Account Get(Guid id) =>
        _accounts.TryGetValue(id, out var account)
            ? account
            : throw new InvalidOperationException($"Account {id} not found.");
}
