using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.EventSourcing.Pipeline.CommandAudit;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions for the Stratara command-audit pipeline behavior.
/// </summary>
public static class CommandAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>CommandAuditBehavior</c> pipeline behaviors for both
    /// <see cref="IRequest"/> (void commands) and <see cref="IRequest{TResult}"/>
    /// (commands with a result). The behaviors persist an audit row via
    /// <see cref="Stratara.Abstractions.Persistence.IWriteUnitOfWork.CreateCommandAuditRepository"/>
    /// for every dispatched <see cref="Stratara.Abstractions.Mediator.ICommandBase"/>; non-command
    /// requests (pure queries) pass through unaffected.
    /// </summary>
    /// <remarks>
    /// Call alongside the other framework registrations at app composition time, for example:
    /// <code>
    /// builder.Services
    ///     .AddMediator()
    ///     .AddCommandAuditing();
    /// </code>
    /// Both behaviors are registered as scoped so they share the request's
    /// <see cref="Stratara.Abstractions.Persistence.IWriteUnitOfWork"/> instance with the rest of
    /// the mediator pipeline.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddCommandAuditing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CommandAuditBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<>), typeof(CommandAuditBehavior<>));
        return services;
    }
}
