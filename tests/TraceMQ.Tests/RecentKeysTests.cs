using Microsoft.Extensions.Logging.Abstractions;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class RecentKeysTests
{
    [Fact]
    public void ListsMostRecentlySeenFirst()
    {
        var keys = new RecentKeys(10);
        keys.Record("a", 100);
        keys.Record("b", 300);
        keys.Record("c", 200);

        Assert.Equal(["b", "c", "a"], keys.Snapshot().Select(k => k.Key));
    }

    [Fact]
    public void CountsRepeatsWithoutDuplicatingTheKey()
    {
        var keys = new RecentKeys(10);
        keys.Record("a", 100);
        keys.Record("a", 200);
        keys.Record("a", 150);

        var snapshot = keys.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(3, snapshot[0].Count);
        // Out-of-order arrivals must not pull the timestamp backwards.
        Assert.Equal(200, snapshot[0].LastSeenTs);
    }

    [Fact]
    public void EvictsTheOldestOnceFull()
    {
        var keys = new RecentKeys(3);
        keys.Record("oldest", 100);
        keys.Record("b", 200);
        keys.Record("c", 300);

        keys.Record("d", 400);

        Assert.Equal(["d", "c", "b"], keys.Snapshot().Select(k => k.Key));
    }

    [Fact]
    public void DoesNotEvictAFresherKeyForAStalerOne()
    {
        var keys = new RecentKeys(2);
        keys.Record("b", 200);
        keys.Record("c", 300);

        keys.Record("ancient", 1);

        Assert.Equal(["c", "b"], keys.Snapshot().Select(k => k.Key));
    }

    [Fact]
    public void TreatsKeysCaseInsensitivelyLikeTheColumnDoes()
    {
        var keys = new RecentKeys(10);
        keys.Record("7347BA7C-D1E5-41C4", 100);
        keys.Record("7347ba7c-d1e5-41c4", 200);

        Assert.Single(keys.Snapshot());
    }

    [Fact]
    public void IgnoresEmptyKeys()
    {
        var keys = new RecentKeys(10);
        keys.Record("", 100);

        Assert.Empty(keys.Snapshot());
    }

    [Fact]
    public void SeedingFromTheDatabaseSurvivesARestart()
    {
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        db.Execute("""
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload) VALUES
              (1, 100, 'codeit/a', 'old',    0, 0, x'00'),
              (2, 300, 'codeit/a', 'newest', 0, 0, x'00'),
              (3, 200, 'codeit/a', 'middle', 0, 0, x'00'),
              (4, 310, 'codeit/a', 'newest', 0, 0, x'00'),
              (5, 400, 'codeit/a', NULL,     0, 0, x'00');
            """);

        var keys = new RecentKeys(10);
        keys.SeedFrom(db.Factory);
        var snapshot = keys.Snapshot();

        Assert.Equal(["newest", "middle", "old"], snapshot.Select(k => k.Key));
        Assert.Equal(2, snapshot[0].Count);
        Assert.Equal(310, snapshot[0].LastSeenTs);
    }

    [Fact]
    public void ConcurrentRecordingStaysWithinCapacity()
    {
        var keys = new RecentKeys(20);

        Parallel.For(0, 5_000, i => keys.Record($"key-{i % 200}", i));

        Assert.True(keys.Snapshot().Count <= 20);
    }
}
