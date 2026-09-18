using System.Runtime;
using System.Runtime.Versioning;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// Hands the heap back to the OS once a burst of thumbnail requests has gone quiet, and at no other
    /// time.
    /// <para>
    /// The problem it exists for (issue #294): a request allocates several hundred MB at full
    /// resolution -- the file's bytes, one float plane per channel, then a debayered three-plane copy,
    /// all on the large object heap -- and every byte of it is garbage by the time
    /// <c>GetThumbnail</c> returns. A collection only runs when something allocates, so an idle
    /// surrogate never runs one, and the LOH is not compacted by default even when it does. One
    /// measured surrogate sat at 7,024 MB of private bytes for thirteen hours after its last thumbnail.
    /// </para>
    /// <para>
    /// <b>Collecting once per request is the obvious fix and the wrong one.</b> A blocking, compacting
    /// gen-2 collection is the most expensive thing the runtime can be asked for, it suspends every
    /// thread, and the shell extracts a folder's thumbnails IN PARALLEL -- so each request would pay
    /// for every other request's collection, and each collection would throw away the exact heap the
    /// next request is about to ask the OS for again. The cost belongs at the END of a burst, where
    /// there is no next request to pay it.
    /// </para>
    /// <para>
    /// So each finished render increments a counter and arms a one-shot timer; the timer compares the
    /// counter against what it saw last time. A tick that finds the counter MOVED means the burst is
    /// still running, and defers. A tick that finds it UNCHANGED since a previous tick, and different
    /// from what was last collapsed at, is the end of a burst and collapses exactly once. A tick that
    /// has already collapsed at this count does nothing at all -- which is what lets the timer stop
    /// itself, so an idle surrogate wakes up for nothing rather than ticking for thirteen hours.
    /// </para>
    /// <para>
    /// <b>Ticks cannot overlap, which is what keeps this lock-free.</b> The timer is one-shot, so it
    /// never re-fires on its own, and <see cref="_armed"/> is cleared only at the very end of
    /// <see cref="Tick"/> -- so while a tick is running, <see cref="Arm"/>'s compare-exchange fails and
    /// nothing can schedule a second one. Only <see cref="_renders"/> is touched from other threads,
    /// and only through <see cref="Interlocked"/>.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class IdleHeapCollapse : IDisposable
    {
        /// <summary>
        /// How long a burst has to be quiet before the heap is given back. A tick can only tell a
        /// finished burst from a running one by seeing the counter stand still ACROSS two of them, so
        /// the collapse lands between one and two of these after the last thumbnail. Three seconds
        /// keeps that inside ten, which is soon enough for a process whose complaint is measured in
        /// hours, while being far longer than the gap between two thumbnails in one folder scroll.
        /// </summary>
        internal const int DefaultQuietMilliseconds = 3_000;

        /// <summary>
        /// The instance the shell handler drives. Created on first use, so a process that is loaded
        /// and never asked for a thumbnail starts no timer.
        /// </summary>
        internal static IdleHeapCollapse Shared { get; } = new IdleHeapCollapse(CollapseHeap, DefaultQuietMilliseconds);

        private readonly Action _collapse;
        private readonly int _quietMilliseconds;
        private readonly Timer _timer;

        private long _renders;
        private long _seenAtLastTick;
        private long _collapsedAt;
        private int _armed;

        /// <param name="collapse">What giving the heap back means. The shared instance passes
        /// <see cref="CollapseHeap"/>; a test passes something it can count, so the policy above can be
        /// driven without a real collection in the test runner's own process.</param>
        /// <param name="quietMilliseconds">The quiet window, or <see cref="Timeout.Infinite"/> to leave
        /// the timer inert and drive <see cref="Tick"/> by hand.</param>
        internal IdleHeapCollapse(Action collapse, int quietMilliseconds)
        {
            _collapse = collapse;
            _quietMilliseconds = quietMilliseconds;
            _timer = new Timer(static state => ((IdleHeapCollapse)state!).Tick(), this, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>How many renders have been recorded; the counter the tick compares against.</summary>
        internal long Renders => Interlocked.Read(ref _renders);

        /// <summary>Whether a tick is scheduled. False on an idle process, which is the point.</summary>
        internal bool IsArmed => Volatile.Read(ref _armed) != 0;

        /// <summary>
        /// One request finished allocating. Called whatever the request's outcome, because a failed
        /// decode has allocated just as much as a successful one -- what must NOT reach here is a
        /// request that returned before touching the file, which allocated nothing and has no reason
        /// to wake the timer.
        /// </summary>
        internal void RecordRender()
        {
            Interlocked.Increment(ref _renders);
            Arm();
        }

        internal void Tick()
        {
            var renders = Interlocked.Read(ref _renders);

            if (renders != _seenAtLastTick)
            {
                // The burst is still running. Collapsing here would stop every thread the shell has
                // extracting in parallel, to throw away the heap the next request is about to want.
                _seenAtLastTick = renders;
                Schedule();
                return;
            }

            if (renders != _collapsedAt)
            {
                _collapsedAt = renders;
                try
                {
                    _collapse();
                }
                catch (Exception ex)
                {
                    // This runs on a pool thread with nobody to throw to, so an escaping exception is
                    // not a failed collection: it is the surrogate process going down, taking every
                    // thumbnail Explorer was drawing with it. Handing memory back is best-effort by
                    // nature and a request has never depended on it, so it is recorded and dropped --
                    // the same rule as never throwing across the COM boundary, on the one code path
                    // in this DLL that has no boundary to return an HRESULT to.
                    ThumbnailDiagnostics.Failure("IdleHeapCollapse", ex.HResult, $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            // Stop. An armed timer on an idle surrogate is thousands of wakeups that find the same
            // number they found last time, in a process whose whole complaint is that it sits idle.
            Volatile.Write(ref _armed, 0);

            // A render that landed between the read above and that write saw the timer armed and did
            // not arm it, so without this re-check its garbage would be the one burst nobody collapses.
            if (Interlocked.Read(ref _renders) != renders)
            {
                Arm();
            }
        }

        public void Dispose() => _timer.Dispose();

        private void Arm()
        {
            // One timer arm per burst rather than one per thumbnail, and the guard that makes the
            // no-overlap argument above hold: while a tick is running this fails, so nothing can
            // schedule a second one behind it.
            if (Interlocked.CompareExchange(ref _armed, 1, 0) == 0)
            {
                Schedule();
            }
        }

        private void Schedule() => _timer.Change(_quietMilliseconds, Timeout.Infinite);

        /// <summary>
        /// What <see cref="Shared"/> means by giving the heap back. Reachable to a test so the one
        /// live call can be exercised without a process-wide singleton in the way.
        /// </summary>
        internal static void CollapseHeap()
        {
            // Compacting the large object heap is not optional here: every plane of a full-resolution
            // decode is far over the 85 KB threshold, so the fragmentation this exists to return is
            // all in the LOH, which an ordinary collection sweeps but never compacts.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

            // Aggressive is the mode that actually DECOMMITS rather than keeping the segments for a
            // next request this process may never get.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
}
