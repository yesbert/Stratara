namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Handler for a query or command that returns <typeparamref name="TResult"/>. Resolved by
/// <see cref="IMediator"/> via DI; registered automatically by
/// <c>AddQueryHandlersFromAssemblyContaining&lt;T&gt;()</c>.
/// </summary>
/// <typeparam name="TRequest">The concrete request type — query or command.</typeparam>
/// <typeparam name="TResult">The result type produced.</typeparam>
public interface IQueryHandler<in TRequest, TResult> where TRequest : IRequest<TResult>
{
    /// <summary>Execute the handler.</summary>
    /// <param name="request">The dispatched request.</param>
    /// <param name="cancellationToken">Propagated from the <see cref="IMediator"/> call site.</param>
    /// <returns>The result of the handler.</returns>
    Task<TResult> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Handler for a command that returns no result. Resolved by <see cref="IMediator"/> via DI;
/// registered automatically by <c>AddCommandHandlersFromAssemblyContaining&lt;T&gt;()</c>.
/// </summary>
/// <typeparam name="TRequest">The concrete command type.</typeparam>
public interface ICommandHandler<in TRequest> where TRequest : IRequest
{
    /// <summary>Execute the handler.</summary>
    /// <param name="command">The dispatched command.</param>
    /// <param name="cancellationToken">Propagated from the <see cref="IMediator"/> call site.</param>
    Task HandleAsync(TRequest command, CancellationToken cancellationToken);
}
