using System.Diagnostics;
using Stratara.Contracts.Session;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;

namespace Stratara.Sessions.Session;

internal sealed class SessionContextProvider : ISessionContextProvider
{
    public SessionContext? Current { get; private set; }

    public void Clear()
    {
        Activity.Current?.SetTag(ApplicationDiagnostics.CorrelationIdTagName, null);
        Activity.Current?.SetTag(ApplicationDiagnostics.CausationIdTagName, null);
        Activity.Current?.SetTag(ApplicationDiagnostics.TenantIdTagName, null);
        Activity.Current?.SetTag(ApplicationDiagnostics.UserIdTagName, null);

        Current = null;
    }

    public void Set(SessionContext context)
    {
        Activity.Current?.SetTag(ApplicationDiagnostics.CorrelationIdTagName, context.CorrelationId);
        Activity.Current?.SetTag(ApplicationDiagnostics.CausationIdTagName, context.CausationId);
        Activity.Current?.SetTag(ApplicationDiagnostics.TenantIdTagName, context.TenantId);
        Activity.Current?.SetTag(ApplicationDiagnostics.UserIdTagName, context.ActorUserId);

        Current = context;
    }
}