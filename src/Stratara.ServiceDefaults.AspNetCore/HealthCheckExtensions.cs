using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Health-check wiring for Stratara ASP.NET Core hosts: registers a default <c>self</c> check + maps the
/// <c>/health</c> and <c>/alive</c> endpoints.
/// </summary>
public static class HealthCheckExtensions
{
    private const string LiveTag = "live";
    private const string HealthCheckName = "self";

    /// <summary>
    /// Registers a baseline health check (`self`) that always returns <see cref="HealthStatus.Healthy"/> and is
    /// tagged <c>live</c>, so it shows up on both the <c>/health</c> and <c>/alive</c> endpoints once mapped via
    /// <see cref="MapDefaultEndpoints"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The host-builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services
            .AddHealthChecks()
            .AddCheck(HealthCheckName, () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Maps the standard health endpoints: <c>/health</c> (full report) and <c>/alive</c> (only checks tagged
    /// <c>live</c>).
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <param name="requireAuthorizationOnHealth">
    /// When <see langword="true"/>, applies <c>RequireAuthorization()</c> to the <c>/health</c> endpoint so
    /// the full health report (which lists every registered dependency by name and may surface error
    /// messages) is only accessible to authenticated callers. The <c>/alive</c> endpoint stays anonymous
    /// because Kubernetes / Aspire liveness probes do not present credentials. Defaults to <see langword="false"/>
    /// to preserve backwards compatibility — see the <c>&lt;remarks&gt;</c> below for guidance.
    /// </param>
    /// <returns>The same app for chaining.</returns>
    /// <remarks>
    /// <b>Information-disclosure risk on <c>/health</c>:</b> the default mapping returns the full health
    /// report including the names and status of every registered dependency. An unauthenticated attacker
    /// can use it to fingerprint the deployment topology. For internet-exposed hosts, opt in to
    /// <paramref name="requireAuthorizationOnHealth"/> = <see langword="true"/>, restrict the endpoint to
    /// an internal port via <c>UseUrls</c> / network policy, or replace it with a custom mapping that
    /// returns only an aggregated status.
    /// </remarks>
    public static WebApplication MapDefaultEndpoints(this WebApplication app, bool requireAuthorizationOnHealth = false)
    {
        var healthCheckPath = Endpoints.HealthEndpointPath;
        var healthBuilder = app.MapHealthChecks(healthCheckPath);
        if (requireAuthorizationOnHealth)
        {
            healthBuilder.RequireAuthorization();
        }

        var alivenessPath = Endpoints.AlivenessEndpointPath;
        app.MapHealthChecks(alivenessPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(LiveTag)
        });

        return app;
    }
}
