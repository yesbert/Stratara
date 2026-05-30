# Write a Validator

`Stratara.Validation` runs request validation as a **mediator pipeline behavior**: every registered
`IValidator<TRequest>` executes *before* the handler, so an invalid command never reaches your domain
logic. The contract is vendor-neutral (no FluentValidation dependency) but FluentValidation-shape-compatible,
so a thin adapter can wrap an existing FluentValidation validator later.

## The contract

`IValidator<in T>` lives in `Stratara.Abstractions.Validation` (so you can reference the contract
without the behavior package):

```csharp
using Stratara.Abstractions.Validation;

public interface IValidator<in T>
{
    ValueTask<ValidationResult> ValidateAsync(T instance, CancellationToken cancellationToken = default);
}
```

`ValidationResult` carries a (possibly empty) list of `ValidationFailure`. **Never return `null`** —
return `ValidationResult.Success` when the instance is valid.

```csharp
public sealed record ValidationFailure(
    string PropertyName,
    string ErrorMessage,
    string? ErrorCode = null,
    object? AttemptedValue = null,
    ValidationSeverity Severity = ValidationSeverity.Error);
```

## Severity — only `Error` blocks

| Severity | Behaviour |
|---|---|
| `Error` (default) | Blocks the request. The pipeline throws `StrataraValidationException`; the handler never runs. |
| `Warning` | Passes through to the handler. Logged for the operator. |
| `Info` | Passes through to the handler. Logged for the operator. |

## Write a validator

```csharp
using Stratara.Abstractions.Validation;

public sealed record RegisterUserCommand(string Email, int Age) : ICommand<Guid>;

public sealed class RegisterUserValidator : IValidator<RegisterUserCommand>
{
    public ValueTask<ValidationResult> ValidateAsync(
        RegisterUserCommand instance,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<ValidationFailure>();

        if (string.IsNullOrWhiteSpace(instance.Email) || !instance.Email.Contains('@'))
        {
            failures.Add(new ValidationFailure(
                nameof(instance.Email),
                "Email must be a non-empty address containing '@'.",
                ErrorCode: "email.invalid",
                AttemptedValue: instance.Email));
        }

        if (instance.Age < 18)
        {
            failures.Add(new ValidationFailure(
                nameof(instance.Age),
                "Age must be at least 18.",
                ErrorCode: "age.minimum",
                AttemptedValue: instance.Age));
        }

        return ValueTask.FromResult(
            failures.Count == 0 ? ValidationResult.Success : new ValidationResult(failures));
    }
}
```

The handler carries **no input guards** — by the time it runs, validation has already passed.

## Register it

Call `AddStrataraValidation()` **before** any other `AddPipelineBehavior*` registration so validation
runs as the *outermost* behavior — rejecting invalid requests before authorization, auditing, or the
handler. Pair it with `AddValidatorsFromAssemblyContaining<T>()`, which discovers and registers every
concrete `IValidator<T>` in the marker's assembly as a scoped service.

```csharp
builder.Services
    .AddMediator()
    .AddStrataraValidation()                          // behavior first (outermost)
    .AddValidatorsFromAssemblyContaining<Program>()   // discover every IValidator<T>
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();
```

## Map the failure to an HTTP response

`StrataraValidationException` is declared in `Stratara.Abstractions.Validation`, so a global exception
handler can catch it and map `Failures` to your own error model — e.g. an RFC-7807 `ProblemDetails` —
**without** referencing the `Stratara.Validation` behavior package:

```csharp
catch (StrataraValidationException ex)
{
    var errors = ex.Failures
        .GroupBy(f => f.PropertyName)
        .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());

    return Results.ValidationProblem(errors);
}
```

## See it run

[`Stratara.Sample.Validation`](../samples/06-validation.md) is a ~80-line runnable program that
dispatches a valid command, a warning-only command (still handled), and an invalid command (blocked).

## Related

- **[Write a Command Handler](write-a-command-handler.md)** — the handler the validator guards.
- **[DI Extensions Cheat Sheet](../reference/di-extensions-cheatsheet.md)** — `AddStrataraValidation()` at a glance.
