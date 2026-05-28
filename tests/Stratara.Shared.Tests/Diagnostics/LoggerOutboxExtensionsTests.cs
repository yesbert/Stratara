using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerOutboxExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerOutboxExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogOutboxWorkerStarted_LogsInformation()
    {
        _loggerMock.Object.LogOutboxWorkerStarted();

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
    public void LogOutboxWorkerStopped_LogsInformation()
    {
        _loggerMock.Object.LogOutboxWorkerStopped();

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
    public void LogOutboxWorkerOperationCanceled_LogsInformation()
    {
        _loggerMock.Object.LogOutboxWorkerOperationCanceled();

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
    public void LogOutboxFailed_LogsError()
    {
        var exception = new InvalidOperationException("outbox error");

        _loggerMock.Object.LogOutboxFailed(exception);

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
