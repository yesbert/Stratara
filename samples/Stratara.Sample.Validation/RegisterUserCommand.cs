using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.Validation;

/// <summary>A command to register a user. Validated before the handler ever runs.</summary>
public sealed record RegisterUserCommand(string Email, int Age) : ICommand<Guid>;

/// <summary>
/// Handles <see cref="RegisterUserCommand"/>. By the time this runs the validation pipeline behavior
/// has already guaranteed the command is valid — the handler carries no defensive guard clauses.
/// </summary>
public sealed class RegisterUserCommandHandler : IQueryHandler<RegisterUserCommand, Guid>
{
    public Task<Guid> HandleAsync(RegisterUserCommand command, CancellationToken cancellationToken)
    {
        var userId = Guid.NewGuid();
        Console.WriteLine($"  Handler ran: registered {command.Email} (age {command.Age}) as {userId}");
        return Task.FromResult(userId);
    }
}
