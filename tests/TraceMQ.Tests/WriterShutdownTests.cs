using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

/// <summary>
/// Measures CPU, so nothing else may be running in this process at the same time.
/// </summary>
[CollectionDefinition("serial", DisableParallelization = true)]
public class SerialCollection;

[Collection("serial")]
public class WriterShutdownTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, error));
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static (TempDb Db, WriterQueue Queue, WriterService Writer, Channel<LogMessage> Channel, CapturingLogger<WriterService> Log) Start()
    {
        var db = new TempDb();
        Schema.Initialize(db.Factory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var channel = Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var queue = new WriterQueue();
        var log = new CapturingLogger<WriterService>();
        var writer = new WriterService(
            channel, db.Factory, queue,
            Options.Create(new StorageOptions { DbPath = db.Path, FlushIntervalMs = 20 }),
            log);
        return (db, queue, writer, channel, log);
    }

    [Fact]
    public async Task DoesNotSpinAfterIngestClosesTheMessageChannel()
    {
        // The regression. Ingest completing the channel makes its awaiter complete
        // synchronously with false forever; re-awaiting that in a loop burns a core with
        // nothing in the log. Today only the hosted-service stop order hides it.
        var (db, _, writer, channel, _) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        channel.Writer.TryWrite(new LogMessage(1, 1, "codeit/a", [0x00], 0, false, null));
        channel.Writer.TryComplete();
        await Task.Delay(100);

        var before = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(400);
        var burned = Process.GetCurrentProcess().TotalProcessorTime - before;
        wall.Stop();

        await writer.StopAsync(CancellationToken.None);

        // A spin burns about one core for the whole window; idle burns almost nothing.
        // Half a core is far above idle and far below a spin.
        Assert.True(burned < wall.Elapsed / 2,
            $"writer burned {burned.TotalMilliseconds:F0}ms of CPU over {wall.ElapsedMilliseconds}ms — it is spinning");
    }

    [Fact]
    public async Task LatchesOnceAndKeepsServingQueuedWork()
    {
        var (db, queue, writer, channel, log) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        channel.Writer.TryComplete();
        await Task.Delay(100);

        // Retention and the settings endpoint still need the writer after ingest has gone.
        var result = await queue
            .EnqueueAsync("after-ingest", async (connection, ct) =>
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 5;";
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            })
            .WaitAsync(TimeSpan.FromSeconds(10));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(5, result);
        lock (log.Messages)
        {
            Assert.Single(log.Messages, m => m.Contains("closed the message channel"));
        }
    }

    [Fact]
    public async Task StopsPromptlyWhileTheStreamIsBusy()
    {
        var (db, _, writer, channel, _) = Start();
        using var _db = db;
        await writer.StartAsync(CancellationToken.None);

        for (var i = 1; i <= 500; i++)
        {
            channel.Writer.TryWrite(new LogMessage(i, i, "codeit/a", [0x00], 0, false, null));
        }

        var stop = Stopwatch.StartNew();
        await writer.StopAsync(CancellationToken.None);
        stop.Stop();

        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {stop.ElapsedMilliseconds}ms");
    }
}
