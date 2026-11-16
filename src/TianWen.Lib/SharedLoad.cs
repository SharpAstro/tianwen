using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib;

/// <summary>
/// A load that happens once for everyone who asks: the first caller starts it, the callers that come while it
/// runs wait on the same task, and the ones after it get an answer at once.
/// </summary>
/// <remarks>
/// <para>The load takes NO cancellation token, by design: it is shared, so it is no caller's to cancel. Under the
/// first caller's token a caller that gave up would fail the load for everyone waiting on it.</para>
/// <para>A FAILED load is not kept. The callers waiting on it see its exception, and the next caller starts it
/// again: with a host that fetches its files, one dropped request must not leave the data gone for the process.
/// The load must therefore be safe to run again over whatever a failed run left behind.</para>
/// </remarks>
internal sealed class SharedLoad(Func<Task> load)
{
    private Task? _task;
    private int _done;

    /// <summary>True once a load has run to its end. Set AFTER the data is there, never when the load starts.</summary>
    public bool IsDone => Volatile.Read(ref _done) != 0;

    public ValueTask RunAsync()
    {
        // Fast path: already loaded, or loading.
        var current = Volatile.Read(ref _task);
        if (current is { IsCompletedSuccessfully: true })
        {
            return ValueTask.CompletedTask;
        }

        if (current is not null)
        {
            return new ValueTask(current);
        }

        // Publish the placeholder FIRST, then start the work. Passing Task.Run(...) as the CompareExchange
        // ARGUMENT evaluates it eagerly, so every racing caller would start a load of its own: the losers
        // return the winner's task and let their own run on. In the filter curve database that appended a
        // second copy of every curve (178 filters became 356), and every candidate was scored twice.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _task, tcs.Task, null);
        if (existing is not null)
        {
            return new ValueTask(existing);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await load();
                Interlocked.Exchange(ref _done, 1);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                // Cleared BEFORE the waiters are told, so a caller that retries on the failure starts a new load
                // and is not handed this one back.
                Interlocked.CompareExchange(ref _task, null, tcs.Task);
                tcs.SetException(ex);
            }
        }, CancellationToken.None);

        return new ValueTask(tcs.Task);
    }
}
