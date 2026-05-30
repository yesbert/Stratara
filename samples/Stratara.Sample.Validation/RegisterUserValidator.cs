using Stratara.Abstractions.Validation;

namespace Stratara.Sample.Validation;

/// <summary>
/// Validates <see cref="RegisterUserCommand"/>. Discovered and registered automatically by
/// <c>AddValidatorsFromAssemblyContaining&lt;Program&gt;()</c>, then run by the validation pipeline
/// behavior before the handler.
/// </summary>
/// <remarks>
/// Demonstrates all three severities: an <see cref="ValidationSeverity.Error"/> blocks the request
/// (the pipeline throws <see cref="StrataraValidationException"/>); a
/// <see cref="ValidationSeverity.Warning"/> passes through to the handler but is logged.
/// </remarks>
public sealed class RegisterUserValidator : IValidator<RegisterUserCommand>
{
    private const int MinimumAge = 18;
    private const int ImplausibleAge = 120;

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

        if (instance.Age < MinimumAge)
        {
            failures.Add(new ValidationFailure(
                nameof(instance.Age),
                $"Age must be at least {MinimumAge}.",
                ErrorCode: "age.minimum",
                AttemptedValue: instance.Age));
        }
        else if (instance.Age > ImplausibleAge)
        {
            failures.Add(new ValidationFailure(
                nameof(instance.Age),
                $"Age {instance.Age} is implausibly high — proceeding, but please double-check.",
                ErrorCode: "age.implausible",
                AttemptedValue: instance.Age,
                Severity: ValidationSeverity.Warning));
        }

        return ValueTask.FromResult(
            failures.Count == 0 ? ValidationResult.Success : new ValidationResult(failures));
    }
}
