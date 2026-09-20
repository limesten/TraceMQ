using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Correlation;

/// <summary>
/// The correlation keys seen most recently on the wire, for the list in the UI's rail.
///
/// Deliberately not a query. <c>GROUP BY correlation_key ORDER BY MAX(ts) DESC</c> cannot
/// stop early, so it scans the whole partial index every time it runs — fine once at startup
/// to survive a restart, ruinous on a 500 ms poll.
/// </summary>
public sealed class RecentKeys
{
    private readonly record struct Seen(long LastSeenTs, int Count);

    private readonly Dictionary<string, Seen> _keys;
    private readonly Lock _gate = new();

    public RecentKeys(int capacity = 20)
    {
        Capacity = capacity;
        _keys = new Dictionary<string, Seen>(capacity + 1, StringComparer.OrdinalIgnoreCase);
    }

    public int Capacity { get; }

    public void Record(string key, long timestampMs)
    {
        if (string.IsNullOrEmpty(key)) return;

        lock (_gate)
        {
            if (_keys.TryGetValue(key, out var existing))
            {
                _keys[key] = new Seen(Math.Max(existing.LastSeenTs, timestampMs), existing.Count + 1);
                return;
            }

            if (_keys.Count >= Capacity)
            {
                var oldest = string.Empty;
                var oldestTs = long.MaxValue;
                foreach (var (candidate, seen) in _keys)
                {
                    if (seen.LastSeenTs < oldestTs)
                    {
                        oldestTs = seen.LastSeenTs;
                        oldest = candidate;
                    }
                }
                if (timestampMs < oldestTs) return;
                _keys.Remove(oldest);
            }

            _keys[key] = new Seen(timestampMs, 1);
        }
    }

    /// <summary>Most recently seen first, which is the order the rail lists them.</summary>
    public IReadOnlyList<RecentKey> Snapshot()
    {
        lock (_gate)
        {
            return _keys
                .Select(kv => new RecentKey(kv.Key, kv.Value.LastSeenTs, kv.Value.Count))
                .OrderByDescending(k => k.LastSeenTs)
                .ToArray();
        }
    }

    /// <summary>
    /// One scan at startup so the rail is not empty after a restart. The partial index means
    /// this only touches rows that actually carry a key.
    /// </summary>
    public void SeedFrom(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, MAX(ts) AS last_seen, COUNT(*) AS seen_count
            FROM messages
            WHERE correlation_key IS NOT NULL
            GROUP BY correlation_key
            ORDER BY last_seen DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Capacity);

        using var reader = cmd.ExecuteReader();
        lock (_gate)
        {
            while (reader.Read())
            {
                _keys[reader.GetString(0)] = new Seen(reader.GetInt64(1), reader.GetInt32(2));
            }
        }
    }
}

public sealed record RecentKey(string Key, long LastSeenTs, int Count);
