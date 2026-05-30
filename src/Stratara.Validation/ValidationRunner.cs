using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Validation;

namespace Stratara.Validation;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for a request, aggregates the failures, and
/// enforces the severity policy: any <see cref="ValidationSeverity.Error"/> failure throws a
/// <see cref="StrataraValidationException"/>; <see cref="ValidationSeverity.Warning"/> and
/// <see cref="ValidationSeverity.Info"/> failures are logged and allowed to pass.
/// </summary>
internal static class ValidationRunner
{
    public static async ValueTask EnsureValidAsync<TRequest>(
        IEnumerable<IValidator<TRequest>> validators,
        TRequest request,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var validatorList = validators as IReadOnlyList<IValidator<TRequest>> ?? validators.ToArray();
        if (validatorList.Count == 0)
        {
            return;
        }

        List<ValidationFailure>? failures = null;
        foreach (var validator in validatorList)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            if (result.IsValid)
            {
                continue;
            }

            (failures ??= []).AddRange(result.Errors);
        }

        if (failures is null)
        {
            return;
        }

        var blocking = failures.Where(static f => f.Severity == ValidationSeverity.Error).ToArray();
        var nonBlockingCount = failures.Count - blocking.Length;
        if (nonBlockingCount > 0)
        {
            ValidationLog.NonBlockingValidationFailures(logger, typeof(TRequest).Name, nonBlockingCount);
        }

        if (blocking.Length > 0)
        {
            throw new StrataraValidationException(blocking);
        }
    }
}
