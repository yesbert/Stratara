namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Thrown by the tenant-isolation pipeline behavior when an <see cref="ITenantScopedRequest"/> is
/// rejected: either its <see cref="ITenantScopedRequest.TenantId"/> does not match the current
/// session's data-owner tenant, or strict mode observed an unauthorized cross-tenant operation.
/// </summary>
/// <remarks>
/// Lives in <c>Stratara.Abstractions</c> so consumers can catch it without referencing the behavior
/// package. ASP.NET hosts translate it to an HTTP 403 (Forbidden) response; worker hosts surface it
/// through their message-failure path.
/// </remarks>
public sealed class TenantAccessDeniedException : Exception
{
    /// <summary>
    /// Initialise a new <see cref="TenantAccessDeniedException"/> for a tenant-isolation rejection.
    /// </summary>
    /// <param name="requestedTenantId">The data-owner tenant named by the rejected request.</param>
    /// <param name="sessionTenantId">The data-owner tenant established on the current session.</param>
    /// <param name="message">A human-readable description of why access was denied.</param>
    public TenantAccessDeniedException(Guid requestedTenantId, Guid sessionTenantId, string message)
        : base(message)
    {
        RequestedTenantId = requestedTenantId;
        SessionTenantId = sessionTenantId;
    }

    /// <summary>The data-owner tenant named by the rejected request.</summary>
    public Guid RequestedTenantId { get; }

    /// <summary>The data-owner tenant established on the current session.</summary>
    public Guid SessionTenantId { get; }
}
