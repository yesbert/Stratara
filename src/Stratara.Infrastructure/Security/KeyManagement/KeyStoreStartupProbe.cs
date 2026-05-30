using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Security;
using Stratara.Diagnostics;
using Stratara.Security;

namespace Stratara.Infrastructure.Security.KeyManagement;

/// <summary>
/// Hosted service that resolves the registered <see cref="IKeyStore"/> exactly once at host start-up to
/// trigger any constructor-time validation eagerly. Combined with <see cref="DummyKeyStore"/>'s
/// development-only whitelist guard, this prevents a misconfigured host from booting healthy and only
/// crashing minutes later on the first encryption code-path.
/// </summary>
/// <remarks>
/// When the resolved key store is <see cref="DummyKeyStore"/>, the probe additionally logs a warning at
/// start-up (event id <see cref="LogEvents.KeyManagement.DummyKeyStoreActive"/>) so an unintentional
/// dependency on the dummy is loud rather than silent — even in <c>Development</c>.
/// </remarks>
internal sealed partial class KeyStoreStartupProbe(ILogger<KeyStoreStartupProbe> logger, IKeyStore keyStore) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (keyStore is DummyKeyStore)
        {
            LogDummyKeyStoreActive(logger);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = LogEvents.KeyManagement.DummyKeyStoreActive,
        Level = LogLevel.Warning,
        Message = "DummyKeyStore is the resolved IKeyStore implementation — encryption uses a deterministic test key derived from a constant baked into the shipping NuGet. NEVER deploy this configuration. Register a real IKeyStore (Azure Key Vault, AWS KMS, HSM) before calling AddSecurity().")]
    private static partial void LogDummyKeyStoreActive(ILogger logger);
}
