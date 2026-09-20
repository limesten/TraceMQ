using Microsoft.Extensions.Logging.Abstractions;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class MessageRingTests
{
    private static LogMessage Add(MessageRing ring, string topic = "codeit/a", string? key = null)
    {
        var msg = new LogMessage(ring.NextId(), 0, topic, [], 0, false, key);
        ring.Add(msg);
        return msg;
    }

    [Fact]
    public void IdsStartAtOneAndAreContiguous()
    {
        var ring = new MessageRing(16);

        var ids = Enumerable.Range(0, 5).Select(_ => Add(ring).Id).ToArray();

        Assert.Equal([1L, 2L, 3L, 4L, 5L], ids);
        Assert.Equal(5, ring.HighWater);
    }

    [Fact]
    public void SeedingContinuesFromWhatIsOnDisk()
    {
        var ring = new MessageRing(16);
        ring.SeedFrom(4_000);

        Assert.Equal(4_001, Add(ring).Id);
        Assert.Equal(4_001, ring.HighWater);
    }

    [Fact]
    public void SeedingDoesNotClaimToHoldMessagesItDoesNotHave()
    {
        // The regression: seeding used to move the high water mark as well, so straight after
        // a restart the ring reported ids it had never seen and served an empty page for
        // them, and the table came up blank against a database full of messages.
        var ring = new MessageRing(16);

        ring.SeedFrom(4_000);

        Assert.Equal(0, ring.HighWater);
        Assert.Empty(ring.After(afterId: 0, limit: 10));
        Assert.Empty(ring.Latest(limit: 10));
    }

    [Fact]
    public void AfterReturnsOnlyWhatIsNewerOldestFirst()
    {
        var ring = new MessageRing(64);
        for (var i = 0; i < 10; i++) Add(ring);

        var page = ring.After(afterId: 7, limit: 100);

        Assert.Equal([8L, 9L, 10L], page.Select(m => m.Id));
    }

    [Fact]
    public void AfterRespectsTheLimit()
    {
        var ring = new MessageRing(64);
        for (var i = 0; i < 10; i++) Add(ring);

        Assert.Equal([1L, 2L], ring.After(afterId: 0, limit: 2).Select(m => m.Id));
    }

    [Fact]
    public void AfterOnAnEmptyRingIsEmpty()
    {
        Assert.Empty(new MessageRing(16).After(afterId: 0, limit: 10));
    }

    [Fact]
    public void LatestReturnsNewestFirstWhichIsDisplayOrder()
    {
        var ring = new MessageRing(64);
        for (var i = 0; i < 10; i++) Add(ring);

        Assert.Equal([10L, 9L, 8L], ring.Latest(limit: 3).Select(m => m.Id));
    }

    [Fact]
    public void OverwrittenEntriesAreDroppedRatherThanReturnedStale()
    {
        var ring = new MessageRing(8);
        for (var i = 0; i < 20; i++) Add(ring);

        // Only the last 8 ids can still be in the ring.
        var page = ring.After(afterId: 0, limit: 100);

        Assert.Equal([13L, 14L, 15L, 16L, 17L, 18L, 19L, 20L], page.Select(m => m.Id));
        Assert.All(page, m => Assert.True(m.Id > 12));
    }

    [Fact]
    public void FiltersWithoutBreakingOrder()
    {
        var ring = new MessageRing(64);
        for (var i = 0; i < 10; i++) Add(ring, topic: i % 2 == 0 ? "codeit/even" : "codeit/odd");

        var page = ring.After(0, 100, m => m.Topic == "codeit/even");

        Assert.Equal([1L, 3L, 5L, 7L, 9L], page.Select(m => m.Id));
    }

    [Fact]
    public void ConcurrentProducersNeverShareAnId()
    {
        // MQTTnet does not guarantee a single dispatch thread, so the id counter has to hold
        // up under real contention.
        var ring = new MessageRing(100_000);
        var ids = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, 10_000, _ => ids.Add(Add(ring).Id));

        Assert.Equal(10_000, ids.Count);
        Assert.Equal(10_000, ids.Distinct().Count());
        Assert.Equal(10_000, ring.HighWater);
    }

    [Fact]
    public void RingAndDatabaseAgreeOnIdsAcrossARestart()
    {
        // Rule 5 in one test: what ingest numbered is what the database holds, and a restart
        // picks up where the last one stopped instead of colliding with it.
        using var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);

        var first = new MessageRing(1_000);
        first.SeedFrom(Schema.LastMessageId(db.Factory));
        var writtenIds = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            var msg = Add(first);
            writtenIds.Add(msg.Id);
            db.Execute($"""
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES ({msg.Id}, 0, 'codeit/a', NULL, 0, 0, x'00');
                """);
        }

        Assert.Equal(50, Schema.LastMessageId(db.Factory));

        var afterRestart = new MessageRing(1_000);
        afterRestart.SeedFrom(Schema.LastMessageId(db.Factory));

        Assert.Equal(51, Add(afterRestart).Id);
        Assert.DoesNotContain(51L, writtenIds);
    }
}

public class MessageRingByteCapTests
{
    private static LogMessage Add(MessageRing ring, int payloadBytes)
    {
        var msg = new LogMessage(ring.NextId(), 0, "codeit/a", new byte[payloadBytes], 0, false, null);
        ring.Add(msg);
        return msg;
    }

    [Fact]
    public void TracksTheBytesItHolds()
    {
        var ring = new MessageRing(capacity: 100, maxBytes: 10_000);

        Add(ring, 100);
        Add(ring, 250);

        Assert.Equal(350, ring.BytesHeld);
        Assert.Equal(1, ring.OldestHeld);
    }

    [Fact]
    public void EvictsTheOldestOnceThePayloadBudgetIsReached()
    {
        // The defect this exists for: bounding by message count alone is not a bound on
        // memory. 100 000 messages of 5 KB is half a gigabyte of payload held alive, and
        // these services publish payloads far larger than 5 KB.
        var ring = new MessageRing(capacity: 1_000, maxBytes: 1_000);

        for (var i = 0; i < 10; i++) Add(ring, 200);

        Assert.True(ring.BytesHeld <= 1_000);
        Assert.Equal(10, ring.HighWater);
        // Only the newest handful survive, far short of the 1 000-message capacity.
        Assert.True(ring.OldestHeld > 1, $"expected eviction, oldest held is {ring.OldestHeld}");
        Assert.All(ring.Latest(100), m => Assert.True(m.Id >= ring.OldestHeld));
    }

    [Fact]
    public void OneOversizedPayloadEvictsManySmallOnes()
    {
        var ring = new MessageRing(capacity: 1_000, maxBytes: 1_000);
        for (var i = 0; i < 5; i++) Add(ring, 100);

        var big = Add(ring, 900);

        Assert.True(ring.BytesHeld <= 1_000);
        Assert.Contains(big.Id, ring.Latest(100).Select(m => m.Id));
    }

    [Fact]
    public void ThePayloadCapNeverEvictsTheMessageJustAdded()
    {
        // Even a payload larger than the whole budget stays: the newest message is the one
        // the live pane is about to show.
        var ring = new MessageRing(capacity: 10, maxBytes: 100);

        var huge = Add(ring, 5_000);

        Assert.Equal([huge.Id], ring.Latest(10).Select(m => m.Id));
    }

    [Fact]
    public void CountStillBoundsTheRingWhenPayloadsAreSmall()
    {
        var ring = new MessageRing(capacity: 4, maxBytes: 1_000_000);

        for (var i = 0; i < 10; i++) Add(ring, 1);

        Assert.Equal([10L, 9L, 8L, 7L], ring.Latest(100).Select(m => m.Id));
        Assert.Equal(7, ring.OldestHeld);
    }

    [Fact]
    public void BytesHeldDoesNotDriftAsTheRingWraps()
    {
        var ring = new MessageRing(capacity: 8, maxBytes: 1_000_000);

        for (var i = 0; i < 100; i++) Add(ring, 10);

        // Eight slots of ten bytes, no matter how many times it wrapped.
        Assert.Equal(80, ring.BytesHeld);
    }

    [Fact]
    public void ConcurrentProducersKeepTheAccountingStraight()
    {
        var ring = new MessageRing(capacity: 10_000, maxBytes: 1_000_000_000);

        Parallel.For(0, 5_000, _ => Add(ring, 20));

        Assert.Equal(5_000, ring.HighWater);
        Assert.Equal(5_000 * 20, ring.BytesHeld);
    }
}
