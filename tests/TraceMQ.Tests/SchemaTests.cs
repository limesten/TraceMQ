using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class SchemaTests
{
    private const string LegacySchema = """
        CREATE TABLE messages (
          id          INTEGER PRIMARY KEY,
          ts          INTEGER NOT NULL,
          topic       TEXT    NOT NULL,
          sequence_id TEXT,
          service     TEXT,
          qos         INTEGER,
          retained    INTEGER,
          payload     BLOB
        );
        CREATE INDEX ix_messages_seq ON messages(sequence_id) WHERE sequence_id IS NOT NULL;
        CREATE INDEX ix_messages_ts ON messages(ts);
        CREATE INDEX ix_messages_topic ON messages(topic, ts);
        """;

    private static void Initialize(TempDb db) =>
        Schema.Initialize(db.Factory, NullLogger.Instance);

    [Fact]
    public void CreatesTheV1ShapeOnAFreshDatabase()
    {
        using var db = new TempDb();

        Initialize(db);

        Assert.Equal(Schema.CurrentVersion, db.Scalar<int>("PRAGMA user_version;"));
        Assert.Equal(
            ["id", "ts", "topic", "correlation_key", "qos", "retained", "payload"],
            db.Strings("SELECT name FROM pragma_table_info('messages');"));
        Assert.Contains("settings", db.Strings("SELECT name FROM sqlite_master WHERE type = 'table';"));
    }

    [Fact]
    public void EnablesWalAndKeepsIt()
    {
        using var db = new TempDb();

        Initialize(db);

        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public void CreatesThePartialIndexOnCorrelationKey()
    {
        using var db = new TempDb();

        Initialize(db);

        var sql = db.Strings("SELECT sql FROM sqlite_master WHERE name = 'ix_messages_corr';").Single();
        Assert.Contains("correlation_key", sql);
        Assert.Contains("WHERE correlation_key IS NOT NULL", sql);
    }

    [Fact]
    public void MigratesALegacyDatabaseWithoutLosingMessages()
    {
        using var db = new TempDb();
        db.Execute(LegacySchema);
        db.Execute("""
            INSERT INTO messages (ts, topic, sequence_id, service, qos, retained, payload) VALUES
              (1789893541861, 'codeit/a/trigger', 'old-id', 'svc', 0, 0, '{"trigger":{"uid":"abc"}}'),
              (1789893541902, 'codeit/a/scanner', NULL,     'svc', 1, 1, '{"trigger":{"uid":"abc"}}'),
              (1789893544314, 'codeit/a/binary',  NULL,     'svc', 0, 0, x'00ff00ff');
            """);

        Initialize(db);

        Assert.Equal(Schema.CurrentVersion, db.Scalar<int>("PRAGMA user_version;"));
        Assert.Equal(3, db.Scalar<int>("SELECT count(*) FROM messages;"));
        Assert.Equal(
            ["id", "ts", "topic", "correlation_key", "qos", "retained", "payload"],
            db.Strings("SELECT name FROM pragma_table_info('messages');"));

        // Ids are the tail cursor, so they have to survive the rebuild unchanged.
        Assert.Equal(["1", "2", "3"], db.Strings("SELECT CAST(id AS TEXT) FROM messages ORDER BY id;"));
        // A non-UTF-8 payload must come through byte for byte.
        Assert.Equal("00FF00FF", db.Scalar<string>("SELECT hex(payload) FROM messages WHERE id = 3;"));
        Assert.Equal(1, db.Scalar<int>("SELECT qos FROM messages WHERE id = 2;"));
    }

    [Fact]
    public void CorrelationKeyComparesCaseInsensitively()
    {
        using var db = new TempDb();
        Initialize(db);
        db.Execute("""
            INSERT INTO messages (ts, topic, correlation_key, qos, retained, payload)
            VALUES (1, 'codeit/a', '7347BA7C-D1E5-41C4-BE7E-C1B19E3A0B0C', 0, 0, x'00');
            """);

        var found = db.Scalar<int>("""
            SELECT count(*) FROM messages
            WHERE correlation_key = '7347ba7c-d1e5-41c4-be7e-c1b19e3a0b0c';
            """);

        Assert.Equal(1, found);
    }

    [Fact]
    public void IsIdempotent()
    {
        using var db = new TempDb();

        Initialize(db);
        Initialize(db);
        Initialize(db);

        Assert.Equal(Schema.CurrentVersion, db.Scalar<int>("PRAGMA user_version;"));
        Assert.Equal(1, db.Scalar<int>("SELECT count(*) FROM sqlite_master WHERE name = 'messages';"));
    }

    [Fact]
    public void RefusesADatabaseFromANewerBuild()
    {
        using var db = new TempDb();
        Initialize(db);
        db.Execute($"PRAGMA user_version = {Schema.CurrentVersion + 1};");

        var ex = Assert.Throws<InvalidOperationException>(() => Initialize(db));

        Assert.Contains("newer than this build", ex.Message);
    }
}

public class SqliteConnectionFactoryTests
{
    private sealed class Env(string name, string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "TraceMQ.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static SqliteConnectionFactory Build(StorageOptions options, string environment, string root) =>
        new(Options.Create(options), new Env(environment, root));

    [Fact]
    public void AnExplicitPathWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "tracemq-tests", Guid.NewGuid().ToString("n"));
        var wanted = Path.Combine(root, "custom", "data.db");

        var factory = Build(new StorageOptions { DbPath = wanted }, "Production", root);

        Assert.Equal(wanted, factory.DbPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(wanted)));
    }

    [Fact]
    public void DevelopmentKeepsTheDatabaseBesideTheProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "tracemq-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        var factory = Build(new StorageOptions(), "Development", root);

        Assert.Equal(Path.Combine(root, "tracemq.db"), factory.DbPath);
    }

    [Fact]
    public void ProductionResolvesSomewhereWritableOnThisPlatform()
    {
        // The regression: CommonApplicationData is %ProgramData% on Windows but /usr/share
        // elsewhere, so a Production build crashed at startup on macOS and Linux before
        // reaching any configuration.
        var root = Path.Combine(Path.GetTempPath(), "tracemq-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        var factory = Build(new StorageOptions(), "Production", root);

        Assert.EndsWith(Path.Combine("CodeIT", "tracemq", "data.db"), factory.DbPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(factory.DbPath)));
    }

    [Fact]
    public void AnUnwritablePathSaysWhatToChange()
    {
        var unwritable = OperatingSystem.IsWindows() ? @"\\?\Z:\nope\data.db" : "/proc/nope/data.db";

        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(new StorageOptions { DbPath = unwritable }, "Production", "."));

        Assert.Contains("Storage:DbPath", ex.Message);
    }
}
