using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    internal const int BoxRadius = 14;
    internal const float HfdFactor = 1.5f;

    /// <summary>
    /// Radius, in multiples of a star's HFD, within which a second centroid is the SAME star rather
    /// than a neighbour: its half-flux radius.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not <see cref="HfdFactor"/>.</b> One radius used to do two jobs with
    /// opposite requirements. As a SUPPRESSION extent it wants to be generous -- it marks the pixels
    /// of a star that has already been measured, so the scan does not re-analyse its whole wing, and
    /// erring wide only costs a little background. As a DEBLEND distance it wants to be tight -- it
    /// decides that a candidate is a re-detection rather than its own star, and erring wide MERGES two
    /// stars into one. At <c>1.5 * HFD</c> (about 1.65 * FWHM, i.e. ~3.9 sigma) it was generous, so the
    /// merge side paid. Measured on the 3008x3008 RGGB fixture (<c>StarMaskDeblendProbe</c>): 41
    /// accepted stars of 3,015 have a second strict local maximum above the detection level within
    /// <c>1.5 * HFD</c> and at least 2 px from the recorded centroid, against 8 within the claim
    /// radius. That is the user-visible complaint -- two tight stars marked as one.</para>
    /// <para><b>Why the half-flux radius.</b> <c>0.5 * HFD</c> is the radius containing half the star's
    /// flux, so two centroids inside it are the same object almost by definition of the measurement,
    /// and it scales with a defocused star instead of needing a second constant. It is not a fresh
    /// number: it is the same HFD the suppression radius is derived from, read at its own definition
    /// rather than at an empirical multiple. Floored at 1 px, since a claim smaller than a pixel
    /// cannot be tested on an integer grid -- and at that floor the mask is
    /// <see cref="ClaimMinMask"/>, not a radius-1 disc, for the diagonal-rounding reason documented
    /// there.</para>
    /// <para>The duplicate class this guards against is unaffected by the tightening, because the
    /// duplicates it was introduced for landed on the SAME position (measured: 1 distinct position out
    /// of the top 100 by flux on an SV605CC frame), not a few pixels away. Pinned: the fixture counts
    /// still assert 0 duplicate pairs, and the synthetic 28-star field still returns exactly 28 at
    /// SNR 20 and 89 at SNR 10, unchanged in both directions.</para>
    /// <para><b>What the split actually recovers, and why the number is small.</b> Only a large or
    /// defocused star has a footprint big enough to swallow a neighbour, so the gain is bounded by how
    /// many of those a frame holds. On the RGGB fixture, pairs of ACCEPTED stars closer than the wider
    /// one's <c>1.5 * HFD</c> went from 2 to 4 and the star count from 3,013 to 3,015: two companions
    /// of HFD 7.53 and 12.26 stars, at 10.1 px and 14.0 px, each of which the old single radius had
    /// suppressed outright. The synthetic field gains nothing because its closest pair is 11.8 px
    /// apart, which no radius here reaches -- a reminder that a synthetic star field cannot exercise
    /// deblending at all.</para>
    /// </remarks>
    internal const float ClaimFactor = 0.5f;

    /// <summary>
    /// The smallest claim: the full 8-neighbourhood of the rounded centroid, used when
    /// <see cref="ClaimFactor"/> times the HFD rounds to 1 px or less.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a 3x3 SQUARE and not <c>MakeStarMask(1)</c>.</b> That generator keeps pixels with
    /// <c>x^2 + y^2 &lt;= r^2</c>, so a radius-1 disc is a PLUS: five pixels, no diagonals. Two
    /// measurements of one star can be a quarter of a pixel apart and still round to DIAGONALLY
    /// adjacent pixels when they straddle a pixel corner -- (100.6, 200.6) and (100.4, 200.4) are
    /// 0.28 px apart and land on (101, 201) and (100, 200) -- and a plus does not cover that, so the
    /// re-detection is recorded as a second star. The 8-neighbourhood is exactly sufficient and no
    /// larger: <c>|a - b| &lt;= 1</c> per axis implies <c>|round(a) - round(b)| &lt;= 1</c> per axis, and
    /// "within 1 px per axis" is the duplicate definition <c>FindStarsFromFitsFileTests</c> asserts.
    /// </para>
    /// <para><b>The fixtures cannot tell these three apart, so do not conclude from them.</b> The plus,
    /// this square and the radius-2 disc all produce byte-identical output on both frames: 3,015 /
    /// 2,753 stars, 0 duplicate pairs, the same four resolved close pairs, the same 5,126 analysed
    /// candidates. The Horsehead frame simply contains no duplicate whose two centroids straddle a
    /// corner. This is a hole closed by construction rather than by measurement, which is the one kind
    /// a test cannot argue for.</para>
    /// <para><b>And do not "fix" it by widening to radius 2 instead.</b> That also closes the hole, but
    /// it is 4 px wider on the axes than needed and it overrides <see cref="ClaimFactor"/> for every
    /// star with HFD below 3 -- i.e. most stars on a well-focused rig -- so the deblend distance stops
    /// being the half-flux radius the design chose and becomes a constant. An earlier version of this
    /// shipped a radius-2 floor, on the mistaken belief that the plus was what let 268 duplicate pairs
    /// through in testing. It was not: those came from a centring bug in the same change (the stamp
    /// offset used the FLOAT <c>0.5 * HFD</c> while the mask carried its rounded INTEGER radius, so the
    /// disc landed up to a pixel off the centroid it was meant to claim). With the offset taken from
    /// <c>round(centroid)</c>, a bare plus is measurably just as good -- which is precisely why the
    /// shape argument, not the measurement, has to decide it.</para>
    /// </remarks>
    internal static readonly BitMatrix ClaimMinMask;
    internal const int MaxScaledRadius = (int)(HfdFactor * BoxRadius * 2) + 1;
    internal static readonly ImmutableArray<BitMatrix> StarMasks;

    static Image()
    {
        var starMasksBuilder = ImmutableArray.CreateBuilder<BitMatrix>(MaxScaledRadius);
        for (var radius = 1; radius < MaxScaledRadius; radius++)
        {
            MakeStarMask(radius, out var mask);
            starMasksBuilder.Add(mask);
        }

        StarMasks = starMasksBuilder.ToImmutable();

        // The 8-neighbourhood, which MakeStarMask cannot express: it generates discs, and a radius-1
        // disc omits the diagonals. See ClaimMinMask.
        ClaimMinMask = new BitMatrix(3, 3);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                ClaimMinMask[y, x] = true;
            }
        }
    }

    static void MakeStarMask(int radius, out BitMatrix starMask)
    {
        var diameter = radius << 1;
        var radius_squared = radius * radius;
        starMask = new BitMatrix(diameter + 1, diameter + 1);

        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius_squared)
                {
                    int pixelX = radius + x;
                    int pixelY = radius + y;
                    if (pixelX >= 0 && pixelX <= diameter && pixelY >= 0 && pixelY <= diameter)
                    {
                        starMask[pixelY, pixelX] = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Find background, noise level, number of stars and their HFD, FWHM, SNR, flux and centroid.
    /// </summary>
    /// <param name="channel">Channel</param>
    /// <param name="snrMin">S/N ratio threshold for star detection</param>
    /// <param name="maxStars"><b>Caps nothing.</b> Its ONLY effect is to supply the default for
    /// <paramref name="minStars"/>, i.e. the retry loop target, so passing it alone reads as "cap the
    /// list here" and means "keep rescanning until you find this many". The returned
    /// <see cref="StarList"/> is never truncated to it. Prefer stating
    /// <paramref name="minStars"/> explicitly; this parameter is retained only because ~50 call sites
    /// pass it.
    /// <para><b>The cost of overshooting it is one extra pass, not a multiple.</b>
    /// <paramref name="maxRetries"/> is decremented BEFORE the loop condition is re-tested, so the
    /// default 2 yields at most TWO passes, and the <c>detection_level &lt;= 7 * noise</c> guard
    /// usually stops it at one. Measured (Debug, per-pass log): on the 3008x3008 RGGB fixture pass 1
    /// costs 29-82 ms and pass 2 costs 57 ms, against 166-175 ms for the <c>Background</c> call that
    /// precedes both -- so the retry chain is the CHEAPEST part of a detection, and lowering this
    /// number to save it trades 63% of the star list (3,014 -> 1,127 on that frame) for ~40 ms.
    /// An earlier version of this doc claimed a pass rescan "triples wall time"; it does not.</para></param>
    /// <param name="minStars">Early-termination threshold: stop retrying as
    /// soon as <c>starList.Count &gt;= minStars</c>. Callers that only need
    /// a handful of well-detected stars (plate solving needs ~6, focus
    /// monitoring needs ~30) should set this far below
    /// <paramref name="maxStars"/>. Default is <c>-1</c> which keeps the
    /// historical behaviour (terminate only when <paramref name="maxStars"/>
    /// is reached or retries run out).</param>
    /// <param name="maxRetries"></param>
    /// <returns></returns>
    // Last-call StarList cache. Plate-solve / save-snapshot / re-solve all hit
    // FindStarsAsync on the same Image with the same params; on a 61 MP polar
    // frame each call costs ~30 s. Cache key includes detection params so a
    // call with different SNR / maxStars / retries doesn't return stale data.
    // Single-slot cache (not a dictionary) -- the typical pattern is "called
    // twice with identical args"; multiple keys aren't a real workload.
    private readonly SemaphoreSlim _starListCacheLock = new(1, 1);
    private (int Channel, float SnrMin, int MaxStars, int MinStars, int MaxRetries, float MaxFirstPassNoiseSigma) _starListCacheKey;
    private StarList? _starListCacheValue;

    /// <summary>
    /// Discards any cached <see cref="StarList"/>. Useful if a caller mutates
    /// the underlying pixel data after a previous detection (rare; pixel data
    /// is treated as immutable in practice, but explicit invalidation is
    /// cheaper than reasoning about it).
    /// </summary>
    public void InvalidateStarListCache()
    {
        // Lock-free invalidation: a reference write is atomic, and the fast-path read in FindStarsAsync is
        // already lock-free and tolerant of a torn read (it re-checks under the semaphore on the slow path).
        // So nulling the slot without taking _starListCacheLock is safe -- and avoids a synchronous
        // SemaphoreSlim.Wait() that would block whatever thread calls this (never block a thread on a wait).
        Volatile.Write(ref _starListCacheValue, null);
    }

    /// <summary>
    /// Mosaic offset of the <see cref="DebayerAlgorithm.BilinearMono"/> grid, in pixels on both axes.
    /// </summary>
    /// <remarks>
    /// <para>That debayer stores, at output index <c>(y, x)</c>, the mean of the 2x2 quad whose
    /// TOP-LEFT is <c>(y, x)</c>. The quad's centre is mosaic <c>(y + 0.5, x + 0.5)</c>, so the mono
    /// image samples the mosaic half a pixel down-right of where it indexes it -- and a centroid
    /// measured on it therefore reads half a pixel SMALL in mosaic coordinates.</para>
    /// <para>Measured, not reasoned: a synthetic mosaic with stars planted at known sub-pixel
    /// positions returns dx = dy = -0.5000 on every star, while the same field as mono returns
    /// 0.0000 (<c>BayerCentroidGroundTruthTests</c>). It showed up as star-detection rings sitting
    /// up-left of their blobs in the viewer, and only visibly so when zoomed in -- at 1:1 half a
    /// pixel is nothing, at 4x it is a clear two.</para>
    /// <para>Corrected here rather than by re-centring the debayer: this moves no pixel values, so
    /// star counts, HFDs and every byte-pinned detector expectation are untouched. Re-centring would
    /// average a different set of neighbours and change all of them.</para>
    /// </remarks>
    internal const float BilinearMonoGridOffset = 0.5f;

    /// <summary>
    /// Ceiling on the FIRST detection pass, in multiples of the frame's noise level.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a ceiling at all.</b> The first pass starts at
    /// <c>max(3.5 * noise, star_level)</c>, and <c>star_level</c> is HISTOGRAM-derived: it is where the
    /// bright tail of the histogram begins. That is a good estimate on a star field, and an arbitrarily
    /// bad one on a frame carrying extended bright signal, because a nebula fills those same high bins.
    /// Measured on a 60 s M42 Ha sub (iTelescope 31, 3055x3056): <c>bg=199.7, noise=74,
    /// star_level=7177</c> -- a first pass at <b>94 sigma</b>, which accepted <b>8</b> stars out of
    /// 1449 analysed candidates while ASTAP found <b>40</b> in the same frame. The plate solve then
    /// failed by one hit (9 against a pair-lock threshold of 10) with the catalogue offering 63 anchors,
    /// so the catalogue was never the constraint.</para>
    /// <para><b>Why 30, and why it is not a new constant.</b> It is the top rung of the retry ladder in
    /// the loop below (<c>min(30 * noise, ...)</c>, stepping 30 -> 7 -> stop). Capping here therefore
    /// starts pass 1 where the first RETRY would have started, and adds no pass to any frame: the
    /// ladder's own judgement about how high a useful detection level can be, applied at the top
    /// instead of one pass later.</para>
    /// <para><b>Why not simply allow the retries instead.</b> <c>CatalogPlateSolver</c> pins
    /// <c>maxRetries: 0</c> deliberately (commit 88bde225): on an under-exposed polar-align rung two
    /// extra passes over an un-binned IMX455 frame burn 2 x 1-2 s and blow the 5.5 s rung budget, and
    /// they cannot conjure stars that are not there. So on the caller that matters here the ladder
    /// cannot run.</para>
    /// <para><b>It is OPT-IN per call, and the default is uncapped.</b> Capping every detection was
    /// measured and rejected: it is the right trade for a solver, which wants the most stars it can get
    /// and cares only about their positions in aggregate, and the wrong one for everything else. On the
    /// full suite it moved four other expectations -- centroid ground truth to 0.183 px (mono) and
    /// 0.251 px (mosaic) against a 0.15 px bound, the dataset star-count gate from 8 subs to 7, and a
    /// registrar noise figure by 0.45% -- because the stars it adds are the faint ones, and faint stars
    /// carry worse centroids. That is the caveat the retry ladder states about itself
    /// (<i>"faint stars have less positional accuracy"</i>), arriving through the front door. So the
    /// plate solver asks for the cap and no one else does.</para>
    /// </remarks>
    internal const float MaxFirstPassNoiseSigma = 30f;

    /// <summary>
    /// Where the first detection pass starts, above background: the histogram's <paramref name="starLevel"/>,
    /// floored at 3.5 sigma and capped at <see cref="MaxFirstPassNoiseSigma"/> sigma.
    /// </summary>
    /// <remarks>
    /// Its own method so the arithmetic is testable without a frame: the inputs that matter are two
    /// scalars, and the case that broke -- a histogram level 97x the noise -- is a pair of numbers read
    /// off a real 18 MB sub that no test fixture can carry. Pinned by <c>FirstPassDetectionLevelTests</c>.
    /// </remarks>
    internal static float FirstPassDetectionLevel(float noiseLevel, float starLevel, float maxNoiseSigma)
        => MathF.Max(3.5f * noiseLevel, MathF.Min(starLevel, maxNoiseSigma * noiseLevel));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public virtual async Task<StarList> FindStarsAsync(int channel, float snrMin = 20f, int maxStars = 500, int minStars = -1, int maxRetries = 2, float maxFirstPassNoiseSigma = float.PositiveInfinity, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        // Default minStars to maxStars preserves the historical behaviour
        // (retries until maxStars is reached). New callers should set minStars
        // to whatever they actually need -- plate-solver wants ~30, focus
        // wants ~30, an overlay browser wants 200.
        if (minStars < 0)
        {
            minStars = maxStars;
        }

        // Cached-result fast path: lock-free first check. The fields are
        // assigned together under the lock below, so a torn read just falls
        // through to the slow path -- no correctness hazard.
        var key = (channel, snrMin, maxStars, minStars, maxRetries, maxFirstPassNoiseSigma);
        if (_starListCacheValue is { } fastCached && _starListCacheKey.Equals(key))
        {
            return fastCached;
        }

        await _starListCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_starListCacheValue is { } cached && _starListCacheKey.Equals(key))
            {
                return cached;
            }

            var result = await DetectStarsAsync(channel, snrMin, maxStars, minStars, maxRetries, maxFirstPassNoiseSigma, logger, cancellationToken).ConfigureAwait(false);
            _starListCacheValue = result;
            _starListCacheKey = key;
            return result;
        }
        finally
        {
            _starListCacheLock.Release();
        }
    }

    // Per-pass counters for FindStarsAsync diagnostics. Heap-allocated only
    // when a logger is supplied so the hot path stays branch-free for
    // production (no-logger) callers. Atomically accumulated once per chunk.
    private sealed class FindStarsPassCounters
    {
        public long ThresholdHits;
        public long AnalyseStarCalls;
        public long Accepted;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task<StarList> DetectStarsAsync(int channel, float snrMin, int maxStars, int minStars, int maxRetries, float maxFirstPassNoiseSigma, ILogger? logger, CancellationToken cancellationToken)
    {
        // ChunkSize = row band height each parallel task processes, matching the max star radius
        // (HfdFactor * BoxRadius) so no star can span two non-adjacent chunks. Decoupled from
        // StarMasks.MaxScaledRadius (full HFD diameter): ChunkSize is a half-diameter guard band,
        // not the pixel mask stamp size, keeping parallelization stable if HfdFactor changes.
        const int ChunkSize = 2 * ((int)(HfdFactor * BoxRadius) + 1);
        const float HalfChunkSizeInv = 1.0f / (2.0f * ChunkSize);
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }
        if (imageMeta.SensorType is SensorType.RGGB && ChannelCount is 1)
        {
            // Detection cannot run on a CFA mosaic, so measure on a mono debayer -- but return
            // centroids in MOSAIC coordinates, because that is the space every caller consumes them
            // in (the viewer's star overlay draws them straight onto the displayed mosaic, and a
            // solver-built WCS is expressed in them).
            var monoImage = await DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: cancellationToken);
            var monoStars = await monoImage.FindStarsAsync(channel, snrMin, maxStars, minStars, maxRetries, maxFirstPassNoiseSigma, logger, cancellationToken);
            return monoStars.ShiftedBy(BilinearMonoGridOffset, BilinearMonoGridOffset);
        }

        var bgStart = logger is not null ? Stopwatch.GetTimestamp() : 0L;
        var (background, star_level, noise_level, hist_threshold) = Background(channel);
        if (logger is not null)
        {
            var bgElapsed = Stopwatch.GetElapsedTime(bgStart);
            logger.LogDebug(
                "Image.FindStarsAsync: Background channel={Channel} bg={Bg:F3} starLevel={StarLevel:F3} noise={Noise:F3} histThreshold={HistThreshold:F3} in {Ms:F1}ms",
                channel, background, star_level, noise_level, hist_threshold, bgElapsed.TotalMilliseconds);
        }

        /* Level above background. Start high, but never above the retry ladder's own top rung: see
           MaxFirstPassNoiseSigma for the frame that made this necessary and why 30 is the ladder's
           number rather than a new one. */
        var detection_level = FirstPassDetectionLevel(noise_level, star_level, maxFirstPassNoiseSigma);
        var retries = maxRetries;

        if (background >= hist_threshold || background <= 0)  /* abnormal file */
        {
            logger?.LogDebug(
                "Image.FindStarsAsync: abnormal frame -- bg={Bg:F3} histThreshold={HistThreshold:F3}, returning empty StarList",
                background, hist_threshold);
            return new StarList([]);
        }

        var starList = new ConcurrentBag<ImagedStar>();
        // Two masks, because the two jobs one mask used to do have opposite requirements: see
        // ClaimFactor. img_star_area is the star's FOOTPRINT (what has already been measured, and
        // what a background estimator wants excluded); img_star_claim is the much smaller
        // half-flux disc that says "a centroid landing here is this same star again".
        var img_star_area = new BitMatrix(height, width);
        var img_star_claim = new BitMatrix(height, width);
        var channelData = Planes[channel].Data;

        // Interleaved chunk processing: two passes (i=0 even chunks, i=1 odd chunks) ensures no two
        // adjacent chunks run simultaneously, so a star near a chunk boundary won't be written into
        // the BitMatrix star mask by one task while a neighbour reads it concurrently; no locking needed.
        //
        // Parallel.For (not ForAsync): the chunk body is purely CPU-bound, no awaits.
        // ForAsync wraps each iteration in async machinery (ValueTask, state machine,
        // per-iteration Task) which is dead weight for sync work. Wrapping the whole
        // pass in Task.Run keeps the outer FindStarsAsync awaitable while the inner
        // Parallel.For uses range partitioning + work-stealing across cores directly.
        //
        // MaxDegreeOfParallelism: ProcessorCount*4. Over-provisioning absorbs chunk
        // imbalance: galactic-plane frames have hot spots where one chunk does 5-10x
        // the AnalyseStar work of a sparse chunk. With MDOP=ProcessorCount alone, a
        // single slow chunk stalls the pass; with MDOP=4x, queued chunks fan out as
        // cores free up. BDN measured ProcessorCount alone was ~8% slower on the
        // runner config.
        var halfChunkCount = (int)Math.Ceiling(height * HalfChunkSizeInv);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4, CancellationToken = cancellationToken };

        var passNumber = 0;
        do
        {
            passNumber++;
            var passCounters = logger is not null ? new FindStarsPassCounters() : null;
            var passStart = logger is not null ? Stopwatch.GetTimestamp() : 0L;
            var passDetectionLevel = detection_level;

            // Capture loop variable into a local readable by the sync Parallel.For
            // body. The sync body avoids async state machine overhead on every chunk.
            var passEvenOdd = 0;
            await Task.Run(() =>
            {
                for (passEvenOdd = 0; passEvenOdd <= 1; passEvenOdd++)
                {
                    var phase = passEvenOdd;
                    Parallel.For(0, halfChunkCount, parallelOptions, halfChunk =>
                    {
                        // Per-chunk thread-local counters: bumped only via plain ++,
                        // so the hot path stays scalar-register cheap. Atomically
                        // folded into the shared PassCounters once at chunk end.
                        long localHits = 0, localCalls = 0, localAccepted = 0;
                        var chunk = 2 * halfChunk + phase;
                        var chunkEnd = Math.Min(height, (chunk + 1) * ChunkSize);
                        for (var fitsY = chunk * ChunkSize; fitsY < chunkEnd; fitsY++)
                        {
                            for (var fitsX = 0; fitsX < width; fitsX++)
                            {
                                // new star. For analyse used sigma is 5, so not too low.
                                var value = channelData[fitsY, fitsX];
                                if (float.IsNaN(value))
                                {
                                    img_star_area[fitsY, fitsX] = true; /* ignore NaN values */
                                }
                                else if (value - background > detection_level)
                                {
                                    localHits++;
                                    // Inside an already-measured star's footprint, a pixel is worth
                                    // analysing only if it could be its own object's peak. That is
                                    // what lets a close companion out of a bright neighbour's mask
                                    // while still skipping the thousands of wing pixels the mask is
                                    // there to skip -- see IsStrictLocalMaximum for why it is nearly
                                    // free.
                                    if (!img_star_area[fitsY, fitsX]
                                        || (!img_star_claim[fitsY, fitsX]
                                            && IsStrictLocalMaximum(channelData, fitsX, fitsY, width, height)))
                                    {
                                        localCalls++;
                                        if (AnalyseStar(channel, fitsX, fitsY, BoxRadius, out var star)
                                            && star.HFD is > 0.8f and <= BoxRadius * 2 /* at least 2 pixels in size */
                                            && star.SNR >= snrMin
                                            && !CentroidAlreadyClaimed(img_star_claim, star, width, height))
                                        {
                                            localAccepted++;
                                            starList.Add(star);
                                            var scaledHfd = HfdFactor * star.HFD;
                                            var r = (int)MathF.Round(scaledHfd); /* radius for marking star area, factor 1.5 is chosen emperiacally. */
                                            var xc_offset = (int)MathF.Round(star.XCentroid - scaledHfd); /* star center as integer */
                                            var yc_offset = (int)MathF.Round(star.YCentroid - scaledHfd);

                                            var mask = StarMasks[Math.Clamp(r - 1, 0, StarMasks.Length - 1)];

                                            img_star_area.SetRegionClipped(yc_offset, xc_offset, mask);

                                            // Centred on the ROUNDED centroid, which is the same
                                            // quantity CentroidAlreadyClaimed tests, so the claim is
                                            // exactly "within claimRadius px of an accepted star".
                                            // The footprint stamp above keeps its historical
                                            // round(c - r) + r form, off by up to a pixel; a claim
                                            // this small cannot afford that.
                                            var claimRadius = Math.Max(1, (int)MathF.Round(ClaimFactor * star.HFD));
                                            var claimMask = claimRadius == 1
                                                ? ClaimMinMask
                                                : StarMasks[Math.Clamp(claimRadius - 1, 0, StarMasks.Length - 1)];
                                            img_star_claim.SetRegionClipped(
                                                (int)MathF.Round(star.YCentroid) - claimRadius,
                                                (int)MathF.Round(star.XCentroid) - claimRadius,
                                                claimMask);
                                        }
                                    }
                                }
                            }
                        }
                        if (passCounters is not null)
                        {
                            Interlocked.Add(ref passCounters.ThresholdHits, localHits);
                            Interlocked.Add(ref passCounters.AnalyseStarCalls, localCalls);
                            Interlocked.Add(ref passCounters.Accepted, localAccepted);
                        }
                    });
                }
            }, cancellationToken);

            if (passCounters is not null)
            {
                var passElapsed = Stopwatch.GetElapsedTime(passStart);
                logger!.LogDebug(
                    "Image.FindStarsAsync: pass={Pass} detectionLevel={DL:F3} thresholdHits={Hits} analyseStarCalls={Calls} accepted={Accepted} cumulative={Cum} retriesRemaining={Retries} in {Ms:F0}ms",
                    passNumber, passDetectionLevel, passCounters.ThresholdHits, passCounters.AnalyseStarCalls,
                    passCounters.Accepted, starList.Count, retries, passElapsed.TotalMilliseconds);
            }

            /* In principle not required. Try again with lower detection level */
            if (detection_level <= 7 * noise_level)
            {
                retries = -1; /* stop */
            }
            else
            {
                retries--;
                detection_level = MathF.Max(6.999f * noise_level, MathF.Min(30 * noise_level, detection_level * 6.999f / 30)); /* very high -> 30 -> 7 -> stop.  Or  60 -> 14 -> 7.0. Or for very short exposures 3.5 -> stop */
            }
        } while (starList.Count < minStars && retries > 0);/* reduce detection level till enough stars are found. Note that faint stars have less positional accuracy */

        return new StarList(starList, img_star_area);
    }

    /// <summary>
    /// True when this candidate's CENTROID already lies inside the accepted-star area, i.e. it is a
    /// re-detection of a star that has already been recorded.
    ///
    /// <para>The area consulted is the TIGHT claim mask (<see cref="ClaimFactor"/>, a half-flux
    /// radius), not the footprint mask that gates the trigger scan. Testing the centroid against the
    /// footprint was the merge bug: at <c>1.5 * HFD</c> a genuine companion's own centroid lands inside
    /// its bright neighbour's area and is discarded as a re-detection.</para>
    ///
    /// <para><b>Why the trigger pixel is not enough.</b> The loop skips pixels already inside
    /// <c>img_star_area</c>, but that area is only <c>HfdFactor * HFD</c> in radius, and HFD
    /// systematically UNDERSTATES a saturated star's footprint: a flat-topped core has a small
    /// half-flux diameter while its halo stays above the detection threshold far outside the mask. So
    /// a halo pixel passes the trigger check, <see cref="AnalyseStar"/> runs, and its centre of
    /// gravity lands back on the same core, which is then recorded again; each copy re-marks the same
    /// small region, so the halo is never covered and the whole ring of it re-detects. Measured on one
    /// SV605CC frame: a single mag-bright star produced the entire top 100 by flux, all at
    /// (1473.93, 2984.91), 1 distinct position out of 100.
    /// </para>
    ///
    /// <para>Testing the resulting centroid instead closes it without widening the footprint mask
    /// (which would swallow genuine close pairs) and without a second dedup pass over the star list.
    /// The cost is one bit read per accepted candidate.</para>
    ///
    /// <para>Consequences of NOT doing this, all of which were live: star count inflated (3,569 vs
    /// 3,349 distinct on that frame) so the dataset quality gate mis-ranks frames; median HFD pulled
    /// toward whichever star duplicated most; the PSF field-radius profile weights one star hundreds
    /// of times; and brightest-K quad selection degenerates, because <see cref="StarQuadList"/>
    /// discards quads whose centroids sit within 1 px of each other, so a duplicated top-K collapses
    /// to a single quad and registration fails outright.</para>
    ///
    /// <para>Races are benign and pre-existing: the interleaved two-phase chunk scheme already means
    /// a neighbouring chunk may be marking the mask concurrently. A missed read costs one duplicate,
    /// never a wrong star.</para>
    /// </summary>
    private static bool CentroidAlreadyClaimed(BitMatrix starArea, in ImagedStar star, int width, int height)
    {
        var cx = (int)MathF.Round(star.XCentroid);
        var cy = (int)MathF.Round(star.YCentroid);
        return cx >= 0 && cx < width && cy >= 0 && cy < height && starArea[cy, cx];
    }

    /// <summary>
    /// True when every one of the eight neighbours is STRICTLY lower, i.e. this pixel could be a
    /// star's own peak rather than a point on someone else's wing.
    /// </summary>
    /// <remarks>
    /// <para><b>What it is for.</b> It is the escape hatch on the footprint mask's trigger skip: a
    /// pixel inside an already-measured star's area is analysed anyway if it looks like its own peak.
    /// Without it, a companion close enough to sit inside a bright star's mask is never even measured,
    /// and the two are reported as one star. Correctness of the dedup does not rest on this test --
    /// <see cref="CentroidAlreadyClaimed"/> still has the final say, against the tight claim mask -- so
    /// a false positive here costs one wasted <see cref="AnalyseStar"/> call and nothing else.</para>
    /// <para><b>Why it is nearly free despite sitting in the hottest loop.</b> Three things, and the
    /// third is the one that matters. It runs only for pixels already inside a footprint; it exits on
    /// the FIRST neighbour that is not lower, which on a monotonically decaying PSF wing is the first
    /// or second read because the neighbour towards the core is brighter; and the CALL SITE asks it
    /// only for pixels outside the claim disc. Without that last exclusion the escape re-analysed every
    /// accepted star's own peak pixel -- the star is accepted from whichever wing pixel the row scan
    /// reached first, so its true peak is still ahead and is always a strict maximum -- which is
    /// roughly one wasted <see cref="AnalyseStar"/> call per star. Measured on the RGGB fixture
    /// (Debug), analysed candidates over both passes: 5,062 with no escape at all, <b>8,270</b> with
    /// the escape but no claim exclusion (+63%, pass wall time 58 -> 75 ms), and <b>5,126</b> as it
    /// stands (+1.3%, 59 ms). For scale, the <c>Background</c> call that precedes the passes costs
    /// 140 ms on that frame on its own.</para>
    /// <para>A trigger pixel inside the claim disc is that star's own core by construction, so a
    /// centroid measured from it is dominated by the star that already claimed it. Two of the closest
    /// pairs seen while measuring this were accepted only because the companion happened to be scanned
    /// BEFORE the brighter star, i.e. before any claim existed -- order-dependent acceptances inside
    /// the band the design calls unresolvable, which excluding the disc also removes.</para>
    /// <para><b>Widening the window buys nothing.</b> Measured at 5x5: 8,249 analysed candidates
    /// against 8,270, the same 3,016 stars and the same resolved pairs. The extra candidates are not
    /// single-pixel noise spikes a wider window would reject -- detection runs on a mono debayer, whose
    /// 2x2 quad means make the noise correlated and its maxima broad.</para>
    /// <para><b>Strict, deliberately.</b> Requiring strictly-lower neighbours disqualifies a SATURATED
    /// flat top by construction: every pixel of a clipped plateau has an equal neighbour, so a bright
    /// star's core generates no escapes at all, which is where a tolerant comparison would have spent
    /// its budget. A NaN neighbour does not disqualify, since every comparison against NaN is false;
    /// NaN pixels are marked into the footprint mask separately and never reach this test.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IsStrictLocalMaximum(float[,] channelData, int x, int y, int width, int height)
    {
        if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1)
        {
            return false;
        }

        var v = channelData[y, x];
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx | dy) != 0 && channelData[y + dy, x + dx] >= v)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Measures one star candidate: HFD, FWHM, SNR, flux and the flux-weighted centroid
    /// (<c>xc</c>, <c>yc</c>). All coordinates are zero-based array positions.
    /// </summary>
    /// <remarks>
    /// <para><b>Attribution.</b> The measurement approach followed here (a median-plus-MAD
    /// background from an annulus outside the aperture, a 3-sigma signal gate, a flux-weighted
    /// centroid with the aperture shrunk until the star is boxed, a radial signal histogram to
    /// size the aperture, and the flux-weighted HFD approximation) is the method Han Kleijn
    /// documented for ASTAP (<see href="https://www.hnsky.org/astap.htm"/>), which in turn
    /// credits Kazuhisa Miyashita for the HFD approximation
    /// (<see href="https://astro-limovie.info/occultation_observation/halffluxdiameter/halffluxdiameter_en.html"/>).
    /// Credit for the method belongs there; see the repository <c>NOTICE</c> file, and
    /// <c>astap-readme.txt</c> at the repository root for ASTAP's own copyright and LGPL-3.0-or-later
    /// notice. ASTAP as a plate *solver* is a separate optional external program here
    /// (<c>AstapPlateSolver</c>), invoked as a process and never linked.</para>
    /// <para><b>FWHM departs from that method deliberately</b> and is measured from an
    /// interpolated radial half-maximum crossing rather than a count of pixels above half
    /// maximum; see <see cref="HalfMaxDiameter"/> for why.</para>
    /// </remarks>
    /// <param name="x1">x</param>
    /// <param name="y1">y</param>
    /// <param name="boxRadius">box radius</param>
    /// <returns>true if a star was detected</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool AnalyseStar(int channel, int x1, int y1, int boxRadius, out ImagedStar star)
    {
        const int maxAnnulusBg = 328; // depends on boxSize <= 50
        Debug.Assert(boxRadius <= 50, nameof(boxRadius) + " should be <= 50 to prevent runtime errors");

        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }

        var r1_square = boxRadius * boxRadius; /*square radius*/
        var r2 = boxRadius + 1; /*annulus width plus 1*/
        var r2_square = r2 * r2;

        var valMax = 0.0f;
        float sumVal;
        float bg;
        float sd_bg;

        float xc = float.NaN, yc = float.NaN;
        int r_aperture = -1;

        if (x1 - r2 <= 0 || x1 + r2 >= width - 1 || y1 - r2 <= 0 || y1 + r2 >= height - 1)
        {
            star = default;
            return false;
        }

        var channelData = Planes[channel].Data;
        Span<float> backgroundScratch = stackalloc float[maxAnnulusBg];
        int backgroundIndex = 0;

        try
        {
            /*calculate the mean outside the the detection area*/
            for (var i = -r2; i <= r2; i++)
            {
                for (var j = -r2; j <= r2; j++)
                {
                    var distance = i * i + j * j; /*working with sqr(distance) is faster then applying sqrt*/
                    /*annulus, circular area outside rs, typical one pixel wide*/
                    if (distance > r1_square && distance <= r2_square)
                    {
                        var value = channelData[y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            backgroundScratch[backgroundIndex++] = value;
                        }
                    }
                }
            }

            var background = backgroundScratch[..backgroundIndex];
            // Use the O(n) quickselect variant: AnalyseStar runs once per
            // candidate pixel (~6500 stars/frame on the runner cfg) and each
            // call medians an annulus buffer up to 328 floats. We don't care
            // that the buffer ends up unsorted -- the next line overwrites
            // every element with its absolute-deviation form.
            bg = MedianFast(background);

            /* fill background with absolute deviations from median */
            for (var i = 0; i < background.Length; i++)
            {
                background[i] = MathF.Abs(background[i] - bg);
            }

            //median absolute deviation (MAD)
            var mad_bg = MedianFast(background);
            sd_bg = mad_bg * MAD_TO_SD;

            // Guard against zero-noise backgrounds (e.g. JPGs from nova.astrometry.net, or uniform synthetic data).
            // Without a minimum noise floor, every background pixel would be classified as signal.
            // The pedestal (if set) provides a scale-aware minimum that survives normalization.
            if (sd_bg == 0)
            {
                sd_bg = pedestal > 0 ? pedestal : 1f;
            }

            // reduce square annulus radius until it is symmetric to remove stars
            bool boxed;
            do
            {
                // Flux-weighted centroid over the signal pixels in the box, plus a count of how
                // many were illuminated. The enclosing loop shrinks the box until the star is
                // "boxed" (fill test below), which is what keeps a neighbour out of the aperture.
                sumVal = 0.0f;
                var sumValX = 0.0f;
                var sumValY = 0.0f;
                var signal_counter = 0;

                for (var i = -boxRadius; i <= boxRadius; i++)
                {
                    for (var j = -boxRadius; j <= boxRadius; j++)
                    {
                        var value = channelData[y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0f * sd_bg)
                            {
                                sumVal += bg_sub_value;
                                sumValX += bg_sub_value * j;
                                sumValY += bg_sub_value * i;
                                signal_counter++; /* how many pixels are illuminated */
                            }
                        }
                    }
                }

                if (sumVal <= 12 * sd_bg)
                {
                    star = default; /*no star found, too noisy */
                    return false;
                }

                var xg = sumValX / sumVal;
                var yg = sumValY / sumVal;

                xc = x1 + xg;
                yc = y1 + yg;
                /* center of gravity found */

                if (xc - boxRadius < 0 || xc + boxRadius > width - 1 || yc - boxRadius < 0 || yc + boxRadius > height - 1)
                {
                    star = default; /* prevent runtime errors near sides of images */
                    return false;
                }

                var rs2_1 = boxRadius + boxRadius + 1;
                /* Fill test: at least 2/9 of the box illuminated. A fill fraction separates one
                   boxed star from a crowded box more reliably than an ovality measure does. */
                boxed = signal_counter >= 2.0f / 9 * (rs2_1 * rs2_1);

                if (!boxed)
                {
                    if (boxRadius > 4)
                    {
                        boxRadius -= 2;
                    }
                    else
                    {
                        boxRadius--; /*try a smaller window to exclude nearby stars*/
                    }
                }

                /* check on hot pixels */
                if (signal_counter <= 1)
                {
                    star = default; /*one hot pixel*/
                    return false;
                }
            } while (!boxed && boxRadius > 1); /*loop and reduce aperture radius until star is boxed*/

            boxRadius += 2; /* add some space */

            // Build signal histogram from center of gravity
            Span<int> distance_histogram = stackalloc int[boxRadius + 1]; // this has a fixed upper bound

            for (var i = -boxRadius; i <= boxRadius; i++)
            {
                for (var j = -boxRadius; j <= boxRadius; j++)
                {
                    var distance = (int)MathF.Round(MathF.Sqrt(i * i + j * j)); /* distance from gravity center */
                    if (distance <= boxRadius) /* build histogram for circle with radius boxRadius */
                    {
                        var value = SubpixelValue(channel, xc + i, yc + j);
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0 * sd_bg) /* 3 * sd should be signal */
                            {
                                distance_histogram[distance]++; /* build distance histogram up to circle with diameter rs */

                                if (bg_sub_value > valMax)
                                {
                                    valMax = bg_sub_value; /* record the peak value of the star */
                                }
                            }
                        }
                    }
                }
            }

            var distance_top_value = 0;
            var histStart = false;
            var illuminated_pixels = 0;
            do
            {
                r_aperture++;
                illuminated_pixels += distance_histogram[r_aperture];
                if (distance_histogram[r_aperture] > 0)
                {
                    /* Only start hunting for the outer edge once some signal has been seen: a
                       defocused star imaged through a central obstruction is dark in the middle,
                       so an empty innermost bin is not the edge. */
                    histStart = true;
                }

                if (distance_top_value < distance_histogram[r_aperture])
                {
                    /* Peak annulus population, which approaches 2*pi*r for an evenly illuminated
                       defocused disk; the loop exit compares later annuli against it. */
                    distance_top_value = distance_histogram[r_aperture];
                }
                /* find a distance where there is no pixel illuminated, so the border of the star image of interest */
            } while (r_aperture < boxRadius && (!histStart || distance_histogram[r_aperture] > 0.1f * distance_top_value));

            if (r_aperture >= boxRadius)
            {
                star = default; /* star is equal or larger then box, abort */
                return false;
            }

            if (r_aperture > 2)
            {
                /* if more than 35% surface is illuminated */
                var r_aperture2_2 = 2 * r_aperture - 2;
                if (illuminated_pixels < 0.35f * (r_aperture2_2 * r_aperture2_2))
                {
                    star = default; /* not a star disk but stars, abort */
                    return false;
                }
            }
        }
        catch
        {
            star = default;
            return false;
        }

        // Get HFD, the radial profile (for FWHM) and second-order moments (for ellipticity).
        sumVal = 0.0f; // reset
        var sumValR = 0.0f;
        // Second moments accumulate only positive-flux pixels around
        // the centroid. Doubles because i*i*val products on a bright
        // star can overflow float precision before normalisation.
        double sumPosFlux = 0, sumMxx = 0, sumMyy = 0, sumMxy = 0;

        // Azimuthally averaged radial profile, as flux and weight per integer radius. Bins
        // 0..r_aperture are complete annuli (a circle of radius <= r_aperture fits inside the
        // square being walked); the one extra bin catches the fractional spill from the outermost
        // samples and is corner-only, so it is used to interpolate against but never as a result.
        Span<float> profileFlux = stackalloc float[r_aperture + 2];
        Span<float> profileWeight = stackalloc float[r_aperture + 2];
        // Redundant today (no [SkipLocalsInit] in this assembly, so stackalloc is zeroed) and kept
        // deliberately: both spans are accumulated into, never fully written, so adding that
        // attribute for perf later would otherwise start every star from uninitialised stack.
        profileFlux.Clear();
        profileWeight.Clear();

        // The centroid is a sub-pixel position, so every sample is a sub-pixel interpolation
        // rather than a pixel lookup.
        for (var i = -r_aperture; i <= r_aperture; i++)
        {
            for (var j = -r_aperture; j <= r_aperture; j++)
            {
                var val = SubpixelValue(channel, xc + i, yc + j) - bg;
                var r = MathF.Sqrt(i * i + j * j); /* distance from the centroid */
                sumVal += val;      /* total star flux */
                sumValR += val * r; /* flux-weighted radius; the HFD approximation below inverts it */

                // Split each sample between the two bracketing integer radii in proportion to its
                // fractional radius. That makes the profile piecewise-linear in r instead of a
                // histogram, which is what lets the half-maximum crossing be interpolated: an
                // integer bin count is exactly what used to quantise FWHM.
                var rFloor = (int)r;
                if (rFloor <= r_aperture)
                {
                    var frac = r - rFloor;
                    profileFlux[rFloor] += val * (1f - frac);
                    profileWeight[rFloor] += 1f - frac;
                    profileFlux[rFloor + 1] += val * frac;
                    profileWeight[rFloor + 1] += frac;
                }

                if (val > 0f)
                {
                    // SubpixelValue takes (channel, x, y), so i = dx, j = dy.
                    sumPosFlux += val;
                    sumMxx += (double)val * i * i;
                    sumMyy += (double)val * j * j;
                    sumMxy += (double)val * i * j;
                }
            }
        }

        var flux = MathF.Max(sumVal, 0.00001f); /* prevent dividing by zero or negative values */
        var hfd = MathF.Max(0.7f, 2 * sumValR / flux);
        var star_fwhm = HalfMaxDiameter(profileFlux, profileWeight, valMax * 0.5f, r_aperture);

        // Moment-based ellipticity from the 2x2 flux-weighted second-
        // moment matrix [[Mxx, Mxy], [Mxy, Myy]] (each normalised by
        // total positive flux). Eigenvalues a², b² are the variances
        // along major/minor axes; ellipticity e = sqrt(1 - b²/a²) gives
        // 0 for a round star, ~0.866 for 2:1 elongation, → 1 for a line.
        // Clamped to [0, 1] to absorb rounding artefacts on near-circular
        // fits where b² can land slightly negative.
        var ellipticity = 0f;
        if (sumPosFlux > 0)
        {
            var mxx = sumMxx / sumPosFlux;
            var myy = sumMyy / sumPosFlux;
            var mxy = sumMxy / sumPosFlux;
            var halfTrace = (mxx + myy) * 0.5;
            var halfDiff = (mxx - myy) * 0.5;
            var disc = Math.Sqrt(halfDiff * halfDiff + mxy * mxy);
            var a2 = halfTrace + disc;
            var b2 = halfTrace - disc;
            if (a2 > 1e-10)
            {
                var ratio = Math.Max(0.0, b2 / a2);
                ellipticity = (float)Math.Sqrt(Math.Max(0.0, 1.0 - ratio));
            }
        }

        // SNR for the shot-noise-limited and sky-limited cases together:
        //   snr = flux / sqrt(flux + r^2 * pi * sd^2)
        // where flux is the signal above 3*sd and the second term is the background variance over
        // the measurement aperture. Assumes Poisson statistics on ADU counts at unity gain
        // (ADU/e- = 1); see https://en.wikipedia.org/wiki/Signal-to-noise_ratio_(imaging) .
        // For [0,1]-normalised images, flux and sd are scaled back to the ADU range first, or the
        // shot-noise term would be meaningless.
        // Scale-invariant SNR: unit-referred samples are lifted to ADU so the thresholds below mean
        // the same thing on a [0,1] master as on a raw ADU frame. Asks Image for the verdict -- this
        // was a THIRD spelling of it, and being the one inside AnalyseStar it is the one that decides
        // whether a star survives, so a frame could pass the histogram gate and still detect nothing.
        var aduScale = HasUnitScalePeak ? ushort.MaxValue : 1.0f;
        var aduFlux = flux * aduScale;
        var aduSdBg = sd_bg * aduScale;
        var snr = aduFlux / MathF.Sqrt(aduFlux + r_aperture * r_aperture * MathF.PI * aduSdBg * aduSdBg);

        star = new ImagedStar(hfd, star_fwhm, snr, flux, xc, yc, ellipticity, bg);
        return true;
    }

    /// <summary>
    /// Full width at half maximum, as twice the radius at which the azimuthally averaged radial
    /// profile falls through <paramref name="halfMax"/>, linearly interpolated between the two
    /// bracketing integer radii. Returns 0 when no radius is above half maximum (nothing
    /// measurable), and the aperture diameter when the profile never descends through it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not the area of the above-half-maximum pixels.</b> Counting samples above half
    /// maximum and inverting the disc area (<c>2*sqrt(count/pi)</c>, the classical recipe, and what
    /// this measured until 2026-08-11) makes FWHM a function of an INTEGER, so it can only ever
    /// take the values <c>2*sqrt(n/pi)</c>: 2.257, 2.523, 2.764, 2.985, 3.192, ... A step of about
    /// 0.2 px at the sizes a typical rig delivers is coarser than the seeing variation being
    /// measured, and the effect is not subtle. Measured over 5,984 registered subs of the 2025-2026
    /// archive, the per-sub median FWHM was the SAME NUMBER (2.523 px, i.e. n = 5) at the 5th, 25th,
    /// 50th and 75th percentile, and the field-radius profile it feeds could not resolve
    /// centre-to-corner PSF growth on four of five optical trains. HFD, being flux-weighted, was
    /// continuous over the same population (2.313 to 3.735 px).</para>
    /// <para>Interpolating a crossing instead costs one extra pair of accumulators, reuses the
    /// radius the HFD sum already computes, and yields a continuous estimate.</para>
    /// <para><b>Scanning outward for the OUTERMOST radius above half maximum is load-bearing.</b> A
    /// heavily defocused star imaged through a central obstruction has an annular profile that dips
    /// in the middle, so the first descending crossing would report the central hole rather than the
    /// star. Taking the last bin above half maximum measures the outer width, which is what FWHM
    /// means for such a profile.</para>
    /// </remarks>
    private static float HalfMaxDiameter(ReadOnlySpan<float> profileFlux, ReadOnlySpan<float> profileWeight, float halfMax, int rAperture)
    {
        var last = -1;
        for (var k = 0; k <= rAperture; k++)
        {
            if (profileWeight[k] > 0f && profileFlux[k] / profileWeight[k] > halfMax)
            {
                last = k;
            }
        }

        if (last < 0)
        {
            // Nothing above half maximum inside the aperture. Reachable because valMax is the peak
            // over the larger box, which can belong to a neighbour outside r_aperture; the previous
            // pixel-count form returned 0 here too, and consumers already filter on FWHM > 0.
            return 0f;
        }

        var inner = profileFlux[last] / profileWeight[last];
        if (profileWeight[last + 1] <= 0f)
        {
            return 2f * rAperture;
        }

        var outer = profileFlux[last + 1] / profileWeight[last + 1];
        var drop = inner - outer;
        if (drop <= 0f)
        {
            // Profile still at or above half maximum at the aperture edge: the star fills the
            // aperture, so the aperture diameter is the best available lower bound.
            return 2f * rAperture;
        }

        return 2f * (last + (inner - halfMax) / drop);
    }
}
