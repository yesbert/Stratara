using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Security.Integrity;

/// <summary>
/// Hosted service that emits one-shot warnings at host start-up when the bus-envelope integrity
/// wiring deviates from a secure-by-default Production posture. Round-3-Audit Finding R3-Sec-009:
/// the default <see cref="BusEnvelopeIntegrityOptions.Mode"/> stays
/// <see cref="BusEnvelopeIntegrityMode.Off"/> for backward-compat. Pre-nuget.org-Audit
/// (2026-05-26): also warn when integrity is configured to verify but no
/// <see cref="IBusEnvelopeSigner"/> is registered — the verifier silently no-ops in that case.
/// </summary>
internal sealed partial class BusEnvelopeIntegrityStartupProbe(
    ILogger<BusEnvelopeIntegrityStartupProbe> logger,
    IHostEnvironment environment,
    IOptions<BusEnvelopeIntegrityOptions> options,
    IBusEnvelopeSigner? signer = null) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = options.Value.Mode;

        if (mode == BusEnvelopeIntegrityMode.Off && environment.IsProduction())
        {
            LogIntegrityOffInProduction(logger);
        }
        else if (mode != BusEnvelopeIntegrityMode.Off && signer is null)
        {
            LogIntegrityEnabledWithoutSigner(logger, mode.ToString());
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = LogEvents.BusEnvelopeIntegrity.IntegrityOffInProduction,
        Level = LogLevel.Warning,
        Message = "BusEnvelopeIntegrityOptions.Mode is Off on a Production host — outbound CommandEnvelope / EventBundle payloads carry no signature, consumers do not verify. Anyone with bus-publish rights can inject forged envelopes with arbitrary SessionContextJson (tenant / actor spoofing). Set Mode to Permissive (rolling) or Strict (enforced) via AddBusEnvelopeIntegrity(...).")]
    private static partial void LogIntegrityOffInProduction(ILogger logger);

    [LoggerMessage(
        EventId = LogEvents.BusEnvelopeIntegrity.IntegrityEnabledWithoutSigner,
        Level = LogLevel.Warning,
        Message = "BusEnvelopeIntegrityOptions.Mode is {Mode} but no IBusEnvelopeSigner is registered — verification silently returns Skipped for every envelope. Register the signer on both publisher AND consumer hosts via AddBusEnvelopeIntegrity(\"<base64-key>\") with a shared 32-byte key, otherwise the integrity contract is no-op.")]
    private static partial void LogIntegrityEnabledWithoutSigner(ILogger logger, string mode);
}
