using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Infrastructure.Security.KeyManagement;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara security primitives (AES-GCM encryption, key store, secure JSON serializer).</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers Stratara's security stack: <see cref="ISecureJsonSerializer"/>, <see cref="ISecureBlobEncryptor"/>,
    /// <see cref="IEncryptionFactory"/>, and a <em>development-only</em> <see cref="IKeyStore"/>
    /// (<see cref="DummyKeyStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="IKeyStore"/> registration uses <c>TryAddSingleton</c> so a consumer-provided
    /// implementation (HSM / Azure Key Vault / AWS KMS) takes precedence when registered first.
    /// </para>
    /// <para>
    /// <strong>Production hosts MUST register a real <see cref="IKeyStore"/> before calling this method</strong> —
    /// the fallback <see cref="DummyKeyStore"/> throws <see cref="InvalidOperationException"/> on construction
    /// in a production environment to prevent accidental deployment of the test key.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ISecureJsonSerializer, SecureJsonSerializer>();
        services.AddSingleton<ISecureBlobEncryptor, SecureBlobEncryptor>();
        services.AddSingleton<IEncryptionFactory, AesGcmEncryptionFactory>();
        services.TryAddSingleton<IKeyStore, DummyKeyStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, KeyStoreStartupProbe>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EncryptionMetadataDriftGuard>());

        return services;
    }
}