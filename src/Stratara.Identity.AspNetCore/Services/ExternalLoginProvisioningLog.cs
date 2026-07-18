using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Source-generated log messages for the JIT external-login provisioning service. Kept separate
/// from the generic service so the logger definitions are non-generic. Only the login provider is
/// logged — never the email or subject identifier, to avoid leaking account-linking material.
/// </summary>
internal static partial class ExternalLoginProvisioningLog
{
    [LoggerMessage(
        EventId = LogEvents.ExternalLoginProvisioning.UserProvisioned,
        Level = LogLevel.Information,
        Message = "Provisioned a new local account for a first external sign-in via {LoginProvider}.")]
    public static partial void UserProvisioned(ILogger logger, string loginProvider);

    [LoggerMessage(
        EventId = LogEvents.ExternalLoginProvisioning.LoginLinked,
        Level = LogLevel.Information,
        Message = "Linked an external {LoginProvider} login to an existing local account after verified-email checks.")]
    public static partial void LoginLinked(ILogger logger, string loginProvider);

    [LoggerMessage(
        EventId = LogEvents.ExternalLoginProvisioning.LinkRefusedUnverified,
        Level = LogLevel.Warning,
        Message = "Refused to auto-link an external {LoginProvider} login to an existing account — email not verified on both sides; interactive linking required.")]
    public static partial void LinkRefusedUnverified(ILogger logger, string loginProvider);

    [LoggerMessage(
        EventId = LogEvents.ExternalLoginProvisioning.ProvisioningDenied,
        Level = LogLevel.Warning,
        Message = "Denied external-login provisioning via {LoginProvider}: {Reason}.")]
    public static partial void ProvisioningDenied(ILogger logger, string loginProvider, string reason);
}
