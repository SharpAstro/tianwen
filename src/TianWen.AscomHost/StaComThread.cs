using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.AscomHost;

/// <summary>
/// A single dedicated STA thread that serves as the one-and-only apartment for every hosted COM object.
/// <para>
/// This is load-bearing, not decoration. Legacy in-proc ASCOM COM drivers create a hidden window (their
/// <c>Connected=true</c> setter pumps it via <c>Application.DoEvents()</c>), and a window has thread
/// affinity: every call to that object must arrive on the thread that created it, or the pump runs on the
/// wrong message queue and the driver hangs / misbehaves. <see cref="JsonRpcServer"/> deliberately uses
/// <c>ConfigureAwait(false)</c>, so its request-handler continuations land on arbitrary thread-pool (MTA)
/// threads -- we therefore marshal each COM operation onto this one STA thread via <see cref="InvokeAsync{T}"/>
/// rather than relying on a synchronization context the server would ignore.
/// </para>
/// <para>Work items run strictly sequentially (one COM object wants exactly that), so state the ops touch
/// -- the handle table in <see cref="AscomComHost"/> -- needs no locking: it is only ever read/written on
/// this thread.</para>
/// </summary>
internal sealed class StaComThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaComThread()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ascom-com-sta",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and completes the returned task with its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException) // queue completed (shutting down)
        {
            tcs.SetCanceled();
        }
        return tcs.Task;
    }

    /// <summary>Runs a void <paramref name="action"/> on the STA thread.</summary>
    public Task InvokeAsync(Action action) => InvokeAsync<object?>(() =>
    {
        action();
        return null;
    });

    public void Dispose() => _queue.CompleteAdding();
}
