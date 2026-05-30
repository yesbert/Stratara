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
        : base("One or more validation failures occurred.")
    {
        Failures = failures;
    }

    /// <summary>The aggregated failures that caused the request to be rejected.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }
}
