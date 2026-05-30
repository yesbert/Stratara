# Stratara.Validation

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Vendor-neutral request validation for Stratara's CQRS pipeline. A mediator pipeline behavior
runs your `IValidator<T>` implementations before the handler and throws an aggregated
`StrataraValidationException` when validation fails — no third-party validation dependency in
the default path.

## Quick start

```csharp
// 1. Register the behavior (outermost) + discover validators.
builder.Services
    .AddStrataraValidation()
    .AddValidatorsFromAssemblyContaining<IAppMarker>();

// 2. Write a validator (the contracts live in Stratara.Abstractions.Validation).
public sealed class CreateOrderValidator : IValidator<CreateOrder>
{
    public ValueTask<ValidationResult> ValidateAsync(CreateOrder cmd, CancellationToken ct = default)
        => ValueTask.FromResult(string.IsNullOrWhiteSpace(cmd.CustomerId)
            ? new ValidationResult([new ValidationFailure(nameof(cmd.CustomerId), "Customer is required.")])
            : ValidationResult.Success);
}

// 3. Catch the failure in your global handler and map it to your error model.
catch (StrataraValidationException ex)
{
    // ex.Failures -> RFC-7807 ProblemDetails 400, your error codes, etc.
}
```

## How it works

- `AddStrataraValidation()` registers `IPipelineBehavior` for both request shapes
  (`IRequest` and `IRequest<TResult>`). Register it **before** other behaviors so validation
  runs outermost — before authorization, auditing, and the handler.
- All validators for a request run; their failures are aggregated.
- **Severity policy:** only `ValidationSeverity.Error` blocks (throws
  `StrataraValidationException`). `Warning` and `Info` failures pass through and are logged.

## Contracts

The validation contracts (`IValidator<T>`, `ValidationResult`, `ValidationFailure`,
`ValidationSeverity`, `StrataraValidationException`) live in **`Stratara.Abstractions`**
(namespace `Stratara.Abstractions.Validation`) so consumers can implement validators and catch
the exception without referencing this behavior package.

The contract shape is FluentValidation-compatible; an optional
`Stratara.Validation.FluentValidation` adapter can be shipped to plug FluentValidation
validators into the same pipeline.

## Dependencies

- `Stratara.Abstractions`
- `Stratara.Mediator`
- `Stratara.Diagnostics`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
