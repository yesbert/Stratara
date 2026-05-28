namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Identifies the relational database provider used by a Stratara DbContext so that
/// convention helpers (e.g. JSON column mapping) can emit provider-appropriate metadata.
/// </summary>
public enum DatabaseProviderType
{
    /// <summary>PostgreSQL via Npgsql (Stratara's primary supported provider).</summary>
    PostgreSql,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>SQLite (typically used for tests and local development).</summary>
    Sqlite
}
