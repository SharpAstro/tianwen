using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
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

        static double Median(IEnumerable<double> v)
        {
            var s = v.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }

        var source = Median(Stars.Select(s => MomentFwhm(plane, s.X, s.Y)));
        var underBilinear = Median(Stars.Select(s => MomentFwhm(bilinear, s.X + 0.5f, s.Y + 0.5f)));
        var underLanczos = Median(Stars.Select(s => MomentFwhm(lanczos, s.X + 0.5f, s.Y + 0.5f)));
        static double Added(double after, double before) => after > before ? Math.Sqrt((after * after) - (before * before)) : 0.0;
        var bilinearAdd = Added(underBilinear, source);
        var lanczosAdd = Added(underLanczos, source);

        var detectorSource = (await image.FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        var detectorBilinear = (await (await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Bilinear)).FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        var detectorLanczos = (await (await image.WarpToReferenceGridAsync(shift, Size, Size, WarpInterpolation.Lanczos3)).FindStarsAsync(0, snrMin: 20f)).Where(s => s.StarFWHM > 0f).Select(s => (double)s.StarFWHM).ToList();
        output.WriteLine($"moment FWHM: source {source:F3}, bilinear {underBilinear:F3} (+{bilinearAdd:F2} in quadrature), lanczos3 {underLanczos:F3} (+{lanczosAdd:F2})");

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

        output.WriteLine($"deepest dip below background within 6 px, over the star's peak: source {Median(Stars.Select(s => DeepestDip(plane, s.X, s.Y))):P2}, bilinear {Median(Stars.Select(s => DeepestDip(bilinear, s.X + 0.5f, s.Y + 0.5f))):P2}, lanczos3 {Median(Stars.Select(s => DeepestDip(lanczos, s.X + 0.5f, s.Y + 0.5f))):P2}");
        output.WriteLine($"detector median FWHM: source {Median(detectorSource):F3} ({detectorSource.Count}), bilinear {Median(detectorBilinear):F3} ({detectorBilinear.Count}), lanczos3 {Median(detectorLanczos):F3} ({detectorLanczos.Count})");

        source.ShouldBe(2.12, tolerance: 0.15);
        bilinearAdd.ShouldBeInRange(0.9, 1.5);
        lanczosAdd.ShouldBeLessThan(0.4);
    }
}
