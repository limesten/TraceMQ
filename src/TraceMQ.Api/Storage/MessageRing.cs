using TraceMQ.Api.Model;

namespace TraceMQ.Api.Storage;

/// <summary>
/// The last N messages, in memory. The live pane reads this instead of the database, so a
/// tail at 1500 msg/s is a field access rather than a query.
///
/// Ids are assigned here and nowhere else. A message's slot is <c>(id - 1) % capacity</c>,
/// so a reader can find any id in one step and tell whether it has since been overwritten by
/// comparing the id in the slot with the one it asked for.
///
/// Bounded by BOTH message count and total payload bytes. Count alone is not a bound on
/// memory: 100 000 messages of 5 KB is half a gigabyte of payload held alive, and these
/// services publish payloads far larger than that. Whichever limit is reached first evicts
/// the oldest messages.
/// </summary>
public sealed class MessageRing
{
    private readonly LogMessage?[] _slots;
    private readonly Lock _gate = new();

    private long _nextId;
    private long _highWater;
    private long _oldestHeld;
    private long _bytesHeld;

    public MessageRing(int capacity = 100_000, long maxBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        Capacity = capacity;
        MaxBytes = maxBytes;
        _slots = new LogMessage?[capacity];
    }

    public int Capacity { get; }
    public long MaxBytes { get; }

    /// <summary>
    /// The highest id actually held; 0 while empty. Deliberately NOT the highest id handed
    /// out: after a restart the counter continues from the database, but the ring holds
    /// nothing, and a reader that trusted the counter would ask for messages that are only on
    /// disk and get an empty page.
    /// </summary>
    public long HighWater => Interlocked.Read(ref _highWater);

    /// <summary>The oldest id still held; 0 while empty.</summary>
    public long OldestHeld => Interlocked.Read(ref _oldestHeld);

    public long BytesHeld => Interlocked.Read(ref _bytesHeld);

    /// <summary>
    /// Continue numbering from what is already on disk. Called once at startup, before ingest
    /// connects: without it a restart reissues ids the database already holds. This moves the
    /// id counter only — the ring is still empty until messages arrive.
    /// </summary>
    public void SeedFrom(long lastPersistedId) => Interlocked.Exchange(ref _nextId, lastPersistedId);

    /// <summary>
    /// The next id. MQTTnet does not guarantee a single dispatch thread, so this has to be
    /// atomic even though there is logically one producer.
    /// </summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    public void Add(LogMessage message)
    {
        lock (_gate)
        {
            var index = (int)((message.Id - 1) % Capacity);

            // Whatever this slot held is gone now, whether by wrap-around or replacement.
            if (_slots[index] is { } displaced)
            {
                _bytesHeld -= displaced.Payload.Length;
            }

            Volatile.Write(ref _slots[index], message);
            _bytesHeld += message.Payload.Length;

            if (_oldestHeld == 0 || _oldestHeld > message.Id)
            {
                Interlocked.Exchange(ref _oldestHeld, message.Id);
            }
            if (message.Id > _highWater)
            {
                Interlocked.Exchange(ref _highWater, message.Id);
            }

            TrimLocked();
        }
    }

    /// <summary>
    /// Drop the oldest messages until the ring is inside both limits. One oversized payload
    /// can evict a great many small ones, which is the point: the cap is on memory, not on
    /// how many messages that buys.
    /// </summary>
    private void TrimLocked()
    {
        while (_oldestHeld > 0 && _oldestHeld < _highWater
               && (_bytesHeld > MaxBytes || _highWater - _oldestHeld + 1 > Capacity))
        {
            var index = (int)((_oldestHeld - 1) % Capacity);
            if (_slots[index] is { } evicted && evicted.Id == _oldestHeld)
            {
                _bytesHeld -= evicted.Payload.Length;
                Volatile.Write(ref _slots[index], null);
            }
            Interlocked.Increment(ref _oldestHeld);
        }
    }

    /// <summary>
    /// Messages newer than <paramref name="afterId"/>, oldest first, at most
    /// <paramref name="limit"/>. Entries evicted since are skipped rather than returned stale.
    /// </summary>
    public List<LogMessage> After(long afterId, int limit, Func<LogMessage, bool>? filter = null)
    {
        var high = HighWater;
        var from = Math.Max(afterId + 1, Math.Max(1, OldestHeld));

        var results = new List<LogMessage>(Math.Min(limit, 256));
        for (var id = from; id <= high && results.Count < limit; id++)
        {
            var slot = Volatile.Read(ref _slots[(int)((id - 1) % Capacity)]);
            if (slot is null || slot.Id != id) continue;
            if (filter is null || filter(slot)) results.Add(slot);
        }
        return results;
    }

    /// <summary>The newest messages, newest first, which is the order the table displays.</summary>
    public List<LogMessage> Latest(int limit, Func<LogMessage, bool>? filter = null)
    {
        var high = HighWater;
        var oldest = Math.Max(1, OldestHeld);

        var results = new List<LogMessage>(Math.Min(limit, 256));
        for (var id = high; id >= oldest && results.Count < limit; id--)
        {
            var slot = Volatile.Read(ref _slots[(int)((id - 1) % Capacity)]);
            if (slot is null || slot.Id != id) continue;
            if (filter is null || filter(slot)) results.Add(slot);
        }
        return results;
    }
}
