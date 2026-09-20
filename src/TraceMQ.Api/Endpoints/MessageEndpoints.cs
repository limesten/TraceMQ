using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this WebApplication app)
    {
        // Phase 3 replaces this with the full surface in PLAN.md section 5. For now it is
        // the smallest thing that keeps the dev loop working against the v1 schema.
        app.MapGet("/api/messages", async (SqliteConnectionFactory factory, int limit = 50) =>
        {
            await using var connection = await factory.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, ts, topic, correlation_key, qos, retained, length(payload)
                FROM messages
                ORDER BY id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            var messages = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new
                {
                    Id = reader.GetInt64(0),
                    Ts = reader.GetInt64(1),
                    Topic = reader.GetString(2),
                    CorrelationKey = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Qos = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    Retained = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    Size = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                });
            }

            return Results.Ok(messages);
        });

        app.MapGet("/api/status", (SqliteConnectionFactory factory, DroppedCounter dropped) =>
            Results.Ok(new { Dropped = dropped.Count, DbPath = factory.DbPath }));
    }
}
