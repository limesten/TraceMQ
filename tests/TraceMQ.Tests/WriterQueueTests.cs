using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class WriterQueueTests
{
    private static (TempDb Db, WriterQueue Queue, WriterService Writer, Channel<LogMessage> Channel) Start()
    {
        var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var channel = Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var queue = new WriterQueue();
        var writer = new WriterService(
            channel,
            db.Factory,
            queue,
            Options.Create(new StorageOptions { DbPath = db.Path, BatchSize = 500, FlushIntervalMs = 20 }),
            NullLogger<WriterService>.Instance);
        return (db, queue, writer, channel);
    }

    [Fact]
    public async Task RunsQueuedWorkOnAnIdleStream()
    {
        // The case that would starve if the writer only ever woke on messages: nothing is
        // arriving, which is exactly when a retention sweep wants its turn.
        var (db, queue, writer, _) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        var result = await queue.EnqueueAsync("count", async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 41 + 1;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }).WaitAsync(TimeSpan.FromSeconds(10));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task QueuedWritesLandOnTheSameConnectionAsTheStream()
    {
        var (db, queue, writer, channel) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        for (var i = 1; i <= 50; i++)
        {
            channel.Writer.TryWrite(new LogMessage(i, i, "codeit/a", [0x00], 0, false, null));
        }

        // Queued work and stream batches interleave freely — the writer drains the queue
        // before flushing — so wait for the stream to land before asking about its rows.
        // Asserting across that boundary would be asserting an ordering nothing promises.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (db.Scalar<int>("SELECT count(*) FROM messages;") < 50)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await queue.EnqueueAsync("delete-old", async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE ts < 10;";
            return await cmd.ExecuteNonQueryAsync(ct);
        }).WaitAsync(TimeSpan.FromSeconds(10));

        await writer.StopAsync(CancellationToken.None);

        // The queued DELETE saw the rows the stream had written, on the same connection,
        // with no lock contention between them.
        Assert.Equal(0, db.Scalar<int>("SELECT count(*) FROM messages WHERE ts < 10;"));
        Assert.Equal(41, db.Scalar<int>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task AFailedItemFaultsItsCallerAndNotTheWriter()
    {
        var (db, queue, writer, _) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        var failing = queue.EnqueueAsync<int>("boom", (_, _) => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(TimeSpan.FromSeconds(10)));

        // The writer is still running and still serving the queue.
        var after = await queue.EnqueueAsync("still-alive", async (connection, ct) =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 7;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }).WaitAsync(TimeSpan.FromSeconds(10));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(7, after);
    }
}
