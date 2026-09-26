namespace Stratara.Abstractions.Validation;

/// <summary>
/// Thrown by the validation pipeline behavior when a request fails validation with one or more
/// <see cref="ValidationSeverity.Error"/>-severity failures.
/// </summary>
/// <remarks>
/// Declared in <c>Stratara.Abstractions</c> so a consumer's global exception handler can catch it
/// and map <see cref="Failures"/> to its own error model (e.g. RFC-7807 ProblemDetails) without
/// taking a dependency on the <c>Stratara.Validation</c> behavior package.
/// </remarks>
public sealed class StrataraValidationException : Exception
{
    /// <summary>
    /// Initialise a new <see cref="StrataraValidationException"/> carrying the blocking failures.
    /// </summary>
    /// <param name="failures">The <see cref="ValidationSeverity.Error"/>-severity failures that blocked the request.</param>
    public StrataraValidationException(IReadOnlyList<ValidationFailure> failures)
        : base(Describe(failures))
    {
        _failures = failures;
    }

    /// <summary>The aggregated failures that caused the request to be rejected; empty on an exception that crossed a process boundary, where only its type, message and inner exception are carried.</summary>
    public IReadOnlyList<ValidationFailure> Failures => _failures ?? [];

    private readonly IReadOnlyList<ValidationFailure>? _failures;

    /// <summary>
    /// Names each failure in the message, so it still says what failed where <see cref="Failures"/> did not cross —
    /// the property and the message, never the attempted value.
    /// </summary>
    private static string Describe(IReadOnlyList<ValidationFailure>? failures) =>
        failures is { Count: > 0 }
            ? "One or more validation failures occurred: " +
              string.Join("; ", failures.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}")) + "."
            : "One or more validation failures occurred.";
}
