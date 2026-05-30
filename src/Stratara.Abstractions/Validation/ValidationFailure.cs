namespace Stratara.Abstractions.Validation;

/// <summary>
/// A single validation failure produced by an <see cref="IValidator{T}"/>. The shape mirrors
/// FluentValidation's failure record so an adapter can map one-to-one without information loss.
/// </summary>
/// <param name="PropertyName">The name of the property that failed validation, or an empty string for object-level failures.</param>
/// <param name="ErrorMessage">A human-readable description of the failure.</param>
/// <param name="ErrorCode">An optional machine-readable code a consumer can map to its own error model.</param>
/// <param name="AttemptedValue">The value that was rejected, when available.</param>
/// <param name="Severity">How the failure is treated by the pipeline. Defaults to <see cref="ValidationSeverity.Error"/>.</param>
public sealed record ValidationFailure(
    string PropertyName,
    string ErrorMessage,
    string? ErrorCode = null,
    object? AttemptedValue = null,
    ValidationSeverity Severity = ValidationSeverity.Error);
