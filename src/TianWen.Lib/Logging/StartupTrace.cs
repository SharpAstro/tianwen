using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;

namespace TianWen.Lib.Logging;

/// <summary>
/// Phase timings for process start-up, measured from the instant the OS created the process and
/// written to the app log as one line per phase once the first frame is on screen.
/// </summary>
/// <remarks>
/// <para><b>Why the reference instant is the OS's and not a stopwatch started in <c>Main</c>.</b> The
/// part of start-up nothing inside the process can see is the part before its first statement runs:
/// the AOT runtime's own init, the loader, and -- for the packaged build -- MSIX activation. A
/// stopwatch started in <c>Main</c> measures everything except that, which is exactly the wrong half
/// to be blind to when the complaint is "it takes a moment to appear". <see cref="Process.StartTime"/>
/// is the creation stamp, so <c>main</c> below IS that invisible cost, reported like any other phase.</para>
///
/// <para><b>Two clocks, deliberately.</b> The offset from process creation to the first
/// <see cref="Mark"/> is taken once from the wall clock, whose resolution on Windows is the system
/// timer tick (~15.6 ms unless something has raised it); every phase after that is a
/// <see cref="Stopwatch"/> delta off that anchor, so the phases themselves are sub-millisecond and
/// only the pre-<c>Main</c> figure carries the coarse tick.</para>
///
/// <para>Marks are recorded into a fixed array with an interlocked cursor: a handful of appends over
/// the life of a process, one of them from the render thread, is not worth a lock, and this way
/// there is nothing for a contended one to stall. Overflow past
/// <see cref="Capacity"/> is dropped rather than grown -- a trace that needs more than that many
/// phases has stopped being a start-up trace.</para>
/// </remarks>
public static class StartupTrace
{
    private const int Capacity = 32;

    private static readonly (string Phase, TimeSpan At)[] _marks = new (string, TimeSpan)[Capacity];
    private static int _count;

    private static readonly Lock _anchorGate = new Lock();
    private static Stopwatch? _stopwatch;
    private static TimeSpan _beforeFirstMark;

    /// <summary>
    /// Record that <paramref name="phase"/> has just finished. The first call also anchors the
    /// trace, so it belongs at the very top of <c>Main</c> -- called later, the time before it is
    /// still counted (it lands in the first phase) but is no longer attributed.
    /// </summary>
    public static void Mark(string phase)
    {
        EnsureAnchored();

        var at = _beforeFirstMark + _stopwatch.Elapsed;
        var slot = Interlocked.Increment(ref _count) - 1;
        if (slot < Capacity)
        {
            _marks[slot] = (phase, at);
        }
    }

    /// <summary>
    /// Write the trace: one line per phase with the time that phase took and the total elapsed at
    /// its end. Safe to call more than once (the second call reports the same marks plus whatever
    /// arrived since), and a no-op when nothing was ever marked.
    /// </summary>
    public static void Log(ILogger logger)
    {
        var count = Math.Min(Volatile.Read(ref _count), Capacity);
        if (count == 0)
        {
            return;
        }

        var previous = TimeSpan.Zero;
        var line = new StringBuilder("startup:");
        for (var i = 0; i < count; i++)
        {
            var (phase, at) = _marks[i];
            line.Append(' ').Append(phase).Append(' ')
                .Append((int)(at - previous).TotalMilliseconds).Append("ms");
            previous = at;
        }

        // One line, not one per phase: a breakdown is only readable side by side, and a user pasting
        // "the slow bit" into an issue then pastes all of it.
        logger.LogInformation("{StartupTrace} | total {TotalMs}ms", line.ToString(),
            (int)previous.TotalMilliseconds);
    }

    [MemberNotNull(nameof(_stopwatch))]
    private static void EnsureAnchored()
    {
        if (_stopwatch is not null)
        {
            return;
        }

        // A lock rather than a CAS because the two fields have to become visible together, and this
        // runs exactly once, on the startup thread, before anything else can contend for it.
        lock (_anchorGate)
        {
            if (_stopwatch is not null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var self = Process.GetCurrentProcess();
                _beforeFirstMark = now - self.StartTime.ToUniversalTime();
                if (_beforeFirstMark < TimeSpan.Zero)
                {
                    _beforeFirstMark = TimeSpan.Zero;
                }
            }
            catch (Exception)
            {
                // A platform that will not report its own process creation time (or a sandbox that
                // refuses the query) costs the pre-Main figure, not the trace: every later phase is
                // still measured, just from the first mark instead.
                _beforeFirstMark = TimeSpan.Zero;
            }

            _stopwatch = stopwatch;
        }
    }
}
