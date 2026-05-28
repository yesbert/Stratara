using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerMessagingExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerMessagingExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogMessageProcessingFailed_LogsError()
    {
        var exception = new InvalidOperationException("Processing failed");

        _loggerMock.Object.LogMessageProcessingFailed("test-topic", exception);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogMessageDeserializationFailed_LogsError()
    {
        var exception = new InvalidOperationException("Deserialization failed");

        _loggerMock.Object.LogMessageDeserializationFailed("test-topic", exception);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogCommandEnvelopeDispatchFailed_LogsWarning()
    {
        var exception = new InvalidOperationException("Dispatch failed");

        _loggerMock.Object.LogCommandEnvelopeDispatchFailed("test-topic", exception);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogEventBundleDispatchFailed_LogsWarning()
    {
        var exception = new InvalidOperationException("Bundle dispatch failed");

        _loggerMock.Object.LogEventBundleDispatchFailed("test-topic", exception);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogConcurrencyConflictRequeued_LogsInformation()
    {
        var streamId = Guid.NewGuid();

        _loggerMock.Object.LogConcurrencyConflictRequeued(streamId, "TestAggregate");

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
