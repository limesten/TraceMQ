using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class WriterServiceTests
{
    private static Channel<LogMessage> NewChannel() =>
        Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private static WriterService NewWriter(TempDb db, Channel<LogMessage> channel) =>
        new(channel,
            db.Factory,
            new WriterQueue(),
            Options.Create(new StorageOptions { DbPath = db.Path, BatchSize = 500, FlushIntervalMs = 20 }),
            NullLogger<WriterService>.Instance);

    // Ids come from ingest, not from SQLite, so the tests assign them the way ingest does.
    private static LogMessage Message(long id, string topic, string payload, string? key = null) =>
        new(id, 1_000_000 + id, topic, Encoding.UTF8.GetBytes(payload), 0, false, key);

    private static async Task WaitForRows(TempDb db, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (db.Scalar<int>("SELECT count(*) FROM messages;") < expected)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task WritesEveryMessageItIsGiven()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var channel = NewChannel();
        var writer = NewWriter(db, channel);
        await writer.StartAsync(CancellationToken.None);

        for (var i = 0; i < 1500; i++)
        {
            Assert.True(channel.Writer.TryWrite(Message(i + 1, $"codeit/a/{i % 7}", $$"""{"n":{{i}}}""")));
        }

        await WaitForRows(db, 1500);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1500, db.Scalar<int>("SELECT count(*) FROM messages;"));
        // Ids are the tail cursor: contiguous, ascending, no gaps.
        Assert.Equal(1, db.Scalar<int>("SELECT min(id) FROM messages;"));
        Assert.Equal(1500, db.Scalar<int>("SELECT max(id) FROM messages;"));
        Assert.Equal(1500, db.Scalar<int>("SELECT count(DISTINCT id) FROM messages;"));
    }

    [Fact]
    public async Task StoresTheCorrelationKeyAndLeavesItNullWhenThereIsNone()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var channel = NewChannel();
        var writer = NewWriter(db, channel);
        await writer.StartAsync(CancellationToken.None);

        channel.Writer.TryWrite(Message(1, "codeit/a/trigger", """{"trigger":{"uid":"KEY"}}""", "KEY"));
        channel.Writer.TryWrite(Message(2, "codeit/conveyor/state", """{"state":"RUNNING"}"""));

        await WaitForRows(db, 2);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal("KEY", db.Scalar<string>("SELECT correlation_key FROM messages WHERE id = 1;"));
        Assert.Equal(1, db.Scalar<int>("SELECT count(*) FROM messages WHERE correlation_key IS NULL;"));
    }

    [Fact]
    public async Task PreservesNonUtf8PayloadsByteForByte()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var channel = NewChannel();
        var writer = NewWriter(db, channel);
        await writer.StartAsync(CancellationToken.None);

        channel.Writer.TryWrite(new LogMessage(1, 1, "codeit/a/binary", [0x00, 0xFF, 0x00, 0xFF], 0, false, null));

        await WaitForRows(db, 1);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal("00FF00FF", db.Scalar<string>("SELECT hex(payload) FROM messages WHERE id = 1;"));
    }

    [Fact]
    public async Task SurvivesAPayloadThatIsNotJsonAtAll()
    {
        // The writer must not care. Extraction already happened upstream, and one unparseable
        // message must not take the rest of its batch down with it.
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var channel = NewChannel();
        var writer = NewWriter(db, channel);
        await writer.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100; i++)
        {
            channel.Writer.TryWrite(Message(i + 1, "codeit/a", i == 50 ? "}}not json{{" : """{"ok":1}"""));
        }

        await WaitForRows(db, 100);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(100, db.Scalar<int>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task IngestToStorageCarriesTheKeyThroughEndToEnd()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var extractor = new CorrelationExtractor(["trigger.uid", "header.correlationId"]);
        var channel = NewChannel();
        var writer = NewWriter(db, channel);
        await writer.StartAsync(CancellationToken.None);

        // What MqttIngestService does per message: extract, then hand off.
        var nextId = 0L;
        foreach (var payload in new[]
        {
            """{"trigger":{"uid":"7347ba7c-d1e5-41c4-be7e-c1b19e3a0b0c"}}""",
            """{"header":{"correlationId":"c0f2e881-4a63-4d9e-9f21-7b0a5d3e6c14"}}""",
            """{"state":"RUNNING"}""",
        })
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            channel.Writer.TryWrite(new LogMessage(++nextId, 1, "codeit/a", bytes, 0, false, extractor.Extract(bytes)));
        }

        await WaitForRows(db, 3);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["7347ba7c-d1e5-41c4-be7e-c1b19e3a0b0c", "c0f2e881-4a63-4d9e-9f21-7b0a5d3e6c14"],
            db.Strings("SELECT correlation_key FROM messages WHERE correlation_key IS NOT NULL ORDER BY id;"));
    }
}
