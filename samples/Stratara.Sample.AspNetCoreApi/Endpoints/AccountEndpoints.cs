using Stratara.Sample.AspNetCoreApi.Commands;
using Stratara.Abstractions.Mediator;
using Stratara.Sample.AspNetCoreApi.Domain;
using Stratara.Sample.AspNetCoreApi.Queries;

namespace Stratara.Sample.AspNetCoreApi.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts");

        group.MapPost("/", async (OpenAccountRequest request, IMediator mediator, CancellationToken ct) =>
        {
            var accountId = await mediator.HandleAsync(
                new OpenAccountCommand(request.OwnerName, request.InitialBalance), ct);
            return Results.Created($"/accounts/{accountId}", new { id = accountId });
        });

        group.MapPost("/{id:guid}/deposits", async (Guid id, AmountRequest request, IMediator mediator, CancellationToken ct) =>
        {
            await mediator.HandleAsync(new DepositCommand(id, request.Amount), ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/withdrawals", async (Guid id, AmountRequest request, IMediator mediator, CancellationToken ct) =>
        {
            try
            {
                await mediator.HandleAsync(new WithdrawCommand(id, request.Amount), ct);
                return Results.NoContent();
            }
            catch (InsufficientBalanceException ex)
            {
                return Results.Problem(
                    title: "Insufficient balance",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("/{id:guid}/balance", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            var balance = await mediator.HandleAsync(new GetBalanceQuery(id), ct);
            return Results.Ok(new { accountId = id, balance });
        });

        return routes;
    }
}

public sealed record OpenAccountRequest(string OwnerName, decimal InitialBalance);
public sealed record AmountRequest(decimal Amount);
