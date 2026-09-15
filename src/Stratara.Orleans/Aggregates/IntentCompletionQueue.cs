using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Persistence;
using Stratara.Diagnostics;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Completes intents outside the turn: a handler that finished hands its intent's id here and its
/// turn ends, and one delete per window removes every id the window collected. What the turn
/// saves is the round trip and the context it used to pay per command; the batching on top of that
/// only shows once completions arrive faster than one per window. The window widens the time an
/// intent stays recorded after it completed — a host that dies inside it resumes the intent after
/// <see cref="OrleansDispatchOptions.IntentGrace"/> and runs the handler a second time, which is
/// the case the durable-intent shape already allows one window earlier — and it is bounded by
/// <see cref="OrleansDispatchOptions.CompletionWindow"/>, far below the grace.
/// </summary>
/// <remarks>
/// A flush that fails leaves its rows in the store, where the drain finds and resumes them; nothing
/// is retried here, because the drain is the retry. A failed flush is logged and counted. On host
/// stop the queue flushes what it holds.
/// </remarks>
internal sealed class IntentCompletionQueue : IHostedService
{
    private static readonly TimeSpan LongestWindow = TimeSpan.FromSeconds(10);

    private readonly Channel<Guid> _completed = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeSpan _window;
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task> _flush;
    private readonly ILogger _logger;
    private Task? _loop;

    public IntentCompletionQueue(IServiceScopeFactory scopeFactory, IOptions<OrleansDispatchOptions> options, ILogger<IntentCompletionQueue> logger)
        : this(options.Value.CompletionWindow, options.Value.CompletionBatchSize, (ids, ct) => DeleteAsync(scopeFactory, ids, ct), logger)
    {
    }

    /// <summary>The batching alone, with what a flush does supplied — for a test of the bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The window is negative or longer than ten seconds, or the batch size is not positive.</exception>
    internal IntentCompletionQueue(TimeSpan window, int batchSize, Func<IReadOnlyList<Guid>, CancellationToken, Task> flush, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, LongestWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _window = window;
        _batchSize = batchSize;
        _flush = flush;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Records that the intent's handler completed. Returns at once while the queue runs; once the
    /// host has stopped the queue — the silo may still be finishing turns then — the record is
    /// deleted here, so no completion is lost to the order in which hosted services stop.
    /// </summary>
    public ValueTask CompleteAsync(Guid intentId) =>
        _completed.Writer.TryWrite(intentId) ? ValueTask.CompletedTask : new ValueTask(FlushAsync([intentId], CancellationToken.None));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = RunAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends the loop and flushes what it holds, within the host's shutdown budget; a flush the store
    /// does not finish in time leaves its rows to the drain like any failed one.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _completed.Writer.TryComplete();
        if (_loop is null)
        {
            return;
        }

        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _loop = null;
        }
    }

    /// <summary>A window starts with its first id and ends when the batch is full or the time is up.</summary>
    private async Task RunAsync()
    {
        var reader = _completed.Reader;
        var ids = new List<Guid>(_batchSize);
        while (await reader.WaitToReadAsync())
        {
            using var window = new CancellationTokenSource(_window);
            while (ids.Count < _batchSize)
            {
                if (reader.TryRead(out var id))
                {
                    ids.Add(id);
                    continue;
                }

                try
                {
                    if (!await reader.WaitToReadAsync(window.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await FlushAsync(ids, CancellationToken.None);
            ids.Clear();
        }

        while (reader.TryRead(out var id))
        {
            ids.Add(id);
        }

        await FlushAsync(ids, CancellationToken.None);
    }

    /// <summary>
    /// A flush that fails — for any reason, including the store's own timeout — is left to the
    /// drain: the rows are still there, and the handlers tolerate a second run. It is logged and
    /// counted, and nothing escapes, so the loop that calls this never faults.
    /// </summary>
    private async Task FlushAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await _flush(ids, cancellationToken);
            ApplicationDiagnostics.Metrics.OrleansCompletionFlushed.Add(ids.Count);
        }
        catch (Exception ex)
        {
            ApplicationDiagnostics.Metrics.OrleansCompletionFailed.Add(1);
            _logger.LogCompletionFlushFailed(ex, ids.Count);
        }
    }

    private static async Task DeleteAsync(IServiceScopeFactory scopeFactory, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        await unitOfWork.CreateOutboxRepository(transaction).DeleteManyAsync(ids, cancellationToken);
        await transaction.SaveChangesAsync(cancellationToken);
    }
}
