namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Dispatches a request to its matching handler through any registered <see cref="IPipelineBehavior{TRequest}"/>
/// or <see cref="IPipelineBehavior{TRequest, TResult}"/> chain.
/// </summary>
/// <remarks>
/// <para>
/// Behaviors run outer-to-inner in DI registration order. Register the mediator with
/// <c>services.AddMediator()</c> and add handlers via <c>AddCommandHandlersFromAssemblyContaining&lt;T&gt;()</c> /
/// <c>AddQueryHandlersFromAssemblyContaining&lt;T&gt;()</c>.
/// </para>
/// <para>
/// For role-gated dispatch, register with <c>services.AddAuthorizingMediator&lt;TAuthorizationProvider&gt;()</c>
/// instead — the registered <see cref="IMediator"/> instance will additionally implement
/// <see cref="IAuthorizingMediator"/>, enforcing any <c>[RequireRole]</c> attributes on the request type
/// before the inner pipeline runs.
/// </para>
/// </remarks>
/// <example>
/// Dispatch a command and a query through the same mediator scope:
/// <code>
/// public sealed record CreateOrder(Guid CustomerId, decimal Amount) : ICommand;
/// public sealed record GetOrder(Guid OrderId) : IQuery&lt;Order&gt;;
///
/// await mediator.HandleAsync(new CreateOrder(customerId, 42m), cancellationToken);
/// var order = await mediator.HandleAsync(new GetOrder(orderId), cancellationToken);
/// </code>
/// </example>
public interface IMediator
{
    /// <summary>
    /// Dispatch a request that returns a result.
    /// </summary>
    /// <typeparam name="TResult">The result type produced by the handler.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Propagated to handler + pipeline behaviors.</param>
    /// <returns>The result produced by the inner pipeline.</returns>
    /// <exception cref="System.InvalidOperationException">No <see cref="IQueryHandler{TRequest,TResult}"/> registered for the concrete request type.</exception>
    Task<TResult> HandleAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatch a request that returns no result.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type — must be a reference type implementing <see cref="IRequest"/>.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Propagated to handler + pipeline behaviors.</param>
    /// <exception cref="System.InvalidOperationException">No <see cref="ICommandHandler{TRequest}"/> registered for the request type.</exception>
    Task HandleAsync<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IRequest;
}
