using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// Hosted service that resolves the <see cref="IMasterKeyProvider"/> once at host start so the
/// KEK is decoded and validated eagerly. A misconfigured or missing master key then fails the host
/// at boot rather than on the first encryption code-path.
/// </summary>
internal sealed class FileKeyStoreStartupProbe(IMasterKeyProvider masterKeyProvider) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
        => await masterKeyProvider.GetMasterKeyAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
