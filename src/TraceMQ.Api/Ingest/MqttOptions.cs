namespace TraceMQ.Api.Ingest;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "tracemq";

    /// <summary>
    /// Deliberately empty, not ["codeit/#"]. The configuration binder APPENDS to a collection
    /// that already holds items, so a default here plus the same value in appsettings yields
    /// two subscriptions to one filter, and the broker then delivers every message twice.
    /// The fallback lives in <see cref="EffectiveTopics"/> instead.
    /// </summary>
    public string[] Topics { get; set; } = [];

    public static readonly string[] DefaultTopics = ["codeit/#"];

    /// <summary>What to actually subscribe to: de-duplicated, never empty.</summary>
    public IReadOnlyList<string> EffectiveTopics()
    {
        var topics = Topics
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return topics.Length > 0 ? topics : DefaultTopics;
    }

    /// <summary>
    /// Topic filters to drop on arrival, MQTT wildcards allowed (<c>codeit/heartbeat/#</c>,
    /// <c>codeit/+/status</c>). A matching message is never stored or shown.
    /// </summary>
    public string[] ExcludeTopics { get; set; } = [];

    /// <summary>The exclude filters to apply: trimmed, de-duplicated, blanks removed.</summary>
    public IReadOnlyList<string> EffectiveExcludeTopics() =>
        ExcludeTopics
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
