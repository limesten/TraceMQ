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
    private readonly WriterQueue _queue;
    private readonly ILogger<WriterService> _log;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    // Kept across iterations. Abandoning a fresh pair of awaiters every time queues a waiter
    // on each channel that lingers until something wakes it, which accumulates whenever one
    // side is busy and the other is quiet.
    private Task<bool>? _messagesReady;
    private Task<bool>? _queueReady;
    private bool _messagesDone;

    public WriterService(
        Channel<LogMessage> channel,
        SqliteConnectionFactory factory,
        WriterQueue queue,
        IOptions<StorageOptions> options,
        ILogger<WriterService> log)
    {
        _reader = channel.Reader;
        _factory = factory;
        _queue = queue;
        _log = log;
        _batchSize = options.Value.BatchSize;
        _flushInterval = TimeSpan.FromMilliseconds(options.Value.FlushIntervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await _factory.OpenAsync(stoppingToken);

        var batch = new List<LogMessage>(_batchSize);
        var written = 0L;

        while (await WaitForWorkAsync(batch, stoppingToken))
        {
            // Queued work first: it is rare, and a retention sweep or a backfill waiting
            // behind a busy stream would never get a turn.
            await DrainQueueAsync(connection, stoppingToken);

            if (batch.Count == 0) continue;

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

    private async Task DrainQueueAsync(SqliteConnection connection, CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var item))
        {
            try
            {
                await item.Run(connection, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The item's own continuation already carries the failure; this is for the log.
                _log.LogError(ex, "Queued write {Name} failed", item.Name);
            }
        }
    }

    /// <summary>
    /// Wake on a message or on queued work, whichever comes first, then take whatever
    /// messages are available. Waiting on the message channel alone would starve the queue
    /// on an idle line, which is exactly when a retention sweep wants to run.
    /// </summary>
    private async Task<bool> WaitForWorkAsync(List<LogMessage> batch, CancellationToken ct)
    {
        if (!await WaitForSignalAsync(ct)) return false;

        if (!_reader.TryPeek(out _)) return true;

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

    /// <summary>
    /// Block until there is something to do, or cancellation.
    ///
    /// Once ingest has completed the message channel, its awaiter completes synchronously
    /// with false every time it is asked. Re-awaiting it in a loop is a spin that pegs a
    /// core with nothing in the log, so from that point the wait is on the queue alone.
    /// </summary>
    private async Task<bool> WaitForSignalAsync(CancellationToken ct)
    {
        try
        {
            if (_messagesDone)
            {
                return await _queue.Reader.WaitToReadAsync(ct);
            }

            _messagesReady ??= _reader.WaitToReadAsync(ct).AsTask();
            _queueReady ??= _queue.Reader.WaitToReadAsync(ct).AsTask();

            var first = await Task.WhenAny(_messagesReady, _queueReady);

            if (ReferenceEquals(first, _queueReady))
            {
                _queueReady = null;
                return true;
            }

            _messagesReady = null;
            if (!await first)
            {
                _messagesDone = true;
                _log.LogInformation("Ingest closed the message channel; serving queued writes only");
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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
