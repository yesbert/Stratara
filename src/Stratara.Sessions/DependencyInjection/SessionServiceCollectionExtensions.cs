using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
    /// a scoped service and register <see cref="SessionContextOptions"/>, read from the
    /// <c>SessionContext</c> configuration section. Pair with
    /// <c>app.UseMiddleware&lt;SessionContextMiddleware&gt;()</c> in the ASP.NET Core
    /// pipeline to populate the context per request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section is read from the <c>IConfiguration</c> the container holds when the options are
    /// first resolved, so a host built with <c>Host.CreateApplicationBuilder</c> or
    /// <c>WebApplication.CreateBuilder</c> needs no further code. A service collection that holds no
    /// configuration gets the defaults.
    /// </para>
    /// <para>
    /// A value configured in code with <c>services.Configure&lt;SessionContextOptions&gt;(...)</c> after
    /// this call takes precedence over the section; one configured before it is overwritten for every
    /// key the section carries. The section is applied once, at the position of the first call, so
    /// calling this method again — directly or through a composite — does not re-apply it.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Registers the scoped context and its accessor. In an ASP.NET host, add the middleware too —
    /// without it the context stays empty for every request:
    /// <code>
    /// // appsettings.json: { "SessionContext": { "AllowTenantHeader": false } }
    /// services.AddSessionContext();
    /// app.UseMiddleware&lt;SessionContextMiddleware&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddSessionContext(this IServiceCollection services)
    {
        services.AddOptions<SessionContextOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<SessionContextOptions>, SessionContextOptionsBinding>());
        services.AddScoped<ISessionContextProvider, SessionContextProvider>();

        return services;
    }
}
