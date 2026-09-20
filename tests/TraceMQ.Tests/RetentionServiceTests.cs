using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class RetentionServiceTests
{
    private static void Seed(TempDb db, params (long Id, long Ts)[] rows)
    {
        foreach (var (id, ts) in rows)
        {
            db.Execute($"""
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES ({id}, {ts}, 'codeit/a', NULL, 0, 0, x'00');
                """);
        }
    }

    private static async Task<int> Sweep(TempDb db, long cutoff)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await RetentionService.SweepAsync(connection, cutoff, CancellationToken.None);
    }

    [Fact]
    public async Task RemovesOnlyWhatIsPastTheWindow()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        Seed(db, (1, 100), (2, 200), (3, 300), (4, 400));

        var removed = await Sweep(db, cutoff: 300);

        Assert.Equal(2, removed);
        Assert.Equal(["3", "4"], db.Strings("SELECT CAST(id AS TEXT) FROM messages ORDER BY id;"));
    }

    [Fact]
    public async Task RemovesNothingWhenEverythingIsInsideTheWindow()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        Seed(db, (1, 500), (2, 600));

        Assert.Equal(0, await Sweep(db, cutoff: 100));
        Assert.Equal(2, db.Scalar<int>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task ChunksThroughABacklogLargerThanOneChunk()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        // More than ChunkSize, so the loop has to run more than once to clear it.
        db.Execute($"""
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {RetentionService.ChunkSize + 500})
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            SELECT n, n, 'codeit/a', NULL, 0, 0, x'00' FROM seq;
            """);

        var removed = await Sweep(db, cutoff: RetentionService.ChunkSize + 1);

        Assert.Equal(RetentionService.ChunkSize, removed);
        Assert.Equal(500, db.Scalar<int>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task LeavesTheDatabaseUsableAndDoesNotVacuum()
    {
        // A retention delete does not shrink the file, and it must not: VACUUM takes an
        // exclusive lock and stalls ingest. Freed pages get reused instead.
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        Seed(db, (1, 100), (2, 200));

        await Sweep(db, cutoff: 1_000);

        Assert.Equal(0, db.Scalar<int>("SELECT count(*) FROM messages;"));
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode;"));
        db.Execute("""
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            VALUES (3, 300, 'codeit/a', NULL, 0, 0, x'00');
            """);
        Assert.Equal(1, db.Scalar<int>("SELECT count(*) FROM messages;"));
    }
}

/// <summary>
/// The loop, as opposed to the sweep. Everything here was untested: that it ticks more than
/// once, that cancellation gets out of it, and that stopping does not strand a sweep that is
/// waiting on the writer.
/// </summary>
public class RetentionLoopTests
{
    private sealed class Harness : IDisposable
    {
        public TempDb Db { get; } = new();
        private readonly WriterService _writer;
        private readonly RetentionService _retention;

        /// <param name="seed">
        /// Runs before the services start. The startup sweep races anything inserted after
        /// StartAsync: it can run first, find nothing, and leave the row for a tick that is
        /// an hour away.
        /// </param>
        public Harness(double sweepMinutes, int retentionDays = 1, Action<Harness>? seed = null)
        {
            Schema.Initialize(Db.Factory, NullLogger.Instance);
            seed?.Invoke(this);
            var queue = new WriterQueue();
            var options = Options.Create(new StorageOptions
            {
                DbPath = Db.Path,
                FlushIntervalMs = 20,
                RetentionDays = retentionDays,
                RetentionSweepMinutes = sweepMinutes,
            });

            _writer = new WriterService(
                Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(100) { SingleReader = true }),
                Db.Factory, queue, options, NullLogger<WriterService>.Instance);
            _retention = new RetentionService(queue, options, NullLogger<RetentionService>.Instance);

            _writer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _retention.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public void InsertOld(long id)
        {
            var ancient = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
            Db.Execute($"""
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES ({id}, {ancient}, 'codeit/a', NULL, 0, 0, x'00');
                """);
        }

        public async Task<bool> WaitUntilEmpty(TimeSpan within)
        {
            using var timeout = new CancellationTokenSource(within);
            try
            {
                while (Db.Scalar<int>("SELECT count(*) FROM messages;") > 0)
                {
                    await Task.Delay(20, timeout.Token);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task<TimeSpan> StopAsync()
        {
            var stop = System.Diagnostics.Stopwatch.StartNew();
            await _retention.StopAsync(CancellationToken.None);
            await _writer.StopAsync(CancellationToken.None);
            stop.Stop();
            return stop.Elapsed;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task SweepsOnStartupWithoutWaitingForTheFirstTick()
    {
        // Seeded before the services start, so the startup sweep cannot miss it. A
        // sixty-minute interval means that if this only ran on a tick, it would not run.
        using var h = new Harness(sweepMinutes: 60, seed: harness => harness.InsertOld(1));

        Assert.True(await h.WaitUntilEmpty(TimeSpan.FromSeconds(10)), "the startup sweep did not run");

        await h.StopAsync();
    }

    [Fact]
    public async Task KeepsSweepingOnEveryTick()
    {
        // The part the sweep tests could never cover: that the loop goes round again. The
        // first sweep happens before the first tick, so only the second proves the loop.
        using var h = new Harness(sweepMinutes: 0.001, seed: harness => harness.InsertOld(1));
        Assert.True(await h.WaitUntilEmpty(TimeSpan.FromSeconds(10)), "the first sweep did not run");

        h.InsertOld(2);

        Assert.True(await h.WaitUntilEmpty(TimeSpan.FromSeconds(10)), "the loop did not tick again");
        await h.StopAsync();
    }

    [Fact]
    public async Task StopsPromptlyWhileSweepsAreCycling()
    {
        // Retention awaits a task only the writer can complete. If that await ever outlived
        // the writer, shutdown would hang here rather than on anyone's machine.
        using var h = new Harness(sweepMinutes: 0.001);
        for (var i = 1; i <= 200; i++) h.InsertOld(i);
        await Task.Delay(100);

        var elapsed = await h.StopAsync();

        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"shutdown took {elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task DoesNothingAtAllWhenRetentionIsDisabled()
    {
        using var h = new Harness(sweepMinutes: 0.001, retentionDays: 0);
        h.InsertOld(1);
        await Task.Delay(300);

        Assert.Equal(1, h.Db.Scalar<int>("SELECT count(*) FROM messages;"));
        await h.StopAsync();
    }
}
