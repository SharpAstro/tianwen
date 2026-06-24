using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Knobs for the live <see cref="RollingWindowStacker"/>.</summary>
public sealed record RollingWindowOptions
{
    /// <summary>
    /// Capture-time span the window covers, measured from frame timestamps. Planetary rotation smears
    /// detail in ~3-6 min (Jupiter), so a time-bounded window tracks current seeing and never stacks
    /// across rotation. Used only when the stream has timestamps; otherwise <see cref="FallbackWindowFrames"/>.
    /// </summary>
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Window size in frames when the stream has no timestamps to derive a capture-time span.</summary>
    public int FallbackWindowFrames { get; init; } = 300;

    /// <summary>
    /// Hard upper bound on the number of frames in the window, applied REGARDLESS of the time span -- the
    /// window is <c>min(time-bound, this)</c>. The per-frame fold/align cost dominates, so a dense capture
    /// (e.g. 30k frames in ~77 s at 387 fps) would otherwise pull the entire capture into a "5-minute"
    /// window and make every stack a full batch integration (tens of seconds per update, not live). Capping
    /// the count -- not just the time span -- is what keeps the live stack responsive.
    /// </summary>
    public int MaxWindowFrames { get; init; } = 500;

    /// <summary>The sharpness metric, weighting each frame's contribution (quality-weighted mean). Laplacian variance by default.</summary>
    public IFrameQualityEstimator QualityEstimator { get; init; } = new LaplacianEnergyEstimator();

    /// <summary>Phase-correlation tile edge for global alignment. <c>0</c> auto-sizes to the reference disk.</summary>
    public int AlignTileSize { get; init; }
}

/// <summary>
/// Live rolling-window planetary stacker: maintains a sliding window of recently-seen frames and folds
/// them into a quality-weighted, globally-aligned running mean, publishing a fresh master on demand. The
/// streaming counterpart to <see cref="LuckyImagingStacker.StackGlobalAsync"/> -- same per-frame
/// primitives (<see cref="FrameGrader"/> metric, <see cref="GlobalAligner"/>, the translate-accumulate
/// kernel, the shared <see cref="PlanetaryMaster"/> finalise), but with O(pixels) incremental
/// <c>add</c>/<c>evict</c> instead of a one-shot pass so it can track a moving playhead at interactive
/// rates.
/// <para>
/// <b>Follow-the-playhead.</b> <see cref="StackToAsync"/> advances the window so it ends at the requested
/// frame, spanning back <see cref="RollingWindowOptions.WindowDuration"/> of capture time. A forward step
/// folds in the new frames and evicts the aged-out ones (each eviction re-folds the frame's cached
/// contribution with a negated weight -- the exact inverse of the add, so the running sum stays correct
/// with no per-frame contribution images to store). A backward jump, a non-contiguous forward jump, or
/// the alignment reference ageing out of the window triggers a full rebuild (re-pick the best-graded
/// frame in the new window as reference, re-fold the window).
/// </para>
/// <para>
/// <b>Threading.</b> Single-writer: drive it from one background task at a time (the live preview source
/// gates re-entry). It loads frames via <see cref="IPlanetaryFrameStream.LoadAsync"/>, so it must run off
/// the render thread. The returned master is a fresh image the caller owns; the internal accumulators are
/// untouched by the master build, so stacking continues across calls.
/// </para>
/// </summary>
public sealed class RollingWindowStacker
{
    private readonly IPlanetaryFrameStream _stream;
    private readonly RollingWindowOptions _options;

    // Grade-once-per-frame cache. A frame's sharpness never changes (it is a file), so once graded its
    // score is reused for the lifetime of the stacker -- a rebuild that re-spans already-seen frames pays
    // no re-grade. Bounded by the frame count (one float each).
    private readonly Dictionary<int, float> _scoreCache = new();

    // The folded contribution of each in-window frame, so eviction can subtract exactly what was added
    // (same shift, negated weight) without re-grading. Weight 0 = graded-but-not-folded (kept so the
    // window membership/contiguity bookkeeping is uniform).
    private readonly Dictionary<int, Contribution> _window = new();

    private float[][,]? _sum;     // per-channel weighted sum at reference (sub-plane) resolution
    private float[,]? _weight;    // shared per-pixel weight (coverage)
    private GlobalAligner? _aligner;
    private int _refIndex = -1;
    private int _windowStart = -1;
    private int _windowEnd = -2;  // < _windowStart so "first call" is always a rebuild
    private int _channels;
    private int _planeW;
    private int _planeH;
    private ImageMeta _meta;

    private readonly record struct Contribution(float Weight, float Dx, float Dy);

    public RollingWindowStacker(IPlanetaryFrameStream stream, RollingWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _options = options ?? new RollingWindowOptions();
    }

    /// <summary>Index of the current alignment reference frame, or <c>-1</c> before the first stack.</summary>
    public int ReferenceIndex => _refIndex;

    /// <summary>First frame index currently in the window (inclusive), or <c>-1</c> before the first stack.</summary>
    public int WindowStart => _windowStart;

    /// <summary>Last frame index currently in the window (inclusive).</summary>
    public int WindowEnd => _windowEnd;

    /// <summary>Number of frames currently held in the window (folded or graded-zero).</summary>
    public int WindowFrameCount => _window.Count;

    /// <summary>
    /// Advances the window to end at <paramref name="playheadIndex"/> (clamped to the stream) and returns
    /// the freshly built RGB master (demosaiced once for a split-CFA source). Heavy: call off the render
    /// thread, one call at a time.
    /// </summary>
    public async Task<Image> StackToAsync(int playheadIndex, CancellationToken cancellationToken = default)
    {
        var count = _stream.FrameCount;
        if (count <= 0)
        {
            throw new InvalidOperationException("The frame stream is empty.");
        }

        var f = Math.Clamp(playheadIndex, 0, count - 1);
        var windowStart = ComputeWindowStart(f);

        var needRebuild =
            _refIndex < 0                       // first stack
            || f < _windowEnd                   // backward jump (would need to re-add evicted frames)
            || windowStart > _windowEnd + 1     // forward jump leaving a gap -> window no longer contiguous
            || _refIndex < windowStart;         // alignment reference aged out of the window

        try
        {
            if (needRebuild)
            {
                await RebuildAsync(windowStart, f, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Grow the leading edge, then drop the trailing edge. Order matters only for peak memory;
                // both are O(pixels) per frame touched.
                for (var i = _windowEnd + 1; i <= f; i++)
                {
                    await AddAsync(i, cancellationToken).ConfigureAwait(false);
                }
                for (var i = _windowStart; i < windowStart; i++)
                {
                    await EvictAsync(i, cancellationToken).ConfigureAwait(false);
                }
                _windowStart = windowStart;
                _windowEnd = f;
            }
        }
        catch (OperationCanceledException)
        {
            // A cancel mid fold/evict leaves the accumulators + window bookkeeping partial and inconsistent.
            // Invalidate so the NEXT StackToAsync does a clean full rebuild (the score cache stays valid --
            // scores don't change). Cheap to throw the partial work away; the window is re-capped + fast.
            Invalidate();
            throw;
        }

        return await BuildMasterAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the current window/reference so the next <see cref="StackToAsync"/> rebuilds from
    /// scratch. Used after a cancellation left the accumulators in a partial state. Keeps the grade cache.</summary>
    private void Invalidate()
    {
        _refIndex = -1;
        _windowStart = -1;
        _windowEnd = -2;
        _window.Clear();
    }

    // The window's first frame: walk back from f while still within WindowDuration of capture time, else
    // a fixed frame count. Both clamp to 0.
    internal int ComputeWindowStart(int f)
    {
        int start;
        if (_stream.HasTimestamps && _stream.TimestampOf(f) is { } tEnd)
        {
            var cutoff = tEnd - _options.WindowDuration;
            start = f;
            while (start > 0 && _stream.TimestampOf(start - 1) is { } tPrev && tPrev >= cutoff)
            {
                start--;
            }
        }
        else
        {
            start = Math.Max(0, f - _options.FallbackWindowFrames + 1);
        }

        // Cap the frame count regardless of the time span: a dense, high-fps capture spans the whole
        // capture in a 5-minute window, and the per-frame fold/align cost makes that a multi-second batch
        // stack rather than a live one. min(time-bound, frame-cap).
        var maxStart = f - _options.MaxWindowFrames + 1;
        return Math.Max(start, maxStart);
    }

    private async Task RebuildAsync(int windowStart, int f, CancellationToken cancellationToken)
    {
        // 1. Pick the reference = the best-graded frame in [windowStart, f] (the integrator's output grid;
        //    every frame aligns to it). Grading is cached, so a rebuild over already-seen frames is cheap.
        var bestIndex = -1;
        var bestScore = float.NegativeInfinity;
        for (var i = windowStart; i <= f; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var s = await EnsureScoreAsync(i, cancellationToken).ConfigureAwait(false);
            if (s > bestScore)
            {
                bestScore = s;
                bestIndex = i;
            }
        }
        if (bestIndex < 0)
        {
            bestIndex = f; // every frame scored <= the seed; fall back to the playhead frame
        }

        // 2. Build the aligner + zeroed accumulators sized to the reference frame.
        var reference = await _stream.LoadAsync(bestIndex, cancellationToken).ConfigureAwait(false);
        try
        {
            var refRegion = PlanetaryDisk.BoundingBox(reference);
            var tileSize = _options.AlignTileSize > 0
                ? NextPowerOfTwo(_options.AlignTileSize)
                : Math.Clamp(NextPowerOfTwo(Math.Max(refRegion.Width, refRegion.Height)), 64, 512);
            _aligner = GlobalAligner.FromReference(reference, refRegion, tileSize);
            _refIndex = bestIndex;
            _channels = reference.ChannelCount;
            _planeH = reference.Height;
            _planeW = reference.Width;
            _meta = reference.ImageMeta;
            _sum = Image.CreateChannelData(_channels, _planeH, _planeW);
            _weight = new float[_planeH, _planeW];
            _window.Clear();
        }
        finally
        {
            reference.Release();
        }

        // 3. Fold the whole window in.
        _windowStart = windowStart;
        _windowEnd = f;
        for (var i = windowStart; i <= f; i++)
        {
            await AddAsync(i, cancellationToken).ConfigureAwait(false);
        }
    }

    // Folds frame `index` into the running sum (loads once; grade is cache-aware). A non-positive score is
    // recorded as a zero contribution so eviction is a no-op but window bookkeeping stays uniform.
    private async Task AddAsync(int index, CancellationToken cancellationToken)
    {
        if (_window.ContainsKey(index))
        {
            return;
        }

        var frame = await _stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            var score = GradeLoaded(index, frame);
            if (score <= 0f)
            {
                _window[index] = default;
                return;
            }

            var shift = _aligner!.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
            frame.AccumulateTranslatedInto(_sum!, _weight!, (float)shift.Dx, (float)shift.Dy, score);
            _window[index] = new Contribution(score, (float)shift.Dx, (float)shift.Dy);
        }
        finally
        {
            frame.Release();
        }
    }

    // Removes frame `index` from the window, subtracting exactly what AddAsync folded (same shift, negated
    // weight). AccumulateTranslatedInto is linear in weight, so +w then -w cancels per pixel; the in-bounds
    // set is identical (same frame, same shift), so it cancels exactly.
    private async Task EvictAsync(int index, CancellationToken cancellationToken)
    {
        if (!_window.Remove(index, out var c) || c.Weight <= 0f)
        {
            return;
        }

        var frame = await _stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            frame.AccumulateTranslatedInto(_sum!, _weight!, c.Dx, c.Dy, -c.Weight);
        }
        finally
        {
            frame.Release();
        }
    }

    // Ensures frame `index` has a cached score, loading + grading it if needed. Used by the rebuild's
    // reference pick before any folding.
    private async Task<float> EnsureScoreAsync(int index, CancellationToken cancellationToken)
    {
        if (_scoreCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var frame = await _stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            return GradeLoaded(index, frame);
        }
        finally
        {
            frame.Release();
        }
    }

    private float GradeLoaded(int index, Image frame)
    {
        if (_scoreCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var score = MathF.Max(0f, _options.QualityEstimator.Score(frame, PlanetaryDisk.BoundingBox(frame)));
        _scoreCache[index] = score;
        return score;
    }

    private async Task<Image> BuildMasterAsync(CancellationToken cancellationToken)
    {
        // Normalise a COPY of the running sum so the accumulators keep their integral state for the next
        // add/evict; _weight is read (not mutated) by NormalizeInPlace.
        var sumCopy = new float[_channels][,];
        for (var c = 0; c < _channels; c++)
        {
            sumCopy[c] = (float[,])_sum![c].Clone();
        }

        var stacked = PlanetaryMaster.NormalizeInPlace(sumCopy, _weight!, _meta);
        return await PlanetaryMaster.MergeAndDemosaicAsync(stacked, _stream.Layout, cancellationToken).ConfigureAwait(false);
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }
}
