using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The live stack's loop: the window ending at the newest frame of a live capture's ring, stacked again and again, each master
/// handed on. ONE loop for the node's planetary run (<c>NodePlanetary</c>) and the probe that measures it
/// (<c>tianwen planetary live</c>, docs/plans/planetary-restoration.md, "The live stack, given the batch stack's learnings"), so what
/// is measured is what runs.
/// </summary>
public static class LiveStackLoop
{
    /// <summary>How often the loop stacks to the newest frame: a master four times a second at most.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Stacks the window ending at the newest frame of <paramref name="stream"/>'s ring every <see cref="Interval"/> while
    /// <paramref name="running"/> says so. A new ring (the first frame, or a new window size, which rebuilds the stream) starts the
    /// stack again at its framing, and so does a new RUN on the same ring (<see cref="LiveCameraFrameStream.RunStart"/>: the stream
    /// started again after an interlude, a filter changed), whose window never reaches back before the run's first frame. Each
    /// master goes to <paramref name="onMaster"/> with the stacker that made it and the frame it
    /// was stacked to, and the caller owns it. A failed stack goes to <paramref name="onFailure"/> and the next starts clean, so the
    /// capture goes on. Returns when <paramref name="running"/> turns false or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task RunAsync(Func<bool> running, Func<LiveCameraFrameStream?> stream, RollingWindowOptions options, ITimeProvider timeProvider,
        Action<Image, RollingWindowStacker, int> onMaster, Action<Exception> onFailure, CancellationToken cancellationToken)
    {
        RollingWindowStacker? stacker = null;
        LiveCameraFrameStream? stacking = null;
        var stackingFrom = 0;
        var built = -1;
        while (running())
        {
            try
            {
                await timeProvider.SleepAsync(Interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (stream() is not { } ring)
            {
                continue;
            }
            // The newest frame first, then the run it belongs to: a run marked between the two starts past the newest frame,
            // which waits for the run's first frame rather than stacking across the change.
            var latest = ring.LatestIndex;
            var from = ring.RunStart;
            if (latest < from)
            {
                continue;
            }
            if (stacker is null || !ReferenceEquals(ring, stacking) || from != stackingFrom)
            {
                stacker = new RollingWindowStacker(ring, options, firstIndex: from);
                stacking = ring;
                stackingFrom = from;
                built = -1;
            }
            if (latest <= built)
            {
                continue;
            }

            try
            {
                var master = await stacker.StackToAsync(latest, cancellationToken);
                built = latest;
                onMaster(master, stacker, latest);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onFailure(ex);
                stacker = null;
            }
        }
    }
}
