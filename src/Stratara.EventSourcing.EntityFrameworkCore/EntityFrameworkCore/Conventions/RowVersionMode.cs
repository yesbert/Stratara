namespace Stratara.EventSourcing.EntityFrameworkCore.Conventions;

/// <summary>
/// How a <c>RowVersion</c> column is stored on disk when applying the row-version convention via
/// <c>ApplyRowVersionConvention</c>.
/// </summary>
public enum RowVersionMode
{
    /// <summary>Store as a raw byte array (default mapping in SQL Server's <c>rowversion</c>).</summary>
    ByteArray,

    /// <summary>Store as an unsigned 32-bit integer (preferred for PostgreSQL via Npgsql).</summary>
    Uint
}
