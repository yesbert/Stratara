using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing;
using Stratara.Shared.Partitioning;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

/// <summary>
/// Every read of one stream or of its snapshot is confined to the stream's partition, which the store works out from
/// the stream id as it does when it writes, so the unique index over partition, stream and version serves it.
/// </summary>
public sealed class StreamReadPartitionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CommandRecorder _recorder = new();
    private PartitionWriteContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = new PartitionWriteContext(new DbContextOptionsBuilder<PartitionWriteContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_recorder)
            .Options);
        await _context.Database.EnsureCreatedAsync();
        _recorder.Commands.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public static TheoryData<string> StreamReads =>
    [
        nameof(EventStreamRepository.StreamExistsAsync),
        nameof(EventStreamRepository.GetFirstOrDefaultAsync),
        nameof(EventStreamRepository.GetManyAsync),
        nameof(EventStreamRepository.GetManyAsync) + "Range",
        nameof(EventStreamRepository.GetVersionOrDefaultAsync),
        nameof(SnapshotRepository.GetAsync),
        nameof(SnapshotRepository.GetAsync) + "ToVersion",
        nameof(SnapshotRepository.GetLatestVersionOrDefaultAsync),
    ];

    [Theory]
    [MemberData(nameof(StreamReads))]
    public async Task A_read_of_one_stream_conditions_on_the_streams_partition(string read)
    {
        var streamId = Guid.NewGuid();
        var events = new EventStreamRepository(_context);
        var snapshots = new SnapshotRepository(_context);
        const string aggregate = "Sample.Aggregate, Sample";

        await (read switch
        {
            nameof(EventStreamRepository.StreamExistsAsync) => (Task)events.StreamExistsAsync(streamId, TestContext.Current.CancellationToken),
            nameof(EventStreamRepository.GetFirstOrDefaultAsync) => (Task)events.GetFirstOrDefaultAsync(streamId, TestContext.Current.CancellationToken),
            nameof(EventStreamRepository.GetManyAsync) => (Task)events.GetManyAsync(streamId, cancellationToken: TestContext.Current.CancellationToken),
            nameof(EventStreamRepository.GetManyAsync) + "Range" => (Task)events.GetManyAsync(streamId, 2, 5, TestContext.Current.CancellationToken),
            nameof(EventStreamRepository.GetVersionOrDefaultAsync) => (Task)events.GetVersionOrDefaultAsync(streamId, TestContext.Current.CancellationToken),
            nameof(SnapshotRepository.GetAsync) => (Task)snapshots.GetAsync(streamId, aggregate, cancellationToken: TestContext.Current.CancellationToken),
            nameof(SnapshotRepository.GetAsync) + "ToVersion" => (Task)snapshots.GetAsync(streamId, aggregate, 3, TestContext.Current.CancellationToken),
            nameof(SnapshotRepository.GetLatestVersionOrDefaultAsync) => (Task)snapshots.GetLatestVersionOrDefaultAsync(streamId, aggregate, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(read), read, null),
        });

        var command = Assert.Single(_recorder.Commands);
        Assert.Contains("\"BucketId\" = ", command.Text, StringComparison.Ordinal);
        Assert.Contains((object)BucketCalculator.GetBucketId(streamId), command.Parameters);
    }

    private sealed record RecordedCommand(string Text, IReadOnlyList<object?> Parameters);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<RecordedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command) =>
            Commands.Add(new RecordedCommand(command.CommandText, [.. command.Parameters.Cast<DbParameter>().Select(p => p.Value)]));
    }

    private sealed class PartitionWriteContext(DbContextOptions<PartitionWriteContext> options)
        : WriteDbContext<PartitionWriteContext>(options);
}
