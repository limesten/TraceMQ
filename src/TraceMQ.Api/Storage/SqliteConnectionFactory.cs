using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TraceMQ.Api.Storage;

/// <summary>
/// The one place that knows where the database is and how a connection to it is configured.
/// Every connection gets busy_timeout; WAL and synchronous are database-level and set once
/// by <see cref="Schema"/> at startup.
/// </summary>
public sealed class SqliteConnectionFactory
{
    public string DbPath { get; }
    public string ConnectionString { get; }

    public SqliteConnectionFactory(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        DbPath = ResolvePath(options.Value, env);

        var directory = Path.GetDirectoryName(DbPath)!;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Cannot create the database directory '{directory}'. Set Storage:DbPath in " +
                "appsettings.json (or the Storage__DbPath environment variable) to a writable " +
                "location.", ex);
        }
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// A connection with the per-connection pragmas applied. Readers call this and dispose
    /// promptly; exactly one long-lived connection from here belongs to WriterService, which
    /// is the only thing that may write (ARCHITECTURE.md section 4, rule 2).
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static string ResolvePath(StorageOptions options, IHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(options.DbPath))
        {
            return Path.GetFullPath(options.DbPath);
        }

        if (env.IsDevelopment())
        {
            return Path.Combine(env.ContentRootPath, "tracemq.db");
        }

        // %ProgramData% on Windows, which is where this ships. CommonApplicationData maps to
        // /usr/share elsewhere and is not writable, so a developer running a Production build
        // on macOS or Linux would otherwise crash before reaching a single line of config.
        var root = Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(root, "CodeIT", "tracemq", "data.db");
    }
}
