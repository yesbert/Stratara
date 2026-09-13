using Microsoft.Data.Sqlite;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Testing.EntityFrameworkCore;

/// <summary>
/// Recognises SQLite's constraint error (primary code 19) as a unique violation when its extended
/// code names a unique or primary-key constraint, or when the message does, anywhere in an
/// exception's inner chain.
/// </summary>
internal sealed class SqliteConflictDetector : IStoreConflictDetector
{
    private const int ConstraintErrorCode = 19;
    private const int UniqueConstraintExtendedCode = 2067;
    private const int PrimaryKeyConstraintExtendedCode = 1555;

    public bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: ConstraintErrorCode } sqlite && IsUniqueConstraint(sqlite))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUniqueConstraint(SqliteException exception) =>
        exception.SqliteExtendedErrorCode is UniqueConstraintExtendedCode or PrimaryKeyConstraintExtendedCode
        || exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}
