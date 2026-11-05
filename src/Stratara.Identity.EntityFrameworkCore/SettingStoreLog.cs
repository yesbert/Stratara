using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Source-generated log messages for the encrypting setting store. Only the setting name and the scope
/// are logged — never the stored value.
/// </summary>
internal static partial class SettingStoreLog
{
    [LoggerMessage(
        EventId = LogEvents.KeyManagement.EncryptedSettingUnreadable,
        Level = LogLevel.Warning,
        Message = "The key of the encrypted setting {SettingName} in tenant scope '{TenantId}', user scope '{UserId}' " +
                  "was revoked or erased; the setting reads as absent.")]
    public static partial void EncryptedSettingKeyUnavailable(ILogger logger, string settingName, string tenantId, string userId);
}
