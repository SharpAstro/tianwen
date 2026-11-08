using System;
using System.Collections.Generic;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Where one colour sat against the reference colour, measured on a frame's stars: the star's light in this colour lies at
/// (reference x + <see cref="Dx"/>, reference y + <see cref="Dy"/>), in the pixels of the frame that was measured (the mosaic's
/// for a CFA mosaic, the plane's for a debayered image). NaN on both axes where the colour could not be read
/// (<see cref="IsMeasured"/> false), never zero: nothing measured is not the same fact as no offset.
/// </summary>
/// <param name="Dx">Median offset along x, px.</param>
/// <param name="Dy">Median offset along y, px.</param>
/// <param name="StarCount">The stars this median is over.</param>
/// <param name="SpreadX">The stars' own scatter about <paramref name="Dx"/>, px: the MAD scaled to a Gaussian sigma. It is the
/// scatter of ONE star, not the error of the median (<see cref="ErrorX"/>).</param>
/// <param name="SpreadY">As <paramref name="SpreadX"/>, along y.</param>
/// <remarks>
/// The sign convention is <c>PlanetaryChannelShift</c>'s (its content sits at (x + Dx, y + Dy) against green), so a reading of
/// either is the other's. The two types stay apart on purpose: the planetary one is a limb fit's answer to a disk, with a
/// refusal and a reading mode of its own, and folding it into this one would change what a planetary stack reports for nothing
/// it needs.
/// </remarks>
public readonly record struct ChannelOffset(double Dx, double Dy, int StarCount, double SpreadX, double SpreadY)
{
    /// <summary>A colour no reading could be made of, over <paramref name="starCount"/> stars.</summary>
    public static ChannelOffset Unmeasured(int starCount) => new(double.NaN, double.NaN, starCount, double.NaN, double.NaN);

    /// <summary>Whether this is a reading at all.</summary>
    public bool IsMeasured => double.IsFinite(Dx) && double.IsFinite(Dy);

    /// <summary>How far from the reference, px; NaN when not measured.</summary>
    public double Length => Math.Sqrt((Dx * Dx) + (Dy * Dy));

    /// <summary>The standard error of the median along x, px: the spread over the square root of the stars, 1.2533 for a median.</summary>
    public double ErrorX => 1.2533 * SpreadX / Math.Sqrt(Math.Max(StarCount, 1));

    /// <summary>The standard error of the median along y, px.</summary>
    public double ErrorY => 1.2533 * SpreadY / Math.Sqrt(Math.Max(StarCount, 1));
}

/// <summary>
/// What <see cref="Image.ChannelOffsets"/> read: each colour against <see cref="Reference"/>. The reference's own entry is
/// exactly zero over the stars that were used. A colour reads only when <see cref="Image.ChannelOffsets"/> found enough stars
/// (<see cref="ChannelOffsetOptions.MinStars"/>), and then ALL three read together; otherwise all three are
/// <see cref="ChannelOffset.Unmeasured"/>, whatever the cause (no stars, too few survived the gates, a mono image).
/// </summary>
/// <param name="Reference">The colour the others are read against.</param>
/// <param name="Red">Red's offset from the reference.</param>
/// <param name="Green">Green's offset from the reference.</param>
/// <param name="Blue">Blue's offset from the reference.</param>
/// <param name="StarsOffered">How many stars the caller's list held.</param>
public sealed record ChannelOffsetsResult(CfaChannel Reference, ChannelOffset Red, ChannelOffset Green, ChannelOffset Blue, int StarsOffered)
{
    /// <summary>The stars every colour was read on.</summary>
    public int StarsUsed => Green.StarCount;

    /// <summary>Whether the colours were read at all.</summary>
    public bool IsMeasured => Red.IsMeasured && Green.IsMeasured && Blue.IsMeasured;

    /// <summary>One colour's offset.</summary>
    public ChannelOffset this[CfaChannel channel] => channel switch
    {
        CfaChannel.Red => Red,
        CfaChannel.Green => Green,
        _ => Blue,
    };

    /// <summary>Nothing could be read, for <paramref name="starsOffered"/> offered stars.</summary>
    public static ChannelOffsetsResult Unmeasured(CfaChannel reference, int starsOffered)
        => new(reference, ChannelOffset.Unmeasured(0), ChannelOffset.Unmeasured(0), ChannelOffset.Unmeasured(0), starsOffered);
}

/// <summary>The gates of <see cref="Image.ChannelOffsets"/>.</summary>
public sealed record ChannelOffsetOptions
{
    /// <summary>The defaults.</summary>
    public static ChannelOffsetOptions Default { get; } = new();

    /// <summary>The most stars read, the brightest first: a median over a few hundred is as good as one over thousands.</summary>
    public int MaxStars { get; init; } = 300;

    /// <summary>Fewer stars than this read nothing: a median over three stars is a guess.</summary>
    public int MinStars { get; init; } = 5;

    /// <summary>A star under this SNR (the detector's) is not read: its centroid noise in the thinner colour is no use.</summary>
    public float MinSnr { get; init; } = 20f;

    /// <summary>A star with a neighbour this fraction of its flux (or more) inside reach of its window is crowded, and skipped.</summary>
    public float CrowdingFluxFraction { get; init; } = 0.1f;

    /// <summary>A colour more than this far, px, from the reference is not dispersion or collimation but a wrong star, and the star is skipped.</summary>
    public float MaxOffsetPx { get; init; } = 1f;
}

public partial class Image
{
    // The centroid window: Gaussian weights of the star's own sigma, 3 sigma plus a pixel out; the background ring is the four pixels beyond.
    private const float ChannelOffsetMinSigma = 1f;
    private const float ChannelOffsetMaxSigma = 3.5f;
    private const int ChannelOffsetRingWidth = 4;
    private const int ChannelOffsetMaxRingPixels = 1024; // pi * (12 + 4 + 0.5)^2 = 855, the widest window
    private const int ChannelOffsetMaxIterations = 12;
    private const float ChannelOffsetConvergedPx = 1e-3f;
    private const float ChannelOffsetSignalSigmas = 8f;
    private const float SaturationFraction = 0.98f;

    /// <summary>
    /// Each colour's centroid offset from <paramref name="reference"/> (green by default) over <paramref name="stars"/>:
    /// the median over the stars that survive the gates, with their count and their scatter. Answers a single-channel Bayer
    /// mosaic (<see cref="IsCfaMosaic"/>) and a three-plane debayered image alike; any other image reads nothing. Translation
    /// only: no rotation, scale or distortion is fitted.
    /// </summary>
    /// <param name="stars">The frame's stars, in the coordinates of THIS image (a mosaic detection's centroids are the mosaic's,
    /// as <see cref="FindStarsAsync"/> returns them; a debayered image's are the plane's).</param>
    /// <param name="reference">The colour the others are measured against.</param>
    /// <param name="options">The gates; <see cref="ChannelOffsetOptions.Default"/> when null.</param>
    /// <remarks>
    /// <para><b>On a mosaic each colour is read on its own photosites and nothing else, at their TRUE positions.</b> A red
    /// photosite sits at (even column, even row) of the cell and a blue one at (odd, odd), so a centroid taken on a sub-plane
    /// by its index carries the cell position as a half-pixel offset of its own. Here the coordinate of every sample is its
    /// position in the mosaic, so that term is never there, whatever the Bayer phase (the pattern comes from
    /// <see cref="ImageMeta.BayerOffsetX"/> / <see cref="ImageMeta.BayerOffsetY"/>, as <see cref="SplitBayerChannels"/> reads it).
    /// Green is its two diagonal phases together.</para>
    /// <para><b>The centroid is Gaussian-weighted and iterated</b> about its own answer, the weight's sigma the star's (from its
    /// HFD and FWHM), after the colour's own background (the median of a ring of that colour's photosites outside the window) is
    /// subtracted. Nothing is thresholded, which would bias a thin colour toward its brightest photosite, and for a symmetric star
    /// the fixed point of the weighting is the star's centre, so a window cut off-centre does not pull the answer. A star
    /// narrower than about 2.5 px HFD is sampled too coarsely on a mosaic's stride-two photosites for that to hold.</para>
    /// <para><b>A star is skipped, never repaired,</b> when it is too faint (<see cref="ChannelOffsetOptions.MinSnr"/>), too near
    /// the edge for its ring, crowded (a neighbour of
    /// <see cref="ChannelOffsetOptions.CrowdingFluxFraction"/> of its flux within reach), saturated in any colour (a flat top
    /// has no centroid), without signal above the noise in any colour, or when a colour lands more than
    /// <see cref="ChannelOffsetOptions.MaxOffsetPx"/> from the reference. All three colours must read on a star for it to count,
    /// so the three colours' counts are one number.</para>
    /// <para>Too few stars (<see cref="ChannelOffsetOptions.MinStars"/>) reads as <see cref="ChannelOffset.Unmeasured"/>:
    /// NaN, never a guess and never zero.</para>
    /// </remarks>
    public ChannelOffsetsResult ChannelOffsets(StarList stars, CfaChannel reference = CfaChannel.Green, ChannelOffsetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stars);
        options ??= ChannelOffsetOptions.Default;

        var cfa = IsCfaMosaic;
        if (!cfa && ChannelCount != 3)
        {
            return ChannelOffsetsResult.Unmeasured(reference, stars.Count);
        }

        float[][,] planes = cfa ? [GetChannelArray(0)] : [GetChannelArray(0), GetChannelArray(1), GetChannelArray(2)];
        var pattern = cfa
            ? imageMeta.SensorType.GetBayerPatternMatrix(imageMeta.BayerOffsetX & 1, imageMeta.BayerOffsetY & 1)
            : null;
        var width = Width;
        var height = Height;

        // A known full scale rejects one saturated photosite; without it the data's own peak stands in, and a single
        // photosite at the peak is the brightest star, so only a flat top of two counts.
        var saturationLevel = imageMeta.SensorFullScaleAdu is { } fullScale && fullScale > 0f ? fullScale * SaturationFraction : MaxValue * SaturationFraction;
        var saturatedPixelsAllowed = imageMeta.SensorFullScaleAdu is > 0f ? 0 : 1;
        if (float.IsNaN(saturationLevel))
        {
            saturationLevel = float.PositiveInfinity;
        }

        // Every star, by x, for the crowding test; the candidates, brightest first, for the read.
        var all = stars.ToArray();
        Array.Sort(all, static (a, b) => a.XCentroid.CompareTo(b.XCentroid));
        var candidates = new List<ImagedStar>(all.Length);
        foreach (var star in all)
        {
            if (star.SNR >= options.MinSnr && float.IsFinite(star.XCentroid) && float.IsFinite(star.YCentroid) && star.Flux > 0f)
            {
                candidates.Add(star);
            }
        }
        candidates.Sort(static (a, b) => b.Flux.CompareTo(a.Flux));

        var wanted = Math.Min(candidates.Count, options.MaxStars);
        using var firstDx = ArrayPoolHelper.Rent<float>(Math.Max(wanted, 1));
        using var firstDy = ArrayPoolHelper.Rent<float>(Math.Max(wanted, 1));
        using var secondDx = ArrayPoolHelper.Rent<float>(Math.Max(wanted, 1));
        using var secondDy = ArrayPoolHelper.Rent<float>(Math.Max(wanted, 1));
        var used = 0;
        var others = OthersThan(reference);

        // The brightest first, over EVERY candidate until MaxStars have read: a crowded or saturated one is skipped, not counted.
        foreach (var star in candidates)
        {
            if (used >= options.MaxStars)
            {
                break;
            }

            var sigma = Math.Clamp((0.5f * (star.HFD + star.StarFWHM)) / 2.355f, ChannelOffsetMinSigma, ChannelOffsetMaxSigma);
            var radius = (int)MathF.Ceiling(3f * sigma) + 1;
            if (IsCrowded(all, star, 2 * radius + ChannelOffsetRingWidth, options.CrowdingFluxFraction))
            {
                continue;
            }

            var refChannel = (int)reference;
            if (!TryCentroid(planes, pattern, width, height, cfa ? 0 : refChannel, cfa ? refChannel : -1, star.XCentroid, star.YCentroid, sigma, radius,
                    maxExcursion: 1.5f, saturationLevel, saturatedPixelsAllowed, out var refX, out var refY))
            {
                continue;
            }

            var ok = true;
            Span<double> dx = stackalloc double[2];
            Span<double> dy = stackalloc double[2];
            for (var i = 0; i < 2 && ok; i++)
            {
                var channel = (int)others[i];
                ok = TryCentroid(planes, pattern, width, height, cfa ? 0 : channel, cfa ? channel : -1, (float)refX, (float)refY, sigma, radius,
                    maxExcursion: options.MaxOffsetPx, saturationLevel, saturatedPixelsAllowed, out var x, out var y);
                dx[i] = x - refX;
                dy[i] = y - refY;
            }

            if (!ok)
            {
                continue;
            }

            firstDx[used] = (float)dx[0];
            firstDy[used] = (float)dy[0];
            secondDx[used] = (float)dx[1];
            secondDy[used] = (float)dy[1];
            used++;
        }

        if (used < options.MinStars)
        {
            return ChannelOffsetsResult.Unmeasured(reference, stars.Count);
        }

        var zero = new ChannelOffset(0d, 0d, used, 0d, 0d);
        var first = Summarise(firstDx.AsSpan(0, used), firstDy.AsSpan(0, used));
        var second = Summarise(secondDx.AsSpan(0, used), secondDy.AsSpan(0, used));
        return reference switch
        {
            CfaChannel.Red => new ChannelOffsetsResult(reference, zero, first, second, stars.Count),
            CfaChannel.Green => new ChannelOffsetsResult(reference, first, zero, second, stars.Count),
            _ => new ChannelOffsetsResult(reference, first, second, zero, stars.Count),
        };
    }

    /// <summary>The two colours that are not <paramref name="reference"/>, in the order red, green, blue.</summary>
    private static CfaChannel[] OthersThan(CfaChannel reference) => reference switch
    {
        CfaChannel.Red => [CfaChannel.Green, CfaChannel.Blue],
        CfaChannel.Green => [CfaChannel.Red, CfaChannel.Blue],
        _ => [CfaChannel.Red, CfaChannel.Green],
    };

    private static ChannelOffset Summarise(Span<float> dx, Span<float> dy)
    {
        var count = dx.Length;
        var (mx, madX) = StatisticsHelper.MedianAndMad(dx);
        var (my, madY) = StatisticsHelper.MedianAndMad(dy);
        return new ChannelOffset(mx, my, count, StatisticsHelper.MAD_TO_SD * madX, StatisticsHelper.MAD_TO_SD * madY);
    }

    /// <summary>
    /// Whether a star at least <paramref name="fluxFraction"/> as bright as <paramref name="star"/> lies within
    /// <paramref name="reach"/> of it. <paramref name="byX"/> is sorted by x, so only the strip of x is walked.
    /// </summary>
    private static bool IsCrowded(ImagedStar[] byX, in ImagedStar star, float reach, float fluxFraction)
    {
        var lo = 0;
        var hi = byX.Length;
        var from = star.XCentroid - reach;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (byX[mid].XCentroid < from)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var reachSquared = reach * reach;
        for (var i = lo; i < byX.Length && byX[i].XCentroid <= star.XCentroid + reach; i++)
        {
            var other = byX[i];
            var dx = other.XCentroid - star.XCentroid;
            var dy = other.YCentroid - star.YCentroid;
            var distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared > 1e-4f && distanceSquared <= reachSquared && other.Flux >= fluxFraction * star.Flux)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One colour's Gaussian-weighted centroid of the star near (<paramref name="startX"/>, <paramref name="startY"/>), on the
    /// photosites of <paramref name="colour"/> (every pixel when it is -1), each at its position in the frame. Reads zero-based array
    /// positions. False where the star is too near the edge for its background ring, saturated, without signal above the noise,
    /// or the centroid leaves <paramref name="maxExcursion"/> from the start.
    /// </summary>
    private static bool TryCentroid(float[][,] planes, int[,]? pattern, int width, int height, int planeIndex, int colour,
        float startX, float startY, float sigma, int radius, float maxExcursion, float saturationLevel, int saturatedPixelsAllowed,
        out double x, out double y)
    {
        x = double.NaN;
        y = double.NaN;
        var plane = planes[planeIndex];
        var ringOuter = radius + ChannelOffsetRingWidth;
        var ix = (int)MathF.Round(startX);
        var iy = (int)MathF.Round(startY);
        if (ix - ringOuter < 0 || iy - ringOuter < 0 || ix + ringOuter >= width || iy + ringOuter >= height)
        {
            return false;
        }

        bool Sampled(int px, int py) => pattern is null || pattern[py & 1, px & 1] == colour;

        // The colour's own background: the median of its photosites in the ring outside the window, and the noise from the MAD.
        Span<float> ring = stackalloc float[ChannelOffsetMaxRingPixels];
        var ringCount = 0;
        var inner = (radius + 1) * (radius + 1);
        var outer = ringOuter * ringOuter;
        for (var py = iy - ringOuter; py <= iy + ringOuter; py++)
        {
            for (var px = ix - ringOuter; px <= ix + ringOuter; px++)
            {
                var d2 = ((px - ix) * (px - ix)) + ((py - iy) * (py - iy));
                if (d2 >= inner && d2 <= outer && Sampled(px, py) && !float.IsNaN(plane[py, px]) && ringCount < ChannelOffsetMaxRingPixels)
                {
                    ring[ringCount++] = plane[py, px];
                }
            }
        }

        if (ringCount < 8)
        {
            return false;
        }

        var (background, mad) = StatisticsHelper.MedianAndMad(ring[..ringCount]);
        var noise = MathF.Max(StatisticsHelper.MAD_TO_SD * mad, 1e-6f * MathF.Max(1f, MathF.Abs(background)));

        var cx = (double)startX;
        var cy = (double)startY;
        var inverseTwoSigmaSquared = 1d / (2d * sigma * sigma);
        var radiusSquared = (double)radius * radius;
        var converged = false;
        double weightSquared = 0d;
        double weightedFlux = 0d;
        for (var iteration = 0; iteration < ChannelOffsetMaxIterations; iteration++)
        {
            double sf = 0d, sx = 0d, sy = 0d, sw2 = 0d;
            var yLo = (int)Math.Floor(cy - radius);
            var yHi = (int)Math.Ceiling(cy + radius);
            var xLo = (int)Math.Floor(cx - radius);
            var xHi = (int)Math.Ceiling(cx + radius);
            for (var py = yLo; py <= yHi; py++)
            {
                for (var px = xLo; px <= xHi; px++)
                {
                    var ddx = px - cx;
                    var ddy = py - cy;
                    var d2 = (ddx * ddx) + (ddy * ddy);
                    if (d2 > radiusSquared || !Sampled(px, py))
                    {
                        continue;
                    }

                    var value = plane[py, px];
                    if (float.IsNaN(value))
                    {
                        return false;
                    }

                    var w = Math.Exp(-d2 * inverseTwoSigmaSquared);
                    var f = value - background;
                    sf += w * f;
                    sx += w * f * px;
                    sy += w * f * py;
                    sw2 += w * w;
                }
            }

            if (sf <= 0d)
            {
                return false;
            }

            var nx = sx / sf;
            var ny = sy / sf;
            var moved = Math.Sqrt(((nx - cx) * (nx - cx)) + ((ny - cy) * (ny - cy)));
            cx = nx;
            cy = ny;
            weightedFlux = sf;
            weightSquared = sw2;
            if (Math.Abs(cx - startX) > maxExcursion || Math.Abs(cy - startY) > maxExcursion)
            {
                return false;
            }

            if (moved < ChannelOffsetConvergedPx)
            {
                converged = true;
                break;
            }
        }

        // A centroid still walking after the budget is not a fixed point; a window with no signal over its own noise has none.
        if (!converged || weightedFlux < ChannelOffsetSignalSigmas * noise * Math.Sqrt(weightSquared))
        {
            return false;
        }

        // Saturation: a flat top has no centroid. Looked for over the window about the answer, in this colour's photosites.
        var saturated = 0;
        var sxLo = (int)Math.Floor(cx - radius);
        var sxHi = (int)Math.Ceiling(cx + radius);
        var syLo = (int)Math.Floor(cy - radius);
        var syHi = (int)Math.Ceiling(cy + radius);
        for (var py = syLo; py <= syHi; py++)
        {
            for (var px = sxLo; px <= sxHi; px++)
            {
                if (Sampled(px, py) && plane[py, px] >= saturationLevel && ++saturated > saturatedPixelsAllowed)
                {
                    return false;
                }
            }
        }

        x = cx;
        y = cy;
        return true;
    }
}
