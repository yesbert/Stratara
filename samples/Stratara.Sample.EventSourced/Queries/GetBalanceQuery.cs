using Stratara.Sample.EventSourced.Projections;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.EventSourced.Queries;

public sealed record GetBalanceQuery(Guid AccountId) : IQuery<AccountBalanceView>;

public sealed class GetBalanceQueryHandler(AccountBalanceProjection projection)
    : IQueryHandler<GetBalanceQuery, AccountBalanceView>
{
    public Task<AccountBalanceView> HandleAsync(GetBalanceQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(projection.Get(query.AccountId));
}
