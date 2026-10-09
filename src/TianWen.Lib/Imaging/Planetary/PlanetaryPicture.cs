using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planetary master's picture in numbers, for setting several of one capture side by side (<c>tianwen planetary compare</c>): the
/// stack, the batch's sharpening, the live view's, another program's. Every value is in the image's own disk units (its sky 0, its disk's
/// mean inside 0.8 radii 1, <see cref="PlanetaryMetrics.Normalise"/>), so pictures of different brightness compare. Three regions:
/// <list type="bullet">
/// <item>the disk (inside <see cref="DiskRadii"/>): its contrast, its detail in each a trous band, and how many pixels are clipped at the
/// image's peak or held at its sky;</item>
/// <item>the limb's glow (1 to <see cref="GlowRadii"/>): the light found against the light the planet's own model through the pupil
/// expects there, the truth's diffraction glow, and how many pixels lie further than <see cref="Sigmas"/> noise from it either way;</item>
/// <item>the sky (past <see cref="SkyRadii"/>): its noise, its gradient across the frame, its structure (block means over what the noise
/// gives them) and its outliers found against the count a Gaussian of that noise expects.</item>
/// </list>
/// A moon (<see cref="PlanetaryMetrics.CompactSources"/>) is left out of the glow and the sky, within <see cref="MoonReachPx"/>.
/// </summary>
public sealed record PlanetaryPicture(
    double Contrast,
    ImmutableArray<double> BandRms,
    int AtPeak,
    int HeldAtSky,
    double GlowFound,
    double GlowExpected,
    double GlowNearFound,
    double GlowNearExpected,
    double GlowExcess,
    double GlowDeficit,
    int GlowBrighter,
    int GlowDarker,
    int GlowPixels,
    double SkyNoise,
    double SkyGradient,
    double SkyBlockScatter,
    int SkyBright,
    int SkyDark,
    double SkyOutliersExpected,
    int SkyPixels,
    int Moons)
{
    /// <summary>The disk's statistics are read inside this many radii, clear of the limb's blur.</summary>
    public const double DiskRadii = 0.9;

    /// <summary>The limb's glow is read from the limb out to this many radii, where the batch's model hands back to the stack.</summary>
    public const double GlowRadii = 1.5;

    /// <summary>
    /// The glow's near part, from the limb to this many radii, where the model and a rendered truth agree within 3 %; past it the model's
    /// wing and the renderer's part ways (#1213), so the near part is the one a glow is held to.
    /// </summary>
    public const double GlowNearRadii = 1.1;

    /// <summary>The sky is read past this many radii, as the metrics read it (<see cref="PlanetaryMetrics.SkyLevel"/>).</summary>
    public const double SkyRadii = 2.5;

    /// <summary>How far round a moon the glow and the sky are not read, px: a stack's moon is 15 px wide above half.</summary>
    public const int MoonReachPx = 25;

    /// <summary>How many noise a pixel must lie from what is expected to count as brighter or darker.</summary>
    public const double Sigmas = 3;

    /// <summary>The side of the sky's blocks, px, whose means set its structure.</summary>
    public const int BlockPx = 32;

    /// <summary>
    /// <paramref name="plane"/> (<paramref name="width"/> by <paramref name="height"/>, row-major) measured about its planet's
    /// <paramref name="disk"/>. <paramref name="expected"/> is the glow the limb should have, full frame in disk units (<see cref="Diffracted"/>),
    /// or empty for none; <paramref name="glowNoise"/> the noise the glow's pixels are counted against, in disk units, so several pictures
    /// count against one (a sharpening lifts its own sky's noise), or null for the image's own. <paramref name="moons"/> are the moons
    /// left out of the glow and the sky (<see cref="MoonsOf"/>), the reference's for several pictures of one frame, since a sharpening's
    /// ring about the limb holds local maxima the moon finder takes for moons; null finds the image's own.
    /// </summary>
    public static PlanetaryPicture Measure(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, ReadOnlySpan<float> expected = default,
        double? glowNoise = null, ImmutableArray<(int X, int Y)>? moons = null)
    {
        var n = width * height;
        var v = PlanetaryMetrics.Normalise(plane, width, height, disk);
        var moonsHere = moons ?? PlanetaryMetrics.CompactSources(v, width, height, disk, count: PlanetaryDering.MaxMoons);
        var radii = new float[n];
        var masked = new bool[n];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                radii[i] = (float)disk.RadiiAt(x, y);
                foreach (var (mx, my) in moonsHere)
                {
                    if (((x - mx) * (x - mx)) + ((y - my) * (y - my)) <= MoonReachPx * MoonReachPx)
                    {
                        masked[i] = true;
                        break;
                    }
                }
            }
        }

        // The disk: its contrast and detail, and what is clipped.
        var peak = float.MinValue;
        foreach (var value in plane)
        {
            peak = Math.Max(peak, value);
        }
        double sum = 0, sumSquares = 0;
        int count = 0, atPeak = 0, heldAtSky = 0;
        for (var i = 0; i < n; i++)
        {
            if (radii[i] <= 1 && plane[i] == peak)
            {
                atPeak++;
            }
            if (radii[i] <= 0.95 && v[i] <= 0)
            {
                heldAtSky++;
            }
            if (radii[i] <= DiskRadii)
            {
                sum += v[i];
                sumSquares += (double)v[i] * v[i];
                count++;
            }
        }
        var mean = count > 0 ? sum / count : double.NaN;
        var contrast = count > 1 ? Math.Sqrt(Math.Max((sumSquares / count) - (mean * mean), 0)) / mean : double.NaN;
        var decomposition = ATrousWaveletTransform.Decompose(v, width, height, 4);
        var bands = ImmutableArray.CreateBuilder<double>(decomposition.ScaleCount);
        for (var j = 0; j < decomposition.ScaleCount; j++)
        {
            var detail = decomposition.Detail(j);
            double energy = 0;
            for (var i = 0; i < n; i++)
            {
                if (radii[i] <= DiskRadii)
                {
                    energy += (double)detail[i] * detail[i];
                }
            }
            bands.Add(count > 0 ? Math.Sqrt(energy / count) : double.NaN);
        }

        // The sky: its noise from the median deviation, its plane by least squares, its blocks and its outliers about that plane.
        var skyCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (radii[i] >= SkyRadii && !masked[i])
            {
                skyCount++;
            }
        }
        var (noise, gradient, blockScatter, bright, dark, outliersExpected) = (double.NaN, double.NaN, double.NaN, 0, 0, 0.0);
        if (skyCount >= 16)
        {
            // The noise is the spread about the plane, so a gradient is not read as noise.
            var (a, b, c) = SkyPlane(v, radii, masked, width, height);
            gradient = (Math.Abs(b) * width) + (Math.Abs(c) * height);
            var sky = new float[skyCount];
            var k = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    if (radii[i] >= SkyRadii && !masked[i])
                    {
                        sky[k++] = (float)(v[i] - (a + (b * x) + (c * y)));
                    }
                }
            }
            var median = StatisticsHelper.MedianFast(sky);
            for (k = 0; k < skyCount; k++)
            {
                sky[k] = Math.Abs(sky[k] - median);
            }
            noise = 1.4826 * StatisticsHelper.MedianFast(sky);
            blockScatter = BlockScatter(v, radii, masked, width, height, a, b, c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    if (radii[i] < SkyRadii || masked[i])
                    {
                        continue;
                    }
                    var residual = v[i] - (a + (b * x) + (c * y));
                    if (residual > Sigmas * noise)
                    {
                        bright++;
                    }
                    else if (residual < -Sigmas * noise)
                    {
                        dark++;
                    }
                }
            }
            outliersExpected = skyCount * 0.5 * Erfc(Sigmas / Math.Sqrt(2));
        }

        // The limb's glow against the planet's own model through the pupil.
        var threshold = Sigmas * (glowNoise ?? noise);
        double found = 0, expectedSum = 0, excess = 0, deficit = 0, nearFound = 0, nearExpected = 0;
        int brighter = 0, darker = 0, glowPixels = 0;
        for (var i = 0; i < n; i++)
        {
            if (radii[i] <= 1 || radii[i] > GlowRadii || masked[i])
            {
                continue;
            }
            glowPixels++;
            found += v[i];
            var near = radii[i] <= GlowNearRadii;
            if (near)
            {
                nearFound += v[i];
            }
            if (expected.IsEmpty)
            {
                continue;
            }
            var difference = v[i] - expected[i];
            expectedSum += expected[i];
            if (near)
            {
                nearExpected += expected[i];
            }
            excess += Math.Max(difference, 0);
            deficit += Math.Max(-difference, 0);
            if (difference > threshold)
            {
                brighter++;
            }
            else if (difference < -threshold)
            {
                darker++;
            }
        }
        return new PlanetaryPicture(contrast, bands.MoveToImmutable(), atPeak, heldAtSky, found, expected.IsEmpty ? double.NaN : expectedSum,
            nearFound, expected.IsEmpty ? double.NaN : nearExpected,
            expected.IsEmpty ? double.NaN : excess, expected.IsEmpty ? double.NaN : deficit, brighter, darker, glowPixels,
            noise, gradient, blockScatter, bright, dark, outliersExpected, skyCount, moonsHere.Length);
    }

    /// <summary>The moons in <paramref name="plane"/> about <paramref name="disk"/>, as <see cref="Measure"/> finds them.</summary>
    public static ImmutableArray<(int X, int Y)> MoonsOf(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
        => PlanetaryMetrics.CompactSources(PlanetaryMetrics.Normalise(plane, width, height, disk), width, height, disk, count: PlanetaryDering.MaxMoons);

    /// <summary>
    /// The glow <paramref name="fit"/>'s planet should have past its limb, full frame in disk units: its sharp model through the pupil's
    /// diffraction at <paramref name="wavelengthNm"/>, as the derived sharpening draws it there (<see cref="PlanetaryLimbFix.ModelFeathered"/>,
    /// the same window and model). Zero beyond the window, where the model has long since fallen to nothing.
    /// </summary>
    public static float[] Diffracted(in LimbFit fit, LimbFitOptions limbOptions, in PlanetAspect aspect, Pupil pupil, double wavelengthNm, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(limbOptions);
        var window = PlanetaryLimbWindow.Of(fit, limbOptions, aspect, width, height);
        var model = window.Through(window.Diffraction(pupil, wavelengthNm));
        var full = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var wy = y - window.Y0;
            if (wy < 0 || wy >= window.Size)
            {
                continue;
            }
            for (var x = 0; x < width; x++)
            {
                var wx = x - window.X0;
                if (wx >= 0 && wx < window.Size)
                {
                    full[(y * width) + x] = model[(wy * window.Size) + wx];
                }
            }
        }
        return full;
    }

    // The sky's plane a + b x + c y by least squares over its pixels.
    private static (double A, double B, double C) SkyPlane(float[] v, float[] radii, bool[] masked, int width, int height)
    {
        // Centred on the frame so the normal equations stay well conditioned.
        var (cx, cy) = (width / 2.0, height / 2.0);
        double s1 = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sv = 0, sxv = 0, syv = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (radii[i] < SkyRadii || masked[i])
                {
                    continue;
                }
                var (dx, dy, value) = (x - cx, y - cy, (double)v[i]);
                s1++;
                sx += dx;
                sy += dy;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
                sv += value;
                sxv += dx * value;
                syv += dy * value;
            }
        }
        ReadOnlySpan<double> normal = [s1, sx, sy, sx, sxx, sxy, sy, sxy, syy];
        Span<double> inverse = stackalloc double[9];
        if (!CameraColorMatrix.TryInvert3(normal, inverse))
        {
            return (s1 > 0 ? sv / s1 : 0, 0, 0);
        }
        var a = (inverse[0] * sv) + (inverse[1] * sxv) + (inverse[2] * syv);
        var b = (inverse[3] * sv) + (inverse[4] * sxv) + (inverse[5] * syv);
        var c = (inverse[6] * sv) + (inverse[7] * sxv) + (inverse[8] * syv);
        // Back from the frame's centre to its corner.
        return (a - (b * cx) - (c * cy), b, c);
    }

    // The scatter of the sky's block means about its plane, over the scatter its noise alone would give a block (its pixels' own
    // spread over the root of their count): one for a white, flat sky, more for any structure or for noise correlated between pixels.
    private static double BlockScatter(float[] v, float[] radii, bool[] masked, int width, int height, double a, double b, double c)
    {
        var means = new List<double>();
        double pixelSpread = 0;
        var pixels = 0;
        for (var by = 0; by + BlockPx <= height; by += BlockPx)
        {
            for (var bx = 0; bx + BlockPx <= width; bx += BlockPx)
            {
                double blockSum = 0;
                var whole = true;
                for (var y = by; y < by + BlockPx && whole; y++)
                {
                    for (var x = bx; x < bx + BlockPx; x++)
                    {
                        var i = (y * width) + x;
                        if (radii[i] < SkyRadii || masked[i])
                        {
                            whole = false;
                            break;
                        }
                        blockSum += v[i] - (a + (b * x) + (c * y));
                    }
                }
                if (!whole)
                {
                    continue;
                }
                var blockMean = blockSum / (BlockPx * BlockPx);
                means.Add(blockMean);
                for (var y = by; y < by + BlockPx; y++)
                {
                    for (var x = bx; x < bx + BlockPx; x++)
                    {
                        var residual = v[(y * width) + x] - (a + (b * x) + (c * y)) - blockMean;
                        pixelSpread += residual * residual;
                        pixels++;
                    }
                }
            }
        }
        if (means.Count < 2 || pixels == 0)
        {
            return double.NaN;
        }
        double meanOfMeans = 0;
        foreach (var m in means)
        {
            meanOfMeans += m;
        }
        meanOfMeans /= means.Count;
        double spread = 0;
        foreach (var m in means)
        {
            spread += (m - meanOfMeans) * (m - meanOfMeans);
        }
        var blockStd = Math.Sqrt(spread / (means.Count - 1));
        var expected = Math.Sqrt(pixelSpread / pixels) / BlockPx;
        return expected > 0 ? blockStd / expected : double.NaN;
    }

    // The complementary error function (Abramowitz and Stegun 7.1.26, to 1.5e-7), for the Gaussian's tail.
    private static double Erfc(double x)
    {
        var t = 1 / (1 + (0.3275911 * Math.Abs(x)));
        var y = t * (0.254829592 + (t * (-0.284496736 + (t * (1.421413741 + (t * (-1.453152027 + (t * 1.061405429))))))));
        var erfc = y * Math.Exp(-x * x);
        return x >= 0 ? erfc : 2 - erfc;
    }
}
