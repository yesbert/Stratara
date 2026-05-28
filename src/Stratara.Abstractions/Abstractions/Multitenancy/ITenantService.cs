namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Resolves the current Subject (data-owner) tenant id, typically from the ambient
/// <see cref="Stratara.Abstractions.Session.ISessionContextProvider"/>.
/// </summary>
public interface ITenantService
{
    /// <summary>Returns the current Subject tenant id.</summary>
    Guid GetTenantId();
}
