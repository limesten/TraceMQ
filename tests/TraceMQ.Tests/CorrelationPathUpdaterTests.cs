using Microsoft.Extensions.Configuration;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class CorrelationPathUpdaterTests
{
    private sealed class Harness : IDisposable
    {
        public TempDb Db { get; } = new();
        public CorrelationSettings Settings { get; }
        public RecentKeys RecentKeys { get; } = new(20);
        public CorrelationPathUpdater Updater { get; }
        private readonly WriterService _writer;

        public Harness(params string[] initialPaths)
        {
            Schema.Initialize(Db.Factory, NullLogger.Instance);
            Settings = new CorrelationSettings(new CorrelationExtractor(initialPaths));
            var queue = new WriterQueue();
            _writer = new WriterService(
                Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(100) { SingleReader = true }),
                Db.Factory,
                queue,
                Options.Create(new StorageOptions { DbPath = Db.Path, FlushIntervalMs = 20 }),
                NullLogger<WriterService>.Instance);
            _writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Updater = new CorrelationPathUpdater(
                queue, Settings, RecentKeys, Db.Factory, NullLogger<CorrelationPathUpdater>.Instance);
        }

        public void Insert(long id, string payload, string? key = null)
        {
            using var connection = Db.Factory.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES ($id, $id, 'codeit/a', $key, 0, 0, $payload);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$payload", Encoding.UTF8.GetBytes(payload));
            cmd.ExecuteNonQuery();
        }

        public void InsertBinary(long id)
        {
            using var connection = Db.Factory.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES ($id, $id, 'codeit/a', 'stale', 0, 0, x'00ff00ff');
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _writer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task BackfillsTheKeyAcrossMessagesAlreadyStored()
    {
        // The case it exists for: the path was wrong, and a day of history is unsearchable
        // until the column is rewritten.
        using var h = new Harness("wrong.path");
        h.Insert(1, """{"trigger":{"uid":"KEY-1"}}""");
        h.Insert(2, """{"trigger":{"uid":"KEY-2"}}""");

        var update = await h.Updater.UpdateAsync(["trigger.uid"]);

        Assert.Equal(2, update.Rewritten);
        Assert.Equal(["KEY-1", "KEY-2"], h.Db.Strings("SELECT correlation_key FROM messages ORDER BY id;"));
    }

    [Fact]
    public async Task ClearsKeysThatTheNewPathsDoNotResolve()
    {
        using var h = new Harness("trigger.uid");
        h.Insert(1, """{"trigger":{"uid":"OLD"}}""", key: "OLD");

        await h.Updater.UpdateAsync(["header.correlationId"]);

        Assert.Equal(0, h.Db.Scalar<int>("SELECT count(*) FROM messages WHERE correlation_key IS NOT NULL;"));
    }

    [Fact]
    public async Task SurvivesPayloadsThatAreNotJson()
    {
        // json_extract raises on malformed JSON, so the statement is guarded; without the
        // guard one bad row would abort the rewrite for every other row.
        using var h = new Harness("trigger.uid");
        h.Insert(1, """{"trigger":{"uid":"KEY"}}""");
        h.Insert(2, "not json at all", key: "stale");
        h.InsertBinary(3);

        var update = await h.Updater.UpdateAsync(["trigger.uid"]);

        Assert.Equal(3, update.Rewritten);
        Assert.Equal("KEY", h.Db.Scalar<string>("SELECT correlation_key FROM messages WHERE id = 1;"));
        Assert.Equal(2, h.Db.Scalar<int>("SELECT count(*) FROM messages WHERE correlation_key IS NULL;"));
    }

    [Fact]
    public async Task UsesTheFirstPathThatResolvesPerRow()
    {
        using var h = new Harness("trigger.uid");
        h.Insert(1, """{"trigger":{"uid":"FROM-FIRST"}}""");
        h.Insert(2, """{"header":{"correlationId":"FROM-SECOND"}}""");

        await h.Updater.UpdateAsync(["trigger.uid", "header.correlationId"]);

        Assert.Equal(
            ["FROM-FIRST", "FROM-SECOND"],
            h.Db.Strings("SELECT correlation_key FROM messages ORDER BY id;"));
    }

    [Fact]
    public async Task PersistsThePathsSoTheySurviveARestart()
    {
        using var h = new Harness("trigger.uid");

        await h.Updater.UpdateAsync(["$.header.correlationId", "trigger.uid"]);

        var reloaded = CorrelationPaths.Load(
            h.Db.Factory, new ConfigurationBuilder().Build(), NullLogger.Instance);
        Assert.Equal(["header.correlationId", "trigger.uid"], reloaded.Paths);
    }

    [Fact]
    public async Task SwapsTheExtractorThatIngestUses()
    {
        using var h = new Harness("trigger.uid");

        await h.Updater.UpdateAsync(["header.correlationId"]);

        var payload = Encoding.UTF8.GetBytes("""{"header":{"correlationId":"NEW"}}""");
        Assert.Equal("NEW", h.Settings.Extractor.Extract(payload));
    }

    [Fact]
    public async Task ForgetsRecentKeysExtractedUnderTheOldPaths()
    {
        using var h = new Harness("trigger.uid");
        h.RecentKeys.Record("under-old-paths", 1);

        await h.Updater.UpdateAsync(["header.correlationId"]);

        Assert.DoesNotContain("under-old-paths", h.RecentKeys.Snapshot().Select(k => k.Key));
    }

    [Fact]
    public void RefusesAPathSetThatResolvesToNothing()
    {
        Assert.Throws<ArgumentException>(() => CorrelationPathUpdater.Validate(null));
        Assert.Throws<ArgumentException>(() => CorrelationPathUpdater.Validate([]));
        Assert.Throws<ArgumentException>(() => CorrelationPathUpdater.Validate([""]));
        Assert.Throws<ArgumentException>(() => CorrelationPathUpdater.Validate(["   ", "$."]));
    }

    [Fact]
    public void NormalisesAndDeduplicates()
    {
        Assert.Equal(
            ["trigger.uid", "header.correlationId"],
            CorrelationPathUpdater.Validate(["$.trigger.uid", " trigger . uid ", "header.correlationId"]));
    }

    [Fact]
    public void CapsThePathCount()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"p{i}.uid").ToArray();

        Assert.Equal(CorrelationExtractor.MaxPaths, CorrelationPathUpdater.Validate(many).Count);
    }
}
