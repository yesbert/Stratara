using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Orleans.Diagnostics;
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

    /// <summary>The name the drain runs under, for the registration that names it.</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddStrataraSingletonWork&lt;OutboxDrainWork&gt;(OutboxDrainWork.WorkName);
    /// </code>
    /// </example>
    public const string WorkName = "outbox-drain";

    /// <inheritdoc/>
    public string Name => WorkName;

    /// <inheritdoc/>
    public TimeSpan Period => _options.PollingInterval;

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (await ResumeRecordedCommandsAsync(cancellationToken))
        {
            await DrainAsync<EventBundle, IEventBundleOutboxDispatcher>(
                (dispatcher, entries, ct) => dispatcher.EnqueueOutboxEntriesAsync(entries, ct),
                static dispatcher => dispatcher is OrleansEventBundleDispatcher { StoresBundles: false },
                cancellationToken);
            return;
        }

        await WarnOfRecordedCommandsAsync(cancellationToken);
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
    /// On the execution model the commands are resumed from the intent store, which knows which are due
    /// and how often each has been handed over; the store is not read for them as plain entries. The
    /// resume runs wherever an intent store is registered, whether or not this silo also dispatches
    /// commands, because the commands may have been recorded by another host. A resumption held back by an active
    /// replay is logged when the holding back begins and when it ends. While a pass finds a full batch due, the next
    /// follows at once, for at most a period.
    /// </summary>
    /// <returns><see langword="true"/> when an intent store is registered on this silo.</returns>
    private async Task<bool> ResumeRecordedCommandsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        if (services.GetService<ICommandIntentStore>() is not { } intents)
        {
            return false;
        }

        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        if (services.GetService<Aggregates.OrleansCommandDispatcher>() is { } dispatcher)
        {
            await WhileFullAsync(clock, ct => dispatcher.ResumeDueAsync(_options.BatchSize, ct), cancellationToken);
            return true;
        }

        var replaySuspension = services.GetRequiredService<Aggregates.ReplaySuspensionTracker>();
        var logger = services.GetRequiredService<ILogger<OutboxDrainWork>>();
        if (services.GetService<IProjectionReplayState>() is { IsReplayActive: true })
        {
            replaySuspension.HeldBack(logger);
            return true;
        }

        replaySuspension.Released(logger);
        var resumer = Aggregates.IntentResumer.Create(services, intents);
        await WhileFullAsync(clock, ct => resumer.ResumeDueAsync(_options.BatchSize, ct), cancellationToken);
        return true;
    }

    /// <summary>
    /// Runs another pass at once while the last one found a full batch due, so a backlog is not resumed one batch per
    /// period; the run ends at a short pass, at cancellation, or once it has lasted a period, and the next run continues.
    /// </summary>
    private async Task WhileFullAsync(TimeProvider clock, Func<CancellationToken, Task<Aggregates.ResumePass>> pass, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        Aggregates.ResumePass last;
        do
        {
            last = await pass(cancellationToken);
        }
        while (last.Full && !cancellationToken.IsCancellationRequested && clock.GetElapsedTime(started) < _options.PollingInterval);
    }

    /// <summary>
    /// A silo without an intent store drains stored commands to the bus; commands the execution model recorded on
    /// another host are not of that kind and would wait unseen, so their presence is logged.
    /// </summary>
    private async Task WarnOfRecordedCommandsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync(cancellationToken);
        var recorded = await unitOfWork.CreateOutboxRepository(transaction).GetManyAsync<RecordedIntent>(1, cancellationToken);
        if (recorded.Count > 0)
        {
            scope.ServiceProvider.GetRequiredService<ILogger<OutboxDrainWork>>().LogRecordedCommandsWithoutIntentStore();
        }
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
