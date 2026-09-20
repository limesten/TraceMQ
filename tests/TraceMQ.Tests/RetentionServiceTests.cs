using Microsoft.Extensions.Logging.Abstractions;
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
