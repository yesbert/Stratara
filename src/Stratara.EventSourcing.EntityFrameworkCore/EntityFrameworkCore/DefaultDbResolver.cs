using Microsoft.Extensions.Configuration;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IDbResolver"/> that resolves the database connection string from the
/// <c>defaultdb</c> entry of the host's <see cref="IConfiguration"/> connection strings.
/// </summary>
/// <remarks>
/// Registered automatically by the Npgsql DI extensions when no consumer-specific resolver is
/// pre-registered. Consumer hosts can override this by registering their own
/// <see cref="IDbResolver"/> (e.g. for per-tenant connection-string routing) before calling
/// the Stratara DI extensions.
/// </remarks>
/// <param name="configuration">Host configuration providing the connection-string section.</param>
internal sealed class DefaultDbResolver(IConfiguration configuration) : IDbResolver
{
    /// <inheritdoc/>
    public string ResolveConnectionString() =>
        configuration.GetConnectionString("defaultdb") ?? throw new InvalidOperationException("Default connection string not found.");
}
