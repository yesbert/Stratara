using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Shared.Diagnostics.Extensions;

/// <summary>
/// Source-generated <see cref="ILogger"/> extensions for the <c>Stratara.Sagas</c> runtime
/// (saga worker lifecycle, dispatch, errors). Event-IDs are defined in <c>LogEvents.Saga</c>.
/// </summary>
public static partial class LoggerSagaExtensions
{
    [LoggerMessage(
        EventId = LogEvents.Saga.SagaWorkerStarted,
        Level = LogLevel.Information,
        Message = "Starting Saga-Worker.")]
    public static partial void LogSagaWorkerStarted(this ILogger logger);

    [LoggerMessage(
        EventId = LogEvents.Saga.SagaWorkerStopped,
        Level = LogLevel.Information,
        Message = "Stopping Saga-Worker.")]
    public static partial void LogSagaWorkerStopped(this ILogger logger);

    [LoggerMessage(
        EventId = LogEvents.Saga.EventsNotRelevantForSaga,
        Level = LogLevel.Debug,
        Message = "None of {EventCount} events ({EventTypeNames}) were relevant for {SagaName}.")]
    public static partial void LogEventsNotRelevantForSaga(this ILogger logger, int eventCount, DistinctEventTypeNames eventTypeNames, string sagaName);

    [LoggerMessage(
        EventId = LogEvents.Saga.SagaFailed,
        Level = LogLevel.Error,
        Message = "Error while processing saga {SagaName}.")]
    public static partial void LogSagaFailed(this ILogger logger, string sagaName, Exception exception);
}
