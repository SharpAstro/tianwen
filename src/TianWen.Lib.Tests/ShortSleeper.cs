using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TianWen.Lib.Tests;

/// <summary>
/// A thread that sleeps short real times exactly, for a wait that has to last "at least a millisecond" and must not
/// hold a CPU while it does: the fake-time pump's dwell (#2081).
/// </summary>
/// <remarks>
/// <para><b>What it replaced.</b> The pump kept its dwell by yielding to the pool until a millisecond had passed:
/// about 1,400 yields each time, on a pool thread that did nothing else. Over three session test classes that was
/// 48 million yields and 25.6 s of one core.</para>
/// <para><b>Why a thread and not a timer.</b> The runtime's timers count whole milliseconds off a clock that ticks in
/// milliseconds, so <c>Task.Delay(1)</c> ends at the next tick, anywhere from at once to a millisecond later. It is
/// not a floor, and asking twice to make it one costs a millisecond more. A thread that sleeps is as exact as the
/// system lets it be. On Linux and macOS a sleep of 1 ms takes 1 ms and a little. On Windows this uses a
/// high-resolution waitable timer, which ends at its next half-millisecond tick. Measured on a win-arm64 laptop over
/// 200 waits each: asked 1.0 ms, it took 1.53 ms at the median and 1.57 at the 90th percentile; a plain
/// <c>Thread.Sleep(1)</c> took 2.0 ms with the timer resolution raised and 15.8 without.</para>
/// <para>One sleeper serves one pump run, its sleeps one after another. It ends with the run
/// (<see cref="DisposeAsync"/>), and is a background thread besides, so it cannot outlive the process.</para>
/// </remarks>
internal sealed partial class ShortSleeper : IAsyncDisposable
{
    private readonly ConcurrentQueue<(TimeSpan Time, TaskCompletionSource Slept)> _asked = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ShortSleeper()
    {
        new Thread(Run) { IsBackground = true, Name = "fake-time pump sleeper" }.Start();
    }

    /// <summary>Ends once <paramref name="time"/> of real time has passed, and not before.</summary>
    public Task After(TimeSpan time)
    {
        var slept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _asked.Enqueue((time, slept));
        _wake.Release();
        return slept.Task;
    }

    /// <summary>Ends the thread once the sleeps already asked for are done, and returns when it has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        // One release with nothing asked is the end: the thread takes a release for each sleep, and the one it
        // finds no sleep for is this one.
        _wake.Release();
        await _ended.Task;
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            using var timer = OperatingSystem.IsWindows() ? HighResolutionTimer() : null;
            while (true)
            {
                _wake.Wait();
                if (!_asked.TryDequeue(out var asked))
                {
                    return;
                }

                Sleep(asked.Time, timer);
                asked.Slept.TrySetResult();
            }
        }
        finally
        {
            _ended.TrySetResult();
        }
    }

    private static void Sleep(TimeSpan time, SafeWaitHandle? timer)
    {
        if (time <= TimeSpan.Zero)
        {
            return;
        }

        // A relative due time is negative, in units of 100 ns, which is what a tick is. The wait has a deadline of
        // its own a second past the sleep: nothing here waits without one.
        var due = -time.Ticks;
        if (timer is not null && OperatingSystem.IsWindows()
            && SetWaitableTimer(timer, in due, 0, 0, 0, false)
            && WaitForSingleObject(timer, (uint)Math.Ceiling(time.TotalMilliseconds) + 1000) == 0)
        {
            return;
        }

        // Whole milliseconds, rounded UP: a sleep of less than one would be asked as zero and return at once.
        Thread.Sleep((int)Math.Ceiling(time.TotalMilliseconds));
    }

    /// <summary>A waitable timer that ends at the system's finest tick, half a millisecond; null where the system has
    /// none (before Windows 10 1803), and the sleep is then a plain one.</summary>
    [SupportedOSPlatform("windows")]
    private static SafeWaitHandle? HighResolutionTimer()
    {
        const uint CreateWaitableTimerHighResolution = 0x2;
        const uint TimerAllAccess = 0x1F0003;
        var timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            return null;
        }

        return timer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long due, int period, nint routine, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
