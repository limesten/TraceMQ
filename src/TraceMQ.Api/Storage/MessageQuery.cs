using System.Text;
using Microsoft.Data.Sqlite;
using TraceMQ.Api.Model;
using TraceMQ.Api.Topics;

namespace TraceMQ.Api.Storage;

public sealed record MessageRow(
    long Id, long Ts, string Topic, string? CorrelationKey, int Qos, bool Retained, long Size);

public sealed record MessageDetail(
    long Id, long Ts, string Topic, string? CorrelationKey, int Qos, bool Retained,
    long Size, string Text, string Encoding);

public sealed record MessageFilter(
    long? AfterId = null,
    long? BeforeId = null,
    string? Topic = null,
    string? Correlation = null,
    long? From = null,
    long? To = null,
    int Limit = 200);

/// <summary>
/// Reads. The live tail is answered from the ring, everything else from SQLite, and the
/// decision between them is <see cref="CanUseRing"/> rather than something implicit.
/// </summary>
public sealed class MessageQuery(SqliteConnectionFactory factory, MessageRing ring)
{
    public const int MaxLimit = 2_000;

    /// <summary>
    /// The ring answers a plain live tail: no correlation search, no time range, no paging
    /// backwards, and a cursor still inside the window it holds.
    /// </summary>
    public bool CanUseRing(MessageFilter filter)
    {
        if (filter.Correlation is { Length: > 0 }) return false;
        if (filter.BeforeId is not null || filter.From is not null || filter.To is not null) return false;

        var high = ring.HighWater;
        if (high == 0) return false;

        var oldestHeld = Math.Max(1, high - ring.Capacity + 1);
        // No cursor means "the newest page", which the ring always has.
        return filter.AfterId is null || filter.AfterId.Value + 1 >= oldestHeld;
    }

    public IReadOnlyList<MessageRow> List(MessageFilter filter)
    {
        var limit = Math.Clamp(filter.Limit, 1, MaxLimit);

        if (CanUseRing(filter))
        {
            var predicate = TopicPredicate(filter.Topic);
            var found = filter.AfterId is null
                ? ring.Latest(limit, predicate)
                : ring.After(filter.AfterId.Value, limit, predicate);

            // Newest first, which is the order the table displays.
            return found
                .OrderByDescending(m => m.Id)
                .Select(m => new MessageRow(
                    m.Id, m.TimestampMs, m.Topic, m.CorrelationKey, m.Qos, m.Retained, m.Payload.Length))
                .ToArray();
        }

        return ListFromDatabase(filter, limit);
    }

    private IReadOnlyList<MessageRow> ListFromDatabase(MessageFilter filter, int limit)
    {
        using var connection = factory.Open();
        SqlFunctions.Register(connection);
        using var cmd = connection.CreateCommand();

        var where = new List<string>();

        if (filter.AfterId is { } after)
        {
            where.Add("id > $afterId");
            cmd.Parameters.AddWithValue("$afterId", after);
        }
        if (filter.BeforeId is { } before)
        {
            where.Add("id < $beforeId");
            cmd.Parameters.AddWithValue("$beforeId", before);
        }
        if (filter.From is { } from)
        {
            where.Add("ts >= $from");
            cmd.Parameters.AddWithValue("$from", from);
        }
        if (filter.To is { } to)
        {
            where.Add("ts <= $to");
            cmd.Parameters.AddWithValue("$to", to);
        }
        if (filter.Correlation is { Length: > 0 } correlation)
        {
            // Exact match; COLLATE NOCASE on the column makes it case-insensitive, and the
            // partial index answers it. Trimmed because pasted GUIDs carry whitespace.
            where.Add("correlation_key = $correlation");
            cmd.Parameters.AddWithValue("$correlation", correlation.Trim());
        }
        AppendTopicPredicate(cmd, where, filter.Topic);

        cmd.CommandText = $"""
            SELECT id, ts, topic, correlation_key, qos, retained, length(payload)
            FROM messages
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<MessageRow>(Math.Min(limit, 256));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? 0 : reader.GetInt64(6)));
        }
        return rows;
    }

    public MessageDetail? Get(long id)
    {
        using var connection = factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, topic, correlation_key, qos, retained, payload
            FROM messages WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var payload = ReadPayload(reader, 6);
        var (text, encoding) = Decode(payload);

        return new MessageDetail(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
            payload.Length,
            text,
            encoding);
    }

    /// <summary>
    /// SQLite is dynamically typed: BLOB affinity on the column does not stop a value being
    /// stored as TEXT, and a straight cast to byte[] then throws. Our writer always binds
    /// bytes, but a database touched by anything else must not take the endpoint down.
    /// </summary>
    private static byte[] ReadPayload(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return [];

        return reader.GetFieldType(ordinal) == typeof(string)
            ? Encoding.UTF8.GetBytes(reader.GetString(ordinal))
            : reader.GetFieldValue<byte[]>(ordinal);
    }

    /// <summary>
    /// UTF-8 when the bytes really are UTF-8, base64 otherwise. Reading the column as a
    /// string instead would throw or silently mangle on the first binary payload.
    /// </summary>
    public static (string Text, string Encoding) Decode(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return (string.Empty, "utf-8");
        }

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Convert.ToBase64String(payload), "base64");
        }
    }

    private static Func<LogMessage, bool>? TopicPredicate(string? topic)
    {
        if (topic is not { Length: > 0 } || topic == "#") return null;
        return message => MqttTopicMatcher.Matches(topic, message.Topic);
    }

    private static void AppendTopicPredicate(SqliteCommand cmd, List<string> where, string? topic)
    {
        if (topic is not { Length: > 0 } || topic == "#") return;

        if (MqttTopicMatcher.AsSqlPrefix(topic) is { Length: > 0 } prefix)
        {
            // The common shape. LIKE on a prefix uses ix_messages_topic; mqtt_match would
            // have to run on every row. The equality is not redundant: "a/b/#" matches the
            // topic "a/b" itself, which a LIKE on "a/b/%" would miss, and then the live tail
            // and history would disagree about the same filter.
            where.Add("(topic = $topicExact OR topic LIKE $topicPrefix)");
            cmd.Parameters.AddWithValue("$topicExact", prefix);
            cmd.Parameters.AddWithValue("$topicPrefix", prefix + "/%");
            return;
        }

        where.Add("mqtt_match($topicFilter, topic) = 1");
        cmd.Parameters.AddWithValue("$topicFilter", topic);
    }
}
