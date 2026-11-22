using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace TianWen.Lib.Extensions;

/// <summary>
/// A line a thread that must not wait says as it goes, written by the thread pool rather than by that thread.
/// </summary>
/// <remarks>
/// <para>When this was written a host's file log was one lock for every logger in its process, its writer flushing each
/// line to disk under it (<c>FileLoggerProvider</c>), and a normal-priority thread a busy machine held off while it held
/// that lock would hold up the thread that logged behind it: the camera's stream thread, the capture's reader (TIA2,
/// review round 1 of #1621).
/// What the line says is taken on the calling thread; only the writing waits, so a line can be written late and out of
/// order with the lines around it. A time a reader needs goes INTO the line, never read off its write.</para>
/// <para>Since #541 the file log has no lock: it queues a line where it is logged and writes it on a thread of its own.
/// What this extension still takes off the calling thread is the line's formatting and whatever other sink a host has
/// (the console's). The capture was measured with it, and not yet without, so its callers keep it.</para>
/// </remarks>
public static class LoggerOffThreadExtensions
{
    private static long _linesLost;

    /// <summary>Lines queued through <see cref="LogDebugOffThread"/> whose write threw, in this process.</summary>
    internal static long LinesLost => Interlocked.Read(ref _linesLost);

    /// <summary>Queues a Debug line to the thread pool, when Debug is enabled.</summary>
    public static void LogDebugOffThread(this ILogger logger, string message, params object?[] args)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ThreadPool.UnsafeQueueUserWorkItem(static line => line.Write(), new OffThreadLine(logger, message, args), preferLocal: false);
        }
    }

    private sealed class OffThreadLine(ILogger logger, string message, object?[] args)
    {
        public void Write()
        {
            try
            {
#pragma warning disable CA2254 // the template is each caller's constant, carried here
                logger.LogDebug(message, args);
#pragma warning restore CA2254
            }
            catch (Exception)
            {
                // A write that throws (the file log closed, a full disk) costs the line, never the process: out of a pool work
                // item it would end it, where the same call on the calling thread threw into that thread's catch (#1628). The
                // logger that failed is the one place to say so, so the line is only counted.
                Interlocked.Increment(ref _linesLost);
            }
        }
    }
}
