using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using IRequest = Stratara.Abstractions.Mediator.IRequest;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Aggregates;

/// <summary>One grain per aggregate, keyed by the aggregate id. Internal: commands reach it through the mediator or the dispatcher.</summary>
[Alias("Stratara.Orleans.IAggregateGrain")]
internal interface IAggregateGrain : IGrainWithGuidKey
{
    /// <summary>Accepts the command into the aggregate's order and completes once it has run; the caller waits for it.</summary>
    [AlwaysInterleave]
    [Alias("ExecuteAsync")]
    Task ExecuteAsync(AggregateCommandEnvelope envelope);

    /// <summary>
    /// Accepts a recorded intent into the aggregate's order and returns once its hand-over is renewed; it runs after
    /// every command accepted before it, and is marked complete once it has. An intent the activation already holds
    /// is not accepted twice.
    /// </summary>
    [AlwaysInterleave]
    [Alias("AcceptIntentAsync")]
    Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope);

    /// <summary>
    /// Runs the accepted commands one after another. The grain calls it on itself, one-way: the call carries no
    /// response and therefore no response timeout, so an order that takes longer than the runtime's response
    /// timeout runs to its end instead of being abandoned as a call that was never delivered.
    /// </summary>
    [OneWay]
    [Alias("RunAcceptedAsync")]
    Task RunAcceptedAsync();
}

/// <summary>One grain per recorded intent that names no aggregate, keyed by the intent id.</summary>
[Alias("Stratara.Orleans.ICommandRunnerGrain")]
internal interface ICommandRunnerGrain : IGrainWithGuidKey
{
    [Alias("ExecuteIntentAsync")]
    Task ExecuteIntentAsync(AggregateCommandEnvelope envelope);
}

/// <summary>
/// The order that replaces the bucket lock. A command reaches the grain through a call that interleaves with
/// whatever the grain is running and only accepts it: the command joins the aggregate's queue and the call returns
/// — for a recorded intent once its hand-over is renewed, for a forwarded command once it has run. The grain runs
/// the queue in a turn of its own, one command at a time, in the order the commands were accepted, so two commands
/// on one aggregate never run concurrently anywhere in the cluster, and a handler that dispatches to its own
/// aggregate is accepted behind itself instead of waiting for itself.
/// </summary>
/// <remarks>
/// Each command runs in a scope of its own with the caller's session restored. A forwarded call invokes the
/// command's handler directly — the pipeline behaviours already ran on the caller's side, before the hand-off; a
/// recorded intent is dispatched through the mediator, whose pipeline never ran for it. A recorded intent's lease
/// starts when it is accepted, so an intent waiting behind a long handler is not handed over again, and an intent the
/// drain hands over while the activation still holds it is not queued a second time. The queue lives in the
/// activation: an activation that ends before running what it accepted fails the forwarded commands back to their
/// callers and stops renewing the recorded intents, which the drain resumes after the grace.
/// </remarks>
[CommandsRolePlacementFilter]
internal sealed class AggregateGrain(IServiceScopeFactory scopeFactory) : Grain, IAggregateGrain
{
    private readonly Queue<Accepted> _accepted = new();
    private readonly HashSet<Guid> _heldIntents = [];
    private bool _running;

    public Task ExecuteAsync(AggregateCommandEnvelope envelope)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Accept(new Accepted(envelope, completion, Intent: null, RequestContext.Get(AggregateTurn.RequestContextKey) as Guid[]));
        return completion.Task;
    }

    /// <summary>The place in the queue is taken before the first await, so accepted intents keep the order their calls arrived in.</summary>
    public async Task AcceptIntentAsync(Guid intentId, AggregateCommandEnvelope envelope)
    {
        if (!_heldIntents.Add(intentId))
        {
            return;
        }

        var scope = scopeFactory.CreateScope();
        var intent = new AcceptedIntent(intentId, scope, IntentLease.StartAsync(scope.ServiceProvider, intentId));
        Accept(new Accepted(envelope, Completion: null, intent, CallerChain: null));
        await intent.Lease;
    }

    /// <summary>
    /// Runs until the queue is empty. A recorded intent's failure is recorded with it and resumed by the drain; a
    /// forwarded command's failure goes back to its caller. Neither stops the commands behind it.
    /// </summary>
    public async Task RunAcceptedAsync()
    {
        try
        {
            while (_accepted.TryDequeue(out var next))
            {
                try
                {
                    if (next.Intent is { } intent)
                    {
                        await RunIntentAsync(next.Envelope, intent);
                    }
                    else
                    {
                        await CommandExecution.RunAsync(scopeFactory, next.Envelope, intentId: null, around: null, next.CallerChain);
                        next.Completion?.TrySetResult();
                    }
                }
                catch (Exception ex)
                {
                    next.Completion?.TrySetException(ex);
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await AbandonAsync();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>Queues the command and, unless the queue is already being run, asks the grain to run it.</summary>
    private void Accept(Accepted accepted)
    {
        _accepted.Enqueue(accepted);
        if (_running)
        {
            return;
        }

        _running = true;
        RequestContext.Remove(AggregateTurn.RequestContextKey);
        this.AsReference<IAggregateGrain>().RunAcceptedAsync().ContinueWith(
            static (_, state) => ((AggregateGrain)state!).OnRunNotDeliveredAsync().Ignore(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Current);
    }

    private async Task RunIntentAsync(AggregateCommandEnvelope envelope, AcceptedIntent intent)
    {
        try
        {
            await CommandExecution.RunIntentAsync(intent.Scope.ServiceProvider, envelope, intent.Id, await intent.Lease, around: null, this.GetPrimaryKey());
        }
        finally
        {
            Release(intent);
        }
    }

    /// <summary>
    /// The one-way call that runs the queue could not be sent — the activation is on its way out — so give up what it
    /// would have run, and let the next command ask again.
    /// </summary>
    private Task OnRunNotDeliveredAsync()
    {
        _running = false;
        return AbandonAsync();
    }

    /// <summary>
    /// Fails every forwarded command still queued back to its caller, and stops renewing every recorded intent still
    /// queued, so the drain resumes it after the grace.
    /// </summary>
    private async Task AbandonAsync()
    {
        while (_accepted.TryDequeue(out var abandoned))
        {
            abandoned.Completion?.TrySetException(new InvalidOperationException("The aggregate's activation ended before the command ran; dispatch it again."));
            if (abandoned.Intent is not { } intent)
            {
                continue;
            }

            try
            {
                await (await intent.Lease).DisposeAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
            finally
            {
                Release(intent);
            }
        }
    }

    private void Release(AcceptedIntent intent)
    {
        intent.Scope.Dispose();
        _heldIntents.Remove(intent.Id);
    }

    private sealed record Accepted(AggregateCommandEnvelope Envelope, TaskCompletionSource? Completion, AcceptedIntent? Intent, Guid[]? CallerChain);

    private sealed record AcceptedIntent(Guid Id, IServiceScope Scope, Task<IntentLease> Lease);
}

/// <summary>Runs an intent that names no aggregate: once, somewhere in the cluster, keyed by the intent.</summary>
[CommandsRolePlacementFilter]
internal sealed class CommandRunnerGrain(IServiceScopeFactory scopeFactory) : Grain, ICommandRunnerGrain
{
    public Task ExecuteIntentAsync(AggregateCommandEnvelope envelope) => CommandExecution.RunAsync(scopeFactory, envelope, this.GetPrimaryKey());
}

/// <summary>
/// What every grain-side execution does: restore the session, rebuild the command, invoke its
/// handler inside the turn, and — for a recorded intent — renew its hand-over while it runs, record the
/// failure of an attempt that throws, and hand the record to the completion queue once the handler has
/// completed, so a crash before that point leaves the record for the drain to resume.
/// </summary>
internal static class CommandExecution
{
    private static readonly ConcurrentDictionary<Type, IHandlerInvoker> Invokers = new();

    public static Task RunAsync(IServiceScopeFactory scopeFactory, AggregateCommandEnvelope envelope, Guid? intentId) =>
        RunAsync(scopeFactory, envelope, intentId, around: null);

    /// <summary>
    /// Runs the command in a scope of its own. <paramref name="around"/> wraps the handler's run — heavy work
    /// acquires its permit there — inside the intent's lease, so a command waiting for a permit is not handed over
    /// again either.
    /// </summary>
    public static async Task RunAsync(IServiceScopeFactory scopeFactory, AggregateCommandEnvelope envelope, Guid? intentId, Func<Func<Task>, Task>? around, IReadOnlyList<Guid>? callerChain = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        if (intentId is not { } id)
        {
            await Wrap(() => InvokeAsync(services, envelope, throughMediator: false, callerChain), around)();
            return;
        }

        await RunIntentAsync(services, envelope, id, await IntentLease.StartAsync(services, id), around);
    }

    /// <summary>
    /// Runs a recorded intent under a lease that is already renewing its hand-over, and takes the lease over: it ends
    /// the lease after the handler, records the failure of an attempt that throws, and hands the record to the
    /// completion queue once the handler has completed.
    /// </summary>
    public static async Task RunIntentAsync(IServiceProvider services, AggregateCommandEnvelope envelope, Guid intentId, IntentLease lease, Func<Func<Task>, Task>? around = null, Guid? aggregateId = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        await using (lease)
        {
            try
            {
                await Wrap(() => InvokeAsync(services, envelope, throughMediator: true), around)();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await lease.RecordFailureAsync(ex, envelope.CommandTypeName, aggregateId);
                throw;
            }
        }

        await services.GetRequiredService<IntentCompletionQueue>().CompleteAsync(intentId);
    }

    private static Func<Task> Wrap(Func<Task> run, Func<Func<Task>, Task>? around) =>
        around is null ? run : () => around(run);

    /// <summary>
    /// Rebuilds the command and runs it inside the turn. A recorded intent goes through the mediator, so
    /// validation, authorization, tenant isolation, audit and resilience run for it as they do on the bus's
    /// command worker; a synchronous forward already ran that pipeline on the caller's side and invokes the
    /// handler directly, so no behaviour runs twice.
    /// </summary>
    private static async Task InvokeAsync(IServiceProvider services, AggregateCommandEnvelope envelope, bool throughMediator, IReadOnlyList<Guid>? callerChain = null)
    {
        var session = JsonSerializer.Deserialize<SessionContext>(envelope.SessionContextJson)
                      ?? throw new InvalidOperationException("The aggregate command envelope carries no session context.");
        services.GetRequiredService<ISessionContextProvider>().Set(session);

        var type = services.GetRequiredService<ITrustedTypeResolver>().Resolve(envelope.CommandTypeName);
        var command = await services.GetRequiredService<ISecureJsonSerializer>()
                          .DeserializeAsync(envelope.CommandJson, type, session.TenantId, session.ActorUserId)
                      ?? throw new InvalidOperationException($"The aggregate command envelope's command of type {type.FullName} deserialised to nothing.");

        using (AggregateTurn.Enter((command as IAggregateScopedCommand)?.AggregateId, callerChain))
        {
            var invoker = Invokers.GetOrAdd(type, BuildInvoker);
            await (throughMediator
                ? invoker.DispatchAsync(services, command, CancellationToken.None)
                : invoker.InvokeHandlerAsync(services, command, CancellationToken.None));
        }
    }

    private static IHandlerInvoker BuildInvoker(Type commandType)
    {
        if (!commandType.IsClass || !typeof(IRequest).IsAssignableFrom(commandType))
        {
            throw new InvalidOperationException($"{commandType.FullName} is not a command class the mediator can dispatch.");
        }

        return (IHandlerInvoker)Activator.CreateInstance(typeof(HandlerInvoker<>).MakeGenericType(commandType))!;
    }

    private interface IHandlerInvoker
    {
        Task DispatchAsync(IServiceProvider services, object command, CancellationToken cancellationToken);

        Task InvokeHandlerAsync(IServiceProvider services, object command, CancellationToken cancellationToken);
    }

    private sealed class HandlerInvoker<TCommand> : IHandlerInvoker where TCommand : class, IRequest
    {
        public Task DispatchAsync(IServiceProvider services, object command, CancellationToken cancellationToken) =>
            services.GetRequiredService<IMediator>().HandleAsync((TCommand)command, cancellationToken);

        public Task InvokeHandlerAsync(IServiceProvider services, object command, CancellationToken cancellationToken)
        {
            var handler = services.GetService<ICommandHandler<TCommand>>()
                          ?? throw new InvalidOperationException($"Handler for '{typeof(TCommand).Name}' not found on the silo.");
            return handler.HandleAsync((TCommand)command, cancellationToken);
        }
    }
}

/// <summary>
/// Marks the ambient flow as running inside the turn of one aggregate — and, behind it, the turns it was sent
/// from — so the forwarding behaviour lets a command for the innermost aggregate through instead of forwarding it
/// again, and refuses a command for an aggregate further out in the chain, whose turn is waiting on this one.
/// A command for any other aggregate is forwarded to that aggregate's activation like any other. The chain
/// travels with a forwarded call in the request context; a recorded command starts a chain of its own, because
/// nobody waits for it.
/// </summary>
internal static class AggregateTurn
{
    /// <summary>The request-context key a forwarded call carries the sender's chain under.</summary>
    public const string RequestContextKey = "stratara.aggregate-chain";

    private static readonly AsyncLocal<Guid[]?> Current = new();

    /// <summary>The turns the ambient flow is inside, outermost first; empty outside any turn.</summary>
    public static IReadOnlyList<Guid> Chain => Current.Value ?? [];

    /// <summary>Whether the ambient flow runs inside the turn of <paramref name="aggregateId"/> — the innermost one.</summary>
    public static bool IsInside(Guid aggregateId) => Current.Value is { Length: > 0 } chain && chain[^1] == aggregateId;

    /// <summary>Whether a turn of <paramref name="aggregateId"/> further out in the chain is waiting on the ambient flow.</summary>
    public static bool Encloses(Guid aggregateId) => Current.Value is { Length: > 1 } chain && Array.IndexOf(chain, aggregateId, 0, chain.Length - 1) >= 0;

    /// <summary>The refusal a send that would close a cycle carries.</summary>
    public static string CycleMessage(Guid target)
    {
        var chain = Chain;
        var sender = chain.Count > 0 ? chain[^1].ToString() : "no aggregate";
        return $"Aggregate {sender} sent a command to aggregate {target}, whose turn is waiting on this one (chain: {string.Join(" -> ", chain)} -> {target}). " +
               "Sends between aggregates must not form a cycle: the receiving aggregate would wait for the sending one, which is waiting for it. " +
               "Send the command from outside the turn — through the outbox dispatcher, which records it and hands it over without waiting — or restructure the handlers so the sends form a directed acyclic graph.";
    }

    /// <summary>The ambient chain as a forwarded call carries it, or <see langword="null"/> outside any turn.</summary>
    public static Guid[]? Carry() => Current.Value is { Length: > 0 } chain ? chain : null;

    /// <summary>
    /// Enters the turn of <paramref name="aggregateId"/> behind <paramref name="callerChain"/> — the chain a forwarded
    /// call arrived with — or behind the ambient chain where none was carried; <see langword="null"/> marks no aggregate
    /// and enters nothing new.
    /// </summary>
    public static IDisposable Enter(Guid? aggregateId, IReadOnlyList<Guid>? callerChain = null)
    {
        var previous = Current.Value;
        var outer = callerChain ?? (IReadOnlyList<Guid>?)previous ?? [];
        Current.Value = aggregateId is { } id ? [.. outer, id] : outer.Count == 0 ? null : [.. outer];
        return new Exit(previous);
    }

    private sealed class Exit(Guid[]? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
