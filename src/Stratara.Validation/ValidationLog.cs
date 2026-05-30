using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Validation;

/// <summary>Source-generated log messages for the validation pipeline behavior.</summary>
internal static partial class ValidationLog
{
    [LoggerMessage(
        EventId = LogEvents.Validation.NonBlockingValidationFailures,
        Level = LogLevel.Warning,
        Message = "{FailureCount} non-blocking validation failure(s) observed on {RequestType}; request still dispatched to the handler.")]
    public static partial void NonBlockingValidationFailures(ILogger logger, string requestType, int failureCount);
}
