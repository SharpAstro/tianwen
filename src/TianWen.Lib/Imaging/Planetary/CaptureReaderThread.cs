using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The thread a <see cref="PlanetaryCapture"/>'s loop runs on (TIA2's capture-priority leg), and its recording's writer
/// (<see cref="SerRecording"/>): a thread of its own at <see cref="Priority"/>, which is also the loop's
/// <see cref="SynchronizationContext"/>, so every await of the loop that keeps its context resumes here rather than on the
/// thread pool.
/// </summary>
/// <remarks>
/// <para><b>Why a thread of its own.</b> The loop was a thread pool continuation at normal priority. With the machine's cores
/// all busy at normal priority (32 spinning processes, or a training run, a test suite), it waited past the driver's
/// 64-frame hand-off (320 ms at 5 ms) and the hand-off dropped the oldest frames: 130 to 197 frames a second of the camera's
/// 197, in process as through the node (docs/plans/live-planetary-capture.md, "Video through the node at the Saturn window").
/// A pool thread cannot be given a priority of its own, and raising the process's would lift the live stack and the view
/// with it, which must stay below the reader.</para>
/// <para><b>Why <see cref="ThreadPriority.AboveNormal"/>, never higher.</b> It is above every normal-priority thread, a busy
/// machine's load included, and below the driver's stream thread, which is <see cref="ThreadPriority.Highest"/> so the body's
/// own buffers never overflow, and below what the operating system keeps for itself.</para>
/// <para><b>Why the loop's awaits keep their context.</b> A thread of its own alone would only START the loop there: an
/// await that completes elsewhere resumes, with <c>ConfigureAwait(false)</c>, on the thread that completed it, a pool
/// thread, and the loop stays on the pool from then on. So the loop's own awaits keep their context and are posted back
/// here, and a driver whose frame wait keeps its context (the DAL's) wakes this thread straight from its stream thread.</para>
/// <para><b>Why the recording's writer too.</b> The loop shares two locks with it, its queue's and the shared byte pool's
/// per-core stacks, and a writer on the pool that a busy machine held off while it held one held the loop up behind it.
/// Measured under 32 normal-priority spinners: with the writer on the pool the loop once went 357 ms without a frame and
/// the hand-off replaced 6; with the writer on a thread of its own, 165 ms at most and none.</para>
/// </remarks>
internal sealed class CaptureReaderThread : SynchronizationContext
{
    /// <summary>The priority the reader runs at.</summary>
    internal const ThreadPriority Priority = ThreadPriority.AboveNormal;

    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();

    // Released once for each post, so the pump wakes for each; never disposed, as it holds no kernel handle until one asks
    // for its wait handle, and a late Post may still release it after the pump has gone.
    private readonly SemaphoreSlim _ready = new SemaphoreSlim(0);

    private int _ended;

    private CaptureReaderThread()
    {
    }

    /// <summary>
    /// Runs <paramref name="loop"/> on a new background thread named <paramref name="name"/> at <see cref="Priority"/>, its
    /// awaits that keep their context resumed there; completes as the loop does.
    /// </summary>
    public static Task Run(string name, Func<Task> loop)
    {
        var reader = new CaptureReaderThread();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => reader.Pump(loop, ended))
        {
            IsBackground = true,
            Name = name,
            Priority = Priority,
        };
        thread.Start();
        return ended.Task;
    }

    private void Pump(Func<Task> loop, TaskCompletionSource ended)
    {
        SetSynchronizationContext(this);
        Task running;
        try
        {
            running = loop();
        }
        catch (Exception ex)
        {
            ended.TrySetException(ex);
            return;
        }

        // Woken once the loop has ended, wherever its last continuation ran.
        running.ContinueWith(static (_, reader) => ((CaptureReaderThread)reader!)._ready.Release(), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (!running.IsCompleted)
        {
            // Blocking, and that is the point (CLAUDE.md: no blocking unless forced): this thread exists to wait for the next
            // frame and run the loop's work at a priority the thread pool cannot give. An await here would hand the wake back
            // to the pool, which is what the reader is taken off; a spin would hold a core while no frame comes.
            _ready.Wait();
            while (_posted.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }

        // Whatever is posted from now on goes to the thread pool, and so does anything posted as the loop ended (Post looks
        // again once it has queued, so nothing is left in between).
        Interlocked.Exchange(ref _ended, 1);
        HandLateWorkToThePool();
        SetSynchronizationContext(null);

        if (running.Exception is { } failed)
        {
            ended.TrySetException(failed.InnerExceptions);
        }
        else if (running.IsCanceled)
        {
            ended.TrySetCanceled();
        }
        else
        {
            ended.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        _posted.Enqueue((d, state));
        if (Volatile.Read(ref _ended) == 1)
        {
            HandLateWorkToThePool();
            return;
        }
        _ready.Release();
    }

    /// <inheritdoc/>
    /// <remarks>Never asked for by an await; a synchronous send to the reader from another thread would block that thread on a
    /// frame's work, so it is refused.</remarks>
    public override void Send(SendOrPostCallback d, object? state)
        => throw new NotSupportedException("A capture's reader thread takes work only by Post");

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => this;

    private void HandLateWorkToThePool()
    {
        while (_posted.TryDequeue(out var late))
        {
            ThreadPool.QueueUserWorkItem(static work => work.Callback(work.State), late, preferLocal: false);
        }
    }
}
