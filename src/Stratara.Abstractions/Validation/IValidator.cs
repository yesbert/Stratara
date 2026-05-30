namespace Stratara.Abstractions.Validation;

/// <summary>
/// Validates instances of <typeparamref name="T"/>. The framework default validation behavior
/// runs every registered <see cref="IValidator{T}"/> for a request before the handler executes.
/// </summary>
/// <remarks>
/// The contract is intentionally vendor-neutral and FluentValidation-shape-compatible: a thin
/// adapter can wrap a FluentValidation validator without losing failure detail. Implementations
/// must never return <see langword="null"/> — return <see cref="ValidationResult.Success"/> when
/// the instance is valid.
/// </remarks>
/// <typeparam name="T">The type this validator inspects.</typeparam>
public interface IValidator<in T>
{
    /// <summary>Validate <paramref name="instance"/> and return the detected failures.</summary>
    /// <param name="instance">The instance to validate.</param>
    /// <param name="cancellationToken">Token to observe while validating.</param>
    /// <returns>A <see cref="ValidationResult"/>; <see cref="ValidationResult.Success"/> when valid.</returns>
    ValueTask<ValidationResult> ValidateAsync(T instance, CancellationToken cancellationToken = default);
}
