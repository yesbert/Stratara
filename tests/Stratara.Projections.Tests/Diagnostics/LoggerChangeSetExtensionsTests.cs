using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Merging.ChangeTracking;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerChangeSetExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerChangeSetExtensionsTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    [Fact]
    public void LogChangeSetNotFound_LogsError()
    {
        _loggerMock.Object.LogChangeSetNotFound("TestAggregate", Guid.NewGuid());

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogChangeSetCreated_LogsDebug()
    {
        var changeSet = new List<ChangeDetail>
        {
            new("Name", "Old", "New", "New")
        };

        _loggerMock.Object.LogChangeSetCreated(Guid.NewGuid(), changeSet);

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
    public void LogChangeSetCreated_DefersFieldNameJoinWhenDebugDisabled()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(false);
        var changeSet = new EnumerationThrowingChangeList();

        loggerMock.Object.LogChangeSetCreated(Guid.NewGuid(), changeSet);

        loggerMock.Verify(
            l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private sealed class EnumerationThrowingChangeList : IReadOnlyList<ChangeDetail>
    {
        public ChangeDetail this[int index] => throw new InvalidOperationException("Indexer must not be invoked when Debug logging is disabled.");
        public int Count => 0;
        public IEnumerator<ChangeDetail> GetEnumerator() => throw new InvalidOperationException("Enumeration must not occur when Debug logging is disabled.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void LogChangeSetCreated_DoesNotIncludeValuesInLogMessage()
    {
        // Round-3-Audit Finding R3-Sec-005: ChangeDetail values may carry [EncryptData]-protected
        // PII (BankAccountNumber, SSN, ...). The log template must surface field names only.
        var sensitivePlaintext = "TOP-SECRET-123456-PII";
        var changeSet = new List<ChangeDetail>
        {
            new("BankAccountNumber", sensitivePlaintext, sensitivePlaintext, sensitivePlaintext),
            new("Email", sensitivePlaintext, sensitivePlaintext, sensitivePlaintext),
        };
        var recordingLogger = new MessageCapturingLogger();

        recordingLogger.LogChangeSetCreated(Guid.NewGuid(), changeSet);

        Assert.NotEmpty(recordingLogger.Messages);
        foreach (var message in recordingLogger.Messages)
        {
            Assert.DoesNotContain(sensitivePlaintext, message);
            Assert.Contains("BankAccountNumber", message);
            Assert.Contains("Email", message);
            Assert.Contains("2", message);
        }
    }

    private sealed class MessageCapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void LogChangeSetApplied_LogsDebug()
    {
        _loggerMock.Object.LogChangeSetApplied(Guid.NewGuid());

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
    public void LogNoChangesToApplied_LogsDebug()
    {
        _loggerMock.Object.LogNoChangesToApplied(Guid.NewGuid());

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
