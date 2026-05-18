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
    /// allocates a fraction of currently-free heap to the cache, leaving the
    /// rest for the strategy's working set.
    /// Returns 0..<paramref name="frameCount"/>.
    /// </summary>
    /// <param name="frameCount">Total frame count for the integration.</param>
    /// <param name="frameBytes">Bytes one cached <see cref="Image"/> occupies.
    /// Typically <c>Width * Height * Channels * sizeof(float)</c>.</param>
    /// <param name="budgetFraction">Share of currently-free RAM allotted to
    /// the strong-cap. <c>null</c> (default) -> <see cref="ScaledBudgetFraction"/>
    /// picks based on total physical RAM (0.65 on a 16 GB host scaling up to
    /// 0.95 on 128 GB+). Tests can pin an explicit fraction.</param>
    public static int DecideCacheCap(int frameCount, long frameBytes, double? budgetFraction = null)
    {
        if (frameCount <= 0 || frameBytes <= 0) return 0;
        var info = GC.GetGCMemoryInfo();
        var fraction = budgetFraction ?? ScaledBudgetFraction(info.TotalAvailableMemoryBytes);
        if (fraction <= 0.0 || fraction > 1.0) throw new ArgumentOutOfRangeException(nameof(budgetFraction));
        var currentlyFree = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var cacheBudget = (long)(currentlyFree * fraction);
        var maxByBytes = cacheBudget / frameBytes;
        if (maxByBytes <= 0) return 0;
        if (maxByBytes >= frameCount) return frameCount;
        return (int)maxByBytes;
    }

    /// <summary>Lower anchor for <see cref="ScaledBudgetFraction"/>: hosts at
    /// or below 16 GB physical RAM get 0.65 of free-RAM as the cache budget.
    /// Why 0.65: the 0.80 we shipped first OOM'd a 16 GB Windows host during
    /// SoL 60s pass-2 (peak <c>MemoryLoad=15.5/15.6 GB</c>) -- the OS itself
    /// needs ~3-4 GB headroom for the page cache + working set of background
    /// services, and the strategy's transient allocations (warped strips,
    /// integrator scratch, GC bursts) eat into the remainder. 0.65 leaves
    /// roughly an extra GB clear at the SoL 60s workload.</summary>
    public const double MinBudgetFraction = 0.65;

    /// <summary>Upper anchor: hosts at or above 128 GB get 0.95. By then the
    /// OS reserve is irrelevant -- the cache could be 99% and the box would
    /// still have multi-GB headroom. Roomy workstations and CI runners with
    /// 64-256 GB physical see no benefit from being conservative.</summary>
    public const double MaxBudgetFraction = 0.95;

    /// <summary>
    /// RAM-scaled budget fraction for the strong cap. Logarithmic
    /// interpolation between <see cref="MinBudgetFraction"/> (at 16 GB) and
    /// <see cref="MaxBudgetFraction"/> (at 128 GB), flat outside the anchors.
    /// Doubling RAM moves a fixed step along the curve, so 16->32 GB feels
    /// like a meaningful upgrade (0.65 -> 0.75) and 64->128 GB is the same
    /// size step (0.85 -> 0.95). Returns a value in [<see cref="MinBudgetFraction"/>,
    /// <see cref="MaxBudgetFraction"/>].
    /// </summary>
    /// <param name="totalRamBytes">Total RAM the GC can claim, typically
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>. On Windows this
    /// reflects the container limit if present, else physical RAM.</param>
    public static double ScaledBudgetFraction(long totalRamBytes)
    {
        const double LowAnchorGb = 16.0;
        const double HighAnchorGb = 128.0;
        var totalGb = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
        if (totalGb <= LowAnchorGb) return MinBudgetFraction;
        if (totalGb >= HighAnchorGb) return MaxBudgetFraction;
        var t = (Math.Log2(totalGb) - Math.Log2(LowAnchorGb)) / (Math.Log2(HighAnchorGb) - Math.Log2(LowAnchorGb));
        return MinBudgetFraction + (MaxBudgetFraction - MinBudgetFraction) * t;
    }
}
