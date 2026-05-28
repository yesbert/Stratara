using System.Collections.Concurrent;
using Stratara.Sample.EventSourced.Domain;
using Stratara.Sample.EventSourced.EventStore;

namespace Stratara.Sample.EventSourced.Projections;

public sealed class AccountBalanceProjection : IProjection
{
    private readonly ConcurrentDictionary<Guid, AccountBalanceView> _views = new();

    public Task HandleAsync(object @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case AccountOpened opened:
                _views[opened.AccountId] = new AccountBalanceView(
                    opened.AccountId, opened.OwnerName, opened.InitialBalance);
                break;
            case AmountDeposited deposited:
                _views.AddOrUpdate(deposited.AccountId,
                    _ => throw NotInitialised(deposited.AccountId),
                    (_, v) => v with { Balance = v.Balance + deposited.Amount });
                break;
            case AmountWithdrawn withdrawn:
                _views.AddOrUpdate(withdrawn.AccountId,
                    _ => throw NotInitialised(withdrawn.AccountId),
                    (_, v) => v with { Balance = v.Balance - withdrawn.Amount });
                break;
        }
        return Task.CompletedTask;
    }

    public AccountBalanceView Get(Guid accountId) =>
        _views.TryGetValue(accountId, out var view)
            ? view
            : throw new InvalidOperationException($"No balance view for account {accountId}.");

    private static InvalidOperationException NotInitialised(Guid accountId) =>
        new($"AmountDeposited / AmountWithdrawn for account {accountId} arrived before AccountOpened.");
}

public sealed record AccountBalanceView(Guid AccountId, string OwnerName, decimal Balance);
