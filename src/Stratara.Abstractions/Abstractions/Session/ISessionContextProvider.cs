using Stratara.Contracts.Session;

namespace Stratara.Abstractions.Session;

/// <summary>
/// Provides ambient access to the dual-identity <see cref="SessionContext"/> (Actor +
/// Subject) for the currently-executing request, worker message, or saga step.
/// </summary>
/// <remarks>
/// Set by middleware on HTTP requests, by the command worker per dispatched message, and
/// by the saga worker per emitted command. Tenant filters, repositories, encryption AAD,
/// and projection routing read the Subject (<see cref="SessionContext.TenantId"/>); audit
/// trails read the Actor (<see cref="SessionContext.ActorTenantId"/> /
/// <see cref="SessionContext.ActorUserId"/>).
/// </remarks>
public interface ISessionContextProvider
{
    /// <summary>The current session context, or <c>null</c> when none is set.</summary>
    SessionContext? Current { get; }

    /// <summary>Clear the current session context — typically at the end of a unit of work.</summary>
    void Clear();

    /// <summary>Set the current session context for the executing logical operation.</summary>
    void Set(SessionContext context);
}
