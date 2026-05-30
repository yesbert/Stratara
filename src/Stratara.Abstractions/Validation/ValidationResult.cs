namespace Stratara.Abstractions.Validation;

/// <summary>
/// The outcome of validating a single instance: the (possibly empty) set of failures an
/// <see cref="IValidator{T}"/> produced.
/// </summary>
/// <param name="Errors">The failures detected. Empty when the instance is valid.</param>
public sealed record ValidationResult(IReadOnlyList<ValidationFailure> Errors)
{
    /// <summary>A shared, reusable result that represents a valid instance with no failures.</summary>
    public static readonly ValidationResult Success = new(Array.Empty<ValidationFailure>());

    /// <summary><see langword="true"/> when there are no failures of any severity.</summary>
    public bool IsValid => Errors.Count == 0;
}
