using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R1's synthetic half: what each resampling kernel does to a star's width when a frame is placed at a
/// fractional shift. Measured on the Orion 2025-10-15 night (docs/plans/deconvolver-training.md,
/// "the third finding placed"), the frames at fractional shifts widened from 2.15 px to 2.4 to 2.7 under
/// bilinear while integer-phase frames kept 2.15, and every master was the mean of its warped frames.
/// Here the truth is a Gaussian field the test rendered, shifted by half a pixel on both axes, and the
/// width is a background-subtracted second moment about the known position, model-free.
/// </summary>
[Collection("Imaging")]
public class WarpInterpolationTests(ITestOutputHelper output)
{
    private const int Size = 320;
    private const float Background = 1000f;
    private const float Sigma = 0.9f;          // FWHM 2.12 px, the Orion night's green width
    private const float Amplitude = 20000f;
    private const float NoiseSigma = 3f;

    private static readonly (float X, float Y)[] Stars =
    [
        (60.37f, 70.62f), (150.13f, 60.88f), (230.74f, 150.26f), (80.51f, 210.44f), (170.62f, 250.19f), (260.30f, 40.70f),
        (40.05f, 140.95f), (120.50f, 120.50f), (200.25f, 90.75f), (280.90f, 230.10f), (100.33f, 280.67f), (210.80f, 200.20f),
    ];

    private static ImageMeta Meta => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    private static (Image Image, float[,] Plane) Render(bool constant = false)
    {
        var rng = new Random(11);
        var data = new float[Size, Size];
        var twoSigmaSq = 2f * Sigma * Sigma;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = Background;
                if (!constant)
                {
                    foreach (var (sx, sy) in Stars)
                    {
                        var dx = x - sx;
                        var dy = y - sy;
                        v += Amplitude * MathF.Exp(-((dx * dx) + (dy * dy)) / twoSigmaSq);
                    }

                    var u1 = 1.0 - rng.NextDouble();
                    var u2 = rng.NextDouble();
                    v += (float)(NoiseSigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                }

                data[y, x] = v;
            }
        }

        return (new Image([data], BitDepth.Float32, Background + Amplitude, Background - (5f * NoiseSigma), 0f, Meta), data);
    }

    /// <summary>Background-subtracted second-moment FWHM about a known centre, in a 15 by 15 window, with
    /// the background the median of the window's rim. Model-free, so the kernel's own shape is measured
    /// and not a fit's opinion of it.</summary>
    private static double MomentFwhm(float[,] plane, float cx, float cy)
    {
        var ix = (int)MathF.Round(cx);
        var iy = (int)MathF.Round(cy);
        var rim = new List<float>();
        for (var dy = -7; dy <= 7; dy++)
        {
            for (var dx = -7; dx <= 7; dx++)
            {
                if (Math.Abs(dx) == 7 || Math.Abs(dy) == 7)
                {
                    rim.Add(plane[iy + dy, ix + dx]);
                }
            }
        }

        rim.Sort();
        var bg = rim[rim.Count / 2];
        double sum = 0, sx = 0, sy = 0;
        for (var dy = -6; dy <= 6; dy++)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                var v = plane[iy + dy, ix + dx] - bg;
                if (v <= 0f)
                {
                    continue;
                }

                sum += v;
                sx += v * dx;
                sy += v * dy;
            }
        }

        var mx = sx / sum;
        var my = sy / sum;
        double vx = 0, vy = 0;
        for (var dy = -6; dy <= 6; dy++)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                var v = plane[iy + dy, ix + dx] - bg;
                if (v <= 0f)
                {
                    continue;
                }

                vx += v * (dx - mx) * (dx - mx);
                vy += v * (dy - my) * (dy - my);
            }
        }

        return 2.3548 * Math.Sqrt((vx + vy) / (2.0 * sum));
    }

    private static float[,] PlaneOf(Image image) => image.GetChannel(0).Data;

    [Fact]
    public async Task AnIntegerShiftUnderLanczosReturnsThePixelsExactly()
    {
        var (image, plane) = Render();
        var warped = await image.WarpToReferenceGridAsync(Matrix3x2.CreateTranslation(3f, -2f), Size, Size, WarpInterpolation.Lanczos3);
        var result = PlaneOf(warped);
        var worst = 0f;
        for (var y = 0; y < Size - 2; y++)
        {
            for (var x = 3; x < Size; x++)
            {
                worst = MathF.Max(worst, MathF.Abs(result[y, x] - plane[y + 2, x - 3]));
            }
        }

        worst.ShouldBeLessThan(1e-2f, "an integer shift is a copy under a kernel that is 1 at zero and 0 at every other integer");
    }

    [Fact]
    public async Task AConstantPlaneStaysConstantUnderLanczosAtAFractionalShift()
    {
        var (image, _) = Render(constant: true);
        var warped = await image.WarpToReferenceGridAsync(Matrix3x2.CreateTranslation(0.5f, 0.5f), Size, Size, WarpInterpolation.Lanczos3);
        var result = PlaneOf(warped);
        for (var y = 4; y < Size - 4; y++)
        {
            for (var x = 4; x < Size - 4; x++)
            {
                result[y, x].ShouldBe(Background, tolerance: 0.05f, $"at ({x}, {y})");
            }
        }
    }

    /// <summary>
    /// The pre-registered prediction: bilinear widens a 2.1 px star by 1.1 to 1.3 px in quadrature at half
    /// phase (a triangle of unit base has a quarter of a pixel of variance there, 1.18 px of FWHM), and
    /// Lanczos-3 by under 0.4. The detector's own median width is printed beside the moment for the real
    /// frames' measure to be read against.
    /// </summary>
    [Fact]
    public async Task AHalfPixelShiftWidensAStarUnderBilinearAndNotUnderLanczos()
    {
        var (image, plane) = Render();
        var shift = Matrix3x2.CreateTranslation(0.5f, 0.5f);
        var bilinear = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Bilinear));
        var lanczos = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3));
        var clamped = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3Clamped));

        static double Median(IEnumerable<double> v)
        {
            var s = v.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }

        var source = Median(Stars.Select(s => MomentFwhm(plane, s.X, s.Y)));
        var underBilinear = Median(Stars.Select(s => MomentFwhm(bilinear, s.X + 0.5f, s.Y + 0.5f)));
        var underLanczos = Median(Stars.Select(s => MomentFwhm(lanczos, s.X + 0.5f, s.Y + 0.5f)));
        var underClamped = Median(Stars.Select(s => MomentFwhm(clamped, s.X + 0.5f, s.Y + 0.5f)));
        static double Added(double after, double before) => after > before ? Math.Sqrt((after * after) - (before * before)) : 0.0;
        var bilinearAdd = Added(underBilinear, source);
        var lanczosAdd = Added(underLanczos, source);
        var clampedAdd = Added(underClamped, source);

        var detectorSource = (await image.FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        var detectorBilinear = (await (await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Bilinear)).FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        var detectorLanczos = (await (await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3)).FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        var detectorClamped = (await (await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3Clamped)).FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        output.WriteLine($"moment FWHM: source {source:F3}, bilinear {underBilinear:F3} (+{bilinearAdd:F2} in quadrature), lanczos3 {underLanczos:F3} (+{lanczosAdd:F2}), lanczos3 clamped {underClamped:F3} (+{clampedAdd:F2})");

        // The sinc kernel's negative lobe, as the deepest pixel below the background within 6 px of each
        // star relative to its peak: what a Moffat fit of the wings sees and an annulus minimum on the
        // wing does not.
        static double DeepestDip(float[,] p, float cx, float cy)
        {
            var ix = (int)MathF.Round(cx);
            var iy = (int)MathF.Round(cy);
            var peak = 0f;
            var min = float.MaxValue;
            for (var dy = -6; dy <= 6; dy++)
            {
                for (var dx = -6; dx <= 6; dx++)
                {
                    var v = p[iy + dy, ix + dx] - Background;
                    peak = MathF.Max(peak, v);
                    min = MathF.Min(min, v);
                }
            }

            return min / peak;
        }

        output.WriteLine($"deepest dip below background within 6 px, over the star's peak: source {Median(Stars.Select(s => DeepestDip(plane, s.X, s.Y))):P2}, bilinear {Median(Stars.Select(s => DeepestDip(bilinear, s.X + 0.5f, s.Y + 0.5f))):P2}, lanczos3 {Median(Stars.Select(s => DeepestDip(lanczos, s.X + 0.5f, s.Y + 0.5f))):P2}, lanczos3 clamped {Median(Stars.Select(s => DeepestDip(clamped, s.X + 0.5f, s.Y + 0.5f))):P2}");
        output.WriteLine($"detector median FWHM: source {Median(detectorSource):F3} ({detectorSource.Count}), bilinear {Median(detectorBilinear):F3} ({detectorBilinear.Count}), lanczos3 {Median(detectorLanczos):F3} ({detectorLanczos.Count}), lanczos3 clamped {Median(detectorClamped):F3} ({detectorClamped.Count})");

        source.ShouldBe(2.12, tolerance: 0.15);
        bilinearAdd.ShouldBeInRange(0.9, 1.5);
        lanczosAdd.ShouldBeLessThan(0.4);
        // The clamp is inert on a smooth profile: a 2 px mono star keeps Lanczos-3's width.
        clampedAdd.ShouldBeLessThan(0.4);
    }

    /// <summary>
    /// A debayered OSC plane samples a 2 px star on a 2 px pitch, so per plane the star is a spike with
    /// 6 percent at its neighbours, and the plain kernel's negative lobes dig a ring of about 13 percent
    /// of the peak two pixels out (the synthetic RGGB fixture's subs measured 400 to 2000 ADU below a
    /// 1000 ADU sky next to a 15000 ADU peak). The clamp at PixInsight's threshold bounds that ring
    /// while keeping the peak; bilinear never rings and is the floor the clamp is judged against.
    /// </summary>
    [Fact]
    public async Task TheClampBoundsTheRingAPerPlaneSpikeDrawsAndLeavesTheKernelAloneElsewhere()
    {
        const int size = 64;
        const float background = 1000f;
        const float peak = 15000f;
        var data = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                data[y, x] = background;
            }
        }

        // The spike a debayered plane makes of a 0.85 sigma star sampled every 2 px: the centre and,
        // two pixels out, exp(-4 / (2 * 0.85^2)) of it.
        var wing = peak * MathF.Exp(-4f / (2f * 0.85f * 0.85f));
        data[32, 32] = background + peak;
        data[32, 30] = background + wing;
        data[32, 34] = background + wing;
        data[30, 32] = background + wing;
        data[34, 32] = background + wing;
        var image = new Image([data], BitDepth.Float32, background + peak, background, 0f, Meta);
        var shift = Matrix3x2.CreateTranslation(0.5f, 0.5f);

        var plain = PlaneOf(await image.WarpToReferenceGridAsync(shift, size, size, WarpInterpolation.Lanczos3));
        var clamped = PlaneOf(await image.WarpToReferenceGridAsync(shift, size, size, WarpInterpolation.Lanczos3Clamped));
        var bilinear = PlaneOf(await image.WarpToReferenceGridAsync(shift, size, size, WarpInterpolation.Bilinear));

        static (float Min, float Max) Extremes(float[,] p)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var y = 26; y <= 40; y++)
            {
                for (var x = 26; x <= 40; x++)
                {
                    min = MathF.Min(min, p[y, x]);
                    max = MathF.Max(max, p[y, x]);
                }
            }

            return (min, max);
        }

        var (plainMin, plainMax) = Extremes(plain);
        var (clampedMin, clampedMax) = Extremes(clamped);
        var (bilinearMin, bilinearMax) = Extremes(bilinear);
        output.WriteLine($"ring below the sky, over the peak: plain {(background - plainMin) / peak:P1}, clamped {(background - clampedMin) / peak:P1}, bilinear {(background - bilinearMin) / peak:P1}; peak kept: plain {(plainMax - background) / peak:P1}, clamped {(clampedMax - background) / peak:P1}, bilinear {(bilinearMax - background) / peak:P1}");

        // 5.9 percent measured on this clean spike; the VNG-debayered fixture's subs, sharper still, ring 13.
        ((background - plainMin) / peak).ShouldBeGreaterThan(0.04f, "the plain kernel rings on a spike, which is what the clamp is for");
        ((background - clampedMin) / peak).ShouldBeLessThan(0.03f);
        bilinearMin.ShouldBeGreaterThanOrEqualTo(background - 0.01f);
        // The clamp attenuates negative lobes only: the interpolated peak stays where Lanczos put it,
        // above bilinear's, which averages a half-phase spike down.
        clampedMax.ShouldBeGreaterThan(bilinearMax);
    }

    /// <summary>
    /// Clamped Lanczos-3 is the default since 7.1 (decided 2026-09-12 on R1's reading and the clamp
    /// sweep), and a default is a behaviour rather than a declaration: the overload that names no
    /// kernel must warp exactly as the clamped one does, and the dataset bake must hand the registrar
    /// the same choice, or a retained master and a user's master of the same night are built
    /// differently and nothing says so.
    /// </summary>
    [Fact]
    public async Task TheKernelNobodyNamesIsClampedLanczos3()
    {
        var (image, _) = Render();
        var shift = Matrix3x2.CreateTranslation(0.5f, 0.5f);

        var unnamed = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size));
        var lanczos = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3Clamped));
        var bilinear = PlaneOf(await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Bilinear));

        // float.Equals, not ==: a half-pixel shift leaves the first row and column NaN under every
        // kernel, and NaN != NaN would count those 639 pixels as a difference.
        var differsFromLanczos = 0;
        var differsFromBilinear = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!unnamed[y, x].Equals(lanczos[y, x]))
                {
                    differsFromLanczos++;
                }

                if (!unnamed[y, x].Equals(bilinear[y, x]))
                {
                    differsFromBilinear++;
                }
            }
        }

        differsFromLanczos.ShouldBe(0);
        differsFromBilinear.ShouldBeGreaterThan(0, "a half-pixel shift under the two kernels cannot agree, so the pin is real");
        new DatasetBuildOptions { ArchiveRoots = [], OutputDir = "unused" }.WarpInterpolation.ShouldBe(WarpInterpolation.Lanczos3Clamped);
    }
}
