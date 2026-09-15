using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using IRequest = Stratara.Abstractions.Mediator.IRequest;

namespace Stratara.Orleans.Aggregates;

/// <summary>One grain per aggregate, keyed by the aggregate id. Internal: commands reach it through the mediator or the dispatcher.</summary>
[Alias("Stratara.Orleans.IAggregateGrain")]
internal interface IAggregateGrain : IGrainWithGuidKey
{
    /// <summary>Runs the command in the grain's turn; the caller waits for it.</summary>
    [Alias("ExecuteAsync")]
    Task ExecuteAsync(AggregateCommandEnvelope envelope);

    /// <summary>Runs a recorded intent in the grain's turn and marks it complete afterwards.</summary>
    [Alias("ExecuteIntentAsync")]
    Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope);
}

/// <summary>One grain per recorded intent that names no aggregate, keyed by the intent id.</summary>
[Alias("Stratara.Orleans.ICommandRunnerGrain")]
internal interface ICommandRunnerGrain : IGrainWithGuidKey
{
    [Alias("ExecuteIntentAsync")]
    Task ExecuteIntentAsync(AggregateCommandEnvelope envelope);
}

/// <summary>
/// The turn that replaces the bucket lock: the runtime hands this grain one call at a time, in
/// arrival order, so two commands on one aggregate never run concurrently anywhere in the cluster.
/// Each call restores the caller's session in a fresh scope. A forwarded call invokes the command's
/// handler directly — the pipeline behaviours already ran on the caller's side, before the hand-off; a
/// recorded intent is dispatched through the mediator, whose pipeline never ran for it.
/// </summary>
internal sealed class AggregateGrain(IServiceScopeFactory scopeFactory) : Grain, IAggregateGrain
{
    public Task ExecuteAsync(AggregateCommandEnvelope envelope) => CommandExecution.RunAsync(scopeFactory, envelope, intentId: null);

    public Task ExecuteIntentAsync(Guid intentId, AggregateCommandEnvelope envelope) => CommandExecution.RunAsync(scopeFactory, envelope, intentId);
}

/// <summary>Runs an intent that names no aggregate: once, somewhere in the cluster, keyed by the intent.</summary>
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
    /// Runs the command. <paramref name="around"/> wraps the handler's run — heavy work acquires its
    /// permit there — inside the intent's lease, so a command waiting for a permit is not handed over
    /// again either.
    /// </summary>
    public static async Task RunAsync(IServiceScopeFactory scopeFactory, AggregateCommandEnvelope envelope, Guid? intentId, Func<Func<Task>, Task>? around)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        Func<Task> run = () => InvokeAsync(services, envelope, throughMediator: intentId is not null);
        if (around is not null)
        {
            var inner = run;
            run = () => around(inner);
        }

        if (intentId is not { } id)
        {
            await run();
            return;
        }

        await using (var lease = await IntentLease.StartAsync(services, id))
        {
            try
            {
                await run();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await lease.RecordFailureAsync(ex);
                throw;
            }
        }

        await services.GetRequiredService<IntentCompletionQueue>().CompleteAsync(id);
    }

    /// <summary>
    /// Rebuilds the command and runs it inside the turn. A recorded intent goes through the mediator, so
    /// validation, authorization, tenant isolation, audit and resilience run for it as they do on the bus's
    /// command worker; a synchronous forward already ran that pipeline on the caller's side and invokes the
    /// handler directly, so no behaviour runs twice.
    /// </summary>
    private static async Task InvokeAsync(IServiceProvider services, AggregateCommandEnvelope envelope, bool throughMediator)
    {
        var session = JsonSerializer.Deserialize<SessionContext>(envelope.SessionContextJson)
                      ?? throw new InvalidOperationException("The aggregate command envelope carries no session context.");
        services.GetRequiredService<ISessionContextProvider>().Set(session);

        var type = services.GetRequiredService<ITrustedTypeResolver>().Resolve(envelope.CommandTypeName);
        var command = await services.GetRequiredService<ISecureJsonSerializer>()
                          .DeserializeAsync(envelope.CommandJson, type, session.TenantId, session.ActorUserId)
                      ?? throw new InvalidOperationException($"The aggregate command envelope's command of type {type.FullName} deserialised to nothing.");

        using (AggregateTurn.Enter((command as IAggregateScopedCommand)?.AggregateId))
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
/// Marks the ambient flow as running inside the turn of one aggregate, so the forwarding behaviour lets a
/// command for that aggregate through instead of forwarding it again. A command for any other aggregate is
/// forwarded to that aggregate's activation like any other.
/// </summary>
internal static class AggregateTurn
{
    private static readonly AsyncLocal<Guid?> Current = new();

    /// <summary>Whether the ambient flow runs inside the turn of <paramref name="aggregateId"/>.</summary>
    public static bool IsInside(Guid aggregateId) => Current.Value == aggregateId;

    /// <summary>Enters the turn of <paramref name="aggregateId"/>; <see langword="null"/> marks no aggregate.</summary>
    public static IDisposable Enter(Guid? aggregateId)
    {
        var previous = Current.Value;
        Current.Value = aggregateId;
        return new Exit(previous);
    }

    private sealed class Exit(Guid? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
