using Microsoft.Extensions.DependencyInjection;
using Stratara.Sessions.Session;
using Stratara.Abstractions.Session;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions for Stratara's session-context provider.
/// </summary>
public static class SessionServiceCollectionExtensions
{
    /// <summary>
    /// Register the concrete <see cref="ISessionContextProvider"/> implementation as
    /// a scoped service plus the <see cref="SessionContextOptions"/> binding. Pair with
    /// <c>app.UseMiddleware&lt;SessionContextMiddleware&gt;()</c> in the ASP.NET Core
    /// pipeline to populate the context per request.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    public static IServiceCollection AddSessionContext(this IServiceCollection services)
    {
        services.AddOptions<SessionContextOptions>();
        services.AddScoped<ISessionContextProvider, SessionContextProvider>();

        return services;
    }
}
