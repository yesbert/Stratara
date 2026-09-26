using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;
using Stratara.Projections.Abstractions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the store that keeps the deleted tenants of each projection in a read context.</summary>
public static class ForgottenTenantServiceCollectionExtensions
{
    /// <summary>
    /// Keeps the tenants that projections declaring <see cref="IForgetsDeletedTenants"/> have seen deleted in
    /// <typeparamref name="TReadContext"/>. <c>AddNpgsqlReadDbContextFactory</c> already does this; call it for a
    /// read context registered another way. Requires a registered <see cref="IDbContextFactory{TContext}"/> for a
    /// read context derived from the framework's read context, which declares the table. A store registered
    /// before this call is kept.
    /// </summary>
    /// <typeparam name="TReadContext">The read context that holds the table of deleted tenants.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddDbContextFactory&lt;AppReadDbContext&gt;(options => options.UseSqlite(connectionString), ServiceLifetime.Scoped)
    ///     .AddStrataraForgottenTenants&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraForgottenTenants<TReadContext>(this IServiceCollection services)
        where TReadContext : DbContext
    {
        services.TryAddScoped<IForgottenTenantStore, ForgottenTenantStore<TReadContext>>();
        return services;
    }
}
