using System;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Two-tier strong+weak cache for per-frame <see cref="Image"/> instances,
/// keyed by frame index. Shared by every integration strategy that wants to
/// avoid re-doing expensive work (raw decode for <see cref="TilePipelinedStrategy"/>,
/// disk read for the staged strategies).
/// <list type="bullet">
/// <item>The first <c>strongCap</c> slots hold a strong reference -- these
///   frames are guaranteed-present for the duration of the integration.</item>
/// <item>Every slot also holds a <see cref="WeakReference{T}"/>. Frames past
///   the strong cap, or frames whose strong slot has been cleared, can still
///   be found alive when GC hasn't reclaimed them yet; lookups promote a
///   weak hit to a strong-ref so subsequent accesses don't risk losing it.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The cache framing is "the on-disk / raw-FITS copy is the source of
/// truth, RAM is a survivable accelerator." A strategy populates the cache
/// at staging-write time (or pass-1 decode time for raw-consuming strategies);
/// downstream consumers (chunk readers, strip warpers) look up before falling
/// back to the source-of-truth path. Both miss handling and hit handling are
/// safe under concurrent strategy reads as long as <see cref="Image"/>
/// instances themselves aren't mutated, which they aren't post-warp.</para>
/// <para>Sizing: <see cref="DecideCacheCap"/> caps the strong slot count at
/// ~50% of currently-free GC heap, leaving the other half for strip arrays,
/// in-flight transients, integrator scratch, and other background allocations.
/// Heuristic, not a hard contract -- callers can pass any <c>strongCap</c>
/// they want.</para>
/// </remarks>
internal sealed class FrameCache
{
    private readonly Image?[] _strong;
    private readonly WeakReference<Image>?[] _weak;

    /// <summary>Number of frames the cache is sized for.</summary>
    public int FrameCount { get; }

    /// <summary>Soft cap on the number of strong references the cache will
    /// retain. The strong slots are filled in frame-index order; once the cap
    /// is reached further <see cref="Set"/> calls land only in the weak tier.
    /// </summary>
    public int StrongCap { get; }

    public FrameCache(int frameCount, int strongCap)
    {
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        FrameCount = frameCount;
        StrongCap = Math.Clamp(strongCap, 0, frameCount);
        _strong = new Image?[frameCount];
        _weak = new WeakReference<Image>?[frameCount];
    }

    /// <summary>
    /// Register a frame in the cache. The strong tier accepts it if there's
    /// still capacity (frame index &lt; <see cref="StrongCap"/>); the weak
    /// tier always tracks it so GC behaviour can promote a bonus hit later.
    /// </summary>
    public void Set(int frameIndex, Image image)
    {
        if ((uint)frameIndex >= (uint)FrameCount) return;
        _weak[frameIndex] = new WeakReference<Image>(image);
        if (frameIndex < StrongCap)
        {
            _strong[frameIndex] = image;
        }
    }

    /// <summary>
    /// Lookup. Returns true and sets <paramref name="image"/> when a strong
    /// or live-weak reference is present; a weak hit is auto-promoted to a
    /// strong reference so subsequent passes can't lose it. Returns false
    /// when no live reference exists in either tier.
    /// </summary>
    public bool TryGet(int frameIndex, out Image image)
    {
        if ((uint)frameIndex >= (uint)FrameCount)
        {
            image = null!;
            return false;
        }

        if (_strong[frameIndex] is { } strong)
        {
            image = strong;
            return true;
        }

        if (_weak[frameIndex] is { } weak && weak.TryGetTarget(out var alive))
        {
            // Bonus cache hit: the GC hadn't reclaimed this frame yet. Adopt
            // a strong reference so subsequent passes don't risk losing it
            // to a later collect. Counts against the strong cap implicitly
            // by overflowing it -- the cap is a soft heuristic, not a hard
            // promise, and the alternative (decode-again) is more expensive.
            _strong[frameIndex] = alive;
            image = alive;
            return true;
        }

        image = null!;
        return false;
    }

    /// <summary>
    /// Heuristic: how many strong references the cache should retain given
    /// <paramref name="frameCount"/> frames at <paramref name="frameBytes"/>
    /// bytes each. Samples <see cref="GC.GetGCMemoryInfo"/> at call time and
    /// allocates ~50% of currently-free heap to the cache, leaving the rest
    /// for the strategy's working set. Returns 0..<paramref name="frameCount"/>.
    /// </summary>
    /// <param name="frameCount">Total frame count for the integration.</param>
    /// <param name="frameBytes">Bytes one cached <see cref="Image"/> occupies.
    /// Typically <c>Width * Height * Channels * sizeof(float)</c>.</param>
    public static int DecideCacheCap(int frameCount, long frameBytes)
    {
        if (frameCount <= 0 || frameBytes <= 0) return 0;
        var info = GC.GetGCMemoryInfo();
        var currentlyFree = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var cacheBudget = currentlyFree / 2;
        var maxByBytes = cacheBudget / frameBytes;
        if (maxByBytes <= 0) return 0;
        if (maxByBytes >= frameCount) return frameCount;
        return (int)maxByBytes;
    }
}
