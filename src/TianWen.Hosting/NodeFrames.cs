using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting;

/// <summary>
/// The frame each source shows now, whoever produced it, and ONE change token per source (P5 part 2 of
/// docs/plans/hardware-in-the-server.md, #934). The frame route, the <c>FRAME-AVAILABLE</c> push and the JPEG previews
/// all read it, so a frame taken outside a session (a preview exposure) is served exactly as a session's sub is.
/// </summary>
/// <remarks>
/// <para><b>Which frame an OTA shows:</b> the node's own preview of that OTA, when it was taken since the node's latest
/// run started; else the latest session's slot, going on or ended. A preview taken before a run is stale once the run
/// starts, and is dropped (released) the moment that is seen; none can be taken during one (<see cref="NodePreviews"/>
/// refuses), so a session going on always shows its own frames.</para>
/// <para><b>The token is the node's, bumped whenever the frame on show changes</b> (another object, or none), never the
/// producer's own number: a session numbers its frames from the start, and a preview slot would number its own, so a
/// client holding a session's frame N would otherwise be answered "unchanged" for a preview that happened to be number
/// N too. The frame last seen is held WEAKLY, only to tell a new frame from the one on show, so a replaced frame is
/// never kept alive by this. A token of 0 is a source that has never shown a frame.</para>
/// <para>The frame and its token come as one pair, the token naming exactly that frame, which the caller leases before
/// reading it: a refused lease means it was replaced in between, and the next ask finds its successor.</para>
/// </remarks>
internal sealed class NodeFrames(IHostedSession hosted)
{
    /// <summary>The frame a source shows, and its token.</summary>
    public readonly record struct Shown(Image? Frame, int Number);

    /// <summary>A preview the node took of one OTA, and the latest run as it was then: the run it is newer than.</summary>
    private sealed record Preview(Image Frame, object? LatestRun);

    private sealed record Seen(WeakReference<Image>? Frame, int Number);

    private readonly ConcurrentDictionary<string, Seen> _seen = new ConcurrentDictionary<string, Seen>(StringComparer.Ordinal);

    // Published by reference swap; a slot's frame is released only once its successor (or its absence) is out.
    private Preview?[] _previews = [];

    // The frames of a source that is a run's own rather than an OTA's (a planetary capture's live frame and master), each
    // with the run it was published in: shown until the node's next run starts, released once replaced or superseded.
    private readonly ConcurrentDictionary<string, Preview> _named = new ConcurrentDictionary<string, Preview>(StringComparer.Ordinal);

    // Completed and replaced on every publish to a run's own source: what a frame stream waits on for the next frame.
    private TaskCompletionSource _published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes on the next publish to any run's own source (P5 part 5c, the frame streams). Take it BEFORE reading the
    /// source, so a frame published in between is never waited past.
    /// </summary>
    public Task NextPublish => Volatile.Read(ref _published).Task;

    /// <summary>The frame OTA <paramref name="index"/> shows now.</summary>
    public Shown Ota(int index) => Observe(FrameSources.Ota(index), ResolveOta(index));

    /// <summary>
    /// The frame a run's own source shows now (<see cref="FrameSources.PlanetaryLive"/>,
    /// <see cref="FrameSources.PlanetaryMaster"/>): the latest one its run published, until the node's next run starts.
    /// </summary>
    public Shown Named(string source) => Observe(source, ResolveNamed(source));

    /// <summary>
    /// Shows <paramref name="frame"/> as <paramref name="source"/>'s, and CONSUMES it: the node owns it from here and
    /// releases it once another replaces it, or once a run has started since.
    /// </summary>
    public void Publish(string source, Image frame)
    {
        var next = new Preview(frame, LatestRun());
        while (true)
        {
            if (_named.TryGetValue(source, out var replaced))
            {
                if (_named.TryUpdate(source, next, replaced))
                {
                    replaced.Frame.Release();
                    Published();
                    return;
                }
            }
            else if (_named.TryAdd(source, next))
            {
                Published();
                return;
            }
        }
    }

    private void Published()
        => Interlocked.Exchange(ref _published, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private Image? ResolveNamed(string source)
    {
        if (!_named.TryGetValue(source, out var shown))
        {
            return null;
        }
        if (ReferenceEquals(shown.LatestRun, LatestRun()))
        {
            return shown.Frame;
        }

        // Its run has been replaced: taken out only if nothing has replaced it in between, and given back.
        if (_named.TryRemove(new KeyValuePair<string, Preview>(source, shown)))
        {
            shown.Frame.Release();
        }
        return null;
    }

    /// <summary>The guide camera's frame now: a session's only.</summary>
    public Shown Guider() => Observe(FrameSources.Guider, hosted.CurrentSession?.LastGuideFrame);

    /// <summary>How many OTA sources there are to watch: a session's slots, or the node's previews, whichever is more.</summary>
    public int OtaCount => Math.Max(hosted.CurrentSession?.LastCapturedImages.Length ?? 0, Volatile.Read(ref _previews).Length);

    /// <summary>
    /// Shows <paramref name="frame"/> as OTA <paramref name="index"/>'s preview, and CONSUMES it: the node owns it from
    /// here and releases it once another replaces it, or once a run has started since.
    /// </summary>
    public void PublishPreview(int index, Image frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var latest = LatestRun();
        while (true)
        {
            var slots = Volatile.Read(ref _previews);
            var next = new Preview?[Math.Max(slots.Length, index + 1)];
            Array.Copy(slots, next, slots.Length);
            var replaced = index < slots.Length ? slots[index] : null;
            next[index] = new Preview(frame, latest);
            if (Interlocked.CompareExchange(ref _previews, next, slots) == slots)
            {
                replaced?.Frame.Release();
                return;
            }
        }
    }

    private Image? ResolveOta(int index)
    {
        if (index < 0)
        {
            return null;
        }

        var session = hosted.CurrentSession;
        var slots = Volatile.Read(ref _previews);
        if (index < slots.Length && slots[index] is { } preview)
        {
            if (ReferenceEquals(preview.LatestRun, LatestRun()))
            {
                return preview.Frame;
            }
            DropStale(index, preview);
        }
        return Slot(session, index);
    }

    private static Image? Slot(Lib.Sequencing.ISessionTelemetry? session, int index)
        => session?.LastCapturedImages is { } images && index < images.Length ? images[index] : null;

    /// <summary>The node's latest run, going on or ended, whatever its kind: what a preview must be newer than.</summary>
    private object? LatestRun() => (object?)hosted.CurrentSession ?? hosted.CurrentRun;

    /// <summary>Takes a preview a run has since superseded out of its slot, and gives it back.</summary>
    private void DropStale(int index, Preview stale)
    {
        while (true)
        {
            var slots = Volatile.Read(ref _previews);
            if (index >= slots.Length || !ReferenceEquals(slots[index], stale))
            {
                return;
            }
            Preview?[] next = [.. slots];
            next[index] = null;
            if (Interlocked.CompareExchange(ref _previews, next, slots) == slots)
            {
                stale.Frame.Release();
                return;
            }
        }
    }

    private Shown Observe(string source, Image? frame)
    {
        var seen = _seen.AddOrUpdate(source,
            static (_, now) => new Seen(now is null ? null : new WeakReference<Image>(now), now is null ? 0 : 1),
            static (_, old, now) => IsSame(old.Frame, now) ? old : new Seen(now is null ? null : new WeakReference<Image>(now), old.Number + 1),
            frame);
        return new Shown(seen.Frame is { } held && held.TryGetTarget(out var shown) ? shown : null, seen.Number);
    }

    private static bool IsSame(WeakReference<Image>? held, Image? now)
        => held is null ? now is null : now is not null && held.TryGetTarget(out var was) && ReferenceEquals(was, now);
}
