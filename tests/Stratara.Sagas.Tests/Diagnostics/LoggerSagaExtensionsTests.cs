using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Diagnostics;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Sagas.Tests.Diagnostics;

public class LoggerSagaExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerSagaExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogSagaPrecedingFactMissing_LogsWarningWithItsEventId()
    {
        var exception = new PrecedingFactMissingException(Guid.NewGuid(), "Updated");

        _loggerMock.Object.LogSagaPrecedingFactMissing(exception, exception.StreamId, exception.EventTypeName, 3);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Id == LogEvents.Saga.PrecedingFactMissing),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
