namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Opt-in marker for long-running commands that must be processed on a dedicated worker lane so
/// they cannot starve interactive commands. Implement it on an <see cref="ICommand"/> or
/// <see cref="ICommand{TResult}"/> whose handler does heavy, slow work (bulk back-fills, crawls,
/// re-indexing, large imports).
/// </summary>
/// <remarks>
/// <para>
/// All commands share a single command topic and worker by default; a flood of slow commands can
/// occupy every consumer slot and block interactive commands for minutes. Marking a command as
/// heavy routes it to a separate durable subscription (<c>HeavyCommandTopic</c> /
/// <c>HeavyCommandSubscription</c>) that a dedicated heavy-command worker drains, keeping the
/// interactive lane permanently free.
/// </para>
/// <para>
/// The marker is inert until a consumer opts in on the dispatch side (the
/// <c>ICommandOutboxDispatcher</c> routes heavy commands to the heavy topic automatically) and
/// runs a heavy-command worker (see <c>AddHeavyCommandWorker()</c>). If a heavy command is
/// dispatched while no heavy worker is bound, the message-bus publish is rejected and the command
/// is preserved in the outbox until a heavy worker comes online — it is never lost.
/// </para>
/// <para>
/// Orthogonal to <see cref="IAggregateScopedCommand"/> (per-aggregate write serialisation): a
/// command can implement both. It is also distinct from job-progress tracking — this marker is
/// purely about transport isolation / scheduling.
/// </para>
/// </remarks>
public interface IHeavyCommand : ICommandBase;
