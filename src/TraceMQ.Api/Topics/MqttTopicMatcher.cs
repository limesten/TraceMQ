namespace TraceMQ.Api.Topics;

/// <summary>
/// MQTT topic filter matching, to the wildcard rules in the MQTT 5 specification.
///
/// One implementation, used in two places: the ring buffer calls it directly for the live
/// tail, and it is registered as the SQL function <c>mqtt_match</c> for history. If live and
/// history disagreed about what <c>+</c> means, the bug would show up as "the filter works
/// until I scroll", which is horrible to find.
/// </summary>
public static class MqttTopicMatcher
{
    /// <summary>A filter with no wildcards at all needs no matcher; equality is enough.</summary>
    public static bool HasWildcards(string filter) =>
        filter.Contains('+') || filter.Contains('#');

    /// <summary>
    /// A filter of the form <c>a/b/#</c> is a plain prefix, which SQL can answer with
    /// <c>topic LIKE 'a/b/%'</c> against ix_messages_topic instead of calling the function on
    /// every row. Returns null when the filter is not that shape.
    /// </summary>
    public static string? AsSqlPrefix(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return null;
        if (filter == "#") return string.Empty;
        if (!filter.EndsWith("/#", StringComparison.Ordinal)) return null;

        var head = filter[..^2];
        return head.Contains('+') || head.Contains('#') ? null : head;
    }

    public static bool Matches(string filter, string topic)
    {
        if (string.IsNullOrEmpty(filter) || topic is null) return false;
        if (!HasWildcards(filter)) return string.Equals(filter, topic, StringComparison.Ordinal);

        // A wildcard at the start of a filter never matches a $-prefixed system topic.
        if (topic.StartsWith('$') && (filter[0] == '#' || filter[0] == '+'))
        {
            return false;
        }

        var filterSegments = filter.Split('/');
        var topicSegments = topic.Split('/');

        for (var i = 0; i < filterSegments.Length; i++)
        {
            var segment = filterSegments[i];

            if (segment == "#")
            {
                // Valid only as the last segment, where it also matches the parent level:
                // "sport/#" matches "sport" as well as "sport/tennis".
                return i == filterSegments.Length - 1;
            }

            if (i >= topicSegments.Length)
            {
                return false;
            }

            if (segment == "+")
            {
                continue;
            }

            if (!string.Equals(segment, topicSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return filterSegments.Length == topicSegments.Length;
    }
}
