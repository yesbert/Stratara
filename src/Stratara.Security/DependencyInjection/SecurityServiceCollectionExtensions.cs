using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Security;
using Stratara.Security;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions for the Stratara.Security key store + envelope encryption primitives.
/// </summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Register the AES-GCM blob encryptor and encryption factory. Used on its own when only the
    /// field/JSON encryption stack is needed, and called internally by
    /// <see cref="AddStrataraFileKeyStore"/>.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAdd</c> so a consumer-registered implementation (or the file key store's
    /// registration) takes precedence. Reads <see cref="StrataraBlobEncryptionOptions"/>; bind it
    /// from configuration or leave the defaults.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    public static IServiceCollection AddStrataraBlobEncryption(this IServiceCollection services)
    {
        services.AddOptions<StrataraBlobEncryptionOptions>();
        services.TryAddSingleton<IEncryptionFactory, AesGcmEncryptionFactory>();
        services.TryAddSingleton<ISecureBlobEncryptor, AesGcmSecureBlobEncryptor>();
        return services;
    }

    /// <summary>
    /// Register the production file-backed key store: a <see cref="FileMasterKeyProvider"/> (KEK
    /// custody), the <see cref="EnvelopeFileKeyStore"/> as <see cref="IKeyStore"/>, the AES-GCM blob
    /// encryptor, and a startup probe that validates the KEK eagerly.
    /// </summary>
    /// <remarks>
    /// Bind options from <see cref="StrataraFileKeyStoreOptions.SectionName"/> and
    /// <see cref="StrataraBlobEncryptionOptions.SectionName"/>. Call this <b>before</b> any
    /// composition that registers a development fallback so the envelope store wins the
    /// <c>TryAdd</c> race. A missing or too-short KEK fails the host at startup with an actionable
    /// message.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configuration">Configuration root or section used to bind the options.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    public static IServiceCollection AddStrataraFileKeyStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StrataraFileKeyStoreOptions>(configuration.GetSection(StrataraFileKeyStoreOptions.SectionName));
        services.Configure<StrataraBlobEncryptionOptions>(configuration.GetSection(StrataraBlobEncryptionOptions.SectionName));

        services.TryAddSingleton<IMasterKeyProvider, FileMasterKeyProvider>();
        services.TryAddSingleton<IKeyStore, EnvelopeFileKeyStore>();
        services.AddStrataraBlobEncryption();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FileKeyStoreStartupProbe>());

        return services;
    }
}
