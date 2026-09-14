using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Completes intents in batches: a handler that finished hands its intent's id here and its turn
/// ends; one delete per window removes every id the window collected. The window widens the time an
/// intent stays recorded after it completed — a host that dies inside it resumes the intent after
/// <see cref="OrleansDispatchOptions.IntentGrace"/> and runs the handler a second time, which is
/// the case the durable-intent shape already allows one window earlier — and it is bounded by
/// <see cref="OrleansDispatchOptions.CompletionWindow"/>, far below the grace.
/// </summary>
/// <remarks>
/// A flush that fails leaves its rows in the store, where the drain finds and resumes them; nothing
/// is retried here, because the drain is the retry. On host stop the queue flushes what it holds.
/// </remarks>
internal sealed class IntentCompletionQueue : IHostedService
{
    private readonly Channel<Guid> _completed = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeSpan _window;
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task> _flush;
    private Task? _loop;

    public IntentCompletionQueue(IServiceScopeFactory scopeFactory, IOptions<OrleansDispatchOptions> options)
        : this(options.Value.CompletionWindow, options.Value.CompletionBatchSize, (ids, ct) => DeleteAsync(scopeFactory, ids, ct))
    {
    }

    /// <summary>The batching alone, with what a flush does supplied — for a test of the bounds.</summary>
    internal IntentCompletionQueue(TimeSpan window, int batchSize, Func<IReadOnlyList<Guid>, CancellationToken, Task> flush)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _window = window;
        _batchSize = batchSize;
        _flush = flush;
    }

    /// <summary>Records that the intent's handler completed; returns at once.</summary>
    public void Complete(Guid intentId) => _completed.Writer.TryWrite(intentId);

    /// <summary>Deletes the intents completed so far, for a test or a caller that must see the store clean.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        while (_completed.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }

        await FlushAsync(ids, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = RunAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _completed.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop;
        }
    }

    private async Task RunAsync()
    {
        var reader = _completed.Reader;
        var ids = new List<Guid>(_batchSize);
        while (await reader.WaitToReadAsync())
        {
            // The window starts with the first id and ends when it is full or the time is up.
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
    /// A flush that fails is left to the drain: the rows are still there, and the handlers
    /// tolerate a second run. Nothing is logged, which the proof of concept's known limitation on
    /// diagnostics already records.
    /// </summary>
    private async Task FlushAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await _flush([.. ids], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    private static async Task DeleteAsync(IServiceScopeFactory scopeFactory, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService<IWriteDbContext>();
        await context.Set<OutboxEntry>().Where(entry => ids.Contains(entry.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
