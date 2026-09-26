using System.Runtime.ExceptionServices;
using Stratara.Abstractions.Validation;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// A validation failure as it crosses from one silo to another: which field failed, how and under what code, so a
/// caller on the far side — the problem-details handler of a web host — can still say which field to correct. The
/// value the field was given does not cross; a failure's message may quote it, but that is the validator's to decide.
/// </summary>
[GenerateSerializer]
[Alias("Stratara.Orleans.ValidationExceptionSurrogate")]
internal readonly record struct ValidationExceptionSurrogate(
    [property: Id(0)] ValidationFailureSurrogate[] Failures,
    [property: Id(1)] string? StackTrace);

/// <summary>One failure of a <see cref="ValidationExceptionSurrogate"/>.</summary>
[GenerateSerializer]
[Alias("Stratara.Orleans.ValidationFailureSurrogate")]
internal readonly record struct ValidationFailureSurrogate(
    [property: Id(0)] string PropertyName,
    [property: Id(1)] string ErrorMessage,
    [property: Id(2)] string? ErrorCode,
    [property: Id(3)] int Severity);

/// <summary>
/// Carries a <see cref="StrataraValidationException"/> between silos with its failures, which Orleans' own exception
/// serialization would drop: it carries an exception's type, message, stack trace and inner exceptions, and nothing
/// the exception adds.
/// </summary>
[RegisterConverter]
internal sealed class ValidationExceptionConverter : IConverter<StrataraValidationException, ValidationExceptionSurrogate>
{
    public StrataraValidationException ConvertFromSurrogate(in ValidationExceptionSurrogate surrogate)
    {
        var exception = new StrataraValidationException([.. (surrogate.Failures ?? []).Select(failure => new ValidationFailure(
            failure.PropertyName,
            failure.ErrorMessage,
            failure.ErrorCode,
            Severity: (ValidationSeverity)failure.Severity))]);

        return surrogate.StackTrace is { Length: > 0 } stackTrace
            ? (StrataraValidationException)ExceptionDispatchInfo.SetRemoteStackTrace(exception, stackTrace)
            : exception;
    }

    public ValidationExceptionSurrogate ConvertToSurrogate(in StrataraValidationException value) =>
        new([.. value.Failures.Select(failure => new ValidationFailureSurrogate(
                failure.PropertyName,
                failure.ErrorMessage,
                failure.ErrorCode,
                (int)failure.Severity))],
            value.StackTrace);
}
