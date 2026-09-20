using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class MessageQueryTests
{
    private static (TempDb Db, MessageRing Ring, MessageQuery Query) NewQuery(int ringCapacity = 1_000)
    {
        var db = new TempDb();
        Schema.Initialize(db.Factory, NullLogger.Instance);
        var ring = new MessageRing(ringCapacity);
        return (db, ring, new MessageQuery(db.Factory, ring));
    }

    private static void Insert(TempDb db, long id, string topic, string? key = null, long ts = 0, string payload = "{}")
    {
        using var connection = db.Factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            VALUES ($id, $ts, $topic, $key, 0, 0, $payload);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", ts == 0 ? id : ts);
        cmd.Parameters.AddWithValue("$topic", topic);
        cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", Encoding.UTF8.GetBytes(payload));
        cmd.ExecuteNonQuery();
    }

    private static void Push(MessageRing ring, string topic, string? key = null)
    {
        var id = ring.NextId();
        ring.Add(new LogMessage(id, id, topic, Encoding.UTF8.GetBytes("{}"), 0, false, key));
    }

    [Fact]
    public void ListReturnsNewestFirst()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        for (var i = 1; i <= 5; i++) Insert(db, i, "codeit/a");

        var rows = query.List(new MessageFilter(BeforeId: 1000));

        Assert.Equal([5L, 4L, 3L, 2L, 1L], rows.Select(r => r.Id));
    }

    [Fact]
    public void ListNeverCarriesPayloadBytes()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        Insert(db, 1, "codeit/a", payload: """{"big":"aaaaaaaaaa"}""");

        var row = query.List(new MessageFilter(BeforeId: 1000)).Single();

        // The row carries the size, not the bytes. Compile-time: MessageRow has no payload.
        Assert.Equal(20, row.Size);
    }

    [Fact]
    public void ListCarriesABoundedPreviewFromBothSources()
    {
        // The rule the list endpoint keeps is that a row is small, not that it says nothing
        // about the payload. Ring and database must agree on what the preview is.
        var (db, ring, query) = NewQuery();
        using var _db = db;
        var payload = "{\"state\":\"RUNNING\",\"pad\":\"" + new string('x', 5_000) + "\"}";
        Insert(db, 1, "codeit/a", payload: payload);
        var id = ring.NextId();
        ring.Add(new LogMessage(id, 1, "codeit/a", Encoding.UTF8.GetBytes(payload), 0, false, null));

        var fromDb = query.List(new MessageFilter(BeforeId: 1000)).Single();
        var fromRing = query.List(new MessageFilter(AfterId: 0)).Single();

        Assert.Equal(fromDb.Preview, fromRing.Preview);
        Assert.StartsWith("{\"state\":\"RUNNING\"", fromDb.Preview);
        Assert.True(fromDb.Preview!.Length <= PayloadPreview.MaxChars);
        // The row knows the real size even though it only carries a slice of it.
        Assert.True(fromDb.Size > 5_000);
    }

    [Fact]
    public void BinaryPayloadsHaveNoPreview()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        db.Execute("""
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            VALUES (1, 1, 'codeit/a', NULL, 0, 0, x'00ff00ff');
            """);

        Assert.Null(query.List(new MessageFilter(BeforeId: 1000)).Single().Preview);
    }

    [Fact]
    public void CorrelationSearchIsExactAndCaseInsensitive()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        Insert(db, 1, "codeit/a", "7347BA7C-D1E5-41C4");
        Insert(db, 2, "codeit/b", "other");

        var rows = query.List(new MessageFilter(Correlation: "  7347ba7c-d1e5-41c4  "));

        Assert.Equal([1L], rows.Select(r => r.Id));
    }

    [Fact]
    public void TheHashFilterIncludesTheParentLevelInSqlToo()
    {
        // The regression this exists for: "codeit/#" matches the topic "codeit" itself, so a
        // LIKE on "codeit/%" alone would make history disagree with the live tail.
        var (db, _, query) = NewQuery();
        using var _db = db;
        Insert(db, 1, "codeit");
        Insert(db, 2, "codeit/a/b");
        Insert(db, 3, "codeitx");
        Insert(db, 4, "other/a");

        var rows = query.List(new MessageFilter(Topic: "codeit/#", BeforeId: 1000));

        Assert.Equal([2L, 1L], rows.Select(r => r.Id));
    }

    [Fact]
    public void SingleLevelWildcardsGoThroughTheSqlFunction()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        Insert(db, 1, "codeit/a/scanner");
        Insert(db, 2, "codeit/b/scanner");
        Insert(db, 3, "codeit/a/b/scanner");

        var rows = query.List(new MessageFilter(Topic: "codeit/+/scanner", BeforeId: 1000));

        Assert.Equal([2L, 1L], rows.Select(r => r.Id));
    }

    [Fact]
    public void RingAndDatabaseReturnTheSameRowsForTheSameFilter()
    {
        var (db, ring, query) = NewQuery();
        using var _db = db;
        foreach (var topic in new[] { "codeit/a/scanner", "codeit/b/scanner", "codeit/a/b/scanner", "other/x" })
        {
            Push(ring, topic);
        }
        for (var i = 1; i <= 4; i++)
        {
            Insert(db, i, new[] { "codeit/a/scanner", "codeit/b/scanner", "codeit/a/b/scanner", "other/x" }[i - 1]);
        }

        var fromRing = query.List(new MessageFilter(AfterId: 0, Topic: "codeit/+/scanner"));
        var fromDb = query.List(new MessageFilter(AfterId: 0, Topic: "codeit/+/scanner", BeforeId: 1000));

        Assert.True(query.CanUseRing(new MessageFilter(AfterId: 0, Topic: "codeit/+/scanner")));
        Assert.Equal(fromDb.Select(r => r.Id), fromRing.Select(r => r.Id));
    }

    [Theory]
    [InlineData(null, null, null, false)]   // no cursor: the ring may be shorter than the page
    [InlineData(5L, null, null, true)]      // a cursor inside the window
    [InlineData(null, 5L, null, false)]     // paging backwards
    [InlineData(null, null, "key", false)]  // a correlation search
    public void RingIsUsedOnlyForAPlainLiveTail(long? afterId, long? beforeId, string? correlation, bool expected)
    {
        var (db, ring, query) = NewQuery();
        using var _db = db;
        for (var i = 0; i < 10; i++) Push(ring, "codeit/a");

        var filter = new MessageFilter(AfterId: afterId, BeforeId: beforeId, Correlation: correlation);

        Assert.Equal(expected, query.CanUseRing(filter));
    }

    [Fact]
    public void ACursorOlderThanTheRingFallsBackToTheDatabase()
    {
        var (db, ring, query) = NewQuery(ringCapacity: 8);
        using var _db = db;
        for (var i = 0; i < 40; i++) Push(ring, "codeit/a");

        Assert.False(query.CanUseRing(new MessageFilter(AfterId: 1)));
        Assert.True(query.CanUseRing(new MessageFilter(AfterId: 38)));
    }

    [Fact]
    public void ATimeRangeAlwaysGoesToTheDatabase()
    {
        var (db, ring, query) = NewQuery();
        using var _db = db;
        Push(ring, "codeit/a");

        Assert.False(query.CanUseRing(new MessageFilter(From: 1)));
        Assert.False(query.CanUseRing(new MessageFilter(To: 1)));
    }

    [Fact]
    public void GetReturnsUtf8PayloadsAsText()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        Insert(db, 1, "codeit/a", payload: """{"trigger":{"uid":"KEY"}}""");

        var detail = query.Get(1)!;

        Assert.Equal("utf-8", detail.Encoding);
        Assert.Equal("""{"trigger":{"uid":"KEY"}}""", detail.Text);
    }

    [Fact]
    public void GetReturnsBinaryPayloadsAsBase64()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        using (var connection = db.Factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
                VALUES (1, 1, 'codeit/a', NULL, 0, 0, x'00ff00ff');
                """;
            cmd.ExecuteNonQuery();
        }

        var detail = query.Get(1)!;

        Assert.Equal("base64", detail.Encoding);
        Assert.Equal(Convert.ToBase64String([0x00, 0xFF, 0x00, 0xFF]), detail.Text);
        Assert.Equal(4, detail.Size);
    }

    [Fact]
    public void GetHandlesAPayloadStoredAsTextRatherThanBlob()
    {
        // SQLite is dynamically typed, so BLOB affinity does not stop a value being stored as
        // TEXT. Our writer always binds bytes, but a row written by anything else must not
        // return 500.
        var (db, _, query) = NewQuery();
        using var _db = db;
        db.Execute("""
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            VALUES (1, 1, 'codeit/a', NULL, 0, 0, '{"stored":"as text"}');
            """);

        var detail = query.Get(1)!;

        Assert.Equal("utf-8", detail.Encoding);
        Assert.Equal("""{"stored":"as text"}""", detail.Text);
        Assert.Equal(20, detail.Size);
    }

    [Fact]
    public void GetOnAMissingIdIsNull()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;

        Assert.Null(query.Get(999));
    }

    [Fact]
    public void LimitIsClamped()
    {
        var (db, _, query) = NewQuery();
        using var _db = db;
        for (var i = 1; i <= 20; i++) Insert(db, i, "codeit/a");

        Assert.Equal(5, query.List(new MessageFilter(BeforeId: 1000, Limit: 5)).Count);
        Assert.Equal(20, query.List(new MessageFilter(BeforeId: 1000, Limit: 999)).Count);
        // A limit of zero or less is clamped up, never treated as "no limit".
        Assert.Single(query.List(new MessageFilter(BeforeId: 1000, Limit: 0)));
    }
}
