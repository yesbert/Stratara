using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;

/// <summary>
/// Preserves the tables a read context maps by copying their rows into a schema of their own, and writes them back.
/// </summary>
/// <remarks>
/// <para>
/// The copy is taken in one repeatable-read transaction, so every table is copied as of one moment, and the record of
/// what was copied is written in the same transaction. The live tables, their indexes, constraints and every object
/// around them are left untouched, so nothing can drift from what the consumer's migrations created. Restoring
/// truncates every preserved table in one statement and inserts the copies parent-first in one transaction: readers
/// wait for it and then see the restored state, never a mix. Every step that touches the preserved state holds one
/// transaction-scoped advisory lock, so hosts that start at once restore an abandoned state once.
/// </para>
/// <para>
/// A table outside the set that references one inside it would make the restore's truncation fail, so it fails the
/// preservation before anything is emptied, as does a cycle of references among the tables. A restore whose copy no
/// longer has the live table's columns refuses and keeps the copy.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The read context whose tables a replay empties.</typeparam>
[SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
    Justification = "Statements are composed of schema, table and column names taken from the context's model, the " +
                    "options and the database catalog, each delimited and with quotes doubled; every value is bound as a " +
                    "DbParameter. Nothing a caller supplies at run time reaches the SQL text.")]
internal sealed class NpgsqlReadModelPreservation<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IOptions<ReadModelRestoreOptions> options,
    IProjectionReplayState? replayState = null) : IReadModelPreservation
    where TContext : DbContext
{
    private const long LockKey = 0x5354_5241_5245_5354;
    private const int MaxIdentifierLength = 63;

    private readonly ReadModelRestoreOptions _options = options.Value;

    /// <inheritdoc/>
    public async Task PreserveAsync(Guid replayId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Database.SetCommandTimeout(_options.CommandTimeout);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        await LockAsync(context, cancellationToken);
        await EnsureSchemaAsync(context, cancellationToken);

        if (await ReadMarkerAsync(context, cancellationToken) is not null)
        {
            await ExecuteAsync(context, $"UPDATE {Marker} SET replay_id = @replay_id", cancellationToken, ("replay_id", replayId));
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var tables = await OrderedTablesAsync(context, cancellationToken);
        for (var position = 0; position < tables.Count; position++)
        {
            var table = tables[position];
            var copy = CopyName(table);
            await ExecuteAsync(context, $"DROP TABLE IF EXISTS {Qualified(_options.Schema, copy)}", cancellationToken);
            await ExecuteAsync(context, $"CREATE TABLE {Qualified(_options.Schema, copy)} AS TABLE {Qualified(table.Schema, table.Name)}", cancellationToken);
            await ExecuteAsync(context,
                $"INSERT INTO {Mapping} (position, source_schema, source_table, copy_table) VALUES (@position, @schema, @table, @copy)",
                cancellationToken, ("position", position), ("schema", table.Schema), ("table", table.Name), ("copy", copy));
        }

        await ExecuteAsync(context,
            $"INSERT INTO {Marker} (id, replay_id, preserved_at) VALUES (1, @replay_id, now())",
            cancellationToken, ("replay_id", replayId));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RestoreAsync(Guid replayId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Database.SetCommandTimeout(_options.CommandTimeout);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await LockAsync(context, cancellationToken);
        if (await ReadMarkerAsync(context, cancellationToken) != replayId)
        {
            return false;
        }

        var preserved = await ReadMappingAsync(context, cancellationToken);
        var columns = new List<IReadOnlyList<string>>(preserved.Count);
        foreach (var (table, copy) in preserved)
        {
            columns.Add(await InsertableColumnsAsync(context, table, copy, cancellationToken));
        }

        if (preserved.Count > 0)
        {
            await ExecuteAsync(context,
                $"TRUNCATE {string.Join(", ", preserved.Select(entry => Qualified(entry.Table.Schema, entry.Table.Name)))}",
                cancellationToken);
        }

        for (var index = 0; index < preserved.Count; index++)
        {
            var (table, copy) = preserved[index];
            var list = string.Join(", ", columns[index].Select(Quote));
            await ExecuteAsync(context,
                $"INSERT INTO {Qualified(table.Schema, table.Name)} ({list}) OVERRIDING SYSTEM VALUE SELECT {list} FROM {Qualified(_options.Schema, copy)}",
                cancellationToken);
            await ResetSequencesAsync(context, table, cancellationToken);
        }

        await DropPreservedAsync(context, preserved, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc/>
    public async Task DiscardAsync(Guid replayId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Database.SetCommandTimeout(_options.CommandTimeout);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await LockAsync(context, cancellationToken);
        if (await ReadMarkerAsync(context, cancellationToken) != replayId)
        {
            return;
        }

        await DropPreservedAsync(context, await ReadMappingAsync(context, cancellationToken), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A preserved state is abandoned when no replay is active. Its replay's outcome decides what happens to it: one
    /// that reads succeeded means the replay rebuilt the read models and only the drop failed, so the state is dropped;
    /// any other — or none, on a coordination state that lost it — means the read models are partial, so it is
    /// restored.
    /// </remarks>
    public async Task<bool> RestoreAbandonedAsync(CancellationToken cancellationToken = default)
    {
        Guid? replayId;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            replayId = await ReadMarkerAsync(context, cancellationToken);
        }

        if (replayId is not { } abandoned)
        {
            return false;
        }

        var progress = replayState?.GetProgress();
        if (progress?.IsActive == true)
        {
            return false;
        }

        if (progress?.LastReplay is { Result: ReplayResult.Succeeded } last && last.RequestId == abandoned)
        {
            await DiscardAsync(abandoned, cancellationToken);
            return false;
        }

        return await RestoreAsync(abandoned, cancellationToken);
    }

    private string Marker => Qualified(_options.Schema, "preservation");

    private string Mapping => Qualified(_options.Schema, "preserved_table");

    private static Task LockAsync(DbContext context, CancellationToken cancellationToken) =>
        ExecuteAsync(context, "SELECT pg_advisory_xact_lock(@key)", cancellationToken, ("key", LockKey));

    private async Task EnsureSchemaAsync(DbContext context, CancellationToken cancellationToken)
    {
        await ExecuteAsync(context, $"CREATE SCHEMA IF NOT EXISTS {Quote(_options.Schema)}", cancellationToken);
        await ExecuteAsync(context,
            $"CREATE TABLE IF NOT EXISTS {Marker} (id integer PRIMARY KEY, replay_id uuid NOT NULL, preserved_at timestamptz NOT NULL)",
            cancellationToken);
        await ExecuteAsync(context,
            $"CREATE TABLE IF NOT EXISTS {Mapping} (position integer PRIMARY KEY, source_schema text NOT NULL, source_table text NOT NULL, copy_table text NOT NULL)",
            cancellationToken);
    }

    private async Task<Guid?> ReadMarkerAsync(DbContext context, CancellationToken cancellationToken)
    {
        var exists = await ScalarAsync(context, "SELECT to_regclass(@marker) IS NOT NULL", cancellationToken, ("marker", Marker));
        if (exists is not true)
        {
            return null;
        }

        return await ScalarAsync(context, $"SELECT replay_id FROM {Marker} WHERE id = 1", cancellationToken) is Guid replayId
            ? replayId
            : null;
    }

    private async Task<List<(PreservedTable Table, string Copy)>> ReadMappingAsync(DbContext context, CancellationToken cancellationToken)
    {
        var mapping = new List<(PreservedTable Table, string Copy)>();
        await using var command = Command(context, $"SELECT source_schema, source_table, copy_table FROM {Mapping} ORDER BY position");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mapping.Add((new PreservedTable(reader.GetString(0), reader.GetString(1)), reader.GetString(2)));
        }

        return mapping;
    }

    private async Task DropPreservedAsync(DbContext context, List<(PreservedTable Table, string Copy)> preserved, CancellationToken cancellationToken)
    {
        foreach (var (_, copy) in preserved)
        {
            await ExecuteAsync(context, $"DROP TABLE IF EXISTS {Qualified(_options.Schema, copy)}", cancellationToken);
        }

        await ExecuteAsync(context, $"DELETE FROM {Mapping}", cancellationToken);
        await ExecuteAsync(context, $"DELETE FROM {Marker}", cancellationToken);
    }

    /// <summary>
    /// The live table's columns a restore inserts — all but generated ones — after checking that the copy carries
    /// exactly the live table's columns, in order: a schema change between the preservation and the restore would
    /// otherwise write rows into the wrong shape.
    /// </summary>
    private async Task<IReadOnlyList<string>> InsertableColumnsAsync(DbContext context, PreservedTable table, string copy, CancellationToken cancellationToken)
    {
        var live = await ColumnsAsync(context, table.Schema, table.Name, cancellationToken);
        var preserved = await ColumnsAsync(context, _options.Schema, copy, cancellationToken);
        if (!live.Select(column => column.Name).SequenceEqual(preserved.Select(column => column.Name), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The read model {table} no longer has the columns it had when it was preserved before the replay; a schema " +
                $"change ran in between. The preserved copy is kept as {Qualified(_options.Schema, copy)} and was not written back.");
        }

        return [.. live.Where(column => !column.Generated).Select(column => column.Name)];
    }

    private static async Task<List<(string Name, bool Generated)>> ColumnsAsync(DbContext context, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = new List<(string Name, bool Generated)>();
        await using var command = Command(context,
            """
            SELECT a.attname, a.attgenerated <> ''
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """,
            ("schema", schema), ("table", table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return columns;
    }

    /// <summary>
    /// Moves every sequence a column of the table owns past the restored rows, because the truncation that emptied the
    /// table may have restarted it, and the next insert would then collide with a restored row.
    /// </summary>
    private static async Task ResetSequencesAsync(DbContext context, PreservedTable table, CancellationToken cancellationToken)
    {
        var owned = new List<(string Column, string Sequence)>();
        await using (var command = Command(context,
                         """
                         SELECT a.attname, pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname)
                         FROM pg_attribute a
                         JOIN pg_class c ON c.oid = a.attrelid
                         JOIN pg_namespace n ON n.oid = c.relnamespace
                         WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
                         """,
                         ("schema", table.Schema), ("table", table.Name)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(1))
                {
                    owned.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (column, sequence) in owned)
        {
            await ExecuteAsync(context,
                $"SELECT setval(@sequence, COALESCE(MAX({Quote(column)}), 0) + 1, false) FROM {Qualified(table.Schema, table.Name)}",
                cancellationToken, ("sequence", sequence));
        }
    }

    /// <summary>
    /// The tables to preserve, parents before the tables that reference them. Every table the context maps — views,
    /// keyless types and SQL queries excluded — that exists in the database, minus the excluded ones, plus the additional
    /// ones. A table outside the set that references one inside it, or a cycle of references inside it, refuses the
    /// preservation: the restore could not write the set back.
    /// </summary>
    private async Task<List<PreservedTable>> OrderedTablesAsync(DbContext context, CancellationToken cancellationToken)
    {
        var wanted = PreservedTables.Of(context.Model, _options);

        var existing = new HashSet<PreservedTable>();
        foreach (var table in wanted)
        {
            if (await ScalarAsync(context, "SELECT to_regclass(@table) IS NOT NULL", cancellationToken, ("table", Qualified(table.Schema, table.Name))) is true)
            {
                existing.Add(table);
            }
        }

        var references = await ReferencesAsync(context, cancellationToken);
        if (references.FirstOrDefault(reference => existing.Contains(reference.Parent) && !existing.Contains(reference.Child)) is { Child: not null } outside)
        {
            throw new InvalidOperationException(
                $"The read models cannot be preserved for the replay: {outside.Child} references {outside.Parent}, and is not " +
                "among the tables a replay empties, so writing them back after a failed replay would fail. Add it with " +
                $"{nameof(ReadModelRestoreOptions)}.{nameof(ReadModelRestoreOptions.AdditionalTables)}, or remove the reference. Nothing was emptied.");
        }

        return Order([.. wanted.Where(existing.Contains)], references.Where(reference => existing.Contains(reference.Child)).ToList());
    }

    private static List<PreservedTable> Order(List<PreservedTable> tables, List<(PreservedTable Child, PreservedTable Parent)> references)
    {
        var parentsOf = tables.ToDictionary(
            table => table,
            table => references.Where(reference => reference.Child == table && reference.Parent != table).Select(reference => reference.Parent).ToHashSet());
        var ordered = new List<PreservedTable>(tables.Count);
        while (parentsOf.Count > 0)
        {
            var ready = parentsOf.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).OrderBy(table => table.ToString(), StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The read models cannot be preserved for the replay: {string.Join(", ", parentsOf.Keys)} reference each other in a " +
                    "cycle, so no order writes them back. Nothing was emptied.");
            }

            foreach (var table in ready)
            {
                ordered.Add(table);
                parentsOf.Remove(table);
            }

            foreach (var parents in parentsOf.Values)
            {
                parents.ExceptWith(ready);
            }
        }

        return ordered;
    }

    private static async Task<List<(PreservedTable Child, PreservedTable Parent)>> ReferencesAsync(DbContext context, CancellationToken cancellationToken)
    {
        var references = new List<(PreservedTable Child, PreservedTable Parent)>();
        await using var command = Command(context,
            """
            SELECT cn.nspname, cl.relname, pn.nspname, pl.relname
            FROM pg_constraint k
            JOIN pg_class cl ON cl.oid = k.conrelid
            JOIN pg_namespace cn ON cn.oid = cl.relnamespace
            JOIN pg_class pl ON pl.oid = k.confrelid
            JOIN pg_namespace pn ON pn.oid = pl.relnamespace
            WHERE k.contype = 'f'
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            references.Add((new PreservedTable(reader.GetString(0), reader.GetString(1)), new PreservedTable(reader.GetString(2), reader.GetString(3))));
        }

        return references;
    }

    /// <summary>
    /// The copy's name: the source's schema and table, joined; where that exceeds PostgreSQL's identifier length, a
    /// prefix and a hash of the whole, so two long names cannot be cut to the same copy.
    /// </summary>
    private static string CopyName(PreservedTable table)
    {
        var name = $"{table.Schema}__{table.Name}";
        if (Encoding.UTF8.GetByteCount(name) <= MaxIdentifierLength)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        var prefix = new string(name.TakeWhile((_, index) => Encoding.UTF8.GetByteCount(name[..(index + 1)]) <= MaxIdentifierLength - hash.Length - 1).ToArray());
        return $"{prefix}_{hash}";
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private static DbCommand Command(DbContext context, string sql, params (string Name, object Value)[] parameters)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (context.Database.GetCommandTimeout() is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task ExecuteAsync(DbContext context, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(context, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(DbContext context, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(context, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }
}
