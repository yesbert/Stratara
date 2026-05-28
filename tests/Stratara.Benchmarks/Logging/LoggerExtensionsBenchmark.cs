using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using Stratara.Abstractions.Merging.ChangeTracking;
using Stratara.Benchmarks.Models;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Merging;
using Stratara.Shared.Merging.ChangeTracking;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Stratara.Benchmarks.Logging;

[MemoryDiagnoser]
public class LoggerExtensionsBenchmark
{
    private readonly Guid _aggregateId = Guid.NewGuid();

    // ReSharper disable once UseNameOfInsteadOfTypeOf
    private readonly string _aggregateType = typeof(Treaty).Name;

    private readonly IReadOnlyList<ChangeDetail> _changeSet = new List<ChangeDetail>
    {
        new("Field1", "OldValue1", "CurrentValue1", "NewValue1"),
        new("Field2", "OldValue2", "CurrentValue2", "NewValue2"),
        new("Field3", "OldValue3", "CurrentValue3", "NewValue3")
    };

    private readonly ILogger _consoleLogger;
    private readonly ILogger _nullLogger = NullLogger.Instance;
    private readonly ILogger _serilogLogger;

    public LoggerExtensionsBenchmark()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            });
        });

        _consoleLogger = loggerFactory.CreateLogger("ConsoleLogger");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        _serilogLogger = new SerilogLoggerFactory(Log.Logger).CreateLogger("SerilogLogger");
    }

    [Benchmark(Baseline = true)]
    public void LogChangeSetCreated_NullLogger()
    {
        _nullLogger.LogChangeSetCreated(_aggregateId, _changeSet);
    }

    [Benchmark]
    public void LogChangeSetCreated_ConsoleLogger()
    {
        _consoleLogger.LogChangeSetCreated(_aggregateId, _changeSet);
    }

    [Benchmark]
    public void LogChangeSetCreated_SerilogLogger()
    {
        _serilogLogger.LogChangeSetCreated(_aggregateId, _changeSet);
    }

    [Benchmark]
    public void LogChangeSetNotFound_NullLogger_WithType()
    {
        _nullLogger.LogChangeSetNotFound(_aggregateType, _aggregateId);
    }

    [Benchmark]
    public void LogChangeSetNotFound_SerilogLogger_WithType()
    {
        _serilogLogger.LogChangeSetNotFound(_aggregateType, _aggregateId);
    }

    [Benchmark]
    public void LogChangeSetApplied_NullLogger()
    {
        _nullLogger.LogChangeSetApplied(_aggregateId);
    }

    [Benchmark]
    public void LogChangeSetApplied_SerilogLogger()
    {
        _serilogLogger.LogChangeSetApplied(_aggregateId);
    }
}