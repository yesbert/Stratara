namespace Stratara.Abstractions.Persistence;

/// <summary>
/// Resolves the connection string used by the current request / scope. Allows hosts to
/// implement per-tenant / per-region DB routing without coupling repositories to the
/// resolver implementation.
/// </summary>
public interface IDbResolver
{
    /// <summary>Return the connection string for the current scope.</summary>
    string ResolveConnectionString();
}
