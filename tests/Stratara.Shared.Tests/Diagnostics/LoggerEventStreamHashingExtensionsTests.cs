using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerEventStreamHashingExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerEventStreamHashingExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogEventStreamHashWorkerStarted_LogsInformation()
    {
        _loggerMock.Object.LogEventStreamHashWorkerStarted();

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
    public void LogEventStreamHashWorkerStopped_LogsInformation()
    {
        _loggerMock.Object.LogEventStreamHashWorkerStopped();

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
    public void LogEventStreamHashWorkerOperationCanceled_LogsInformation()
    {
        _loggerMock.Object.LogEventStreamHashWorkerOperationCanceled();

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
    public void LogEventStreamHashWorkerFailed_LogsError()
    {
        var exception = new InvalidOperationException("hashing failed");

        _loggerMock.Object.LogEventStreamHashWorkerFailed(exception);

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
