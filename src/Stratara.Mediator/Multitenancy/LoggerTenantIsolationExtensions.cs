using Microsoft.Extensions.Logging;
using Stratara.Diagnostics;

namespace Stratara.Mediator.Multitenancy;

/// <summary>
/// Source-generated log messages for the tenant-isolation pipeline behavior.
/// </summary>
internal static partial class LoggerTenantIsolationExtensions
{
    [LoggerMessage(
        EventId = LogEvents.TenantIsolation.SubjectMismatchRejected,
        Level = LogLevel.Warning,
        Message = "Tenant-isolation rejected request '{RequestType}': requested tenant {RequestedTenantId} does not match session tenant {SessionTenantId}.")]
    public static partial void LogTenantSubjectMismatchRejected(this ILogger logger, string requestType, Guid requestedTenantId, Guid sessionTenantId);

    [LoggerMessage(
        EventId = LogEvents.TenantIsolation.CrossTenantRejected,
        Level = LogLevel.Warning,
        Message = "Tenant-isolation (strict) rejected cross-tenant request '{RequestType}': actor tenant {ActorTenantId} operating on tenant {SessionTenantId} was not authorized.")]
    public static partial void LogCrossTenantRejected(this ILogger logger, string requestType, Guid actorTenantId, Guid sessionTenantId);

    [LoggerMessage(
        EventId = LogEvents.TenantIsolation.CrossTenantAllowed,
        Level = LogLevel.Information,
        Message = "Tenant-isolation (strict) permitted cross-tenant request '{RequestType}': actor tenant {ActorTenantId} operating on tenant {SessionTenantId}.")]
    public static partial void LogCrossTenantAllowed(this ILogger logger, string requestType, Guid actorTenantId, Guid sessionTenantId);
}
