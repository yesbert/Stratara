using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Contracts.Messages;
using Stratara.Orleans.Projections;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.Singleton;

/// <summary>
/// The outbox drain as singleton work: the same two passes the bus-hosted outbox worker makes —
/// stored commands, then stored bundles, each as one bounded batch handed to its dispatcher — without
/// the distributed lock, because the grain that runs it is the only one in the cluster that does.
/// </summary>
public sealed class OutboxDrainWork(IServiceScopeFactory scopeFactory, IOptions<OutboxDrainOptions> options) : ISingletonWork
{
    private readonly OutboxDrainOptions _options = options.Value;

    /// <inheritdoc/>
    public string Name => "outbox-drain";

    /// <inheritdoc/>
    public TimeSpan Period => _options.PollingInterval;

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await DrainAsync<CommandEnvelope, ICommandOutboxDispatcher>(
            (dispatcher, entries, ct) => dispatcher.EnqueueOutboxEntriesAsync(entries, ct),
            static _ => false,
            cancellationToken);
        await DrainAsync<EventBundle, IEventBundleOutboxDispatcher>(
            (dispatcher, entries, ct) => dispatcher.EnqueueOutboxEntriesAsync(entries, ct),
            static dispatcher => dispatcher is OrleansEventBundleDispatcher { StoresBundles: false },
            cancellationToken);
    }

    /// <summary>
    /// One bounded batch of one kind, handed to its dispatcher. A dispatcher that never stores that
    /// kind is not asked, and the store is not read for it.
    /// </summary>
    private async Task DrainAsync<TMessage, TDispatcher>(
        Func<TDispatcher, IReadOnlyList<OutboxEntry>, CancellationToken, Task> dispatch,
        Func<TDispatcher, bool> storesNone,
        CancellationToken cancellationToken)
        where TDispatcher : notnull
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<TDispatcher>();
        if (storesNone(dispatcher))
        {
            return;
        }

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var repository = unitOfWork.CreateOutboxRepository(transaction);

        var entries = await repository.GetManyAsync<TMessage>(_options.BatchSize, cancellationToken);
        if (entries.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await dispatch(scope.ServiceProvider.GetRequiredService<TDispatcher>(), entries, cancellationToken);
    }
}

/// <summary>Settings for <see cref="OutboxDrainWork"/>.</summary>
public sealed class OutboxDrainOptions
{
    /// <summary>How often the drain runs.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many stored messages of each kind one run hands to its dispatcher.</summary>
    public int BatchSize { get; set; } = 100;
}
