using Microsoft.Data.Sqlite;

namespace TraceMQ.Api.Storage;

/// <summary>
/// Owns the database shape. Runs once at startup, before any hosted service, on its own
/// connection. Migrations are keyed on PRAGMA user_version.
/// </summary>
public static class Schema
{
    public const int CurrentVersion = 1;

    public static void Initialize(SqliteConnectionFactory factory, ILogger log)
    {
        using var connection = factory.Open();

        // Database-level, persisted in the file header: setting these once here is enough.
        // Without WAL a reader blocks the writer and the UI stalls the logger.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                """;
            pragma.ExecuteNonQuery();
        }

        var version = ReadUserVersion(connection);
        if (version == CurrentVersion)
        {
            log.LogInformation("Database at {Path}, schema v{Version}", factory.DbPath, version);
            return;
        }

        if (version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database at {factory.DbPath} is schema v{version}, newer than this build (v{CurrentVersion}).");
        }

        log.LogInformation("Migrating {Path} from schema v{From} to v{To}", factory.DbPath, version, CurrentVersion);

        using var transaction = connection.BeginTransaction();
        if (version < 1)
        {
            MigrateTo1(connection, transaction, log);
        }
        SetUserVersion(connection, transaction, CurrentVersion);
        transaction.Commit();
    }

    /// <summary>
    /// v1 is the correlation-key shape. A database from before the rename carries a
    /// `messages` table with `sequence_id` and `service`; rebuild it rather than dropping
    /// it, so a developer does not lose the capture they were looking at.
    /// </summary>
    private static void MigrateTo1(SqliteConnection connection, SqliteTransaction transaction, ILogger log)
    {
        var legacy = TableExists(connection, transaction, "messages")
            && !ColumnExists(connection, transaction, "messages", "correlation_key");

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages_v1 (
                id              INTEGER PRIMARY KEY,
                ts              INTEGER NOT NULL,
                topic           TEXT    NOT NULL,
                correlation_key TEXT COLLATE NOCASE,
                qos             INTEGER,
                retained        INTEGER,
                payload         BLOB
            );
            """;
        cmd.ExecuteNonQuery();

        if (legacy)
        {
            log.LogInformation("Rebuilding the legacy messages table onto the v1 shape");
            cmd.CommandText = """
                INSERT INTO messages_v1 (id, ts, topic, correlation_key, qos, retained, payload)
                SELECT id, ts, topic, NULL, qos, retained, payload FROM messages;
                """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DROP TABLE messages;";
            cmd.ExecuteNonQuery();
        }
        else if (TableExists(connection, transaction, "messages"))
        {
            cmd.CommandText = "DROP TABLE messages_v1;";
            cmd.ExecuteNonQuery();
        }

        if (TableExists(connection, transaction, "messages_v1"))
        {
            cmd.CommandText = "ALTER TABLE messages_v1 RENAME TO messages;";
            cmd.ExecuteNonQuery();
        }

        // The partial index skips the noise topics that carry no key.
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_messages_corr
                ON messages(correlation_key) WHERE correlation_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_messages_ts    ON messages(ts);
            CREATE INDEX IF NOT EXISTS ix_messages_topic ON messages(topic, ts);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column;";
        cmd.Parameters.AddWithValue("$column", column);
        return cmd.ExecuteScalar() is not null;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // PRAGMA does not take parameters; the value is an int constant from this assembly.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}
