using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerBackgroundTasksExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerBackgroundTasksExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogQueuedHostedServiceStarted_LogsInformation()
    {
        _loggerMock.Object.LogQueuedHostedServiceStarted();

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
    public void LogQueuedHostedServiceStopped_LogsInformation()
    {
        _loggerMock.Object.LogQueuedHostedServiceStopped();

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
    public void LogJobSuccessfulExecuted_LogsDebug()
    {
        _loggerMock.Object.LogJobSuccessfulExecuted("TestJob");

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
    public void LogJobFailedExecuted_LogsError()
    {
        var exception = new InvalidOperationException("test error");

        _loggerMock.Object.LogJobFailedExecuted(exception);

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
