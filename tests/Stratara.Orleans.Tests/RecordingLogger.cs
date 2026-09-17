using Microsoft.Extensions.Logging;

namespace Stratara.Orleans.Tests;

/// <summary>A log entry as a <see cref="RecordingLogger"/> keeps it.</summary>
internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>A logger that keeps what it is given, for a test to assert on.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
