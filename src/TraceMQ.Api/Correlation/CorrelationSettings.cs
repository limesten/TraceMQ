using Microsoft.Data.Sqlite;
using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Correlation;

/// <summary>
/// The live correlation paths. Ingest reads <see cref="Extractor"/> for every message, so the
/// swap is a single volatile write: messages in flight use whichever extractor they picked
/// up, and the backfill catches everything already stored.
/// </summary>
public sealed class CorrelationSettings(CorrelationExtractor initial)
{
    private volatile CorrelationExtractor _extractor = initial;

    public CorrelationExtractor Extractor => _extractor;

    public IReadOnlyList<string> Paths => _extractor.Paths;

    public void Replace(CorrelationExtractor extractor) => _extractor = extractor;
}

public sealed record PathUpdate(IReadOnlyList<string> Paths, int Rewritten);

/// <summary>
/// Changing the paths and rewriting the column to match. Expected to run once per site, if
/// ever — but without it a typo, or discovering the right path a day into logging, leaves
/// every message stored so far unsearchable.
/// </summary>
public sealed class CorrelationPathUpdater(
    WriterQueue queue,
    CorrelationSettings settings,
    RecentKeys recentKeys,
    SqliteConnectionFactory factory,
    ILogger<CorrelationPathUpdater> log)
{
    public static IReadOnlyList<string> Validate(IEnumerable<string>? requested)
    {
        var paths = (requested ?? [])
            .Select(CorrelationExtractor.Normalize)
            .Where(segments => segments.Length > 0)
            .Select(segments => string.Join('.', segments))
            .Distinct(StringComparer.Ordinal)
            .Take(CorrelationExtractor.MaxPaths)
            .ToArray();

        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one correlation path is required.");
        }

        return paths;
    }

    public async Task<PathUpdate> UpdateAsync(IEnumerable<string>? requested)
    {
        var paths = Validate(requested);

        var rewritten = await queue.EnqueueAsync(
            "correlation-backfill",
            (connection, ct) => ApplyAsync(connection, paths, ct));

        settings.Replace(new CorrelationExtractor(paths));

        // Keys extracted under the old paths are meaningless now.
        recentKeys.Clear();
        recentKeys.SeedFrom(factory);

        log.LogInformation("Correlation paths set to {Paths}; rewrote {Count} rows",
            string.Join(", ", paths), rewritten);

        return new PathUpdate(paths, rewritten);
    }

    private static async Task<int> ApplyAsync(
        SqliteConnection connection, IReadOnlyList<string> paths, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction();

        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            save.Parameters.AddWithValue("$key", CorrelationPaths.SettingsKey);
            save.Parameters.AddWithValue("$value", CorrelationPaths.Serialize(paths));
            await save.ExecuteNonQueryAsync(ct);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;

        // CAST is not optional: payload is a BLOB, and from SQLite 3.45 the JSON functions
        // read a BLOB argument as JSONB rather than as JSON text. Without it this writes
        // nulls across the whole table.
        var extracts = new List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            extracts.Add($"json_extract(CAST(payload AS TEXT), $p{i})");
            update.Parameters.AddWithValue($"$p{i}", "$." + paths[i]);
        }

        // COALESCE needs two arguments or more, and one configured path is the common case.
        var firstMatch = extracts.Count == 1 ? extracts[0] : $"COALESCE({string.Join(", ", extracts)})";

        // Rows whose payload is not JSON are cleared rather than skipped: leaving a key
        // extracted under the previous paths would be worse than having none.
        update.CommandText = $"""
            UPDATE messages
               SET correlation_key = CASE
                     WHEN json_valid(CAST(payload AS TEXT))
                     THEN {firstMatch}
                     ELSE NULL
                   END;
            """;

        var rewritten = await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return rewritten;
    }
}
