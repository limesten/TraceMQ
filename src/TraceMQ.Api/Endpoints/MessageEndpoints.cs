using Microsoft.Data.Sqlite;

namespace TraceMQ.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this WebApplication app)
    {

        app.MapGet("/api/messages", async (SqliteConnection connection, int limit = 50) =>
        {
            var messages = new List<object>();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, ts, topic, sequence_id, service, qos, retained, payload FROM messages ORDER BY ts DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new
                {
                    Id = reader.GetInt64(0),
                    Ts = reader.GetInt64(1),
                    Topic = reader.GetString(2),
                    SequenceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Service = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Qos = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    Retained = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6),
                    Payload = reader.GetString(7),
                });
            }

            return Results.Ok(messages);
        });


        app.MapGet("/api/ping", () => new { message = "pongz", at = DateTimeOffset.Now });
    }
}