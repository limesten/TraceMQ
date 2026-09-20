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
}
