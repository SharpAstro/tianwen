using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TianWen.Lib.Devices;

/// <summary>
/// An asynchronous wait that ends when it should on Windows: what <see cref="SystemTimeProvider"/> waits with for a wait short
/// enough that a timer tick would be a real share of it (#2020).
/// </summary>
/// <remarks>
/// <para><b>On Windows every timer-based wait wakes on the system timer tick, 15.625 ms by default</b>, so a
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> or a BCL timer of 1 ms lasts about 15.6 ms (2026-11-05). For a guide
/// pulse timed on our side the length of the wait IS the correction, so the tick is guiding error. TianWen is a library and
/// neither relies on its host calling <c>timeBeginPeriod(1)</c> nor calls it (process-wide, and a power cost).</para>
/// <para>Here each wait is a high-resolution waitable timer (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Windows 10 1803 and
/// later), waited on by the thread pool's wait thread (<see cref="ThreadPool.UnsafeRegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object?, int, bool)"/>)
/// and completed through a <see cref="TaskCompletionSource"/>, so no thread is blocked and nothing changes for the rest of the
/// process. It is the async twin of TianWen.DAL's blocking <c>PreciseWait</c>, kept here because Lib must not need a DAL that
/// has it. Where the timer cannot be made, and on every other system, whose waits are already fine-grained, it is
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</para>
/// <para><b>Only up to <see cref="PreciseUpTo"/>.</b> The tick's error is absolute: a 1 s guide pulse a tick long moves the mount
/// as far wrong as a 50 ms one, so the threshold covers the whole guide-pulse range (<c>GuideLoop.MaxPulseMs</c>, 2 s) rather
/// than only the waits where the tick is a large fraction. Past it the waits are polls, settles and timeouts, where 16 ms is
/// nothing and a kernel timer plus a slot on the pool's wait thread buys nothing.</para>
/// </remarks>
internal static partial class HighResolutionWait
{
    /// <summary>The longest wait made precise: past the longest guide pulse (2 s), with room.</summary>
    internal static readonly TimeSpan PreciseUpTo = TimeSpan.FromMilliseconds(2500);

    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    /// <summary>Whether a wait of <paramref name="duration"/> goes through the high-resolution timer on this system.</summary>
    internal static bool Applies(TimeSpan duration)
        => OperatingSystem.IsWindows() && duration > TimeSpan.Zero && duration <= PreciseUpTo;

    /// <summary>
    /// Waits <paramref name="duration"/>, precisely where <see cref="Applies"/>, else as <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// A cancelled token ends it with an <see cref="OperationCanceledException"/> and releases the timer.
    /// </summary>
    internal static Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!Applies(duration) || cancellationToken.IsCancellationRequested)
        {
            return Task.Delay(duration, cancellationToken);
        }

        var timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
        // Negative is relative, in 100 ns units.
        var dueTime = -Math.Max(1L, duration.Ticks);
        if (timer.IsInvalid || !SetWaitableTimer(timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            // Older Windows refuses the flag: an ordinary delay, a tick long at worst.
            timer.Dispose();
            return Task.Delay(duration, cancellationToken);
        }

        return WaitAsync(timer, cancellationToken);
    }

    /// <summary>
    /// Waits for the armed <paramref name="timer"/>, then unregisters the wait and closes the timer, in that order and on every
    /// path: all the clean-up runs here, after the await, so neither a firing nor a cancellation races it.
    /// </summary>
    private static async Task WaitAsync(SafeWaitHandle timer, CancellationToken cancellationToken)
    {
        // Disposed last: closing the handle cancels a timer that has not fired. The registration holds its own reference to
        // the handle until it is unregistered, so the close never pulls a handle out from under the wait thread.
        using var handle = new TimerWaitHandle(timer);
        // Asynchronous continuations: a cancellation never runs the waiter's code on the thread that cancelled.
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.UnsafeRegisterWaitForSingleObject(handle,
            static (state, _) => (state as TaskCompletionSource)?.TrySetResult(), fired, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            using (cancellationToken.UnsafeRegister(static (state, token) => (state as TaskCompletionSource)?.TrySetCanceled(token), fired))
            {
                await fired.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            wait.Unregister(null);
        }
    }

    /// <summary>A waitable timer as a <see cref="WaitHandle"/>, which is what the thread pool waits on.</summary>
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle timer) => SafeWaitHandle = timer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
