using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Messaging;
using Stratara.Infrastructure.Security.Integrity;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions that wire HMAC integrity protection for bus envelopes.</summary>
public static class IntegrityServiceCollectionExtensions
{
    /// <summary>
    /// Opt in to HMAC integrity protection on every bus envelope. The framework signs outbound
    /// <c>CommandEnvelope</c> and <c>EventBundle</c> instances and verifies inbound envelopes
    /// according to <see cref="BusEnvelopeIntegrityOptions.Mode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When this method is not called, <see cref="IBusEnvelopeSigner"/> stays unregistered and
    /// the framework behaves exactly as it did before: envelopes carry no signature, the
    /// dispatcher does not verify, and <see cref="BusEnvelopeIntegrityOptions.Mode"/> resolves to
    /// <see cref="BusEnvelopeIntegrityMode.Off"/>. The threat model trade-off is documented in
    /// <c>SECURITY.md</c>.
    /// </para>
    /// <para>
    /// Every publisher and consumer that share a bus must agree on
    /// <see cref="BusEnvelopeIntegrityOptions.SharedKey"/> and the active mode. When rolling out
    /// integrity to an existing fleet, deploy in two phases:
    /// </para>
    /// <list type="number">
    ///   <item><description>Publishers + consumers on <see cref="BusEnvelopeIntegrityMode.Permissive"/> — new envelopes carry a signature, unsigned in-flight envelopes are still accepted with a warning log.</description></item>
    ///   <item><description>Once every host is signing, switch both fleets to <see cref="BusEnvelopeIntegrityMode.Strict"/> — unsigned or tampered envelopes are rejected.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Callback that populates <see cref="BusEnvelopeIntegrityOptions"/>.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <c>null</c>.</exception>
    /// <example>
    /// The key is a <c>byte[]</c> of at least 32 bytes and must be identical on every host that shares
    /// the bus. Read it from a secret store rather than from <c>appsettings.json</c>:
    /// <code>
    /// services.AddBusEnvelopeIntegrity(options =&gt;
    /// {
    ///     options.Mode = BusEnvelopeIntegrityMode.Strict;
    ///     options.SharedKey = Convert.FromBase64String(configuration["BusEnvelopeIntegrity:SharedKey"]!);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddBusEnvelopeIntegrity(this IServiceCollection services, Action<BusEnvelopeIntegrityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<BusEnvelopeIntegrityOptions>().Configure(configure);
        services.AddSingleton<IBusEnvelopeSigner, HmacBusEnvelopeSigner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, BusEnvelopeIntegrityStartupProbe>());
        return services;
    }

    /// <summary>
    /// Overload that binds <see cref="BusEnvelopeIntegrityOptions"/> from configuration section
    /// <see cref="BusEnvelopeIntegrityOptions.SectionName"/> (<c>"BusEnvelopeIntegrity"</c>).
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configuration">The configuration root to bind from.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
    /// <example>
    /// Binds <c>Mode</c> from the <c>BusEnvelopeIntegrity</c> section and leaves the key to the caller,
    /// so a host using this overload still has to assign <see cref="BusEnvelopeIntegrityOptions.SharedKey"/>:
    /// <code>
    /// // appsettings.json: { "BusEnvelopeIntegrity": { "Mode": "Permissive" } }
    /// services.AddBusEnvelopeIntegrity(configuration);
    /// services.Configure&lt;BusEnvelopeIntegrityOptions&gt;(
    ///     o =&gt; o.SharedKey = Convert.FromBase64String(configuration["BusEnvelopeIntegrity:SharedKey"]!));
    /// </code>
    /// </example>
    public static IServiceCollection AddBusEnvelopeIntegrity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<BusEnvelopeIntegrityOptions>().Bind(configuration.GetSection(BusEnvelopeIntegrityOptions.SectionName));
        services.AddSingleton<IBusEnvelopeSigner, HmacBusEnvelopeSigner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, BusEnvelopeIntegrityStartupProbe>());
        return services;
    }
}
