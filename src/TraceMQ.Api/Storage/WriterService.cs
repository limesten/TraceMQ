using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Model;

namespace TraceMQ.Api.Storage;

/// <summary>
/// Owns the single writer connection. Nothing else in the process writes, which is what
/// makes SQLITE_BUSY on writes impossible (ARCHITECTURE.md section 4, rule 2).
/// </summary>
public sealed class WriterService : BackgroundService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ChannelReader<LogMessage> _reader;
    private readonly ILogger<WriterService> _log;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    public WriterService(
        Channel<LogMessage> channel,
        SqliteConnectionFactory factory,
        IOptions<StorageOptions> options,
        ILogger<WriterService> log)
    {
        _reader = channel.Reader;
        _factory = factory;
        _log = log;
        _batchSize = options.Value.BatchSize;
        _flushInterval = TimeSpan.FromMilliseconds(options.Value.FlushIntervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await _factory.OpenAsync(stoppingToken);

        var batch = new List<LogMessage>(_batchSize);
        var written = 0L;

        while (await WaitForBatchAsync(batch, stoppingToken))
        {
            try
            {
                await FlushAsync(connection, batch, stoppingToken);
                written += batch.Count;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "Failed to write a batch of {Count} messages", batch.Count);
            }
            batch.Clear();
        }

        _log.LogInformation("Writer stopped after {Written} messages", written);
    }

    private async Task<bool> WaitForBatchAsync(List<LogMessage> batch, CancellationToken ct)
    {
        bool hasData;
        try
        {
            hasData = await _reader.WaitToReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (!hasData) return false;

        using var timeout = new CancellationTokenSource(_flushInterval);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            while (batch.Count < _batchSize && _reader.TryRead(out var msg))
            {
                batch.Add(msg);
            }

            while (batch.Count < _batchSize)
            {
                if (!await _reader.WaitToReadAsync(linked.Token))
                    break;

                while (batch.Count < _batchSize && _reader.TryRead(out var msg))
                {
                    batch.Add(msg);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Flush interval hit: commit what we have rather than holding it.
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Fall through and flush what was already taken off the channel.
        }

        return true;
    }

    private static async Task FlushAsync(SqliteConnection connection, List<LogMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        await using var transaction = connection.BeginTransaction();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages (id, ts, topic, correlation_key, qos, retained, payload)
            VALUES ($id, $ts, $topic, $correlationKey, $qos, $retained, $payload);
            """;

        var id = insert.CreateParameter(); id.ParameterName = "$id";
        var ts = insert.CreateParameter(); ts.ParameterName = "$ts";
        var topic = insert.CreateParameter(); topic.ParameterName = "$topic";
        var correlationKey = insert.CreateParameter(); correlationKey.ParameterName = "$correlationKey";
        var qos = insert.CreateParameter(); qos.ParameterName = "$qos";
        var retained = insert.CreateParameter(); retained.ParameterName = "$retained";
        var payload = insert.CreateParameter(); payload.ParameterName = "$payload";
        insert.Parameters.AddRange([id, ts, topic, correlationKey, qos, retained, payload]);

        foreach (var msg in batch)
        {
            id.Value = msg.Id;
            ts.Value = msg.TimestampMs;
            topic.Value = msg.Topic;
            correlationKey.Value = (object?)msg.CorrelationKey ?? DBNull.Value;
            qos.Value = msg.Qos;
            retained.Value = msg.Retained;
            payload.Value = msg.Payload;
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
