using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TraceMQ.Api.Model;

namespace TraceMQ.Api.Storage;

public sealed class WriterService : BackgroundService
{
    private readonly string _connectionString = "Data Source=tracemq.db";
    private readonly ChannelReader<LogMessage> _reader;
    private const int BatchSize = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    public WriterService(Channel<LogMessage> channel)
    {
        _reader = channel.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        await pragma.ExecuteNonQueryAsync(stoppingToken);

        var createTable = connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY,
                ts          INTEGER NOT NULL,
                topic       TEXT    NOT NULL,
                sequence_id TEXT,
                service     TEXT,
                qos         INTEGER,
                retained    INTEGER,
                payload     BLOB
            );

            CREATE INDEX IF NOT EXISTS ix_messages_seq   ON messages(sequence_id) WHERE sequence_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_messages_ts    ON messages(ts);
            CREATE INDEX IF NOT EXISTS ix_messages_topic ON messages(topic, ts);
            """;

        await createTable.ExecuteNonQueryAsync(stoppingToken);

        var batch = new List<LogMessage>(BatchSize);

        while (await WaitForBatchAsync(batch, stoppingToken))
        {
            await FlushAsync(connection, batch, stoppingToken);
            batch.Clear();
        }
    }

    private async Task<bool> WaitForBatchAsync(List<LogMessage> batch, CancellationToken ct)
    {
        bool hasData = await _reader.WaitToReadAsync(ct);
        if (!hasData) return false;

        using var timeout = new CancellationTokenSource(FlushInterval);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            while (batch.Count < BatchSize && _reader.TryRead(out var msg))
            {
                batch.Add(msg);
            }

            while (batch.Count < BatchSize)
            {
                if (!await _reader.WaitToReadAsync(linked.Token))
                    break;

                while (batch.Count < BatchSize && _reader.TryRead(out var msg))
                {
                    batch.Add(msg);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // flush interval hit - flush whatever we have
        }

        return true;
    }

    private static async Task FlushAsync(SqliteConnection connection, List<LogMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        using var transaction = connection.BeginTransaction();
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages (ts, topic, payload, qos, retained)
            VALUES ($timestamp, $topic, $payload, $qos, $retained);
            """;

        var timestamp = insert.CreateParameter(); timestamp.ParameterName = "$timestamp";
        var topic = insert.CreateParameter(); topic.ParameterName = "$topic";
        var payload = insert.CreateParameter(); payload.ParameterName = "$payload";
        var qos = insert.CreateParameter(); qos.ParameterName = "$qos";
        var retained = insert.CreateParameter(); retained.ParameterName = "$retained";
        insert.Parameters.AddRange([timestamp, topic, payload, qos, retained]);

        foreach (var msg in batch)
        {
            timestamp.Value = msg.TimestampMs;
            topic.Value = msg.Topic;
            payload.Value = msg.Payload;
            qos.Value = msg.Qos;
            retained.Value = msg.Retained;
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}