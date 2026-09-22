using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

namespace Stratara.Mediator.Multitenancy;

/// <summary>
/// Shared enforcement logic for the tenant-isolation pipeline behavior, used by both the
/// result and void request shapes.
/// </summary>
internal static class TenantIsolationGuard
{
    public static async Task EnsureAuthorizedAsync(
        ITenantScopedRequest request,
        ISessionContextProvider sessionContextProvider,
        ICrossTenantAuthorizer crossTenantAuthorizer,
        TenantIsolationOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var requestType = request.GetType().Name;

        var session = sessionContextProvider.Current
                      ?? throw new TenantAccessDeniedException(
                          request.TenantId,
                          Guid.Empty,
                          $"Tenant-scoped request '{requestType}' was dispatched without a session context.");

        if (request.TenantId != session.TenantId)
        {
            logger.LogTenantSubjectMismatchRejected(requestType, request.TenantId, session.TenantId);
            throw new TenantAccessDeniedException(
                request.TenantId,
                session.TenantId,
                $"Tenant-scoped request '{requestType}' targets tenant {request.TenantId} but the session operates on tenant {session.TenantId}.");
        }

        if (options.Mode != TenantIsolationMode.Strict)
        {
            return;
        }

        if (session.ActorTenantId == session.TenantId)
        {
            return;
        }

        if (!options.AuthorizePlatformActor && IsPlatformInitiated(session))
        {
            logger.LogPlatformActorAllowed(requestType, session.TenantId);
            return;
        }

        var allowed = await crossTenantAuthorizer.IsCrossTenantAllowedAsync(session, cancellationToken);
        if (!allowed)
        {
            logger.LogCrossTenantRejected(requestType, session.ActorTenantId, session.TenantId);
            throw new TenantAccessDeniedException(
                request.TenantId,
                session.TenantId,
                $"Cross-tenant request '{requestType}' by actor tenant {session.ActorTenantId} on tenant {session.TenantId} was not authorized.");
        }

        logger.LogCrossTenantAllowed(requestType, session.ActorTenantId, session.TenantId);
    }

    /// <summary>
    /// Work the platform started on a tenant's behalf, as <c>SessionContext.ForPlatform</c> builds it.
    /// Both reserved identities are required: a session carrying one of them and a real principal in
    /// the other is a cross-tenant operation like any other.
    /// </summary>
    private static bool IsPlatformInitiated(SessionContext session) =>
        session.ActorTenantId == SessionContext.SystemActorTenantId
        && session.ActorUserId == SessionContext.SystemActorUserId;
}
