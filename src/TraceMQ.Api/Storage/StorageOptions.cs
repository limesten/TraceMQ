namespace TraceMQ.Api.Storage;

public sealed class StorageOptions
{
    /// <summary>
    /// Where the database lives. Empty means: the content root in Development, and
    /// %ProgramData%\CodeIT\tracemq\data.db otherwise. Program Files is not writable by a
    /// service account, so the production default must never fall back to the install
    /// directory — that failure only shows up at the customer.
    /// </summary>
    public string? DbPath { get; set; }

    public int RetentionDays { get; set; } = 7;

    /// <summary>Rows per write transaction. See ARCHITECTURE.md section 4, rule 3.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Commit after this long even if the batch is not full.</summary>
    public int FlushIntervalMs { get; set; } = 100;

    /// <summary>
    /// How often retention sweeps. Minutes, fractional allowed so a test can drive the loop
    /// without waiting for the real interval — which is why the loop had no coverage before.
    /// </summary>
    public double RetentionSweepMinutes { get; set; } = 10;

    /// <summary>How many messages the live pane's in-memory ring holds.</summary>
    public int RingCapacity { get; set; } = 100_000;

    /// <summary>
    /// And how many payload bytes, whichever limit is reached first. Count alone is not a
    /// bound on memory: 100 000 messages of 5 KB is half a gigabyte held alive.
    /// </summary>
    public long RingBytes { get; set; } = 256L * 1024 * 1024;
}
