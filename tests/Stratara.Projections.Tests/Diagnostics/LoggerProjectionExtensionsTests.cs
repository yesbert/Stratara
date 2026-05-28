using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerProjectionExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerProjectionExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogProjectionWorkerStarted_LogsInformation()
    {
        _loggerMock.Object.LogProjectionWorkerStarted();

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogProjectionWorkerStopped_LogsInformation()
    {
        _loggerMock.Object.LogProjectionWorkerStopped();

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogEventsNotRelevantForProjection_LogsDebug()
    {
        _loggerMock.Object.LogEventsNotRelevantForProjection(0, new DistinctEventTypeNames(Array.Empty<IEvent>()), "TestProjection");

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogProjectionFailed_LogsError()
    {
        var exception = new InvalidOperationException("Projection failed");

        _loggerMock.Object.LogProjectionFailed(exception, "TestProjection");

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
