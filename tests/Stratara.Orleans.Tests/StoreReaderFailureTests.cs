using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Diagnostics;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A read that fails is a stall — counted and logged whichever wake-up or poll started the catch-up — and a batch
/// cut short by a cancellation still records the checkpoint for the entries it applied.
/// </summary>
public sealed class StoreReaderFailureTests
{
    private const string Consumer = "View";
    private const int Partition = 2;

    [Fact]
    public async Task A_read_that_fails_counts_a_stall_logs_it_and_the_next_read_that_succeeds_clears_it()
    {
        var reader = new FlakyReader();
        var logger = new RecordingLogger();
        using var stalls = new StallCounter();
        var loop = LoopOver(reader, new EmptyCheckpoints(), logger);

        reader.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count)));

        Assert.Equal(1, stalls.Value(Consumer, Partition));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogEvents.Orleans.CatchUpFaulted, entry.EventId.Id);
        Assert.Contains(Consumer, entry.Message, StringComparison.Ordinal);

        reader.FailNext = false;
        await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        Assert.Equal(0, stalls.Value(Consumer, Partition));
    }

    [Fact]
    public async Task A_batch_cut_by_a_cancellation_still_writes_the_checkpoint_for_what_it_applied()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FlakyReader();
        var checkpoints = new EmptyCheckpoints();
        var loop = LoopOver(reader, checkpoints, new RecordingLogger());

        var applied = await loop.CatchUpAsync(
            (batch, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(batch.Entries.Count / 2);
            },
            static () => false,
            cancellation.Token);

        Assert.Equal(5, applied);
        var written = Assert.Single(checkpoints.Written);
        Assert.Equal(5, written.Position);
        Assert.False(written.Token.IsCancellationRequested, "the checkpoint for the applied entries was written with the cancelled token");
    }

    private static StoreReaderLoop LoopOver(ICommittedPositionReader reader, IProjectionCheckpointStore checkpoints, ILogger logger)
    {
        var services = new ServiceCollection().AddSingleton(reader).AddSingleton(checkpoints).BuildServiceProvider();
        return new StoreReaderLoop(services.GetRequiredService<IServiceScopeFactory>(), ResiliencePipeline.Empty, Consumer, Partition, 10, logger);
    }

    /// <summary>Ten entries per read, or a failure when told to.</summary>
    private sealed class FlakyReader : ICommittedPositionReader
    {
        public string Name => "flaky/16";

        public bool FailNext { get; set; }

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                throw new InvalidOperationException("the store is unreachable");
            }

            var entries = Enumerable.Range(1, batchSize)
                .Select(offset => new CommittedEntry(new EventStreamEntry
                {
                    StreamId = Guid.NewGuid(),
                    Version = 1,
                    EventTypeName = "Probe",
                    AggregateTypeName = "ProbeAggregate",
                    DataJson = "{}",
                    BucketId = 0,
                    TenantId = Guid.NewGuid(),
                    ActorTenantId = Guid.NewGuid(),
                    ActorUserId = Guid.NewGuid(),
                }, afterPosition + offset))
                .ToList();
            return Task.FromResult(new CommittedBatch(entries, entries[^1].Position) { HasMore = false });
        }
    }

    private sealed class EmptyCheckpoints : IProjectionCheckpointStore
    {
        public List<(long Position, CancellationToken Token)> Written { get; } = [];

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            Written.Add((position, cancellationToken));
            return Task.CompletedTask;
        }
    }

    /// <summary>Observes the stall counter for one consumer and partition.</summary>
    private sealed class StallCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<(string, int), long> _values = [];

        public StallCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName && instrument.Name == ApplicationDiagnostics.Metrics.OrleansReaderStalledName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                string? consumer = null;
                var partition = -1;
                foreach (var tag in tags)
                {
                    if (tag.Key == ApplicationDiagnostics.MetricTags.Projection)
                    {
                        consumer = tag.Value as string;
                    }
                    else if (tag.Key == ApplicationDiagnostics.MetricTags.Partition)
                    {
                        partition = tag.Value is int value ? value : -1;
                    }
                }

                lock (_values)
                {
                    var key = (consumer ?? string.Empty, partition);
                    _values[key] = _values.GetValueOrDefault(key) + measurement;
                }
            });
            _listener.Start();
        }

        public long Value(string consumer, int partition)
        {
            lock (_values)
            {
                return _values.GetValueOrDefault((consumer, partition));
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record LogEntry(EventId EventId, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(eventId, formatter(state, exception)));
    }
}
