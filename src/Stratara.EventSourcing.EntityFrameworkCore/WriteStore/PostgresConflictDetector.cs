using Npgsql;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

/// <summary>
/// Recognises PostgreSQL's unique-violation error (SQL state <c>23505</c>) anywhere in an
/// exception's inner chain.
/// </summary>
internal sealed class PostgresConflictDetector : IStoreConflictDetector
{
    private const string UniqueViolationSqlState = "23505";

    public bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: UniqueViolationSqlState })
            {
                return true;
            }
        }

        return false;
    }
}
