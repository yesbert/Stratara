using Stratara.Sample.CqrsBasics.Infrastructure;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.CqrsBasics.Queries;

public sealed record GetBalanceQuery(Guid AccountId) : IQuery<decimal>;

public sealed class GetBalanceQueryHandler(InMemoryAccountRepository accounts)
    : IQueryHandler<GetBalanceQuery, decimal>
{
    public Task<decimal> HandleAsync(GetBalanceQuery query, CancellationToken cancellationToken)
    {
        var account = accounts.Get(query.AccountId);
        return Task.FromResult(account.Balance);
    }
}
