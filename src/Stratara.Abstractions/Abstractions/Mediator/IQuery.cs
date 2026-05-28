namespace Stratara.Abstractions.Mediator;

/// <summary>
/// A query that returns no result. Marker interface — must be side-effect-free.
/// </summary>
public interface IQuery : IRequest;

/// <summary>
/// A query that returns <typeparamref name="TResult"/>. Must be side-effect-free — no state
/// changes, no message-bus dispatch, no external system mutation. Implement <see cref="ICommand"/>
/// or <see cref="ICommand{TResult}"/> for any write operation.
/// </summary>
/// <typeparam name="TResult">The result type produced by the query handler.</typeparam>
public interface IQuery<TResult> : IRequest<TResult>;
