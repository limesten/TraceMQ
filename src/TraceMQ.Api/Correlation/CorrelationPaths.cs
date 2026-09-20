using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Correlation;

/// <summary>
/// Where the configured paths come from, in order: the settings table if a site has chosen
/// them, otherwise appsettings, otherwise the shape every CodeIT service publishes today.
/// </summary>
public static class CorrelationPaths
{
    public const string SettingsKey = "correlation_paths";

    public static readonly string[] Default = ["trigger.uid"];

    public static CorrelationExtractor Load(
        SqliteConnectionFactory factory, IConfiguration configuration, ILogger log)
    {
        var stored = ReadStored(factory);
        if (stored is { Length: > 0 })
        {
            log.LogInformation("Correlation paths from the database: {Paths}", string.Join(", ", stored));
            return new CorrelationExtractor(stored);
        }

        var configured = configuration.GetSection("Correlation:Paths").Get<string[]>();
        if (configured is { Length: > 0 })
        {
            log.LogInformation("Correlation paths from configuration: {Paths}", string.Join(", ", configured));
            return new CorrelationExtractor(configured);
        }

        log.LogInformation("Correlation paths defaulted to: {Paths}", string.Join(", ", Default));
        return new CorrelationExtractor(Default);
    }

    /// <summary>Stored as one path per line, which is how the UI edits them.</summary>
    public static string Serialize(IEnumerable<string> paths) => string.Join('\n', paths);

    public static string[] Deserialize(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[]? ReadStored(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        return cmd.ExecuteScalar() is string value ? Deserialize(value) : null;
    }
}
