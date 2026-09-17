using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Outbox;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Orleans.EntityFrameworkCore.Intents;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the command-intent store in the write store.</summary>
public static class OrleansIntentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Keeps the commands the Orleans execution model records, and the bookkeeping of their bounded
    /// resume, in the outbox table of <typeparamref name="TWriteContext"/>. Binds no configuration.
    /// Requires a registered <see cref="IDbContextFactory{TContext}"/> for the write context. The
    /// execution model's command dispatcher requires this registration, or another
    /// <see cref="ICommandIntentStore"/>, and the host fails at start without one. An intent store
    /// registered before this call is kept.
    /// </summary>
    /// <typeparam name="TWriteContext">A write context derived from the framework's write context.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddBackendServices();
    /// builder.Services
    ///     .AddNpgsqlWriteDbContextFactory&lt;AppWriteDbContext&gt;()
    ///     .AddStrataraOrleansCommandDispatcher()
    ///     .AddStrataraIntentStore&lt;AppWriteDbContext&gt;()
    ///     .AddStrataraSingletonWork&lt;OutboxDrainWork&gt;(OutboxDrainWork.WorkName);
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraIntentStore<TWriteContext>(this IServiceCollection services)
        where TWriteContext : DbContext, IWriteDbContext
    {
        services.TryAddScoped<ICommandIntentStore, CommandIntentStore<TWriteContext>>();
        return services;
    }
}
