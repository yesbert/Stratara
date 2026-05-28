namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// Resolves the current user's id, typically from the ambient
/// <see cref="Stratara.Abstractions.Session.ISessionContextProvider"/>.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>Returns the current Actor user id.</summary>
    Guid GetId();
}
