using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The comet's own light, isolated, so it can be SUBTRACTED from every frame instead of the frames
/// being thrown away wherever it happens to be.
///
/// <para><b>Why subtraction rather than the mask in <see cref="CometMask"/>.</b> Excluding a disc
/// works only when the body moves much further than its coma is wide, and measurement says that is
/// rarely true. On C/2025 R2 (SWAN) the smear reaches 165 px against 357 px of travel, so covering it
/// costs <c>2*165/357</c> = 92% of the session at the track centreline -- and stopping short at 84 px
/// left the wings untouched (removal fell to exactly 0.00 beyond 90 px while the coma was still
/// 0.38 sigma there) as two bars flanking the track. On 10P/Tempel 2 the body moves **45 px in 3.5
/// hours**, so every radius past 23 px masks 100% of the session and there is nothing left to stack
/// at all. Masking is not merely worse there; it is arithmetically impossible.
///
/// Subtraction has no such geometry. Every frame survives, so there is no coverage hole, no noise
/// band, no hard edge and no bars, and the tail and the faint wings come out because they are IN the
/// model rather than approximated by a circle.</para>
///
/// <para><b>Where the model comes from, and the irony in it.</b> A star remover run on a
/// COMET-ALIGNED plate removes the comet: the coma is the only compact source there, because every
/// real star is a long streak. That was the original bug in this whole area -- <c>sxt</c> eating the
/// nucleus -- and it is the most reliable comet extractor available, simply pointed at the plate
/// where the behaviour is what you want. Measured on SWAN: differencing the comet-aligned master
/// against its own star-removed self recovers 100% of the comet out to 120 px (35.4 sigma at the
/// core, 101% at r=25-50, 108% at r=80-120) while leaking NO star trails -- the fraction of pixels
/// above 1 sigma at r=600-1300 is 0.0000.</para>
///
/// <para><b>The one correction that difference needs.</b> A constant pedestal separates the two
/// plates (+0.20 sigma on SWAN), which is invisible locally and dominates once integrated: 90% of
/// the positive flux in the raw difference sits outside r=170, i.e. it is all pedestal spread over
/// millions of pixels. It is removed as a far-field median before the model is usable.</para>
/// </summary>
internal sealed class CometModel
{
    private readonly float[][,] _planes;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Where the body sits inside <see cref="_planes"/>, in model pixels.</summary>
    private readonly Vector2 _centre;

    /// <summary>Beyond this the model is consistent with zero and is not worth sampling.</summary>
    public float ReachPx { get; }

    private CometModel(float[][,] planes, int width, int height, Vector2 centre, float reachPx)
    {
        _planes = planes;
        _width = width;
        _height = height;
        _centre = centre;
        ReachPx = reachPx;
    }

    /// <summary>
    /// Builds the model from a comet-aligned master by removing what the star remover calls stars
    /// (on this plate, the comet) and keeping the difference.
    /// </summary>
    /// <param name="cometMaster">The comet-aligned integration, linear.</param>
    /// <param name="alreadyStarless">True when that integration was built from per-frame star-removed
    /// plates (<c>--remove-stars</c>), in which case it IS the comet and nothing is differenced.</param>
    /// <param name="centreInMaster">The body's position in that master's pixels.</param>
    /// <param name="trailDirection">The drift vector. On a comet-aligned plate every star streaks
    /// along it, which is what makes the residue removable by shape.</param>
    /// <param name="remover">Any <see cref="IStarRemover"/>; the RC / SAS choice does not matter here.</param>
    /// <returns><c>null</c> when the remover declines or the difference holds no comet, which must
    /// leave the caller free to fall back rather than emit a layer with nothing subtracted.</returns>
    public static async Task<CometModel?> TryBuildAsync(
        Image cometMaster,
        bool alreadyStarless,
        Vector2 centreInMaster,
        Vector2 trailDirection,
        IStarRemover remover,
        ILogger logger,
        CancellationToken ct)
    {
        var w = cometMaster.Width;
        var h = cometMaster.Height;
        var channels = cometMaster.ChannelCount;
        var cx = (int)MathF.Round(centreInMaster.X);
        var cy = (int)MathF.Round(centreInMaster.Y);

        // Crop to a box around the body BEFORE the remover sees it, for two reasons.
        //
        // The load-bearing one: a comet-aligned canvas carries NaN wherever the frames do not all
        // overlap, and RC-Astro answers an all-NaN plate for an input holding any -- the whole image,
        // not just the uncovered part. SharpenPipeline already guards this way for the same reason.
        // Differencing that gives NaN everywhere, which this code then floored to zero and reported
        // as "the difference holds no comet", blaming the remover while sxt had in fact run licensed,
        // on the GPU, to completion. A box around the body sits well inside the covered region.
        //
        // The cheap one: the model only has to describe the comet, so this is roughly seven times
        // less work for the remover than the full canvas, and every later sample stays in cache.
        var half = Math.Min(Math.Min(cx, w - 1 - cx), Math.Min(cy, h - 1 - cy));
        half = Math.Min(half, 600);
        if (half < 40)
        {
            logger.LogWarning("  [comet] the body sits {Half} px from the master's edge; too close to model", half);
            return null;
        }

        var size = half * 2;
        var cropPlanes = new float[channels][,];
        var nanFilled = 0;
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            // Any NaN still inside the box is filled with the box median rather than left to poison
            // the remover. Rare this close to the centre, and cheap to be certain about.
            var finiteSum = 0.0;
            var finiteN = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var v = cometMaster[c, cy - half + y, cx - half + x];
                    plane[y, x] = v;
                    if (float.IsFinite(v)) { finiteSum += v; finiteN++; }
                }
            }
            var fill = finiteN > 0 ? (float)(finiteSum / finiteN) : 0f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (!float.IsFinite(plane[y, x])) { plane[y, x] = fill; nanFilled++; }
                }
            }
            cropPlanes[c] = plane;
        }

        var cropMin = float.MaxValue;
        var cropMax = float.MinValue;
        foreach (var p in cropPlanes)
        {
            foreach (var v in p)
            {
                if (v < cropMin) { cropMin = v; }
                if (v > cropMax) { cropMax = v; }
            }
        }
        var rawCrop = new Image(cropPlanes, cometMaster.BitDepth, cropMax, cropMin, 0f, cometMaster.ImageMeta);

        // The clean path. A comet layer stacked from per-frame star-removed plates holds the comet and
        // nothing else, so the model is simply that -- no star remover, no difference, no trail
        // residue to chase afterwards. Everything below this branch exists only because a comet layer
        // built from ORDINARY frames has star trails in it, and taking them back out is a losing game:
        // on a comet-aligned plate every star IS a trail, the remover takes them along with the comet,
        // and each one that survives into the model is subtracted at 89 positions into a dark streak.
        if (alreadyStarless)
        {
            var starlessPlanes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[size, size];
                Array.Copy(cropPlanes[c], plane, cropPlanes[c].Length);
                starlessPlanes[c] = plane;
            }
            var ped = SubtractFarFieldPedestal(starlessPlanes, half);
            _ = ped;
            var n0 = EstimateFarFieldSigma(starlessPlanes, half);
            SmoothWingsInPolarBins(starlessPlanes, half, trailDirection);
            var n1 = EstimateFarFieldSigma(starlessPlanes, half);
            var pk = RadialMedian(starlessPlanes, half, 0, 20);
            var fl = MathF.Max(0.01f * pk, 2f * n1);
            var rr2 = 0f;
            for (var rr = 20; rr < half; rr += 10)
            {
                if (RadialMedian(starlessPlanes, half, rr, rr + 10) <= fl) { break; }
                rr2 = rr + 10;
            }
            if (rr2 < 20f)
            {
                logger.LogWarning(
                    "  [comet] the starless comet master holds no comet at ({Cx}, {Cy}): peak {Peak:F6}, "
                        + "floor {Floor:F6}, noise {Noise:F6}", cx, cy, pk, fl, n1);
                return null;
            }
            logger.LogInformation(
                "  [comet] model taken DIRECTLY from the starless comet layer: {Size}x{Size} px, "
                    + "reach {Reach:F0} px (peak {Peak:F6}, floor {Floor:F6}, noise {Noise:F6} raw {Raw:F6})",
                size, size, rr2, pk, fl, n1, n0);
            return new CometModel(starlessPlanes, size, size, new Vector2(half, half), rr2);
        }

        // Normalise to the integrator's own target before the remover sees it. A star remover is a
        // neural net and cares where its input sits in [0,1], not merely that the SNR is good.
        //
        // This is not hypothetical. The comet layer is auto-picked as BayerDrizzle, which does no
        // per-frame normalisation, so its master's background sits at 0.0145; the plate this
        // technique was proven on was an InRamAllFrames master, normalised to a background of 0.5.
        // Handed the un-normalised crop, sxt found only the very peak (crop max 0.063 -> 0.044) and
        // left the whole coma: radial medians ran 0.000028 at r=20 against a noise floor of 0.000077,
        // i.e. nothing. The per-frame star-removal path already rescales for the same reason.
        //
        // The absolute scale is free to change here because the per-frame amplitude is FITTED later
        // rather than derived, so any linear factor is absorbed by FitScale.
        var crop = Normalizer.Apply(rawCrop, Normalizer.ComputeStats(rawCrop), 0.5f);
        for (var c = 0; c < channels; c++)
        {
            cropPlanes[c] = crop.GetChannelArray(c);
        }

        Image cometless;
        try
        {
            cometless = await remover.EnhanceAsync(crop, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "  [comet] the star remover failed on the comet-aligned crop");
            return null;
        }

        if (cometless.Width != size || cometless.Height != size || cometless.ChannelCount != channels)
        {
            logger.LogWarning(
                "  [comet] the star remover returned {W}x{H}x{C} for a {S}x{S}x{MC} crop; cannot difference them",
                cometless.Width, cometless.Height, cometless.ChannelCount, size, channels);
            return null;
        }

        // Both plates' levels, and whether they are even distinct objects. "The difference holds no
        // comet" has at least three unrelated causes -- the remover handed back its input, it handed
        // back NaN, or the crop landed off the body -- and the symptom alone tells them apart not at
        // all. That cost one wrong diagnosis already.
        logger.LogDebug(
            "  [comet] model inputs: raw crop {CMin:F6}..{CMax:F6} ({NaN} NaN filled) -> normalised "
                + "{NMin:F6}..{NMax:F6} | star-removed {SMin:F6}..{SMax:F6} | same instance: {Same}",
            cropMin, cropMax, nanFilled, crop.MinValue, crop.MaxValue,
            cometless.MinValue, cometless.MaxValue, ReferenceEquals(crop, cometless));
        if (!float.IsFinite(cometless.MinValue) || !float.IsFinite(cometless.MaxValue))
        {
            logger.LogWarning(
                "  [comet] the star remover answered a non-finite plate ({Min}..{Max}); cannot build a model from it",
                cometless.MinValue, cometless.MaxValue);
            return null;
        }

        var planes = new float[channels][,];
        // Far-field pedestal, per channel: the median in an annulus outside any plausible coma. A
        // constant offset between the two plates is invisible locally and dominates once integrated
        // -- 90% of the raw difference's positive flux was pedestal spread over millions of pixels.
        var pedestal = new float[channels];
        var reach = 0f;

        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            var far = new System.Collections.Generic.List<float>(4096);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var d = cropPlanes[c][y, x] - cometless[c, y, x];
                    plane[y, x] = float.IsFinite(d) ? d : 0f;
                    var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                    // Strided, NOT first-N-encountered: a raw cap fills from the first rows scanned,
                    // which is the top edge of the crop rather than a ring around the body, and the
                    // pedestal then carries whatever gradient happens to live up there.
                    if (r > half * 0.75f && ((x & 3) | (y & 3)) == 0 && float.IsFinite(d))
                    {
                        far.Add(d);
                    }
                }
            }
            if (far.Count > 64)
            {
                far.Sort();
                pedestal[c] = far[far.Count / 2];
            }
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    plane[y, x] -= pedestal[c];
                }
            }
            planes[c] = plane;
        }

        // Smooth the wings in POLAR bins before measuring reach. The raw difference carries the
        // noise of two plates, and past ~60 px the coma sits under it -- so an unsmoothed model has
        // to be truncated there, which leaves exactly the wings this method exists to remove. That
        // noise does NOT average away downstream either: the same model is subtracted from every
        // frame, so its error enters the master coherently rather than as 1/sqrt(N).
        //
        // A coma is close to azimuthally symmetric, so averaging within a radius/angle cell buys
        // sqrt(cell) in noise for almost no loss of shape -- at r=100 a 15-degree cell holds ~500 px,
        // about 23x. Angle bins are kept narrow enough to preserve a tail, which is a real asymmetry
        // rather than noise (measured on SWAN at PA 160-180, out to 250-350 px).
        // Noise measured BEFORE smoothing, and used for the reach test AFTER it. Smoothing reduces
        // the noise of the ESTIMATE, not the noise of the plate, and the question reach is asking is
        // "is there comet here" -- against the data, not against how finely we averaged it. Measured
        // against the smoothed scatter (~23x smaller) the test answered 570 px, the whole crop, long
        // past where the coma ends: the control is already -0.07 sigma by 165 px. Everything beyond
        // was contamination, faithfully subtracted, and it dug thin dark streaks to -0.68 sigma.
        var rawNoise = EstimateFarFieldSigma(planes, half);

        // Strip star-trail residue BEFORE smoothing. On a comet-aligned plate every star IS a trail,
        // and the star remover takes them as readily as the comet -- so the difference holds the
        // comet PLUS whatever trail flux went with it. Subtracted at the comet-relative position in
        // all 89 frames, each of those becomes a dark streak in the finished star layer. Measured on
        // the manual pair at r=600-1300: median +0.20 sigma, p99 +0.47 sigma. That p99 is the
        // streaks, and an earlier check passed this plate as clean by asking only for the fraction
        // above 1 sigma, which was 0.0000. The wrong question.
        OpenAcrossTrails(planes, half, trailDirection);

        SmoothWingsInPolarBins(planes, half, trailDirection);

        // Measured AFTER smoothing, so it describes the estimator that reach is actually reading.
        var noise = EstimateFarFieldSigma(planes, half);
        // Relative to the comet's OWN brightness, not to a noise floor. Neither absolute threshold
        // works: the smoothed scatter is ~23x below the plate's, so judging against it ran to 570 px
        // and subtracted contamination; judging against the unsmoothed plate noise stopped at 80 px
        // and left the wings, which are individually below the per-pixel noise yet perfectly real
        // once averaged over an annulus. What ends a coma is the coma -- so follow the profile out
        // until it falls to a small fraction of its own centre, which on SWAN puts the edge at ~150 px
        // where the measured smear does in fact reach zero.
        //
        // First fall ends it rather than the last rise: a coma's profile is monotonic, so anything
        // further out that clears the bar again is not more comet.
        var peak = RadialMedian(planes, half, 0, 20);
        // The RELATIVE term governs and the noise term is only a backstop against the estimator's
        // own floor. Getting that balance wrong either way was the whole difficulty: 0.5x the
        // UNsmoothed noise (0.029 against a 0.014 relative floor) swamped the relative term and cut
        // the wings off at 80 px, while judging against the smoothed noise alone ran to the crop edge
        // at 570 px and subtracted contamination as thin dark streaks.
        var floor = MathF.Max(0.01f * peak, 2f * noise);
        for (var rr = 20; rr < half; rr += 10)
        {
            if (RadialMedian(planes, half, rr, rr + 10) <= floor)
            {
                break;
            }
            reach = rr + 10;
        }
        if (reach < 20f)
        {
            // The profile, not just the verdict. "No comet" can mean the remover left the body in
            // place, that it took the whole plate, or that the pedestal was mis-measured and pushed
            // everything negative -- and those want completely different fixes.
            var profile = new System.Text.StringBuilder();
            for (var rr = 20; rr <= 200; rr += 20)
            {
                profile.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"{rr}:{RadialMedian(planes, half, rr, rr + 20):F6} ");
            }
            logger.LogWarning(
                "  [comet] the star-removed difference holds no comet: noise {Noise:F6}, pedestal {Ped}, "
                    + "radial medians {Profile}",
                noise, string.Join("/", Array.ConvertAll(pedestal, p => p.ToString("F6"))), profile.ToString());
            return null;
        }

        logger.LogInformation(
            "  [comet] model built from {Name}: {Size}x{Size} px, reach {Reach:F0} px "
                + "(peak {Peak:F6}, floor {Floor:F6}, noise {Noise:F6} raw {Raw:F6}), pedestal removed {Ped}",
            remover.Name, size, size, reach, peak, floor, noise, rawNoise,
            string.Join("/", Array.ConvertAll(pedestal, p => p.ToString("F6"))));

        return new CometModel(planes, size, size, new Vector2(half, half), reach);
    }

    /// <summary>
    /// Grey opening (erode then dilate) with a linear structuring element laid ACROSS the trail
    /// direction, applied outside the core.
    /// </summary>
    /// <remarks>
    /// An opening flattens a peak as readily as a streak -- the 10P work measured the comet's
    /// contribution falling 37%, all of it at the middle -- so the core is protected and the effect
    /// faded in, exactly as the smoothing is.
    /// </remarks>
    private static void OpenAcrossTrails(float[][,] planes, int half, Vector2 trailDirection)
    {
        const int RSolid = 25;
        const int RFull = 45;
        const int Len = 11;          // half-length across the trail; a star trail is a few px wide
        // RANK, not min/max. A strict opening biases each pass by roughly two sigma and the two do
        // not cancel, so on faint noisy wings it tracks the noise floor rather than the signal -- it
        // cost the model 490 px of its 570 px reach when tried that way. A low/high percentile pair
        // removes a narrow trail just as well (a trail is a handful of samples out of 23 across it)
        // while the two biases DO cancel for symmetric noise.
        const float LowRank = 0.2f;

        var d = trailDirection.LengthSquared() > 1e-12f
            ? Vector2.Normalize(trailDirection)
            : new Vector2(1f, 0f);
        // Across, not along.
        var ax = -d.Y;
        var ay = d.X;
        var n = planes[0].GetLength(0);

        foreach (var plane in planes)
        {
            var eroded = new float[n, n];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    eroded[y, x] = RankAlongLine(plane, x, y, ax, ay, Len, LowRank);
                }
            }

            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = x - half;
                    var dy = y - half;
                    var r = MathF.Sqrt(dx * dx + dy * dy);
                    if (r < RSolid)
                    {
                        continue;
                    }
                    var v = RankAlongLine(eroded, x, y, ax, ay, Len, 1f - LowRank);
                    var blend = r >= RFull ? 1f : (r - RSolid) / (RFull - RSolid);
                    plane[y, x] = plane[y, x] * (1f - blend) + v * blend;
                }
            }
        }
    }

    /// <summary>Removes the far-field median, per channel, in place. Returns what it removed.</summary>
    private static float[] SubtractFarFieldPedestal(float[][,] planes, int half)
    {
        var removed = new float[planes.Length];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = planes[c];
            var n = plane.GetLength(0);
            var far = new System.Collections.Generic.List<float>(4096);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                    // Strided over the whole ring, never the first N encountered -- a raw cap fills
                    // from the top rows of the crop and carries whatever gradient lives there.
                    if (r > half * 0.75f && ((x & 3) | (y & 3)) == 0 && float.IsFinite(plane[y, x]))
                    {
                        far.Add(plane[y, x]);
                    }
                }
            }
            if (far.Count > 64)
            {
                far.Sort();
                removed[c] = far[far.Count / 2];
            }
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    plane[y, x] -= removed[c];
                }
            }
        }
        return removed;
    }

    /// <summary>Value at <paramref name="rank"/> along a line through (x, y), clamped at the edges.</summary>
    private static float RankAlongLine(float[,] src, int x, int y, float ax, float ay, int len, float rank)
    {
        var n = src.GetLength(0);
        Span<float> buf = stackalloc float[2 * len + 1];
        var m = 0;
        for (var k = -len; k <= len; k++)
        {
            var sx = (int)MathF.Round(x + ax * k);
            var sy = (int)MathF.Round(y + ay * k);
            if ((uint)sx >= (uint)n || (uint)sy >= (uint)n)
            {
                continue;
            }
            buf[m++] = src[sy, sx];
        }
        if (m == 0)
        {
            return src[y, x];
        }
        var used = buf[..m];
        used.Sort();
        return used[Math.Clamp((int)(rank * (m - 1)), 0, m - 1)];
    }

    /// <summary>
    /// Replaces the model outside <c>RSolid</c> with its own median in (radius, angle) cells, faded in
    /// over a short band so no seam appears at the handover.
    /// </summary>
    private static void SmoothWingsInPolarBins(float[][,] planes, int half, Vector2 trailDirection)
    {
        const int RSolid = 25;      // inside this the model is kept verbatim: the core is not smooth
        const int RFull = 45;       // beyond this it is fully smoothed; between the two it fades
        const int RBin = 4;
        const int ABins = 24;       // 15 degrees

        var nR = half / RBin + 2;
        var n = planes[0].GetLength(0);
        foreach (var plane in planes)
        {
            var cells = new System.Collections.Generic.List<float>[nR, ABins];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = x - half;
                    var dy = y - half;
                    var r = MathF.Sqrt(dx * dx + dy * dy);
                    if (r < RSolid || r >= half)
                    {
                        continue;
                    }
                    var ri = (int)(r / RBin);
                    var ai = (int)((MathF.Atan2(dy, dx) + MathF.PI) / (2f * MathF.PI) * ABins) % ABins;
                    if (ri >= nR)
                    {
                        continue;
                    }
                    (cells[ri, ai] ??= new System.Collections.Generic.List<float>(64)).Add(plane[y, x]);
                }
            }

            var med = new float[nR, ABins];
            for (var ri = 0; ri < nR; ri++)
            {
                for (var ai = 0; ai < ABins; ai++)
                {
                    var list = cells[ri, ai];
                    if (list is null || list.Count == 0)
                    {
                        continue;
                    }
                    list.Sort();
                    var m = list[list.Count / 2];
                    if (list.Count < 12)
                    {
                        med[ri, ai] = m;
                        continue;
                    }
                    // Upper-clipped mean, not a bare median. The contamination here is ONE-SIDED --
                    // the remover leaves positive trail flux behind, never negative -- and a trail
                    // crossing a 15-degree cell can be 10-30% of it, enough to drag a median as well
                    // as a mean. Clipping the upper tail and averaging the rest is unbiased for the
                    // symmetric noise and blind to the trails. A grey opening was tried first and
                    // rejected: it uses a local MIN, which on faint noisy wings digs into the noise
                    // floor and cost the model 490 px of its 570 px reach.
                    var mad = 0f;
                    {
                        var devs = new float[list.Count];
                        for (var k = 0; k < list.Count; k++) { devs[k] = MathF.Abs(list[k] - m); }
                        Array.Sort(devs);
                        mad = devs[devs.Length / 2] * 1.4826f;
                    }
                    var hiCut = m + 2f * MathF.Max(mad, 1e-9f);
                    var sum = 0.0;
                    var kept = 0;
                    foreach (var v in list)
                    {
                        if (v <= hiCut) { sum += v; kept++; }
                    }
                    med[ri, ai] = kept > 0 ? (float)(sum / kept) : m;
                }
            }

            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = x - half;
                    var dy = y - half;
                    var r = MathF.Sqrt(dx * dx + dy * dy);
                    if (r < RSolid)
                    {
                        continue;
                    }
                    if (r >= half)
                    {
                        plane[y, x] = 0f;
                        continue;
                    }
                    var ri = Math.Min((int)(r / RBin), nR - 1);
                    var ai = (int)((MathF.Atan2(dy, dx) + MathF.PI) / (2f * MathF.PI) * ABins) % ABins;
                    var blend = r >= RFull ? 1f : (r - RSolid) / (RFull - RSolid);
                    plane[y, x] = plane[y, x] * (1f - blend) + med[ri, ai] * blend;
                }
            }
        }
    }

    /// <summary>Coarse median over a subsample, for a one-line "is this plate what I think it is".</summary>
    private static float SampleMedian(Image img)
    {
        var vals = new System.Collections.Generic.List<float>(4096);
        var step = Math.Max(1, Math.Min(img.Width, img.Height) / 64);
        for (var y = 0; y < img.Height; y += step)
        {
            for (var x = 0; x < img.Width; x += step)
            {
                var v = img[0, y, x];
                if (float.IsFinite(v))
                {
                    vals.Add(v);
                }
            }
        }
        if (vals.Count == 0)
        {
            return float.NaN;
        }
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private static float EstimateFarFieldSigma(float[][,] planes, int half)
    {
        var vals = new System.Collections.Generic.List<float>(4096);
        var p = planes[0];
        var n = p.GetLength(0);
        for (var y = 0; y < n; y += 3)
        {
            for (var x = 0; x < n; x += 3)
            {
                var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                if (r > half * 0.8f)
                {
                    vals.Add(MathF.Abs(p[y, x]));
                }
            }
        }
        if (vals.Count < 32)
        {
            return 1e-6f;
        }
        vals.Sort();
        return MathF.Max(vals[vals.Count / 2] * 1.4826f, 1e-9f);
    }

    private static float RadialMedian(float[][,] planes, int half, int r0, int r1)
    {
        var vals = new System.Collections.Generic.List<float>(2048);
        var p = planes[0];
        var n = p.GetLength(0);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                if (r >= r0 && r < r1)
                {
                    vals.Add(p[y, x]);
                }
            }
        }
        if (vals.Count == 0)
        {
            return 0f;
        }
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private float Sample(int channel, float x, float y)
    {
        // Bilinear. Sub-pixel placement is not a nicety here: a model offset by half a pixel
        // subtracts a dipole rather than a coma, and a dipole is more visible than the smear it
        // replaced.
        if (x < 0f || y < 0f || x >= _width - 1 || y >= _height - 1)
        {
            return 0f;
        }
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        var p = _planes[channel];
        var a = p[y0, x0];
        var b = p[y0, x0 + 1];
        var c = p[y0 + 1, x0];
        var d = p[y0 + 1, x0 + 1];
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    /// <summary>
    /// Subtracts the model from one raw CFA frame, in place, and returns how many pixels it touched.
    /// </summary>
    /// <param name="frame">The calibrated Bayer mosaic, one channel.</param>
    /// <param name="sourceToCometFrame">That frame's <c>starSolution * translate(-rate * dt)</c>:
    /// source pixels onto the comet-aligned reference grid, where the body does not move.</param>
    /// <param name="anchorRefPx">The body's position on that grid, which is the rate fit's own
    /// intercept. At dt = 0 the comet compose is the identity, so this is the same anchor
    /// <see cref="CometMask"/> uses.</param>
    /// <param name="pattern">CFA colour per photosite, <c>pattern[y &amp; 1, x &amp; 1]</c>. The model
    /// is per-channel and the frame is a mosaic, so each photosite must take the amount of ITS OWN
    /// colour -- subtracting a luminance average would leave a coloured residue exactly where the
    /// comet was.</param>
    /// <param name="scale">Amplitude for this frame, from <see cref="FitScale"/>.</param>
    public int SubtractFrom(
        Image frame, Matrix3x2 sourceToCometFrame, Vector2 anchorRefPx, int[,] pattern, float scale)
    {
        if (!Matrix3x2.Invert(sourceToCometFrame, out _) || scale == 0f || !float.IsFinite(scale))
        {
            return 0;
        }

        var plane = frame.GetChannelArray(0);
        var touched = 0;
        var (x0, y0, x1, y1) = SourceBounds(frame, sourceToCometFrame, anchorRefPx);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var mp = ToModel(new Vector2(x, y), sourceToCometFrame, anchorRefPx);
                var v = Sample(pattern[y & 1, x & 1], mp.X, mp.Y);
                if (v == 0f)
                {
                    continue;
                }
                plane[y, x] -= scale * v;
                touched++;
            }
        }
        return touched;
    }

    /// <summary>
    /// Least-squares amplitude of the model in this frame: <c>sum(d*m) / sum(m*m)</c> over the
    /// pixels the model actually covers, against a local background.
    /// </summary>
    /// <remarks>
    /// Fitted per frame rather than derived from the normalisation, and that is deliberate. The
    /// analytic route would have to track the integrator's own per-frame normalisation constants
    /// exactly, and it would still assume the comet's brightness and the sky transparency were
    /// constant across the session. A fit assumes none of that; it also degrades gracefully, since a
    /// frame where the comet is faint simply fits a smaller amplitude.
    /// </remarks>
    public float FitScale(Image frame, Matrix3x2 sourceToCometFrame, Vector2 anchorRefPx, int[,] pattern)
    {
        var plane = frame.GetChannelArray(0);
        var (x0, y0, x1, y1) = SourceBounds(frame, sourceToCometFrame, anchorRefPx);
        if (x1 <= x0 || y1 <= y0)
        {
            return 0f;
        }

        // Local background from the rim of the box, per CFA colour: the comet sits on sky, and the
        // sky level is not the same for the R, G and B photosites.
        Span<double> bgSum = stackalloc double[4];
        Span<int> bgN = stackalloc int[4];
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var mp = ToModel(new Vector2(x, y), sourceToCometFrame, anchorRefPx);
                var dx = mp.X - _centre.X;
                var dy = mp.Y - _centre.Y;
                if (dx * dx + dy * dy < ReachPx * ReachPx)
                {
                    continue;
                }
                var v = plane[y, x];
                if (!float.IsFinite(v))
                {
                    continue;
                }
                var k = ((y & 1) << 1) | (x & 1);
                bgSum[k] += v;
                bgN[k]++;
            }
        }

        double num = 0, den = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var ch = pattern[y & 1, x & 1];
                var mp = ToModel(new Vector2(x, y), sourceToCometFrame, anchorRefPx);
                var m = Sample(ch, mp.X, mp.Y);
                if (m <= 0f)
                {
                    continue;
                }
                var v = plane[y, x];
                if (!float.IsFinite(v))
                {
                    continue;
                }
                var k = ((y & 1) << 1) | (x & 1);
                if (bgN[k] < 16)
                {
                    continue;
                }
                var d = v - (float)(bgSum[k] / bgN[k]);
                num += (double)d * m;
                den += (double)m * m;
            }
        }
        if (den <= 0)
        {
            return 0f;
        }
        var scale = (float)(num / den);
        // A negative or absurd amplitude means the fit found something other than the comet. Clamp
        // rather than subtract nonsense; the frame then keeps its comet and the layer says so in the
        // aggregate the caller logs.
        // Bounded, but not by a magic number: the units depend on how the master was normalised
        // (0.5 background here) against the frames' own scale, and the fit legitimately lands near 87
        // on this data. A hard 100 would have silently dropped frames as soon as anything upstream
        // rescaled. Only reject what cannot be a real amplitude.
        return scale is > 0f and < 1e6f ? scale : 0f;
    }

    private Vector2 ToModel(Vector2 source, Matrix3x2 sourceToCometFrame, Vector2 anchorRefPx)
    {
        var onCometGrid = Vector2.Transform(source, sourceToCometFrame);
        return onCometGrid - anchorRefPx + _centre;
    }

    private (int X0, int Y0, int X1, int Y1) SourceBounds(
        Image frame, Matrix3x2 sourceToCometFrame, Vector2 anchorRefPx)
    {
        if (!Matrix3x2.Invert(sourceToCometFrame, out var inv))
        {
            return (0, 0, -1, -1);
        }
        // Corners of the model box pushed back into source space; the affine may rotate, so take the
        // AABB of the four rather than assuming an axis-aligned box maps to one.
        var r = ReachPx;
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var (ox, oy) in new[] { (-r, -r), (r, -r), (-r, r), (r, r) })
        {
            var p = Vector2.Transform(anchorRefPx + new Vector2(ox, oy), inv);
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
        }
        return (
            Math.Max(0, (int)MathF.Floor(minX)),
            Math.Max(0, (int)MathF.Floor(minY)),
            Math.Min(frame.Width - 1, (int)MathF.Ceiling(maxX)),
            Math.Min(frame.Height - 1, (int)MathF.Ceiling(maxY)));
    }
}
