namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Marker interface for a request that returns a result of type <typeparamref name="TResult"/>.
/// </summary>
/// <remarks>
/// Implemented by <see cref="IQuery{TResult}"/> and <see cref="ICommand{TResult}"/>. The generic
/// argument is used by <see cref="IMediator"/> to resolve the matching <see cref="IQueryHandler{TRequest,TResult}"/>
/// — it is not consumed inside this interface itself, so it triggers an unused-type-parameter
/// analyzer warning that the framework suppresses by design.
/// </remarks>
/// <typeparam name="TResult">The result type the handler produces.</typeparam>
public interface IRequest<TResult>; // NOSONAR — marker interface: TResult is used by the framework infrastructure to resolve handlers via generic type constraints

/// <summary>
/// Marker interface for a request that returns no result.
/// </summary>
/// <remarks>
/// Implemented by <see cref="IQuery"/> and <see cref="ICommand"/>. Routed by <see cref="IMediator"/>
/// to the matching <see cref="ICommandHandler{TRequest}"/>.
/// </remarks>
public interface IRequest;
