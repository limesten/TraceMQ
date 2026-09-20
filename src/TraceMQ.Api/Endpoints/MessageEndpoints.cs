using Microsoft.Extensions.Options;
using TraceMQ.Api.Ingest;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Storage;

namespace TraceMQ.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this WebApplication app)
    {
        // Rows only. The payload rides on /api/messages/{id}, on click: the UI shows one at a
        // time, and at 1500 msg/s the difference is a 40 KB poll against a 4 MB one.
        app.MapGet("/api/messages", (
            MessageQuery query,
            long? afterId, long? beforeId, string? topic, string? correlation,
            long? from, long? to, int limit = 200) =>
        {
            var rows = query.List(new MessageFilter(afterId, beforeId, topic, correlation, from, to, limit));
            return Results.Ok(rows);
        });

        app.MapGet("/api/messages/{id:long}", (MessageQuery query, long id) =>
            query.Get(id) is { } detail ? Results.Ok(detail) : Results.NotFound());

        app.MapGet("/api/correlations/recent", (RecentKeys keys) => Results.Ok(keys.Snapshot()));

        app.MapGet("/api/settings", (CorrelationExtractor extractor) =>
            Results.Ok(new { CorrelationPaths = extractor.Paths }));

        app.MapGet("/api/status", (
            SqliteConnectionFactory factory,
            DroppedCounter dropped,
            MessageRing ring,
            BrokerState broker,
            IOptions<StorageOptions> storage) =>
        {
            var file = new FileInfo(factory.DbPath);
            return Results.Ok(new
            {
                Broker = new { broker.Connected, broker.Endpoint, broker.Topics },
                Dropped = dropped.Count,
                HighWaterId = ring.HighWater,
                RingCapacity = ring.Capacity,
                DbPath = factory.DbPath,
                DbBytes = file.Exists ? file.Length : 0,
                storage.Value.RetentionDays,
            });
        });
    }
}
