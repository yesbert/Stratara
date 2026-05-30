using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Infrastructure.Security.KeyManagement;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Security;
using Stratara.Abstractions.Security;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara security primitives (secure JSON serializer + blob encryption + key store).</summary>
public static class InfrastructureSecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers Stratara's security stack: <see cref="ISecureJsonSerializer"/>, the AES-GCM blob
    /// encryptor and <see cref="IEncryptionFactory"/> (delegated to <c>Stratara.Security</c>), and a
    /// <em>development-only</em> <see cref="IKeyStore"/> fallback (<see cref="DummyKeyStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="IKeyStore"/> registration uses <c>TryAddSingleton</c> so a production key store
    /// registered first — e.g. <c>AddStrataraFileKeyStore(configuration)</c> from
    /// <c>Stratara.Security</c>, or an HSM / Key Vault / KMS implementation — takes precedence.
    /// </para>
    /// <para>
    /// <strong>Production hosts MUST register a real <see cref="IKeyStore"/> before calling this method.</strong>
    /// The fallback <see cref="DummyKeyStore"/> throws on construction outside the Development
    /// environment to prevent accidental deployment of the test key.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ISecureJsonSerializer, SecureJsonSerializer>();
        services.AddStrataraBlobEncryption();
        services.TryAddSingleton<IKeyStore, DummyKeyStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, KeyStoreStartupProbe>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EncryptionMetadataDriftGuard>());

        return services;
    }
}
