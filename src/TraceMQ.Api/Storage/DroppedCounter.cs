namespace TraceMQ.Api.Storage;

/// <summary>
/// Messages evicted from the ingest channel before the writer could drain them. The honest
/// signal that the tool is losing data; surfaced in the header of the UI.
/// </summary>
public sealed class DroppedCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}
