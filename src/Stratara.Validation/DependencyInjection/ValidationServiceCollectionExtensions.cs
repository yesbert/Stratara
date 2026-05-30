using Stratara.Abstractions.Validation;
using Stratara.Validation;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register the Stratara validation pipeline behavior and discover
/// <see cref="IValidator{T}"/> implementations.
/// </summary>
public static class ValidationServiceCollectionExtensions
{
    /// <summary>
    /// Register the validation pipeline behaviors for both request shapes
    /// (<see cref="Stratara.Abstractions.Mediator.IRequest"/> and
    /// <see cref="Stratara.Abstractions.Mediator.IRequest{TResult}"/>).
    /// </summary>
    /// <remarks>
    /// Call this <b>before</b> any other <c>AddPipelineBehavior*</c> registration so validation
    /// runs as the outermost behavior — rejecting invalid requests before authorization, auditing,
    /// or the handler execute. Pair with <see cref="AddValidatorsFromAssemblyContaining{T}"/> to
    /// register the concrete validators.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddStrataraValidation()
    ///     .AddValidatorsFromAssemblyContaining&lt;IAppMarker&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraValidation(this IServiceCollection services)
    {
        services.AddPipelineBehavior(typeof(ValidationPipelineBehavior<>));
        services.AddPipelineBehaviorWithResult(typeof(ValidationPipelineBehavior<,>));
        return services;
    }

    /// <summary>
    /// Scan the assembly containing <typeparamref name="T"/> and register every concrete
    /// <see cref="IValidator{T}"/> implementation as a scoped service against each closed
    /// <see cref="IValidator{T}"/> interface it implements.
    /// </summary>
    /// <typeparam name="T">A marker type from the assembly to scan. Typically a domain marker interface.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    public static IServiceCollection AddValidatorsFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var assembly = typeof(T).Assembly;

        foreach (var type in assembly
                     .GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.IsGenericType && @interface.GetGenericTypeDefinition() == typeof(IValidator<>))
                {
                    services.AddScoped(@interface, type);
                }
            }
        }

        return services;
    }
}
