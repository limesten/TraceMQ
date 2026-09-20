using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TraceMQ.Api.Storage;

/// <summary>
/// The way anything other than the message stream gets a write done: retention deletes, the
/// correlation backfill, settings. Work is handed to <see cref="WriterService"/> and runs on
/// its connection, between batches.
///
/// This exists because of rule 2 (ARCHITECTURE.md section 4). A request thread opening its
/// own connection to run a DELETE would make SQLITE_BUSY on writes possible again, which the
/// single-writer design rules out by construction.
/// </summary>
public sealed class WriterQueue
{
    public sealed record WorkItem(Func<SqliteConnection, CancellationToken, Task> Run, string Name);

    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<WorkItem> Reader => _work.Reader;

    public Task<T> EnqueueAsync<T>(string name, Func<SqliteConnection, CancellationToken, Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var item = new WorkItem(async (connection, token) =>
        {
            try
            {
                completion.TrySetResult(await work(connection, token));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, name);

        if (!_work.Writer.TryWrite(item))
        {
            completion.TrySetException(new InvalidOperationException("The writer queue is closed."));
        }

        return completion.Task;
    }

    public Task EnqueueAsync(string name, Func<SqliteConnection, CancellationToken, Task> work) =>
        EnqueueAsync(name, async (connection, token) =>
        {
            await work(connection, token);
            return true;
        });

    // Deliberately no Complete(): the queue has several producers (retention, the settings
    // endpoint) and no single owner to close it. The writer's exit signal is its stopping
    // token, and an earlier guard that tested Reader.Completion here was dead code that made
    // the spin below it look handled.
}
