namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>Creates a store database a test host writes to, if it does not exist.</summary>
public static class PostgresTimerHostSchema
{
    public static async Task EnsureDatabaseAsync(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database!;
        builder.Database = "postgres";
        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new Npgsql.NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }
}
