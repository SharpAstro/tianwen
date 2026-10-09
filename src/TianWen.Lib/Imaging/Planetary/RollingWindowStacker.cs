using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Geometry;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

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

    /// <summary>
    /// The sharpness metric, which picks the reference and weights each frame's contribution (quality-weighted mean): the gradient,
    /// the batch stack's (<see cref="PlanetaryStackOptions.QualityEstimator"/>), since it cost the live stack nothing and its last
    /// master less error (planetary live, docs/plans/planetary-restoration.md, "The live stack, given the batch stack's learnings").
    /// </summary>
    public IFrameQualityEstimator QualityEstimator { get; init; } = new GradientEnergyEstimator();

    /// <summary>Correlation tile edge for global alignment. <c>0</c> auto-sizes to the reference disk.</summary>
    public int AlignTileSize { get; init; }

    /// <summary>
    /// Whether frames are registered by phase correlation (whitened) or by a plain cross-correlation, its peak climbed (the default,
    /// the batch stack's, <see cref="PlanetaryStackOptions.WhitenedCorrelation"/>): it left the live stack's last master less error
    /// and kept nine tenths of its throughput.
    /// </summary>
    public bool WhitenedCorrelation { get; init; }

    /// <summary>
    /// The kernel each frame is resampled by as it is folded in (bilinear by default; the batch stack's is clamped Lanczos-3,
    /// <see cref="PlanetaryStackOptions.Interpolation"/>, which a live stack keeping up with a fast capture cannot afford: it folded
    /// two fifths as many frames a second). An eviction folds by the same kernel, so it cancels exactly.
    /// </summary>
    public WarpInterpolation Interpolation { get; init; } = WarpInterpolation.Bilinear;

    /// <summary>
    /// The share of the window's frames folded into the master (#1174): every frame is graded as it arrives, and folded only when its
    /// score is among the best this share of the scores graded in the window so far, its own included; a rebuild folds the window's
    /// best this share outright. One folds every frame. A quarter is the measured default: with <see cref="ReReferenceInPlace"/> it is
    /// the one share that kept up with 216 and 250 frames a second (99 and 96 % of the frames graded, under 0.75 s behind), and its
    /// master was the sharpest of the shares tried (the twin's bands 1 to 4: 1.537 against 1.645 folding every frame).
    /// </summary>
    public double KeepFraction { get; init; } = 0.25;

    /// <summary>
    /// How the stack answers its alignment reference ageing out of a window that is still sliding (#1174). False folds the window
    /// again around the window's best frame, a rebuild, which a window of 500 frames asks for every 2.3 s of a 216 frames a second
    /// capture, a stall of seconds each. True takes the best frame the window has folded as the new reference and keeps the sum: each
    /// later frame is registered to the new reference and moved onto the sum's grid by the new reference's own registration (the shifts
    /// add), so nothing is folded again. A new reference registered more than a quarter of the aligner's tile from the grid rebuilds
    /// all the same, so the grid follows a drifting planet. On by default (#1174): it cost no band error at any share, and at 60 frames
    /// a second it took the stack's p90 interval between masters from 6.5 s to 0.43 s.
    /// </summary>
    public bool ReReferenceInPlace { get; init; } = true;

    /// <summary>
    /// The rolling stack's recipe before the enhanced pipeline (#1159): the Laplacian, phase correlation, bilinear, every frame folded
    /// and the window folded again whenever its reference ages out (#1174). What <c>planetary live</c> measures every other recipe against.
    /// </summary>
    public static RollingWindowOptions Legacy { get; } = new RollingWindowOptions
    {
        QualityEstimator = new LaplacianEnergyEstimator(),
        WhitenedCorrelation = true,
        Interpolation = WarpInterpolation.Bilinear,
        KeepFraction = 1,
        ReReferenceInPlace = false,
    };
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

    // Grade-once-per-frame cache. A frame's sharpness never changes, so once graded its score is reused --
    // a rebuild that re-spans already-seen frames pays no re-grade. It used to say "bounded by the frame
    // count", which is no bound at all on a LIVE stream: that count grows for as long as the capture runs,
    // and the cache kept an entry for every frame ever graded. TrimScoreCache now keeps the window and one
    // window before it, the only frames a rebuild or a short backward scrub can ask for again; a longer
    // scrub re-grades, which costs time and nothing else.
    private readonly Dictionary<int, (float Score, PixelRect Box)> _scoreCache = new();

    // The lowest index that may still have a cached score, so a trim walks only the indices it removes.
    private int _scoreCacheFloor = int.MaxValue;

    // How many frames this stacker has graded, and how many of them held their planet whole (FrameGrader.DropsCutFrames).
    private int _gradedFrames;
    private int _wholeFrames;

    // The planet's elongation in the last SmearWindow whole frames graded, a ring, and their median, read again every SmearRefresh
    // frames: a live stack learns the run's typical shape as it goes, and leaves out a frame the telescope's motion smeared once it
    // has read SmearRefresh of them (FrameGrader.IsSmeared, #1300).
    private const int SmearWindow = 512;
    private const int SmearRefresh = 32;
    private readonly RollingMedian _runElongation = new RollingMedian(SmearWindow, SmearRefresh);

    // The planet's brightness (its peak above the sky) in the same frames, read the same way: a frame under half the run's median is
    // left out as dim (FrameGrader.IsDim, #1307).
    private readonly RollingMedian _runBrightness = new RollingMedian(SmearWindow, SmearRefresh);

    // The folded contribution of each in-window frame, so eviction can subtract exactly what was added
    // (same shift, negated weight) without re-grading. Weight 0 = graded-but-not-folded (kept so the
    // window membership/contiguity bookkeeping is uniform).
    private readonly Dictionary<int, Contribution> _window = new();

    // The positive scores of the window's graded frames, ascending: what a frame's rank in the window is read against (KeepFraction).
    private readonly List<float> _windowScores = new();

    // During a rebuild, the score a frame must reach to be folded: the window's best KeepFraction, read once every frame is graded.
    private float _rebuildThreshold = float.NaN;

    private float[][,]? _sum;     // per-channel weighted sum at reference (sub-plane) resolution
    private float[,]? _weight;    // shared per-pixel weight (coverage)
    private GlobalAligner? _aligner;

    /// <summary>
    /// The aligner and the two accumulators, which <c>PrepareAsync</c> creates together against the
    /// reference frame and which therefore exist together or not at all.
    /// </summary>
    /// <remarks>
    /// Asked as one precondition rather than asserted three at a time at each of the four call
    /// sites: a rolling window with no reference frame cannot add, evict or finish, and saying so by
    /// name beats a null-forgiving <c>!</c> that would surface as a bare NullReferenceException
    /// somewhere inside the accumulate.
    /// </remarks>
    private (float[][,] Sum, float[,] Weight, GlobalAligner Aligner) Accumulators
        => (_sum, _weight, _aligner) is ({ } sum, { } weight, { } aligner)
            ? (sum, weight, aligner)
            : throw new InvalidOperationException(
                "The rolling window has no reference frame yet; PrepareAsync must run first.");
    private int _refIndex = -1;
    private int _windowStart = -1;
    private int _windowEnd = -2;  // < _windowStart so "first call" is always a rebuild
    private int _rebuilds;
    private int _behindRebuilds;
    private int _agedRebuilds;
    private int _droppedRebuilds;
    private int _reReferences;
    // Where the current reference lies on the sum's grid: zero after a rebuild, the reference's own registration after a re-reference in
    // place (ReReferenceInPlace), added to every later frame's registration to the reference.
    private (double X, double Y) _gridOffset;
    private long _folds;
    private int _channels;
    private int _planeW;
    private int _planeH;
    private ImageMeta _meta;

    // Weight is the score the frame was folded with, zero when it was not folded; Score its grade, kept so an eviction takes it out of
    // the window's scores (zero for a frame the ring dropped before it was graded, or that graded out).
    private readonly record struct Contribution(float Weight, float Dx, float Dy, float Score);

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

    /// <summary>How many times the window has been folded again from its frames: the first stack, a jump, the reference ageing out, or a ring that dropped a frame the sum still held.</summary>
    public int Rebuilds => _rebuilds;

    /// <summary>
    /// The <see cref="Rebuilds"/> by their cause, for <c>planetary live</c> (#1174): a window that moved past the last one's end (the
    /// stack fell behind), an alignment reference that aged out of a window still sliding (which a stack that keeps up does too, once a
    /// window), and a ring that dropped a frame the sum held. The first stack, a backward jump and a cancelled stack are the rest.
    /// </summary>
    public (int Behind, int ReferenceAged, int RingDropped) RebuildCauses => (_behindRebuilds, _agedRebuilds, _droppedRebuilds);

    /// <summary>How many times the reference has aged out and been replaced without a rebuild (<see cref="RollingWindowOptions.ReReferenceInPlace"/>).</summary>
    public int ReReferences => _reReferences;

    /// <summary>How many frames have been registered and folded in so far, rebuilds included: what the stack has done, where its window's end says only how far it has reached.</summary>
    public long Folds => _folds;

    /// <summary>The frames the window's sum holds: its frames less those graded out, those a live ring dropped before they could be folded, and those below the window's best <see cref="RollingWindowOptions.KeepFraction"/>.</summary>
    public int FoldedFrameCount => _window.Values.Count(c => c.Weight > 0f);

    /// <summary>How many frames have been graded so far: what the stack has SEEN, which keeps up with a capture where the folds need not (#1174).</summary>
    public long GradedFrames => _gradedFrames;

    /// <summary>How many frames have a cached score, for the test that holds it bounded.</summary>
    internal int ScoreCacheCount => _scoreCache.Count;

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

        var aged = _refIndex >= 0 && _refIndex < windowStart; // alignment reference aged out of the window
        var needRebuild =
            _refIndex < 0                       // first stack
            || f < _windowEnd                   // backward jump (would need to re-add evicted frames)
            || windowStart > _windowEnd + 1     // forward jump leaving a gap -> window no longer contiguous
            || (aged && !_options.ReReferenceInPlace);

        try
        {
            if (needRebuild)
            {
                if (_refIndex >= 0 && f >= _windowEnd)
                {
                    if (windowStart > _windowEnd + 1)
                    {
                        _behindRebuilds++;
                    }
                    else
                    {
                        _agedRebuilds++;
                    }
                }
                await RebuildAsync(windowStart, f, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Grow the leading edge, then drop the trailing edge. Order matters only for peak memory;
                // both are O(pixels) per frame touched. Poll the token each iteration so a cancel aborts
                // promptly even when AddAsync short-circuits a cached frame (no per-frame LoadAsync token check).
                for (var i = _windowEnd + 1; i <= f; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddAsync(i, cancellationToken).ConfigureAwait(false);
                }
                var subtracted = true;
                for (var i = _windowStart; i < windowStart && subtracted; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    subtracted = await EvictAsync(i, cancellationToken).ConfigureAwait(false);
                }

                if (subtracted)
                {
                    _windowStart = windowStart;
                    _windowEnd = f;
                    // The slide registered its new frames to the aged reference, which is still a valid one; the frames from here
                    // on are registered to the window's best folded frame instead, or the window is folded again around it.
                    if (aged && !await TryReReferenceAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _agedRebuilds++;
                        await RebuildAsync(windowStart, f, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    // A live ring dropped a frame the sum still holds, so the sum can no longer be undone: fold the
                    // window again from the frames the ring does hold.
                    _droppedRebuilds++;
                    await RebuildAsync(windowStart, f, cancellationToken).ConfigureAwait(false);
                }
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

        TrimScoreCache();
        return await BuildMasterAsync(cancellationToken).ConfigureAwait(false);
    }

    // Drops the scores of frames more than one window behind the current one (see _scoreCache).
    private void TrimScoreCache()
    {
        var keepFrom = _windowStart - _options.MaxWindowFrames;
        if (keepFrom <= _scoreCacheFloor)
        {
            return;
        }

        for (var i = _scoreCacheFloor; i < keepFrom; i++)
        {
            _scoreCache.Remove(i);
        }

        _scoreCacheFloor = keepFrom;
    }

    /// <summary>Drops the current window/reference so the next <see cref="StackToAsync"/> rebuilds from
    /// scratch. Used after a cancellation left the accumulators in a partial state. Keeps the grade cache.</summary>
    private void Invalidate()
    {
        _refIndex = -1;
        _windowStart = -1;
        _windowEnd = -2;
        _window.Clear();
        _windowScores.Clear();
        _rebuildThreshold = float.NaN;
        _gridOffset = default;
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

    // Takes the window's best folded frame (ties to the earliest) as the alignment reference without folding the window again
    // (ReReferenceInPlace): the sum stays on its grid, and the new reference's own registration onto it, stored as it was folded, is what
    // every later frame's registration is moved by. False when nothing in the window is folded, when the best frame lies more than a
    // quarter of the aligner's tile from the grid, or when the ring has dropped it: the caller rebuilds.
    private async Task<bool> TryReReferenceAsync(CancellationToken cancellationToken)
    {
        var (best, bestScore) = (-1, float.NegativeInfinity);
        for (var i = _windowStart; i <= _windowEnd; i++)
        {
            if (_window.TryGetValue(i, out var c) && c.Weight > 0f && c.Score > bestScore)
            {
                (best, bestScore) = (i, c.Score);
            }
        }
        if (best < 0)
        {
            return false;
        }

        var placed = _window[best];
        var (_, _, aligner) = Accumulators;
        var reach = aligner.TileSize / 4.0;
        if (Math.Abs(placed.Dx) > reach || Math.Abs(placed.Dy) > reach)
        {
            return false;
        }
        if (await _stream.TryLoadAsync(best, cancellationToken).ConfigureAwait(false) is not { } reference)
        {
            return false;
        }
        try
        {
            var region = _scoreCache.TryGetValue(best, out var cached) ? cached.Box : PlanetaryDisk.BoundingBox(reference);
            _aligner = GlobalAligner.FromReference(reference, region, aligner.TileSize, _options.WhitenedCorrelation);
        }
        finally
        {
            reference.Release();
        }
        _refIndex = best;
        _gridOffset = (placed.Dx, placed.Dy);
        _reReferences++;
        return true;
    }

    private async Task RebuildAsync(int windowStart, int f, CancellationToken cancellationToken)
    {
        _rebuilds++;
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

        // 2. Build the aligner + zeroed accumulators sized to the reference frame. A live ring can drop the chosen
        //    frame between the grading and here; the playhead frame is the newest, so the ring still holds it.
        if (await _stream.TryLoadAsync(bestIndex, cancellationToken).ConfigureAwait(false) is not { } reference)
        {
            bestIndex = f;
            reference = await _stream.LoadAsync(f, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var refRegion = PlanetaryDisk.BoundingBox(reference);
            var tileSize = _options.AlignTileSize > 0
                ? NextPowerOfTwo(_options.AlignTileSize)
                : Math.Clamp(NextPowerOfTwo(Math.Max(refRegion.Width, refRegion.Height)), 64, 512);
            _aligner = GlobalAligner.FromReference(reference, refRegion, tileSize, _options.WhitenedCorrelation);
            _refIndex = bestIndex;
            _gridOffset = default;
            _channels = reference.ChannelCount;
            _planeH = reference.Height;
            _planeW = reference.Width;
            _meta = reference.ImageMeta;
            _sum = Image.CreateChannelData(_channels, _planeH, _planeW);
            _weight = new float[_planeH, _planeW];
            _window.Clear();
            _windowScores.Clear();
        }
        finally
        {
            reference.Release();
        }

        // 3. Fold the whole window in. Poll the token each iteration: this is the expensive loop (up to
        // MaxWindowFrames align+fold passes), so a cancel during a rebuild must abort it promptly rather
        // than run the whole window -- LiveStackPreviewSource.DisposeAsync awaits exactly this stack at
        // shutdown, so a prompt abort keeps the (bounded) drain short.
        _windowStart = windowStart;
        _windowEnd = f;
        _rebuildThreshold = RebuildThreshold(windowStart, f);
        try
        {
            for (var i = windowStart; i <= f; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddAsync(i, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _rebuildThreshold = float.NaN;
        }
    }

    // The score a rebuilt window's frame must reach to be folded: its window's best KeepFraction of the positive scores graded (every one
    // is by now, for the reference pick). NaN folds every positive one.
    private float RebuildThreshold(int windowStart, int f)
    {
        if (_options.KeepFraction >= 1)
        {
            return float.NaN;
        }
        var scores = new List<float>(f - windowStart + 1);
        for (var i = windowStart; i <= f; i++)
        {
            if (_scoreCache.TryGetValue(i, out var s) && s.Score > 0f)
            {
                scores.Add(s.Score);
            }
        }
        if (scores.Count == 0)
        {
            return float.NaN;
        }
        scores.Sort();
        return scores[scores.Count - KeptCount(scores.Count)];
    }

    // How many of n scores the best KeepFraction is: at least one.
    private int KeptCount(int n) => Math.Clamp((int)Math.Ceiling(_options.KeepFraction * n), 1, n);

    // Whether a frame scoring `score`, its score already among the window's, is folded: in a rebuild, at or above the window's threshold;
    // otherwise when fewer than the kept count of the window's scores beat it.
    private bool Kept(float score)
    {
        if (_options.KeepFraction >= 1)
        {
            return true;
        }
        if (!float.IsNaN(_rebuildThreshold))
        {
            return score >= _rebuildThreshold;
        }
        var above = _windowScores.Count - UpperBound(_windowScores, score);
        return above < KeptCount(_windowScores.Count);
    }

    // The index of the first score in the ascending list greater than `score`.
    private static int UpperBound(List<float> sorted, float score)
    {
        var (lo, hi) = (0, sorted.Count);
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid] <= score)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    // Folds frame `index` into the running sum (loads once; grade is cache-aware). A non-positive score is
    // recorded as a zero contribution so eviction is a no-op but window bookkeeping stays uniform, and so is a
    // frame a live ring has already dropped (a stacker that fell behind a fast camera, 92 frames a second).
    private async Task AddAsync(int index, CancellationToken cancellationToken)
    {
        if (_window.ContainsKey(index))
        {
            return;
        }

        if (await _stream.TryLoadAsync(index, cancellationToken).ConfigureAwait(false) is not { } frame)
        {
            _window[index] = default;
            return;
        }
        try
        {
            var (score, box) = GradeLoaded(index, frame);
            if (score <= 0f)
            {
                _window[index] = default;
                return;
            }
            _windowScores.Insert(UpperBound(_windowScores, score), score);
            if (!Kept(score))
            {
                _window[index] = new Contribution(0f, 0f, 0f, score);
                return;
            }

            var (sum, weight, aligner) = Accumulators;
            // The grade's own box (FrameGrader.GradeCutAndBox), BoundingBox's at its defaults: a second scan of the frame for it cost
            // a fold a third of its time on a fast capture (#1174).
            var shift = aligner.Estimate(frame, box);
            var (dx, dy) = ((float)(shift.Dx + _gridOffset.X), (float)(shift.Dy + _gridOffset.Y));
            frame.AccumulateTranslatedInto(sum, weight, dx, dy, score, _options.Interpolation);
            _window[index] = new Contribution(score, dx, dy, score);
            _folds++;
        }
        finally
        {
            frame.Release();
        }
    }

    // Removes frame `index` from the window, subtracting exactly what AddAsync folded (same shift, negated
    // weight). AccumulateTranslatedInto is linear in weight, so +w then -w cancels per pixel; the in-bounds
    // set is identical (same frame, same shift), so it cancels exactly. False when the frame carried weight
    // and a live ring has dropped it since: then what it added cannot be taken back, and the caller rebuilds.
    private async Task<bool> EvictAsync(int index, CancellationToken cancellationToken)
    {
        if (!_window.Remove(index, out var c))
        {
            return true;
        }
        if (c.Score > 0f && _windowScores.BinarySearch(c.Score) is var at && at >= 0)
        {
            _windowScores.RemoveAt(at);
        }
        if (c.Weight <= 0f)
        {
            return true;
        }

        if (await _stream.TryLoadAsync(index, cancellationToken).ConfigureAwait(false) is not { } frame)
        {
            return false;
        }

        try
        {
            var (sum, weight, _) = Accumulators;
            frame.AccumulateTranslatedInto(sum, weight, c.Dx, c.Dy, -c.Weight, _options.Interpolation);
        }
        finally
        {
            frame.Release();
        }
        return true;
    }

    // Ensures frame `index` has a cached score, loading + grading it if needed. Used by the rebuild's
    // reference pick before any folding. A frame a live ring has dropped scores zero, so it is never picked.
    private async Task<float> EnsureScoreAsync(int index, CancellationToken cancellationToken)
    {
        if (_scoreCache.TryGetValue(index, out var cached))
        {
            return cached.Score;
        }

        if (await _stream.TryLoadAsync(index, cancellationToken).ConfigureAwait(false) is not { } frame)
        {
            return 0f;
        }
        try
        {
            return GradeLoaded(index, frame).Score;
        }
        finally
        {
            frame.Release();
        }
    }

    private (float Score, PixelRect Box) GradeLoaded(int index, Image frame)
    {
        if (_scoreCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        // A frame whose planet the frame's edge cuts, or which holds none, scores zero once the run has held enough whole ones
        // (FrameGrader.DropsCutFrames): a live stack learns the capture as it goes.
        var (graded, cut, box, elongation, brightness) = FrameGrader.GradeCutAndBox(_options.QualityEstimator, frame);
        _gradedFrames++;
        _wholeFrames += cut ? 0 : 1;
        if (!cut && float.IsFinite(elongation))
        {
            _runElongation.Add(elongation);
        }
        if (!cut && float.IsFinite(brightness))
        {
            _runBrightness.Add(brightness);
        }
        var leftOut = FrameGrader.LeftOutBecause(cut, FrameGrader.DropsCutFrames(_wholeFrames, _gradedFrames), elongation, _runElongation.Value,
            brightness, _runBrightness.Value);
        var score = leftOut != FrameExclusion.None ? 0f : MathF.Max(0f, graded);
        _scoreCache[index] = (score, box);
        if (index < _scoreCacheFloor)
        {
            // A backward scrub graded below the floor: the next trim must walk from here.
            _scoreCacheFloor = index;
        }

        return (score, box);
    }

    // The weight a pixel's sum must pass to count as covered (#1319): half the least score the sum holds. A covered pixel holds at
    // least one folded frame's whole score (AccumulateTranslatedInto adds a frame's weight whole or not at all), while one that no
    // folded frame reaches holds only what rounding left as frames were evicted from it: at most 1.1e-5 against scores of about 0.1 on
    // the twin, whose ratio read -3.17 to 1.16 on frames between 0.07 and 0.3. A window that is never folded again from nothing (the
    // reference re-taken in place, #1174) keeps that residue for good.
    private float UncoveredWeight()
    {
        var least = float.PositiveInfinity;
        foreach (var contribution in _window.Values)
        {
            if (contribution.Weight > 0f && contribution.Weight < least)
            {
                least = contribution.Weight;
            }
        }
        return float.IsFinite(least) ? 0.5f * least : 0f;
    }

    // Persistent normalise destination for the split-CFA path, where the normalised sub-planes are
    // transient scratch (merged + demosaiced into a fresh master below, never escaping this class) --
    // reused across rebuilds so a live master publish stops re-allocating 4 full sub-planes each time.
    // Shape-checked lazily because the reference (and with it the plane geometry) can change on a
    // window rebuild. Mono/RGB masters must NOT use it: there NormalizeInto's result IS the returned
    // master (MergeAndDemosaicAsync passes it through), and the previous master may still be displayed
    // or cached for a wavelet re-sharpen -- overwriting its backing arrays would corrupt it.
    private float[][,]? _sumScratch;

    private async Task<Image> BuildMasterAsync(CancellationToken cancellationToken)
    {
        // Normalise into a destination WITHOUT disturbing the accumulators (they keep their integral
        // state for the next add/evict; _weight is read, not mutated). NormalizeInto fuses the old
        // Clone()-then-NormalizeInPlace into a single read-sum/write-dst pass.
        float[][,] dst;
        // Must mirror MergeAndDemosaicAsync's demosaic gate exactly: only when the sub-planes are
        // consumed by merge+demosaic is the destination transient enough to be the shared scratch.
        if (_stream.Layout == PlanetaryFrameLayout.SplitCfa && _channels == 4)
        {
            if (_sumScratch is not { } scratch || scratch.Length != _channels
                || scratch[0].GetLength(0) != _planeH || scratch[0].GetLength(1) != _planeW)
            {
                _sumScratch = scratch = Image.CreateChannelData(_channels, _planeH, _planeW);
            }
            dst = scratch;
        }
        else
        {
            dst = Image.CreateChannelData(_channels, _planeH, _planeW);
        }

        var (sum, weight, _) = Accumulators;
        var stacked = PlanetaryMaster.NormalizeInto(sum, weight, dst, _meta, UncoveredWeight());
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

    // A median over the last `window` values added, read again every `refresh` of them: the live stack's run elongation and brightness
    // (#1300, #1307), which were one block written twice with the names swapped (the audit on #1343). NaN until `refresh` values are in.
    private sealed class RollingMedian(int window, int refresh)
    {
        private readonly float[] _ring = new float[window];
        private readonly float[] _scratch = new float[window];
        private int _added;

        public double Value { get; private set; } = double.NaN;

        public void Add(float value)
        {
            _ring[_added++ % window] = value;
            if (_added % refresh == 0)
            {
                var count = Math.Min(_added, window);
                _ring.AsSpan(0, count).CopyTo(_scratch);
                Value = StatisticsHelper.MedianFast(_scratch.AsSpan(0, count));
            }
        }
    }
}
