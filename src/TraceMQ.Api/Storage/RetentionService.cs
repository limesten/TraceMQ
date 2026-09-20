using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TraceMQ.Api.Storage;

/// <summary>
/// Drops messages past the retention window. Runs through <see cref="WriterQueue"/> so the
/// deletes happen on the one writer connection, and in chunks so a sweep of a large backlog
/// never holds a long write lock against the incoming stream.
/// </summary>
public sealed class RetentionService(
    WriterQueue queue,
    IOptions<StorageOptions> options,
    ILogger<RetentionService> log) : BackgroundService
{
    public const int ChunkSize = 10_000;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = options.Value.RetentionDays;
        if (retentionDays <= 0)
        {
            log.LogInformation("Retention disabled (RetentionDays = {Days})", retentionDays);
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
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
}
