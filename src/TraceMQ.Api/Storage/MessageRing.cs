using TraceMQ.Api.Model;

namespace TraceMQ.Api.Storage;

/// <summary>
/// The last N messages, in memory. The live pane reads this instead of the database, so a
/// tail at 1500 msg/s is a field access rather than a query.
///
/// Ids are assigned here and nowhere else. A message's slot is <c>(id - 1) % capacity</c>,
/// so a reader can find any id in one step and tell whether it has since been overwritten by
/// comparing the id in the slot with the one it asked for.
/// </summary>
public sealed class MessageRing
{
    private readonly LogMessage?[] _slots;
    private long _nextId;
    private long _highWater;

    public MessageRing(int capacity = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new LogMessage?[capacity];
    }

    public int Capacity { get; }

    /// <summary>
    /// The highest id actually held in the ring; 0 while it is empty. Deliberately NOT the
    /// highest id handed out: after a restart the counter continues from the database, but
    /// the ring itself holds nothing, and a reader that trusted the counter would ask for
    /// messages that are only on disk and get an empty page.
    /// </summary>
    public long HighWater => Interlocked.Read(ref _highWater);

    /// <summary>
    /// Continue numbering from what is already on disk. Called once at startup, before
    /// ingest connects: without it a restart reissues ids that the database already holds.
    /// This moves the id counter only — the ring is still empty until messages arrive.
    /// </summary>
    public void SeedFrom(long lastPersistedId)
    {
        Interlocked.Exchange(ref _nextId, lastPersistedId);
    }

    /// <summary>
    /// The next id. MQTTnet does not guarantee a single dispatch thread, so this has to be
    /// atomic even though there is logically one producer.
    /// </summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    public void Add(LogMessage message)
    {
        var index = (int)((message.Id - 1) % Capacity);
        Volatile.Write(ref _slots[index], message);

        // Publish the id only after the slot is visible, so a reader that sees the high
        // water mark can always find the message behind it.
        long current;
        do
        {
            current = Interlocked.Read(ref _highWater);
            if (message.Id <= current) return;
        }
        while (Interlocked.CompareExchange(ref _highWater, message.Id, current) != current);
    }

    /// <summary>
    /// Messages newer than <paramref name="afterId"/>, oldest first, at most
    /// <paramref name="limit"/>. Entries that have been overwritten since are skipped rather
    /// than returned stale — a caller that has fallen further behind than the ring is deep
    /// should be reading the database instead.
    /// </summary>
    public List<LogMessage> After(long afterId, int limit, Func<LogMessage, bool>? filter = null)
    {
        var high = HighWater;
        var oldest = Math.Max(1, high - Capacity + 1);
        var from = Math.Max(afterId + 1, oldest);

        var results = new List<LogMessage>(Math.Min(limit, 256));
        for (var id = from; id <= high && results.Count < limit; id++)
        {
            var slot = Volatile.Read(ref _slots[(int)((id - 1) % Capacity)]);
            if (slot is null || slot.Id != id)
            {
                // Overwritten while we were walking. Everything older is gone too.
                continue;
            }
            if (filter is null || filter(slot))
            {
                results.Add(slot);
            }
        }
        return results;
    }

    /// <summary>The newest messages, newest first, which is the order the table displays.</summary>
    public List<LogMessage> Latest(int limit, Func<LogMessage, bool>? filter = null)
    {
        var high = HighWater;
        var oldest = Math.Max(1, high - Capacity + 1);

        var results = new List<LogMessage>(Math.Min(limit, 256));
        for (var id = high; id >= oldest && results.Count < limit; id--)
        {
            var slot = Volatile.Read(ref _slots[(int)((id - 1) % Capacity)]);
            if (slot is null || slot.Id != id)
            {
                break;
            }
            if (filter is null || filter(slot))
            {
                results.Add(slot);
            }
        }
        return results;
    }
}
