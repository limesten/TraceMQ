using Microsoft.Data.Sqlite;
using TraceMQ.Api.Topics;

namespace TraceMQ.Api.Storage;

public static class SqlFunctions
{
    /// <summary>
    /// Give a connection the functions that queries need. Registered per connection, so every
    /// reader has to call this. mqtt_match shares its implementation with the ring buffer's
    /// filter, which is the point.
    /// </summary>
    public static void Register(SqliteConnection connection)
    {
        connection.CreateFunction(
            "mqtt_match",
            (string? filter, string? topic) =>
                filter is not null && topic is not null && MqttTopicMatcher.Matches(filter, topic) ? 1 : 0,
            isDeterministic: true);
    }
}
