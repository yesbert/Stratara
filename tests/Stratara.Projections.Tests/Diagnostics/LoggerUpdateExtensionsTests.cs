using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerUpdateExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerUpdateExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogUpdateAggregateDeleted_LogsWarning()
    {
        _loggerMock.Object.LogUpdateAggregateDeleted(Guid.NewGuid());

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogUpdateAggregateNull_LogsError()
    {
        _loggerMock.Object.LogUpdateAggregateNull(Guid.NewGuid());

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
