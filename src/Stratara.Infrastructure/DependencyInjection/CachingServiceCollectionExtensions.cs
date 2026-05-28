using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI helpers for wiring the Redis cache used by the Stratara framework.
/// </summary>
public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IConnectionMultiplexer"/> built from the
    /// <c>ConnectionStrings:redis</c> configuration entry.
    /// </summary>
    /// <remarks>
    /// Before Stratara 1.x cleanup this method delegated to
    /// <c>builder.AddRedisClient("redis")</c> from <c>Aspire.StackExchange.Redis</c>, which also
    /// registered a Redis health check and OpenTelemetry instrumentation. This vendor-direct
    /// implementation does <strong>neither</strong> — consumers that need them must register
    /// them explicitly (e.g. <c>AddHealthChecks().AddRedis(...)</c> from
    /// <c>AspNetCore.HealthChecks.Redis</c>, and <c>AddRedisInstrumentation()</c> from
    /// <c>OpenTelemetry.Instrumentation.StackExchangeRedis</c>).
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the <c>redis</c> connection string is missing.</exception>
    public static IHostApplicationBuilder AddCaching(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("redis")
            ?? throw new InvalidOperationException("Missing connection string 'redis'.");

        builder.Services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));

        return builder;
    }
}
