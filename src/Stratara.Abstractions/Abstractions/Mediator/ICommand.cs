namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Common base for any command type. Used by infrastructure (audit, outbox) to match command
/// instances regardless of whether they return a result.
/// </summary>
public interface ICommandBase;

/// <summary>
/// A command — a write operation that produces no result. Dispatched via <see cref="IMediator"/>
/// to the matching <see cref="ICommandHandler{TRequest}"/>, or via <c>ICommandOutboxDispatcher</c>
/// for asynchronous out-of-process execution.
/// </summary>
public interface ICommand : IRequest, ICommandBase;

/// <summary>
/// A command that synchronously returns a result. Used for in-process exceptions to the
/// outbox-dispatch rule — e.g. registration flows, infrastructure-destructive operations, or
/// anything that requires the caller to receive the result before the dispatch completes.
/// </summary>
/// <typeparam name="TResult">The result type produced by the command handler.</typeparam>
public interface ICommand<TResult> : IRequest<TResult>, ICommandBase;
