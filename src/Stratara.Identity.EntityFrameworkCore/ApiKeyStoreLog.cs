using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Source-generated log messages for the API-key store. Kept separate from the generic store so
/// the logger definitions are non-generic. Only key ids and tenant ids are logged — never the raw
/// key or its digest, so a log stream never yields a usable credential.
/// </summary>
internal static partial class ApiKeyStoreLog
{
    [LoggerMessage(
        EventId = LogEvents.ApiKeys.KeyImported,
        Level = LogLevel.Information,
        Message = "Imported a caller-supplied machine key {ApiKeyId} for tenant {TenantId}.")]
    public static partial void KeyImported(ILogger logger, Guid apiKeyId, Guid tenantId);

    [LoggerMessage(
        EventId = LogEvents.ApiKeys.KeyImportNoOp,
        Level = LogLevel.Information,
        Message = "Import of machine key {ApiKeyId} for tenant {TenantId} was a no-op — the key is already stored.")]
    public static partial void KeyImportNoOp(ILogger logger, Guid apiKeyId, Guid tenantId);

    [LoggerMessage(
        EventId = LogEvents.ApiKeys.KeyImportDiffers,
        Level = LogLevel.Warning,
        Message = "Import of machine key {ApiKeyId} for tenant {TenantId} supplied a different {DifferingParameter}; " +
                  "the stored key was left untouched. Revoke and import a new key to change it.")]
    public static partial void KeyImportDiffers(ILogger logger, Guid apiKeyId, Guid tenantId, string differingParameter);
}
