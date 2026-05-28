namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Pipeline behavior invoked around each <see cref="IMediator"/> dispatch of an <see cref="IRequest"/>
/// (commands without a result). Behaviors form a chain: each calls <c>next</c> to
/// invoke the next behavior or the handler at the chain's end.
/// </summary>
/// <remarks>
/// Behaviors run in DI registration order — the first registered behavior is the outermost
/// wrapper, the handler is invoked innermost. A behavior may short-circuit the pipeline by
/// returning without awaiting <c>next</c>. Register with
/// <c>services.AddPipelineBehavior(typeof(MyBehavior&lt;&gt;))</c>.
/// </remarks>
/// <typeparam name="TRequest">The concrete request type the behavior observes.</typeparam>
public interface IPipelineBehavior<in TRequest> where TRequest : IRequest
{
    /// <summary>Run the behavior around the rest of the pipeline.</summary>
    /// <param name="request">The dispatched request instance.</param>
    /// <param name="next">Delegate invoking the next behavior or the final handler.</param>
    /// <param name="cancellationToken">Propagated from the <see cref="IMediator"/> call site.</param>
    Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken);
}

/// <summary>
/// Pipeline behavior invoked around each <see cref="IMediator"/> dispatch of an
/// <see cref="IRequest{TResult}"/>. Behaviors form a chain: each calls <c>next</c>
/// to invoke the next behavior or the handler at the chain's end.
/// </summary>
/// <remarks>
/// Behaviors run in DI registration order — the first registered behavior is the outermost
/// wrapper, the handler is invoked innermost. A behavior may short-circuit the pipeline by
/// returning without awaiting <c>next</c>, or transform the result returned from
/// the inner pipeline. Register with
/// <c>services.AddPipelineBehaviorWithResult(typeof(MyBehavior&lt;,&gt;))</c>.
/// </remarks>
/// <typeparam name="TRequest">The concrete request type the behavior observes.</typeparam>
/// <typeparam name="TResult">The result type produced by the handler at the chain's end.</typeparam>
public interface IPipelineBehavior<in TRequest, TResult> where TRequest : IRequest<TResult>
{
    /// <summary>Run the behavior around the rest of the pipeline.</summary>
    /// <param name="request">The dispatched request instance.</param>
    /// <param name="next">Delegate invoking the next behavior or the final handler.</param>
    /// <param name="cancellationToken">Propagated from the <see cref="IMediator"/> call site.</param>
    /// <returns>The result produced by the inner pipeline, possibly transformed by this behavior.</returns>
    Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken);
}
