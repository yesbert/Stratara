using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;

namespace Stratara.Mediator;

internal sealed class Mediator(Tracer tracer, IServiceProvider serviceProvider) : IMediator
{
    private static readonly ConcurrentDictionary<Type, object> WrapperCache = new();

    public async Task<TResult> HandleAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var span = tracer.StartActiveSpan($"Handle {request.GetType().Name}");

        var wrapper = (IRequestPipelineWrapper<TResult>)WrapperCache.GetOrAdd(
            request.GetType(),
            requestType =>
            {
                var wrapperType = typeof(RequestPipelineWrapper<,>).MakeGenericType(requestType, typeof(TResult));
                return Activator.CreateInstance(wrapperType)
                       ?? throw new InvalidOperationException($"Unable to construct pipeline wrapper for '{requestType.Name}'");
            });

        return await wrapper.HandleAsync(request, serviceProvider, cancellationToken);
    }

    public async Task HandleAsync<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        using var span = tracer.StartActiveSpan($"Handle {request.GetType().Name}");

        var handler = serviceProvider.GetService<ICommandHandler<TRequest>>()
                      ?? throw new InvalidOperationException($"Handler for '{typeof(TRequest).Name}' not found");

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest>>().ToArray();

        Task InvokeHandler() => handler.HandleAsync(request, cancellationToken);

        var next = (Func<Task>)InvokeHandler;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var current = next;
            next = () => behavior.HandleAsync(request, current, cancellationToken);
        }

        await next();
    }

    private interface IRequestPipelineWrapper<TResult>
    {
        Task<TResult> HandleAsync(
            IRequest<TResult> request,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken);
    }

    private sealed class RequestPipelineWrapper<TRequest, TResult> : IRequestPipelineWrapper<TResult>
        where TRequest : IRequest<TResult>
    {
        public Task<TResult> HandleAsync(
            IRequest<TResult> request,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken)
        {
            var typedRequest = (TRequest)request;
            var handler = serviceProvider.GetService<IQueryHandler<TRequest, TResult>>()
                          ?? throw new InvalidOperationException($"Handler for '{typeof(TRequest).Name}' not found");

            var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResult>>().ToArray();

            Task<TResult> InvokeHandler() => handler.HandleAsync(typedRequest, cancellationToken);

            var next = (Func<Task<TResult>>)InvokeHandler;
            for (var i = behaviors.Length - 1; i >= 0; i--)
            {
                var behavior = behaviors[i];
                var current = next;
                next = () => behavior.HandleAsync(typedRequest, current, cancellationToken);
            }

            return next();
        }
    }
}
