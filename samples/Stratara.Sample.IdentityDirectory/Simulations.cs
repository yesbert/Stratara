using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.IdentityDirectory;

[RequirePermission(Permissions.SimulationsRead)]
public sealed record ListSimulationsQuery : IQuery<IReadOnlyList<string>>;

[RequirePermission(Permissions.SimulationsDelete)]
public sealed record DeleteSimulationCommand(string Name) : ICommand<bool>;

public static class Permissions
{
    public const string SimulationsRead = "sims.read";
    public const string SimulationsDelete = "sims.delete";
}

public sealed class ListSimulationsQueryHandler : IQueryHandler<ListSimulationsQuery, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> HandleAsync(ListSimulationsQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["baseline-2026", "stress-test-q3"]);
}

public sealed class DeleteSimulationCommandHandler : IQueryHandler<DeleteSimulationCommand, bool>
{
    public Task<bool> HandleAsync(DeleteSimulationCommand command, CancellationToken cancellationToken)
    {
        Console.WriteLine($"    Handler ran: deleted '{command.Name}'.");
        return Task.FromResult(true);
    }
}
