using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The comet's own light, isolated, so it can be SUBTRACTED from every frame instead of the frames
/// being thrown away wherever it happens to be, and ADDED back once onto the finished star layer.
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
/// <para><b>Where the model comes from.</b> The clean source is a comet layer stacked from PER-FRAME
/// star-removed plates (<c>--remove-stars</c>): it holds the comet and nothing else, so the model is
/// a crop of it. The fallback, for a comet layer built from ordinary frames, is to run a star remover
/// on the COMET-ALIGNED crop -- there the coma is the only compact source, every real star being a
/// streak, so the remover takes the comet -- and keep the difference. That difference also carries
/// whatever trail flux the remover took along with the body, and subtracted at 89 comet-relative
/// positions each survivor becomes a dark streak, so the fallback opens across the trail direction
/// first and is still the worse model (streaks p0.5 -0.415 sigma against -0.035 from starless
/// plates).</para>
///
/// <para><b>How far the model reaches is decided PER CHANNEL, from the profile's own asymptote.</b>
/// The first version judged reach on channel 0 alone against a floor of 1% of the peak. On a gas-rich
/// comet channel 0 is red, the faintest channel by 3x, so the model stopped at 100 px while green
/// still held 1.4 sigma of coma there, 1% of its peak at 200 px and measurable wings to 300 px --
/// and everything outside the box stayed in all 89 frames and smeared along the track as a band. A
/// relative floor is the wrong test anyway: a coma's wings fall roughly as 1/r, so at any fixed
/// fraction of the peak there is still coherent signal in the annulus, and the same model is
/// subtracted from every frame so that signal does not average away. What ends a coma is that its
/// profile stops falling. So each channel's annular-median profile is followed outward until it no
/// longer sets a new minimum; that minimum IS the channel's pedestal (the sky under the coma), the
/// radius where it sits is that channel's reach, and beyond a channel's reach its plane is zero. On
/// SWAN this also stops correctly short of the sky gradient, which turns the profile back upward
/// past ~440 px.</para>
///
/// <para><b>The amplitude is FITTED per frame</b> (<see cref="FitScale"/>), never derived, which is
/// what makes the model indifferent to transparency, to the integrator's normalisation and to the
/// units of whatever produced it (it ran 87 on one path and 1580 on another). The fit is confined to
/// the CORE, where the coma dominates everything else, and the sky under it is a per-CFA-colour
/// MEDIAN from beyond the reach; extending the fit to the wings would let every field star in an
/// 800 px box vote on the comet's brightness, and a mean sky would let them bias it upward.</para>
/// </summary>
internal sealed class CometModel
{
    /// <summary>Annulus width of the radial profile the reach and pedestal are read from.</summary>
    private const int ProfileStepPx = 10;

    /// <summary>The profile has reached its asymptote once this many consecutive annuli fail to set a
    /// new minimum. Three is enough: a genuine 1/r wing keeps setting them, a flat asymptote stops
    /// within a few steps, and the sky gradient that turns the profile upward stops it at once.</summary>
    private const int StaleAnnuliAtAsymptote = 3;

    /// <summary>The amplitude is read over an annulus of the coma: from <see cref="FitInnerRadiusPx"/>
    /// out to where the brightest channel has fallen to <see cref="FitCoreFraction"/> of its peak, but
    /// never less than <see cref="MinFitRadiusPx"/> out.</summary>
    /// <remarks>
    /// The inner cut is the nucleus. A star remover takes a comet's central condensation along with
    /// the stars (it is compact and star-like), so a model built from starless plates is short of
    /// exactly that flux while every frame still has it. Measured on 10P/Tempel 2, a fit that included
    /// the centre raised the amplitude to cover the missing core and over-subtracted the whole coma
    /// around it: a bowl of -2.3 sigma at 7-20 px from the track with a +2 to +3.5 sigma line of
    /// leftover condensation running along it. Twelve pixels is several seeing discs, well past any
    /// footprint a star remover takes out of a point source, and costs the fit nothing it needs.
    /// </remarks>
    private const float FitInnerRadiusPx = 12f;
    private const float FitCoreFraction = 0.15f;
    private const float MinFitRadiusPx = 30f;

    private readonly float[][,] _planes;
    private readonly int _size;

    /// <summary>Where the body sits inside <see cref="_planes"/>, in model pixels, SUB-PIXEL. The
    /// crop is cut on whole pixels but the body is not on one, and a model offset by half a pixel
    /// subtracts a dipole rather than a coma.</summary>
    private readonly Vector2 _centre;

    /// <summary>The largest per-channel reach; beyond this every plane is zero.</summary>
    public float ReachPx { get; }

    /// <summary>Radius of the core the per-frame amplitude is fitted over.</summary>
    public float FitRadiusPx { get; }

    /// <summary>Per channel: where that channel's profile reached its asymptote. Zero for a channel
    /// that held no comet, whose plane is then all zero.</summary>
    public ImmutableArray<float> ReachPerChannelPx { get; }

    /// <summary>Per channel: the pedestal-removed median over the central 20 px.</summary>
    public ImmutableArray<float> PeakPerChannel { get; }

    public int ChannelCount => _planes.Length;

    private CometModel(
        float[][,] planes, int size, Vector2 centre,
        ImmutableArray<float> reachPerChannel, ImmutableArray<float> peakPerChannel, float fitRadiusPx)
    {
        _planes = planes;
        _size = size;
        _centre = centre;
        ReachPerChannelPx = reachPerChannel;
        PeakPerChannel = peakPerChannel;
        var reach = 0f;
        foreach (var r in reachPerChannel)
        {
            reach = MathF.Max(reach, r);
        }
        ReachPx = reach;
        FitRadiusPx = fitRadiusPx;
    }

    /// <summary>
    /// Builds the model from a comet-aligned master: a crop of it when that master was stacked from
    /// star-removed plates, otherwise the difference against the star remover's output on the crop.
    /// </summary>
    /// <param name="cometMaster">The comet-aligned integration, linear.</param>
    /// <param name="alreadyStarless">True when that integration was built from per-frame star-removed
    /// plates (<c>--remove-stars</c>), in which case it IS the comet and nothing is differenced.</param>
    /// <param name="centreInMaster">The body's position in that master's pixels, sub-pixel.</param>
    /// <param name="trailDirection">The drift vector. On a comet-aligned plate every star streaks
    /// along it, which is what lets the fallback path remove trail residue by shape.</param>
    /// <param name="remover">Any <see cref="IStarRemover"/>; unused when
    /// <paramref name="alreadyStarless"/>.</param>
    /// <returns><c>null</c> when the remover declines or no channel holds a comet, which must leave the
    /// caller free to fall back rather than emit a layer with nothing subtracted.</returns>
    public static async Task<CometModel?> TryBuildAsync(
        Image cometMaster,
        bool alreadyStarless,
        Vector2 centreInMaster,
        Vector2 trailDirection,
        IStarRemover? remover,
        ILogger logger,
        CancellationToken ct)
    {
        var w = cometMaster.Width;
        var h = cometMaster.Height;
        var channels = cometMaster.ChannelCount;
        if (!float.IsFinite(centreInMaster.X) || !float.IsFinite(centreInMaster.Y))
        {
            logger.LogWarning("  [comet] the body's position is not finite; cannot model it");
            return null;
        }
        var cx = (int)MathF.Round(centreInMaster.X);
        var cy = (int)MathF.Round(centreInMaster.Y);

        // Crop to a box around the body BEFORE anything sees it, for two reasons.
        //
        // The load-bearing one: a comet-aligned canvas carries NaN wherever the frames do not all
        // overlap, and RC-Astro answers an all-NaN plate for an input holding any -- the whole image,
        // not just the uncovered part. SharpenPipeline already guards this way for the same reason.
        // A box around the body sits well inside the covered region.
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
        // The crop's own centre pixel (half, half) is master pixel (cx, cy); the body is the
        // sub-pixel remainder away from it.
        var centre = new Vector2(half + (centreInMaster.X - cx), half + (centreInMaster.Y - cy));
        var cropPlanes = new float[channels][,];
        var nanFilled = 0;
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            // Any NaN still inside the box is filled with the box mean rather than left to poison
            // the remover or the profile. Rare this close to the centre, and cheap to be certain about.
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

        // The clean path. A comet layer stacked from per-frame star-removed plates holds the comet and
        // nothing else, so the model is simply that -- no star remover, no difference, no trail
        // residue to chase afterwards. Everything below this branch exists only because a comet layer
        // built from ORDINARY frames has star trails in it, and taking them back out is a losing game.
        if (alreadyStarless)
        {
            return Finish(cropPlanes, half, centre, trailDirection, logger, "the starless comet layer");
        }

        if (remover is null)
        {
            logger.LogWarning("  [comet] the comet layer holds stars and no IStarRemover is registered; cannot model the body");
            return null;
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

        // Normalise to the integrator's own target before the remover sees it. A star remover is a
        // neural net and cares where its input sits in [0,1], not merely that the SNR is good.
        //
        // This is not hypothetical. A drizzled comet layer does no per-frame normalisation, so its
        // master's background sits at 0.0145; the plate this technique was proven on was an
        // InRamAllFrames master, normalised to a background of 0.5. Handed the un-normalised crop,
        // sxt found only the very peak (crop max 0.063 -> 0.044) and left the whole coma: radial
        // medians ran 0.000028 at r=20 against a noise floor of 0.000077, i.e. nothing.
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
                cometless.Width, cometless.Height, cometless.ChannelCount, size, size, channels);
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
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var d = cropPlanes[c][y, x] - cometless[c, y, x];
                    plane[y, x] = float.IsFinite(d) ? d : 0f;
                }
            }
            planes[c] = plane;
        }

        // Strip star-trail residue BEFORE smoothing. On a comet-aligned plate every star IS a trail,
        // and the star remover takes them as readily as the comet -- so the difference holds the
        // comet PLUS whatever trail flux went with it. Subtracted at the comet-relative position in
        // all 89 frames, each of those becomes a dark streak in the finished star layer. Measured on
        // the manual pair at r=600-1300: median +0.20 sigma, p99 +0.47 sigma. That p99 is the
        // streaks, and an earlier check passed this plate as clean by asking only for the fraction
        // above 1 sigma, which was 0.0000. The wrong question.
        OpenAcrossTrails(planes, half, trailDirection);

        return Finish(planes, half, centre, trailDirection, logger, $"the {remover.Name} difference");
    }

    /// <summary>
    /// The half of the build both sources share: smooth the wings, read each channel's reach and
    /// pedestal off its own profile, and refuse when no channel holds a comet.
    /// </summary>
    private static CometModel? Finish(
        float[][,] planes, int half, Vector2 centre, Vector2 trailDirection, ILogger logger, string source)
    {
        var channels = planes.Length;
        // Noise of each plane itself, BEFORE smoothing. It is what decides whether a channel holds a
        // comet at all: a flat plate's profile still has a minimum somewhere, so "the centre is above
        // the asymptote" is true of pure noise, and the centre has to clear the plate's own scatter.
        var rawNoise = new float[channels];
        for (var c = 0; c < channels; c++)
        {
            rawNoise[c] = EstimateFarFieldSigma(planes[c], half);
        }

        // Smooth the wings in POLAR bins before reading the profile. The raw plate carries the
        // noise of an 89-frame stack, and past ~60 px the coma sits under it -- so an unsmoothed
        // model has to be truncated there, which leaves exactly the wings this method exists to
        // remove. That noise does NOT average away downstream either: the same model is subtracted
        // from every frame, so its error enters the master coherently rather than as 1/sqrt(N).
        //
        // A coma is close to azimuthally symmetric, so averaging within a radius/angle cell buys
        // sqrt(cell) in noise for almost no loss of shape -- at r=100 a 15-degree cell holds ~500 px,
        // about 23x. Angle bins are kept narrow enough to preserve a tail, which is a real asymmetry
        // rather than noise (measured on SWAN at PA 160-180, out to 250-350 px).
        SmoothWingsInPolarBins(planes, half, trailDirection);

        var reach = new float[channels];
        var pedestal = new float[channels];
        var peak = new float[channels];
        var profiles = new float[channels][];
        var validChannels = 0;
        for (var c = 0; c < channels; c++)
        {
            var profile = RadialProfile(planes[c], half, ProfileStepPx);
            profiles[c] = profile;
            var (reachPx, asymptote) = FindAsymptote(profile, ProfileStepPx);
            var plane = planes[c];
            var n = plane.GetLength(0);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = x - half;
                    var dy = y - half;
                    plane[y, x] = dx * dx + dy * dy < reachPx * reachPx ? plane[y, x] - asymptote : 0f;
                }
            }
            var centrePeak = RadialMedian(plane, half, 0, 20);
            if (reachPx < 2 * ProfileStepPx || !(centrePeak > 3f * rawNoise[c]))
            {
                // This channel holds no comet the profile can see. Its plane subtracts nothing.
                Array.Clear(plane);
                reach[c] = 0f;
                pedestal[c] = asymptote;
                peak[c] = 0f;
                continue;
            }
            reach[c] = reachPx;
            pedestal[c] = asymptote;
            peak[c] = centrePeak;
            validChannels++;
        }

        if (validChannels == 0)
        {
            // The profile, not just the verdict. "No comet" can mean the crop landed off the body,
            // that the remover left it in place, or that it took the whole plate -- and those want
            // completely different fixes.
            var text = new StringBuilder();
            for (var c = 0; c < channels; c++)
            {
                text.Append(CultureInfo.InvariantCulture, $"ch{c}:");
                for (var k = 0; k < Math.Min(profiles[c].Length, 12); k++)
                {
                    text.Append(CultureInfo.InvariantCulture, $" {profiles[c][k]:F6}");
                }
                text.Append(" |");
            }
            logger.LogWarning(
                "  [comet] {Source} holds no comet at the predicted position: plate noise {Noise}, "
                    + "radial medians ({Step} px steps) {Profile}",
                source, string.Join("/", Array.ConvertAll(rawNoise, n => n.ToString("F6", CultureInfo.InvariantCulture))),
                ProfileStepPx, text.ToString());
            return null;
        }

        // The fit core: out to where the brightest channel has fallen to a tenth of its peak. Read
        // off the pedestal-removed profile of that channel.
        var brightest = 0;
        for (var c = 1; c < channels; c++)
        {
            if (peak[c] > peak[brightest]) { brightest = c; }
        }
        var fitRadius = MinFitRadiusPx;
        for (var k = 2; k < profiles[brightest].Length; k++)
        {
            var r = (k + 1) * ProfileStepPx;
            if (r > reach[brightest]) { break; }
            if (profiles[brightest][k] - pedestal[brightest] < FitCoreFraction * peak[brightest]) { break; }
            fitRadius = r;
        }
        fitRadius = Math.Clamp(fitRadius, MinFitRadiusPx, MathF.Max(MinFitRadiusPx, reach[brightest]));

        var perChannel = new StringBuilder();
        for (var c = 0; c < channels; c++)
        {
            perChannel.Append(CultureInfo.InvariantCulture,
                $"ch{c} reach {reach[c]:F0} px peak {peak[c]:F6} pedestal {pedestal[c]:F6}; ");
        }
        logger.LogInformation(
            "  [comet] model taken from {Source}: {Size}x{Size} px, centre ({Cx:F2}, {Cy:F2}), fit core r<{Fit:F0} px, "
                + "plate noise {Noise:F6}; {PerChannel}",
            source, half * 2, half * 2, centre.X, centre.Y, fitRadius, rawNoise[brightest], perChannel.ToString());

        return new CometModel(
            planes, half * 2, centre,
            ImmutableArray.Create(reach), ImmutableArray.Create(peak), fitRadius);
    }

    /// <summary>
    /// Follows an annular-median profile outward from 20 px until it stops falling, and answers where
    /// that happened and the level there.
    /// </summary>
    /// <param name="profile">Annular medians, <c>profile[k]</c> covering <c>[k*step, (k+1)*step)</c>.</param>
    /// <returns>The reach (outer edge of the annulus holding the minimum) and the minimum itself,
    /// which is the sky under the coma.</returns>
    internal static (float ReachPx, float Asymptote) FindAsymptote(ReadOnlySpan<float> profile, int step)
    {
        var startK = Math.Min(2, Math.Max(0, profile.Length - 1));
        if (profile.Length == 0)
        {
            return (0f, 0f);
        }
        var minValue = profile[startK];
        var minK = startK;
        var stale = 0;
        for (var k = startK + 1; k < profile.Length; k++)
        {
            if (profile[k] < minValue)
            {
                minValue = profile[k];
                minK = k;
                stale = 0;
            }
            else if (++stale >= StaleAnnuliAtAsymptote)
            {
                break;
            }
        }
        return ((minK + 1) * step, minValue);
    }

    /// <summary>Annular medians of one plane about the crop centre, <paramref name="step"/> px wide,
    /// out to the inscribed circle.</summary>
    private static float[] RadialProfile(float[,] plane, int half, int step)
    {
        var n = plane.GetLength(0);
        var bins = half / step;
        var cells = new List<float>[bins];
        for (var k = 0; k < bins; k++)
        {
            cells[k] = new List<float>(2 * (int)(MathF.PI * (2 * k + 1) * step * step) + 16);
        }
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                var k = (int)(r / step);
                if (k < bins)
                {
                    cells[k].Add(plane[y, x]);
                }
            }
        }
        var profile = new float[bins];
        for (var k = 0; k < bins; k++)
        {
            var list = cells[k];
            if (list.Count == 0)
            {
                profile[k] = k > 0 ? profile[k - 1] : 0f;
                continue;
            }
            list.Sort();
            profile[k] = list[list.Count / 2];
        }
        return profile;
    }

    /// <summary>
    /// Grey opening (erode then dilate) with a linear structuring element laid ACROSS the trail
    /// direction, applied outside the core. Fallback path only.
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
    /// Replaces the model outside <c>RSolid</c> with its own robust mean in (radius, angle) cells,
    /// faded in over a short band so no seam appears at the handover.
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
            var cells = new List<float>[nR, ABins];
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
                    (cells[ri, ai] ??= new List<float>(64)).Add(plane[y, x]);
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
                    // a star remover leaves positive residue behind, never negative -- and a trail
                    // crossing a 15-degree cell can be 10-30% of it, enough to drag a median as well
                    // as a mean. Clipping the upper tail and averaging the rest is blind to the
                    // trails, and for symmetric noise it shifts every cell by the same small amount,
                    // which the pedestal (read off the same smoothed planes) then absorbs.
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

    private static float EstimateFarFieldSigma(float[,] p, int half)
    {
        var vals = new List<float>(4096);
        var n = p.GetLength(0);
        for (var y = 0; y < n; y += 3)
        {
            for (var x = 0; x < n; x += 3)
            {
                var r = MathF.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
                if (r > half * 0.8f && float.IsFinite(p[y, x]))
                {
                    vals.Add(p[y, x]);
                }
            }
        }
        if (vals.Count < 32)
        {
            return 1e-6f;
        }
        vals.Sort();
        var med = vals[vals.Count / 2];
        for (var i = 0; i < vals.Count; i++)
        {
            vals[i] = MathF.Abs(vals[i] - med);
        }
        vals.Sort();
        return MathF.Max(vals[vals.Count / 2] * 1.4826f, 1e-9f);
    }

    private static float RadialMedian(float[,] p, int half, int r0, int r1)
    {
        var vals = new List<float>(2048);
        var n = p.GetLength(0);
        var y0 = Math.Max(0, half - r1);
        var y1 = Math.Min(n - 1, half + r1);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = y0; x <= y1; x++)
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
        if (x < 0f || y < 0f || x >= _size - 1 || y >= _size - 1)
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
    /// <param name="bodyOnGrid">The body's position on that grid (<see cref="CometCompose.BodyOnGrid"/>).</param>
    /// <param name="pattern">CFA colour per photosite, <c>pattern[y &amp; 1, x &amp; 1]</c>. The model
    /// is per-channel and the frame is a mosaic, so each photosite must take the amount of ITS OWN
    /// colour -- subtracting a luminance average would leave a coloured residue exactly where the
    /// comet was.</param>
    /// <param name="scale">Amplitude for this frame, from <see cref="FitScale"/>.</param>
    public int SubtractFrom(
        Image frame, Matrix3x2 sourceToCometFrame, Vector2 bodyOnGrid, int[,] pattern, float scale)
    {
        if (!Matrix3x2.Invert(sourceToCometFrame, out _) || scale == 0f || !float.IsFinite(scale))
        {
            return 0;
        }

        var plane = frame.GetChannelArray(0);
        var touched = 0;
        var (x0, y0, x1, y1) = SourceBounds(frame, sourceToCometFrame, bodyOnGrid, ReachPx);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var mp = ToModel(new Vector2(x, y), sourceToCometFrame, bodyOnGrid);
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
    /// Amplitude of the model in this frame: the MEDIAN of the per-pixel ratio <c>d / m</c> over the
    /// fit annulus, against a per-CFA-colour sky read from beyond the reach.
    /// </summary>
    /// <remarks>
    /// <para>Fitted per frame rather than derived from the normalisation, and that is deliberate. The
    /// analytic route would have to track the integrator's own per-frame normalisation constants
    /// exactly, and it would still assume the comet's brightness and the sky transparency were
    /// constant across the session. A fit assumes none of that; it also degrades gracefully, since a
    /// frame where the comet is faint simply fits a smaller amplitude.</para>
    /// <para>A median of ratios rather than least squares, because the frame still has its stars and
    /// the model is missing the nucleus, and both bias a least-squares amplitude the same way. A
    /// bright star's halo inside the annulus adds positive <c>d</c> over a patch of pixels that a
    /// clipping pass never reaches (its core is clipped, its wings are not), and the nucleus the
    /// remover took out of the model is positive <c>d</c> exactly where <c>m</c> is largest, so it
    /// dominates <c>sum(d*m)</c>. Each is a minority of the annulus, and a median is blind to a
    /// minority. The annulus stops where the coma has fallen to 15% of its peak, because a ratio
    /// amplifies any error in the sky by <c>1/m</c>; and it starts outside the nucleus, see
    /// <see cref="FitInnerRadiusPx"/>.</para>
    /// </remarks>
    public float FitScale(Image frame, Matrix3x2 sourceToCometFrame, Vector2 bodyOnGrid, int[,] pattern)
    {
        var plane = frame.GetChannelArray(0);
        var (x0, y0, x1, y1) = SourceBounds(frame, sourceToCometFrame, bodyOnGrid, ReachPx);
        if (x1 <= x0 || y1 <= y0)
        {
            return 0f;
        }

        // Sky under the body, per CFA colour, as a MEDIAN of what lies beyond the reach inside the
        // box: the comet sits on sky, the sky level differs between the R, G and B photosites, and a
        // mean would let every star in the corners lift it.
        var sky = new List<float>[4];
        for (var k = 0; k < 4; k++)
        {
            sky[k] = new List<float>(4096);
        }
        var reach2 = ReachPx * ReachPx;
        for (var y = y0; y <= y1; y += 3)
        {
            for (var x = x0; x <= x1; x += 3)
            {
                var mp = ToModel(new Vector2(x, y), sourceToCometFrame, bodyOnGrid);
                var dx = mp.X - _centre.X;
                var dy = mp.Y - _centre.Y;
                if (dx * dx + dy * dy < reach2)
                {
                    continue;
                }
                var v = plane[y, x];
                if (!float.IsFinite(v))
                {
                    continue;
                }
                sky[((y & 1) << 1) | (x & 1)].Add(v);
            }
        }
        Span<float> skyLevel = stackalloc float[4];
        Span<bool> skyKnown = stackalloc bool[4];
        for (var k = 0; k < 4; k++)
        {
            if (sky[k].Count < 16)
            {
                continue;
            }
            sky[k].Sort();
            skyLevel[k] = sky[k][sky[k].Count / 2];
            skyKnown[k] = true;
        }

        // The annulus samples: one ratio per pixel where the model has weight and is trusted.
        var fit2 = FitRadiusPx * FitRadiusPx;
        var inner2 = FitInnerRadiusPx * FitInnerRadiusPx;
        var (fx0, fy0, fx1, fy1) = SourceBounds(frame, sourceToCometFrame, bodyOnGrid, FitRadiusPx);
        var capacity = Math.Max(16, (fx1 - fx0 + 1) * (fy1 - fy0 + 1));
        var ratios = ArrayPool<float>.Shared.Rent(capacity);
        try
        {
            var count = 0;
            for (var y = fy0; y <= fy1; y++)
            {
                for (var x = fx0; x <= fx1; x++)
                {
                    var k = ((y & 1) << 1) | (x & 1);
                    if (!skyKnown[k])
                    {
                        continue;
                    }
                    var mp = ToModel(new Vector2(x, y), sourceToCometFrame, bodyOnGrid);
                    var dx = mp.X - _centre.X;
                    var dy = mp.Y - _centre.Y;
                    var r2 = dx * dx + dy * dy;
                    if (r2 >= fit2 || r2 < inner2)
                    {
                        continue;
                    }
                    var m = Sample(pattern[y & 1, x & 1], mp.X, mp.Y);
                    if (m <= 0f)
                    {
                        continue;
                    }
                    var v = plane[y, x];
                    if (!float.IsFinite(v))
                    {
                        continue;
                    }
                    ratios[count++] = (v - skyLevel[k]) / m;
                }
            }
            if (count < 64)
            {
                return 0f;
            }
            var sorted = ratios.AsSpan(0, count);
            sorted.Sort();
            var scale = sorted[count / 2];

            // A negative or absurd amplitude means the fit found something other than the comet.
            // Bounded, but not by a magic number: the units depend on how the master was normalised
            // against the frames' own scale, and the fit legitimately lands near 87 on one path and
            // 3500 on another. Only reject what cannot be a real amplitude.
            return scale is > 0f and < 1e6f ? scale : 0f;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ratios);
        }
    }

    /// <summary>
    /// Adds the model onto a canvas, in place, at <paramref name="centreOnCanvas"/>, one scale per
    /// channel. The compose step: the star layer plus the body, placed where the ephemeris says it
    /// was at the reference epoch. Returns the pixels touched.
    /// </summary>
    /// <param name="canvasPlanes">The canvas's channel planes, as many as the model has.</param>
    /// <param name="centreOnCanvas">Where the body's centre lands, sub-pixel.</param>
    /// <param name="scalePerChannel">Model units to canvas units, per channel.</param>
    public int AddTo(float[][,] canvasPlanes, Vector2 centreOnCanvas, ReadOnlySpan<float> scalePerChannel)
    {
        if (canvasPlanes.Length != _planes.Length || scalePerChannel.Length != _planes.Length)
        {
            throw new ArgumentException(
                $"the model has {_planes.Length} channels; the canvas has {canvasPlanes.Length} and {scalePerChannel.Length} scales were given");
        }
        var h = canvasPlanes[0].GetLength(0);
        var w = canvasPlanes[0].GetLength(1);
        var x0 = Math.Max(0, (int)MathF.Floor(centreOnCanvas.X - ReachPx) - 1);
        var y0 = Math.Max(0, (int)MathF.Floor(centreOnCanvas.Y - ReachPx) - 1);
        var x1 = Math.Min(w - 1, (int)MathF.Ceiling(centreOnCanvas.X + ReachPx) + 1);
        var y1 = Math.Min(h - 1, (int)MathF.Ceiling(centreOnCanvas.Y + ReachPx) + 1);
        var touched = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var mx = x - centreOnCanvas.X + _centre.X;
                var my = y - centreOnCanvas.Y + _centre.Y;
                var any = false;
                for (var c = 0; c < _planes.Length; c++)
                {
                    var v = Sample(c, mx, my);
                    if (v == 0f)
                    {
                        continue;
                    }
                    var cur = canvasPlanes[c][y, x];
                    if (!float.IsFinite(cur))
                    {
                        continue;
                    }
                    canvasPlanes[c][y, x] = cur + scalePerChannel[c] * v;
                    any = true;
                }
                if (any)
                {
                    touched++;
                }
            }
        }
        return touched;
    }

    /// <summary>The model's value at an offset from the body, per channel: for tests and for the
    /// compose to report what it placed.</summary>
    internal float ValueAt(int channel, Vector2 offsetFromBody)
        => Sample(channel, _centre.X + offsetFromBody.X, _centre.Y + offsetFromBody.Y);

    private Vector2 ToModel(Vector2 source, Matrix3x2 sourceToCometFrame, Vector2 bodyOnGrid)
    {
        var onCometGrid = Vector2.Transform(source, sourceToCometFrame);
        return onCometGrid - bodyOnGrid + _centre;
    }

    private static (int X0, int Y0, int X1, int Y1) SourceBounds(
        Image frame, Matrix3x2 sourceToCometFrame, Vector2 bodyOnGrid, float radius)
    {
        if (!Matrix3x2.Invert(sourceToCometFrame, out var inv))
        {
            return (0, 0, -1, -1);
        }
        // Corners of the model box pushed back into source space; the affine may rotate, so take the
        // AABB of the four rather than assuming an axis-aligned box maps to one.
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var (ox, oy) in new[] { (-radius, -radius), (radius, -radius), (-radius, radius), (radius, radius) })
        {
            var p = Vector2.Transform(bodyOnGrid + new Vector2(ox, oy), inv);
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
