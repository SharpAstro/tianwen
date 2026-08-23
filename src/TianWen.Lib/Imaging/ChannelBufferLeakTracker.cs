using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// DEBUG-only leak detection for <see cref="ChannelBuffer"/>: an explicit table of every buffer that
/// has been created and not yet released, keyed so a survivor can be attributed to the code that
/// produced it. P2 of <c>docs/plans/frame-lifecycle.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Why an instrument at all.</b> Both tile-pipelined stacking strategies failed to release
/// their raw frame and both were correct anyway, because a self-owned file load makes the omission
/// free -- and the same code became a real leak the moment the read was pooled. Nothing in the type
/// system noticed, and it was eventually found by watching process memory. A table finds it at the
/// call site instead, which is the difference between a measurement and a diagnosis.</para>
///
/// <para><b>Two distinct answers, because a buffer can fail to be released in two ways.</b>
/// <see cref="ChannelBufferLeakReport.LiveCount"/> is what is outstanding right now: normal
/// mid-session (a camera frame between exposures), and a leak when it is read at a point where
/// nothing should still be held -- the end of a stack run, the end of a test. That one is
/// deterministic and needs no GC. <see cref="ChannelBufferLeakReport.LeakCount"/> counts buffers the
/// collector took while they were still outstanding, which is unambiguous: nobody was ever going to
/// release them. It needs a collection to have happened, hence <c>collectFirst</c>.</para>
///
/// <para><b>Not a finalizer, deliberately.</b> A finalizer on <see cref="ChannelBuffer"/> would put
/// EVERY buffer on the finalizer queue in DEBUG and change GC timing in exactly the tests that assert
/// pooling behaviour (<c>FitsPooledReadTests.Pool_StopsRetainingOnceTheByteBudgetIsReached</c> reads
/// <c>Array2DPool.RetainedBytes</c>). A weak reference observes the same fact and enqueues nothing.
/// For the same reason the table holds a weak reference and never a strong one: a diagnostic that
/// keeps its subject alive has changed the thing it measures, turning a buffer the collector would
/// have reclaimed into one it cannot.</para>
///
/// <para><b>The weak reference is to the ARRAY, not to the buffer.</b> The array is what the pool and
/// the camera are waiting for, so its liveness is the fact worth watching -- and it is available in a
/// field initialiser, where <c>this</c> is not, which is what keeps <see cref="ChannelBuffer"/> on
/// its primary constructor rather than being restructured for the benefit of a debug aid.</para>
///
/// <para><b>Cost in a Release build is nothing:</b> every method below has an empty body outside
/// DEBUG, so the calls inline away and only the caller-info literals remain at the call site. This is
/// per-frame-channel work on a type that wraps a multi-megabyte array, never per-pixel.
/// <see cref="IsActive"/> is a compile-time constant so a test can state its premise instead of
/// silently asserting nothing -- <b>CI runs the suite in Release, where this is off.</b></para>
/// </remarks>
internal static class ChannelBufferLeakTracker
{
    /// <summary>Whether tracking is compiled in. False in Release, where every method here is empty.</summary>
#if DEBUG
    internal const bool IsActive = true;
#else
    internal const bool IsActive = false;
#endif

#if DEBUG
    private sealed record Entry(WeakReference<float[,]> Array, string Producer, int Line, long Bytes);

    private static readonly ConcurrentDictionary<long, Entry> _outstanding = new();
    private static readonly ConcurrentDictionary<string, SiteTally> _leaksBySite = new();
    private static long _nextId;
    private static long _leakCount;
    private static long _leakedBytes;
#endif

    /// <summary>
    /// Records a newly created buffer against the site that produced it, returning the handle
    /// <see cref="Unregister"/> takes. Zero, and no work at all, outside DEBUG.
    /// </summary>
    internal static long Register(float[,] data, string producer, int producerLine)
    {
#if DEBUG
        var id = Interlocked.Increment(ref _nextId);

        // Attribution is stored unformatted: both parts are compile-time literals, so keeping them
        // apart costs no allocation per buffer and the string is built only for a site that leaks.
        _outstanding[id] = new Entry(
            new WeakReference<float[,]>(data), producer, producerLine, (long)data.Length * sizeof(float));
        return id;
#else
        return 0;
#endif
    }

    /// <summary>Clears a buffer from the table on its final release. No-op outside DEBUG.</summary>
    internal static void Unregister(long id)
    {
#if DEBUG
        _outstanding.TryRemove(id, out _);
#endif
    }

    /// <summary>
    /// Sweeps the table and reports what is outstanding, plus the cumulative count of buffers the
    /// collector took while they were still outstanding.
    /// </summary>
    /// <param name="collectFirst">
    /// Force a full collection first. Required for <see cref="ChannelBufferLeakReport.LeakCount"/> to
    /// mean anything -- an unreleased buffer is indistinguishable from a live one until the collector
    /// has had a chance to take it. Off by default, because a blocking Gen2 collection is not
    /// something a caller should get by accident.
    /// </param>
    internal static ChannelBufferLeakReport Report(bool collectFirst = false)
    {
#if DEBUG
        if (collectFirst)
        {
            // Twice, with the finalizer drain between: a buffer reachable only through a finalizable
            // owner does not become unreachable until that queue has run.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        var liveBySite = new Dictionary<string, SiteTally>();
        var liveCount = 0;
        var liveBytes = 0L;

        foreach (var (id, entry) in _outstanding)
        {
            if (entry.Array.TryGetTarget(out _))
            {
                liveCount++;
                liveBytes += entry.Bytes;
                Accumulate(liveBySite, Describe(entry), entry.Bytes);
                continue;
            }

            // Gone while still outstanding, so nobody was ever going to release it. Removed as it is
            // counted, so a second Report cannot bill the same leak twice.
            if (_outstanding.TryRemove(id, out var leaked))
            {
                Interlocked.Increment(ref _leakCount);
                Interlocked.Add(ref _leakedBytes, leaked.Bytes);
                _leaksBySite.AddOrUpdate(
                    Describe(leaked),
                    static (_, bytes) => new SiteTally(1, bytes),
                    static (_, tally, bytes) => new SiteTally(tally.Count + 1, tally.Bytes + bytes),
                    leaked.Bytes);
            }
        }

        return new ChannelBufferLeakReport(
            liveCount,
            liveBytes,
            Volatile.Read(ref _leakCount),
            Volatile.Read(ref _leakedBytes),
            ToSites(liveBySite),
            ToSites(_leaksBySite));
#else
        _ = collectFirst;
        return default;
#endif
    }

#if DEBUG
    private static string Describe(Entry entry) => entry.Producer + ":" + entry.Line;

    private static void Accumulate(Dictionary<string, SiteTally> into, string site, long bytes)
        => into[site] = into.TryGetValue(site, out var tally)
            ? new SiteTally(tally.Count + 1, tally.Bytes + bytes)
            : new SiteTally(1, bytes);

    private static ImmutableArray<ChannelBufferSite> ToSites(IEnumerable<KeyValuePair<string, SiteTally>> tallies)
    {
        var builder = ImmutableArray.CreateBuilder<ChannelBufferSite>();
        foreach (var (site, tally) in tallies)
        {
            builder.Add(new ChannelBufferSite(site, tally.Count, tally.Bytes));
        }

        // Worst first: a report is read top-down when something is wrong.
        builder.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return builder.ToImmutable();
    }

    private readonly record struct SiteTally(int Count, long Bytes);
#endif
}

/// <summary>One producing site's share of a <see cref="ChannelBufferLeakReport"/>.</summary>
/// <param name="Producer">The member and line that constructed the buffers, from caller info.</param>
/// <param name="Count">How many buffers that site accounts for.</param>
/// <param name="Bytes">Their combined backing-array bytes.</param>
internal readonly record struct ChannelBufferSite(string Producer, int Count, long Bytes);

/// <summary>
/// What <see cref="ChannelBufferLeakTracker.Report"/> found. All zero in a Release build, where
/// tracking is compiled out -- check <see cref="ChannelBufferLeakTracker.IsActive"/> before reading a
/// zero as evidence of anything.
/// </summary>
/// <param name="LiveCount">Buffers created and not yet released. Normal mid-session; a leak wherever nothing should still be held.</param>
/// <param name="LiveBytes">Their combined backing-array bytes.</param>
/// <param name="LeakCount">Cumulative buffers the collector took while they were still outstanding. Never anything but a bug.</param>
/// <param name="LeakedBytes">Their combined backing-array bytes.</param>
/// <param name="Live">The outstanding buffers grouped by producing site, worst first.</param>
/// <param name="Leaked">The collected-while-outstanding buffers grouped by producing site, worst first.</param>
internal readonly record struct ChannelBufferLeakReport(
    int LiveCount,
    long LiveBytes,
    long LeakCount,
    long LeakedBytes,
    ImmutableArray<ChannelBufferSite> Live,
    ImmutableArray<ChannelBufferSite> Leaked)
{
    /// <summary>A one-line-per-site rendering, for the message on a failing assertion.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("live ").Append(LiveCount).Append(" (").Append(LiveBytes >> 20).Append(" MiB)")
            .Append(", collected while outstanding ").Append(LeakCount)
            .Append(" (").Append(LeakedBytes >> 20).Append(" MiB)");
        Append(text, "live", Live);
        Append(text, "leaked", Leaked);
        return text.ToString();

        static void Append(StringBuilder text, string label, ImmutableArray<ChannelBufferSite> sites)
        {
            if (sites.IsDefault)
            {
                return;
            }

            foreach (var site in sites)
            {
                text.Append(Environment.NewLine).Append("  ").Append(label).Append(' ')
                    .Append(site.Count).Append(" x ").Append(site.Producer)
                    .Append(" (").Append(site.Bytes >> 20).Append(" MiB)");
            }
        }
    }
}
