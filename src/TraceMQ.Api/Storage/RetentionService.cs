using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TraceMQ.Api.Storage;

/// <summary>
/// Drops messages past the retention window, and the oldest messages once the database is
/// past its size cap. Runs through <see cref="WriterQueue"/> so the deletes happen on the one
/// writer connection, and in chunks so a sweep of a large backlog never holds a long write
/// lock against the incoming stream.
/// </summary>
public sealed class RetentionService(
    WriterQueue queue,
    IOptions<StorageOptions> options,
    ILogger<RetentionService> log) : BackgroundService
{
    public const int ChunkSize = 10_000;

    /// <summary>
    /// Past the cap, trim down to this share of it rather than to just under it. Otherwise
    /// every sweep after the first finds the database a few rows over and trims those again.
    /// </summary>
    public const double TrimTargetRatio = 0.9;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = options.Value.RetentionDays;
        var maxBytes = Math.Max(options.Value.MaxDbSizeMb, 0) * 1024 * 1024;
        if (retentionDays <= 0 && maxBytes == 0)
        {
            log.LogInformation("Retention disabled (RetentionDays = {Days}, MaxDbSizeMb = 0)", retentionDays);
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(options.Value.RetentionSweepMinutes, 0.0001));
        log.LogInformation(
            "Retention sweeping every {Interval}: older than {Days} days, size cap {MaxMb} MB (0 = none)",
            interval, retentionDays, options.Value.MaxDbSizeMb);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (retentionDays > 0) await SweepByAgeAsync(retentionDays);
                if (maxBytes > 0) await SweepBySizeAsync(maxBytes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Retention sweep failed; will retry");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task SweepByAgeAsync(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        var removed = await queue.EnqueueAsync(
            "retention",
            (connection, ct) => SweepAsync(connection, cutoff, ct));

        if (removed > 0)
        {
            log.LogInformation("Retention removed {Count} messages older than {Days} days",
                removed, retentionDays);
        }
        else
        {
            // Otherwise there is no way to tell "nothing was old enough" from
            // "retention never ran", which is the first question at a customer.
            log.LogDebug("Retention swept, nothing older than {Days} days", retentionDays);
        }
    }

    /// <summary>
    /// One queue item per chunk, not one for the whole trim. Cutting a database down after
    /// the cap was lowered can mean millions of rows, and the writer has to get back to the
    /// message stream between chunks or the ingest channel fills and drops.
    /// </summary>
    private async Task SweepBySizeAsync(long maxBytes, CancellationToken stoppingToken)
    {
        var used = await queue.EnqueueAsync("size check", UsedBytesAsync);
        if (used <= maxBytes)
        {
            log.LogDebug("Size check: {UsedMb} MB in use, under the {MaxMb} MB cap",
                used / 1048576, maxBytes / 1048576);
            return;
        }

        var target = (long)(maxBytes * TrimTargetRatio);
        var removed = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var chunk = await queue.EnqueueAsync(
                "size cap",
                (connection, ct) => TrimChunkAsync(connection, target, ct));
            if (chunk == 0) break;
            removed += chunk;
        }

        log.LogInformation(
            "Size cap: removed {Count} oldest messages, {UsedMb} MB was in use against a {MaxMb} MB cap",
            removed, used / 1048576, maxBytes / 1048576);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes in chunks, each its own transaction. No VACUUM: it takes an exclusive lock and
    /// stalls ingest, and the file reaches a steady size and reuses freed pages anyway.
    /// </summary>
    public static async Task<int> SweepAsync(SqliteConnection connection, long cutoffMs, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM messages
                WHERE id IN (SELECT id FROM messages WHERE ts < $cutoff ORDER BY id LIMIT $chunk);
                """;
            command.Parameters.AddWithValue("$cutoff", cutoffMs);
            command.Parameters.AddWithValue("$chunk", ChunkSize);

            var removed = await command.ExecuteNonQueryAsync(ct);
            total += removed;

            if (removed < ChunkSize) break;
        }

        return total;
    }

    /// <summary>
    /// Bytes in pages that hold data. The file length is no use here: deleted rows go to the
    /// freelist and the file keeps its size, so it would read as over the cap forever.
    /// </summary>
    public static async Task<long> UsedBytesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (p.page_count - f.freelist_count) * s.page_size
            FROM pragma_page_count() AS p, pragma_freelist_count() AS f, pragma_page_size() AS s;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Deletes the oldest messages, at most one chunk, sized from the average row so a small
    /// overshoot costs a small delete. Returns how many went; 0 once usage is at or under
    /// <paramref name="targetBytes"/>, or the table is empty.
    /// </summary>
    public static async Task<int> TrimChunkAsync(SqliteConnection connection, long targetBytes, CancellationToken ct)
    {
        var used = await UsedBytesAsync(connection, ct);
        if (used <= targetBytes) return 0;

        // Ids are assigned by ingest and deleted oldest first, so the span is the row count
        // near enough, and unlike count(*) it costs two rowid lookups rather than a scan.
        long rows;
        await using (var span = connection.CreateCommand())
        {
            span.CommandText = "SELECT MAX(id) - MIN(id) + 1 FROM messages;";
            if (await span.ExecuteScalarAsync(ct) is not long n) return 0;
            rows = n;
        }

        var perRow = Math.Max(1.0, (double)used / rows);
        var take = (int)Math.Clamp(Math.Ceiling((used - targetBytes) / perRow), 1, ChunkSize);

        await using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM messages
            WHERE id IN (SELECT id FROM messages ORDER BY id LIMIT $take);
            """;
        delete.Parameters.AddWithValue("$take", take);
        return await delete.ExecuteNonQueryAsync(ct);
    }
}
