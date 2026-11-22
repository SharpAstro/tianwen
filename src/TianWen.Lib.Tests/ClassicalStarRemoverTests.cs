using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The classical starless plate builder against a field whose truth is known: a sky with a smooth nebula and a
/// gradient, noise correlated as a master's is, a knot of structure three times a star's width, and Moffat stars of
/// known position and amplitude, some saturated, a close pair, one beside the absent ring. The stars are rendered here
/// with a finer pixel integration than the builder's, so the test does not share the model it judges. The plate is
/// compared with the field before the stars were added, noise included, so what is measured is the subtraction's own
/// error and nothing else.
/// </summary>
// Not in the "Imaging" collection, which runs its classes one at a time for no reason but grouping: this class is
// among its longest and shares no state with the rest, so it runs beside them (#2054).
public class ClassicalStarRemoverTests(ITestOutputHelper output)
{
    private const int Size = 1024;
    private const int Ring = 12;
    private const double Sigma = 0.001;
    private const double Sky = 0.1;
    private const double Fwhm = 3.0;
    private const double Beta = 3.0;
    private const double Clip = 0.9;

    private sealed record Injected(double X, double Y, double Peak, bool Saturated);

    private sealed record Field(Image Image, float[][] Truth, List<Injected> Stars, (double X, double Y, double Peak, double Fwhm) Knot);

    /// <summary>The plain mono field, for a test outside this class that only needs a plate of stars to fit.</summary>
    internal static Image MonoField() => MakeField(1).Image;

    // Textured: a patch of nebula texture (a 4 sigma ripple 40 px in period, faded out over 100 px) about (750, 700), which a
    // smooth glow is not. Dents: single pixels of the finished image moved by so many sigma after the stars are rendered (a
    // peak pixel lower than the star's profile, or a dark pixel of the input's own), in dentChannel alone or in every channel.
    // Correlation: the noise's between neighbours, a stack's 0.35 unless a test asks for more (0.7 is the kernel's most).
    private static Field MakeField(int channels, int seed = 7, (double X, double Y, double Peak)? giant = null, bool textured = false, Injected[]? extra = null, double[]? skies = null, double[]? gains = null, (int X, int Y, double Sigmas)[]? dents = null, int? dentChannel = null, double correlation = 0.35)
    {
        var rng = new Random(seed);
        var alpha = Fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0));
        var noise = CorrelatedNoise(rng, Size, Size, correlation);
        var knot = (X: 700.0, Y: 300.0, Peak: 40 * Sigma, Fwhm: 3 * Fwhm);

        var truth = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            truth[c] = new float[Size * Size];
            var cNoise = c == 0 ? noise : CorrelatedNoise(rng, Size, Size, correlation);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var nebula = 0.03 * Math.Exp(-((x - 400.0) * (x - 400.0) + (y - 600.0) * (y - 600.0)) / (2 * 120.0 * 120.0));
                    var gradient = 0.01 * x / Size;
                    var ks = knot.Fwhm / 2.3548;
                    var knotValue = knot.Peak * Math.Exp(-((x - knot.X) * (x - knot.X) + (y - knot.Y) * (y - knot.Y)) / (2 * ks * ks));
                    var ripple = textured
                        ? 4 * Sigma * Math.Sin(2 * Math.PI * x / 40.0) * Math.Sin(2 * Math.PI * y / 40.0)
                            * Math.Exp(-((x - 750.0) * (x - 750.0) + (y - 700.0) * (y - 700.0)) / (2 * 100.0 * 100.0))
                        : 0.0;
                    truth[c][y * Size + x] = (float)((skies?[c] ?? Sky) + nebula + gradient + knotValue + ripple + Sigma * cNoise[y * Size + x]);
                }
            }
        }

        var stars = new List<Injected>();
        for (var k = 0; k < 900; k++)
        {
            var x = Ring + 4 + rng.NextDouble() * (Size - 2 * Ring - 8);
            var y = Ring + 4 + rng.NextDouble() * (Size - 2 * Ring - 8);
            if (Math.Abs(x - knot.X) < 30 && Math.Abs(y - knot.Y) < 30)
            {
                continue;
            }
            stars.Add(new Injected(x, y, Sigma * Math.Exp(Math.Log(5) + rng.NextDouble() * (Math.Log(800) - Math.Log(5))), false));
        }
        // Saturated, far from each other; a close pair (1.33 FWHM apart); one beside the absent ring.
        foreach (var (x, y) in new[] { (150.0, 150.0), (850.0, 150.0), (150.0, 850.0), (850.0, 850.0), (512.0, 900.0) })
        {
            stars.Add(new Injected(x, y, 20.0, true));
        }
        stars.Add(new Injected(300.0, 450.0, 40 * Sigma, false));
        stars.Add(new Injected(304.0, 450.0, 20 * Sigma, false));
        stars.Add(new Injected(Ring + 4.0, 500.0, 60 * Sigma, false));
        if (giant is { } g)
        {
            stars.Add(new Injected(g.X, g.Y, g.Peak, true));
        }
        if (extra is not null)
        {
            stars.AddRange(extra);
        }

        var planes = new float[channels][,];
        gains ??= channels == 3 ? [0.8, 1.0, 0.6] : [1.0];
        for (var c = 0; c < channels; c++)
        {
            float[] plane = [.. truth[c]];
            foreach (var s in stars)
            {
                Render(plane, s.X, s.Y, s.Peak * gains[c], alpha, Beta);
            }
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var absent = x < Ring || y < Ring || x >= Size - Ring || y >= Size - Ring;
                    var v = Math.Min(plane[y * Size + x], (float)Clip);
                    planes[c][y, x] = absent ? 0f : v;
                    if (absent)
                    {
                        truth[c][y * Size + x] = 0f;
                    }
                }
            }
            if (dents is not null && (dentChannel is null || dentChannel == c))
            {
                foreach (var (x, y, sigmas) in dents)
                {
                    planes[c][y, x] += (float)(sigmas * Sigma);
                }
            }
        }
        var meta = new ImageMeta { SensorType = channels == 3 ? SensorType.Color : SensorType.Monochrome };
        var image = new Image(planes, BitDepth.Float32, (float)Clip, 0f, 0f, meta);
        return new Field(image, truth, stars, knot);
    }

    // White Gaussian through a separable [a, 1, a] kernel, so neighbours correlate as a stack's warp makes them.
    private static float[] CorrelatedNoise(Random rng, int w, int h, double rho)
    {
        var a = (1.0 - Math.Sqrt(1.0 - 2.0 * rho * rho)) / (2.0 * rho);
        var white = new double[w * h];
        for (var i = 0; i < white.Length; i++)
        {
            white[i] = Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
        }
        var tmp = new double[w * h];
        var dst = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                tmp[y * w + x] = white[y * w + x] + a * (white[y * w + Math.Max(0, x - 1)] + white[y * w + Math.Min(w - 1, x + 1)]);
            }
        }
        var norm = 1.0 + 2.0 * a * a;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                dst[y * w + x] = (float)((tmp[y * w + x] + a * (tmp[Math.Max(0, y - 1) * w + x] + tmp[Math.Min(h - 1, y + 1) * w + x])) / norm);
            }
        }
        return dst;
    }

    // Six-point Gauss-Legendre per axis over every pixel, out to a hundredth of the noise: a higher order than the
    // builder's own integration, so the truth is not the model's. (Point sampling outside a core, the first version of
    // this renderer, erred by 0.2 percent of the peak at 3.5 px, which a bright star turns into a sigma of false error.)
    private static readonly (double Node, double Weight)[] Gl6 =
    {
        (-0.4662347571, 0.0856622462), (-0.3306046932, 0.1803807865), (-0.1193095930, 0.2339569673),
        (0.1193095930, 0.2339569673), (0.3306046932, 0.1803807865), (0.4662347571, 0.0856622462),
    };

    // The integral is taken only where it differs from one sample at the pixel's centre by more than a thousandth of the
    // noise. A pixel's mean differs from its centre value by about a 24th of the profile's Laplacian, which for a Moffat
    // is bounded by 4 beta (beta + 2) / alpha^2 (1 + r^2 / alpha^2)^-(beta + 1); with four times that as margin, the
    // field is the fully integrated one to 0.001 sigma, while the 36 samples a pixel are spent on a core some 20 px
    // across instead of a window out to the hundredth of the noise (370 px for a saturated star). Integrating the whole
    // window was most of this class's time on CI: about 6 s a field, and nearly every test builds one.
    private static void Render(float[] plane, double cx, double cy, double peak, double alpha, double beta)
    {
        var reach = alpha * Math.Sqrt(Math.Pow(peak / (0.01 * Sigma), 1.0 / beta) - 1.0);
        var r = (int)Math.Ceiling(reach);
        // Sample once where 4 * peak * beta (beta + 2) / (6 alpha^2) * (1 + u)^-(beta + 1) < 0.001 sigma, u = r^2 / alpha^2.
        var core = Math.Pow(4.0 * peak * beta * (beta + 2.0) / (6.0 * alpha * alpha) / (0.001 * Sigma), 1.0 / (beta + 1.0));
        var coreR2 = Math.Max(0.0, core - 1.0) * alpha * alpha;
        for (var y = Math.Max(0, (int)cy - r); y <= Math.Min(Size - 1, (int)cy + r); y++)
        {
            for (var x = Math.Max(0, (int)cx - r); x <= Math.Min(Size - 1, (int)cx + r); x++)
            {
                var (dx, dy) = (x - cx, y - cy);
                var d2 = dx * dx + dy * dy;
                if (d2 > coreR2)
                {
                    plane[y * Size + x] += (float)(peak * Math.Pow(1.0 + d2 / (alpha * alpha), -beta));
                    continue;
                }
                var sum = 0.0;
                foreach (var (ny, wy) in Gl6)
                {
                    foreach (var (nx, wx) in Gl6)
                    {
                        var ox = x - cx + nx;
                        var oy = y - cy + ny;
                        sum += wx * wy * Math.Pow(1.0 + (ox * ox + oy * oy) / (alpha * alpha), -beta);
                    }
                }
                plane[y * Size + x] += (float)(peak * sum);
            }
        }
    }

    private static FittedStar? Nearest(StarlessPlate plate, double x, double y, double within = 1.5)
    {
        FittedStar? best = null;
        var bestD = within * within;
        foreach (var s in plate.Stars)
        {
            var d = (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best;
    }

    // RMS of plate minus truth within radius of a point, in noise sigma, on channel c.
    private static double ErrorRms(StarlessPlate plate, Field field, double x, double y, double radius, int c = 0)
    {
        var plane = plate.Plate.GetChannelSpan(c);
        double sum2 = 0;
        var n = 0;
        var r = (int)Math.Ceiling(radius);
        for (var yy = (int)y - r; yy <= (int)y + r; yy++)
        {
            for (var xx = (int)x - r; xx <= (int)x + r; xx++)
            {
                if ((xx - x) * (xx - x) + (yy - y) * (yy - y) > radius * radius || xx < 0 || yy < 0 || xx >= Size || yy >= Size)
                {
                    continue;
                }
                var d = plane[yy * Size + xx] - field.Truth[c][yy * Size + xx];
                sum2 += d * d;
                n++;
            }
        }
        return Math.Sqrt(sum2 / Math.Max(1, n)) / Sigma;
    }

    [Fact(Timeout = 300_000)]
    public async Task StarsComeOutAndTheSubtractionErrorStaysUnderTheNoise()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);
        var stats = plate.Statistics;
        output.WriteLine($"FWHM {stats.FwhmPx[0]:F2} beta {stats.MoffatBeta[0]:F2} field scale {stats.FieldWidthScale:F3} beta {stats.FieldBeta:F2}, inpaint {stats.InpaintFraction:P2}, {stats.Seconds:F1} s");
        foreach (var b in stats.Bands)
        {
            output.WriteLine($"  {b.SigmaLow,5}-{b.SigmaHigh,-5} found {b.Found,4} subtracted {b.Subtracted,4} knots {b.Knots,3} inpainted {b.Inpainted,3} leftover {b.Leftover,3} residual {b.ResidualMedian:F2} bias {b.BiasMedian:F2}");
        }

        var plain = field.Stars.Where(static s => !s.Saturated && s.Peak >= 7 * Sigma).ToArray();
        var subtracted = plain.Count(s => Nearest(plate, s.X, s.Y) is { Outcome: StarFitOutcome.Subtracted });
        var completeness = (double)subtracted / plain.Length;
        var errors = plain.Where(s => Nearest(plate, s.X, s.Y) is { Outcome: StarFitOutcome.Subtracted, Inpainted: false })
            .Select(s => ErrorRms(plate, field, s.X, s.Y, Fwhm)).OrderBy(static e => e).ToArray();
        output.WriteLine($"completeness {completeness:P1} of {plain.Length}; subtraction error RMS median {errors[errors.Length / 2]:F3} sigma, p90 {errors[errors.Length * 9 / 10]:F3}");
        var bandMedians = new List<double>();
        foreach (var (lo, hi) in new[] { (7.0, 20.0), (20.0, 100.0), (100.0, 800.0) })
        {
            var band = plain.Where(s => s.Peak >= lo * Sigma && s.Peak < hi * Sigma)
                .Where(s => Nearest(plate, s.X, s.Y) is { Outcome: StarFitOutcome.Subtracted, Inpainted: false })
                .Select(s => ErrorRms(plate, field, s.X, s.Y, Fwhm)).OrderBy(static e => e).ToArray();
            bandMedians.Add(band[band.Length / 2]);
            output.WriteLine($"  peak {lo}-{hi} sigma: {band.Length} stars, error median {band[band.Length / 2]:F3}");
        }

        // What a fit leaves is its own noise (an amplitude and a centre read through correlated noise), the same in sigma
        // at any brightness. A model error grows with the star instead, so the brightest band against the faintest is the
        // test of the model, and the faintest's level the test that nothing else is added.
        // No holes: a subtracted star's core more than 3 sigma over root n below its sky is the plate's one forbidden
        // artefact; noise alone puts about 0.1 percent of cores there.
        var holes = stats.Bands.Sum(static b => b.Holes);
        output.WriteLine($"holes {holes} of {stats.Bands.Sum(static b => b.Subtracted)} subtracted");
        holes.ShouldBeLessThan(Math.Max(3, stats.Bands.Sum(static b => b.Subtracted) / 100));
        completeness.ShouldBeGreaterThan(0.95);
        bandMedians[2].ShouldBeLessThan(1.2 * bandMedians[0]);
        bandMedians[0].ShouldBeLessThan(0.6);
        stats.InpaintFraction.ShouldBeLessThan(0.05f);
    }

    [Fact(Timeout = 300_000)]
    public async Task AKnotThreeTimesAStarsWidthIsLeftInThePlate()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var near = plate.Stars.Where(s => Math.Abs(s.X - field.Knot.X) < 6 && Math.Abs(s.Y - field.Knot.Y) < 6).ToArray();
        output.WriteLine(string.Join("; ", near.Select(static s => $"{s.Outcome} w={s.WidthScale:F2} z={s.Significance:F1}")));
        near.ShouldNotContain(static s => s.Outcome == StarFitOutcome.Subtracted);
        ErrorRms(plate, field, field.Knot.X, field.Knot.Y, field.Knot.Fwhm).ShouldBeLessThan(0.5);
    }

    [Fact(Timeout = 300_000)]
    public async Task ASaturatedStarIsFittedOnItsWingsAndItsCoreFilledFromAround()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var s in field.Stars.Where(static s => s.Saturated))
        {
            var fitted = Nearest(plate, s.X, s.Y, 2.0);
            fitted.ShouldNotBeNull();
            output.WriteLine($"({s.X}, {s.Y}): {fitted.Value.Outcome} saturated={fitted.Value.Saturated} inpainted={fitted.Value.Inpainted} w={fitted.Value.WidthScale:F2} A={fitted.Value.Amplitude:E2} second={fitted.Value.SecondPass}; nearby {string.Join(", ", plate.Stars.Where(o => Math.Abs(o.X - s.X) < 20 && Math.Abs(o.Y - s.Y) < 20).Select(static o => $"({o.X:F1},{o.Y:F1}) {o.Outcome} z={o.Significance:F0}"))}");
            fitted.Value.Saturated.ShouldBeTrue();
            fitted.Value.Inpainted.ShouldBeTrue();
            var core = ErrorRms(plate, field, s.X, s.Y, 6.0);
            var wings = ErrorRms(plate, field, s.X, s.Y, 30.0);
            output.WriteLine($"({s.X}, {s.Y}): core error {core:F2} sigma, within 30 px {wings:F2}");
            core.ShouldBeLessThan(3.0);
            wings.ShouldBeLessThan(3.0);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AGiantWhosePlateauPassesTheSkyAnnulusCapIsTakenAndLeavesNoHole()
    {
        // A plateau about 45 px in radius, wider than the 40 px a star's sky annulus is capped at, as eta Carinae's is at
        // 288 mm (67 px): every annulus clamp's floor passed its ceiling there, and the master failed.
        var field = MakeField(1, giant: (512.0, 512.0, 1.0e7));
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var fitted = Nearest(plate, 512.0, 512.0, 3.0);
        fitted.ShouldNotBeNull();
        var plateau = 0;
        var truthPlane = field.Truth[0];
        var platePlane = plate.Plate.GetChannelSpan(0);
        double sum = 0;
        for (var y = 512 - 45; y <= 512 + 45; y++)
        {
            for (var x = 512 - 45; x <= 512 + 45; x++)
            {
                if ((x - 512) * (x - 512) + (y - 512) * (y - 512) <= 40 * 40)
                {
                    sum += (platePlane[y * Size + x] - truthPlane[y * Size + x]) / Sigma;
                    plateau++;
                }
            }
        }
        var bias = sum / plateau;
        output.WriteLine($"giant: {fitted.Value.Outcome} saturated={fitted.Value.Saturated} inpainted={fitted.Value.Inpainted}; plate minus truth over its 40 px core {bias:F2} sigma a pixel");
        fitted.Value.Outcome.ShouldBe(StarFitOutcome.Subtracted);
        fitted.Value.Saturated.ShouldBeTrue();
        fitted.Value.Inpainted.ShouldBeTrue();
        Math.Abs(bias).ShouldBeLessThan(1.0, "the giant's core is neither a hole nor a glow");
    }

    [Fact(Timeout = 300_000)]
    public async Task AClosePairIsTwoStarsAndTheStarBesideTheRingIsTaken()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        Nearest(plate, 300.0, 450.0, 1.2).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        Nearest(plate, 304.0, 450.0, 1.2).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        Nearest(plate, Ring + 4.0, 500.0, 1.5).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        var pairError = ErrorRms(plate, field, 302.0, 450.0, 5.0);
        output.WriteLine($"close pair: error {pairError:F3} sigma; {string.Join("; ", plate.Stars.Where(static s => Math.Abs(s.X - 302) < 6 && Math.Abs(s.Y - 450) < 4).Select(static s => $"({s.X:F2},{s.Y:F2}) {s.Outcome} w={s.WidthScale:F2} A={s.Amplitude:E2}"))}");
        // Each star's fit leaves its own noise, about 0.5 sigma (the first test), and this disc holds both cores.
        pairError.ShouldBeLessThan(1.1);
    }

    [Fact(Timeout = 300_000)]
    public async Task TheAbsentRingIsNeitherWrittenNorFilled()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var plane = plate.Plate.GetChannelSpan(0);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (x < Ring || y < Ring || x >= Size - Ring || y >= Size - Ring)
                {
                    plane[y * Size + x].ShouldBe(0f);
                    plate.Inpainted[y, x].ShouldBeFalse();
                }
            }
        }
    }

    // #1867: a whole-plate invariant on an ordinary field (nothing on it diverges: its brightest unsaturated star is 0.8 against a
    // limit of 3.6): no catalogue row is an unsaturated Subtracted Moffat whose amplitude is past the margin, and the star beside the
    // absent ring is still found. AnUnsaturatedEdgeFitAboveTheCeilingIsNotSubtracted is the test that drives the guard.
    [Fact(Timeout = 300_000)]
    public async Task NoUnsaturatedSubtractedRowOfAnOrdinaryPlateIsPastTheMargin()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var s in plate.Stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted && s.Model == StarFitModel.Moffat && !s.Saturated))
        {
            s.ChannelAmplitudes.All(a => a <= ClassicalStarRemover.OvershootMargin * Clip).ShouldBeTrue($"({s.X}, {s.Y}) peaks past the clip");
        }
        Nearest(plate, Ring + 4.0, 500.0, within: 3.0).ShouldNotBeNull();
    }

    // #1867: a bright SATURATED star cut by the frame's edge (centre just inside the absent ring, only the covered part of its
    // wings seen) is still taken: an off-canvas centre is where a cut star's true centre lies, never a divergence.
    [Fact(Timeout = 300_000)]
    public async Task ASaturatedStarCutByTheEdgeIsStillSubtracted()
    {
        var field = MakeField(1, extra: [new Injected(Ring - 1.0, 512.0, 20.0, true)]);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var fitted = Nearest(plate, Ring - 1.0, 512.0, 6.0);
        fitted.ShouldNotBeNull();
        output.WriteLine($"edge star: {fitted.Value.Outcome} at ({fitted.Value.X:F1}, {fitted.Value.Y:F1}) saturated={fitted.Value.Saturated}");
        fitted.Value.Outcome.ShouldBe(StarFitOutcome.Subtracted);
        var truthPlane = field.Truth[0];
        var platePlane = plate.Plate.GetChannelSpan(0);
        double sum = 0;
        var n = 0;
        for (var y = 512 - 3; y <= 512 + 3; y++)
        {
            for (var x = Ring; x < Ring + 6; x++)
            {
                sum += (platePlane[y * Size + x] - truthPlane[y * Size + x]) / Sigma;
                n++;
            }
        }
        output.WriteLine($"plate minus truth beside the edge star {sum / n:F1} sigma a pixel");
        Math.Abs(sum / n).ShouldBeLessThan(50.0, "the star's light is off the plate");
    }

    // #1867 review: the plate builder's margin, applied as the builder applies it: each channel against OvershootMargin (4) times its
    // own brightest covered sample, a row just under admitted and one just over refused, in any channel, and a non-number refused.
    [Theory]
    [InlineData(3.59, 0.2, false)]   // just under 4 x 0.9 in red
    [InlineData(3.61, 0.2, true)]    // just over in red
    [InlineData(0.5, 2.01, true)]    // just over 4 x 0.5 in green, the dimmer channel
    [InlineData(0.5, 1.99, false)]   // just under in green
    [InlineData(double.NaN, 0.2, true)]
    public void TheOvershootMarginRefusesJustOverItAndAdmitsJustUnderItPerChannel(double red, double green, bool refused)
        => ClassicalStarRemover.AmplitudeOvershoots([red, green], [0.9f, 0.5f]).ShouldBe(refused);

    // #2352: a fit may end 32 px from its star (the tiles' reach of 128 less a window's 96) or, for a saturated star
    // with a plateau wider than that, the plateau's radius. Past the larger of the two, on either axis, it has left its
    // star, and a centre that is not a number has left it too. The star is at (100, 100).
    [Theory]
    [InlineData(100.0, 100.0, 2.0, false)]                    // on its star
    [InlineData(132.0, 100.0, 2.0, false)]                    // at the tiles' allowance, on x
    [InlineData(132.5, 100.0, 2.0, true)]                     // just past it
    [InlineData(100.0, 68.0, 2.0, false)]                     // at it on y, the other way
    [InlineData(100.0, 67.5, 2.0, true)]
    [InlineData(131.0, 133.0, 2.0, true)]                     // inside on one axis is not inside
    [InlineData(160.0, 100.0, 60.0, false)]                   // a plateau's allowance is the larger
    [InlineData(160.5, 100.0, 60.0, true)]
    [InlineData(double.NaN, 100.0, 2.0, true)]
    [InlineData(100.0, double.PositiveInfinity, 2.0, true)]
    public void AFitHasLeftItsStarPastTheLargerOfTheTilesAllowanceAndTheStarsOwn(double x, double y, double drift, bool left)
        => ClassicalStarRemover.RanAway(x, y, 100.0, 100.0, drift).ShouldBe(left);

    // #2374: where a centre off the covered frame stands on it, which is what the rule above is asked about such a centre.
    // A frame of 1000 x 800 whose outer 12 px are absent canvas when ringed.
    [Theory]
    [InlineData(500.4, 300.6, false, 500, 301)]     // on the frame and covered: itself, to the pixel
    [InlineData(-40.3, 600.2, false, 0, 600)]       // left of a frame with no ring: straight back in
    [InlineData(1500.0, 600.0, false, 999, 600)]    // far to the right: the depth does not matter
    [InlineData(-10.0, -20.0, false, 0, 0)]         // past a corner: the corner
    [InlineData(5.0, 600.0, true, 12, 600)]         // on the absent ring: the first covered column beside it
    [InlineData(-28.0, 600.0, true, 12, 600)]       // outside the frame AND its ring
    [InlineData(500.0, 795.0, true, 500, 787)]      // on the bottom ring: the last covered row
    [InlineData(3.0, 4.0, true, 12, 12)]            // in the ring's corner: the cover's corner
    public void ACentreOffTheCoverStandsOnTheCoveredPixelNearestToIt(double x, double y, bool ringed, int standsX, int standsY)
    {
        const int Width = 1000;
        const int Height = 800;
        Func<int, int, bool> covered = ringed
            ? static (px, py) => px >= 12 && px < Width - 12 && py >= 12 && py < Height - 12
            : static (_, _) => true;

        ClassicalStarRemover.NearestCovered(x, y, Width, Height, covered, reach: 128).ShouldBe((standsX, standsY));
    }

    // #2374, from the review of #2379: the decision itself, of which the field test below reaches one answer. A frame of
    // 1000 x 800 and a star whose peak the finder took in the first covered column, at (coverFrom, 400). The cover
    // begins at column 200, with absent canvas left of it, or at column 0, the frame's own edge.
    [Theory]
    [InlineData(200, 160.0, 400.0, false, false)]       // 40 px deep, straight out: a star the canvas cut
    [InlineData(200, 160.0, 432.0, false, false)]       // and 32 px along the edge, which is the allowance
    [InlineData(200, 160.0, 433.0, false, true)]        // a pixel past it: the fit ran along the edge
    [InlineData(200, 160.0, 440.0, false, true)]        // 40 deep and 40 along
    [InlineData(200, 0.0, 400.0, false, true)]          // 200 px of canvas to the cover: nothing in reach, it stands nowhere
    [InlineData(200, 160.0, 400.0, true, true)]         // SATURATED and 40 px deep: held on the centre itself, past its 32
    [InlineData(200, 170.0, 400.0, true, false)]        // saturated and 30 px deep: within them
    [InlineData(200, 230.0, 400.0, false, false)]       // on the cover: the centre itself
    [InlineData(200, 240.0, 400.0, false, true)]
    [InlineData(0, -40.0, 400.0, false, false)]         // past an edge with no canvas, straight out
    [InlineData(0, -40.0, 440.0, false, true)]          // and along it
    [InlineData(0, -500.0, 400.0, false, false)]        // no depth is refused by THIS rule: reach ends such a fit, after it
    [InlineData(0, -40.0, 400.0, true, true)]           // saturated, past the edge
    [InlineData(200, double.NaN, 400.0, false, true)]   // not a number
    public void AFitOffTheCoverHasLeftItsStarOnlyAlongTheEdgeAndASaturatedOneWhereItsCentreHas(
        int coverFrom, double x, double y, bool saturated, bool left)
    {
        bool Covered(int px, int _) => px >= coverFrom;

        ClassicalStarRemover.HasLeftItsStar(x, y, coverFrom, 400.0, drift: 2.0, saturated, 1000, 800, Covered).ShouldBe(left);
    }

    [Fact]
    public void ACentreWithNothingCoveredInReachOrThatIsNotANumberStandsNowhere()
    {
        ClassicalStarRemover.NearestCovered(5.0, 400.0, 1000, 800, static (px, _) => px >= 200, reach: 128).ShouldBeNull("the cover begins 195 px away");
        ClassicalStarRemover.NearestCovered(5.0, 400.0, 1000, 800, static (px, _) => px >= 133, reach: 128).ShouldBe((133, 400), "128 px away is in reach");
        ClassicalStarRemover.NearestCovered(double.NaN, 400.0, 1000, 800, static (_, _) => true, reach: 128).ShouldBeNull();
        ClassicalStarRemover.NearestCovered(5.0, double.NegativeInfinity, 1000, 800, static (_, _) => true, reach: 128).ShouldBeNull();
    }

    // #1873 review: a colour field with a dim channel, whose brightest covered sample (its sky 0.01 plus its brightest star, 20 x 0.003,
    // so about 0.07) sits under the luminance sky (the mean of 0.20, 0.08 and 0.01, about 0.097), must still have its stars subtracted.
    // Compared against the luminance sky, the guard left that channel a threshold of 0 and refused every star with light in it.
    [Fact(Timeout = 300_000)]
    public async Task AColourFieldWithUnequalChannelSkiesStillHasItsStarsSubtracted()
    {
        var field = MakeField(3, skies: [0.20, 0.08, 0.01], gains: [0.8, 1.0, 0.003]);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var unsaturated = plate.Stars.Where(static s => !s.Saturated && s.Outcome is StarFitOutcome.Subtracted or StarFitOutcome.NoFit).ToArray();
        var refused = unsaturated.Count(static s => s.Outcome == StarFitOutcome.NoFit);
        output.WriteLine($"unequal skies: {unsaturated.Length - refused} subtracted, {refused} NoFit");

        unsaturated.Length.ShouldBeGreaterThan(200);
        refused.ShouldBeLessThan(unsaturated.Length / 20, "an unequal channel sky is not a diverged fit");
    }

    // #2352: on this field the finder takes a second peak on the rim of a saturated star for a source of its own, and a
    // Moffat fitted to a rim runs away. The one found 14 px from the star at (850, 850) ran 364 px on its first fit, to
    // 24 px from the saturated star at (512, 900). Its second window was then cut THERE, on ground another tile of the
    // same phase was subtracting from, and the refused row was recorded there too, with numbers that differed from run
    // to run. A fit that has left its star is no fit: the row says where the source was found, and claims nothing.
    [Fact(Timeout = 300_000)]
    public async Task ASourceWhoseFitRunsAwayIsRecordedWhereItWasFoundAndNotWhereTheFitRanTo()
    {
        var field = MakeField(3, skies: [0.20, 0.08, 0.01], gains: [0.8, 1.0, 0.003]);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var refused = plate.Stars.Where(static s => s.Outcome == StarFitOutcome.NoFit).ToArray();
        foreach (var s in refused)
        {
            output.WriteLine($"refused: ({s.X:F2}, {s.Y:F2}) {s.Significance:F1} sigma, amplitude {s.Amplitude:G4}, width {s.WidthScale:F2}");
        }

        static bool Near(FittedStar s, double x, double y, double radius)
            => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) <= radius * radius;

        // The rim source of the star at (850, 850) is recorded on that star, with no fit's numbers.
        var onItsStar = refused.Where(static s => Near(s, 850.0, 850.0, 20.0)).ToArray();
        onItsStar.Length.ShouldBe(1, "the source found on the rim of the star at (850, 850) is recorded there");
        onItsStar[0].Amplitude.ShouldBe(0f, "a fit that ran away gives the row no amplitude");
        onItsStar[0].WidthScale.ShouldBe(1f, "nor a width");
        // The other source whose first fit ran away, 328 px, was found on the rim of the star at (850, 150). Its row
        // was on its star before the fix as well: its second fit, cut where the first ran to, found no star there, so
        // the row kept where it was found. So this pins what such a row holds and would not have shown the defect;
        // the star at (850, 850) above is the one that did (#2374).
        var onTheOtherStar = refused.Where(static s => Near(s, 850.0, 150.0, 20.0)).ToArray();
        onTheOtherStar.Length.ShouldBe(1, "the source found on the rim of the star at (850, 150) is recorded there");
        onTheOtherStar[0].Amplitude.ShouldBe(0f);
        onTheOtherStar[0].WidthScale.ShouldBe(1f);
        // The star at (512, 900) had no source refused on it, so nothing refused is recorded on it.
        refused.Count(static s => Near(s, 512.0, 900.0, 40.0)).ShouldBe(0, "no row lies where another star's fit ran to");
    }

    // #2374: a star 40 px outside the covered frame, of which only a wing shows at the edge. Its fit finds the true
    // centre, off the frame, within a few pixels. That centre is 41 px from the peak the finder took at the edge, past
    // the 32 px a fit may end from its star, and measured on the centre itself the fit was refused as one that ran away
    // and the row put back at the edge. It has not left its star: straight out from the edge is where a star the edge
    // cut IS, and only a fit that went ALONG the edge has left. So the row is at the star's true centre. (This is one
    // depth, past the 32 px. The rule sets none, and how deep a fit can be is its window's reach: the rows above.)
    // Whether such a star is then subtracted is another rule's. This one is left as TooNarrow: a wing alone, fitted at
    // its true centre, asks for a core a third of the field's width, and the width is judged before the amplitude's
    // ceiling is. The outcome is asserted so that a change which leaves something else here is seen.
    [Fact(Timeout = 300_000)]
    public async Task AStarStraightOutFromTheEdgeDeeperThanAFitMayEndFromItsStarIsNotTakenForAFitThatRanAway()
    {
        const double Outside = 40.0;
        const double Y = 600.0;
        // 30 sigma where its wing meets the first covered column.
        var alpha = Fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0));
        var peak = 30 * Sigma * Math.Pow(1.0 + Outside * Outside / (alpha * alpha), Beta);
        var field = MakeField(1, extra: [new Injected(Ring - Outside, Y, peak, false)]);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var beside = plate.Stars.Where(s => Math.Abs(s.Y - Y) < 12 && s.X < Ring + 6).ToArray();
        foreach (var s in beside)
        {
            output.WriteLine($"beside the edge: ({s.X:F2}, {s.Y:F2}) {s.Outcome} {s.Significance:F1} sigma, amplitude {s.Amplitude:G4}, width {s.WidthScale:F2}");
        }

        var atTheStar = beside.Where(s => Math.Abs(s.X - (Ring - Outside)) < 6 && Math.Abs(s.Y - Y) < 6).ToArray();
        atTheStar.Length.ShouldBe(1, "the fit found the star where it is, 40 px outside the cover, and its row says so");
        atTheStar[0].Outcome.ShouldBe(StarFitOutcome.TooNarrow, "a wing alone is too narrow for a star of this field, and stays in the plate");
        beside.Count(s => s.X >= Ring && s.Outcome == StarFitOutcome.NoFit && s.Amplitude == 0f)
            .ShouldBe(0, "it is not put back at the edge as a fit that ran away");
    }

    // #1867: an UNSATURATED star whose centre is past the covered sky, its true peak (5.0) far above the clip but only a faint
    // wing (under the clip) seen. Whatever a fit makes of it, it is never an unsaturated Subtracted row over the ceiling.
    [Fact(Timeout = 300_000)]
    public async Task AnUnsaturatedEdgeFitAboveTheCeilingIsNotSubtracted()
    {
        var field = MakeField(1, extra: [new Injected(Ring - 4.0, 512.0, 5.0, false)]);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var s in plate.Stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted && !s.Saturated))
        {
            s.ChannelAmplitudes.All(a => float.IsFinite(a) && a <= ClassicalStarRemover.OvershootMargin * Clip).ShouldBeTrue($"({s.X}, {s.Y}) peaks past the clip");
        }
        var near = plate.Stars.Where(s => Math.Abs(s.X - (Ring - 4.0)) < 8 && Math.Abs(s.Y - 512.0) < 8).ToArray();
        foreach (var s in near)
        {
            output.WriteLine($"near the edge: {s.Outcome} ({s.X:F1}, {s.Y:F1}) amp={(s.ChannelAmplitudes.Length > 0 ? s.ChannelAmplitudes[0] : float.NaN):G4} saturated={s.Saturated}");
        }

        // The diverged fit lands on this star and is kept as NoFit, never as a Subtracted row.
        near.ShouldNotBeEmpty();
        near.ShouldAllBe(static s => s.Outcome != StarFitOutcome.Subtracted);
        near.ShouldContain(static s => s.Outcome == StarFitOutcome.NoFit);
    }

    [Fact(Timeout = 300_000)]
    public async Task EachStarSaysWhetherItSitsOnNebulosityAndOnTexture()
    {
        // The smooth nebula peaks 30 sigma over the sky at (400, 600); the dark corner carries under 1.5 sigma of the
        // gradient; the ripple patch at (750, 700) adds texture with no level.
        var field = MakeField(1, textured: true);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var subtracted = plate.Stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted).ToArray();
        FittedStar[] Within(double x, double y, double r) => [.. subtracted.Where(s => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) < r * r)];
        var onNebula = Within(400, 600, 80);
        var onTexture = Within(750, 700, 80);
        var dark = subtracted.Where(static s => s.X < 150 && s.Y < 200).ToArray();
        static float Median(IEnumerable<float> v) => v.Order().ElementAt(v.Count() / 2);
        foreach (var (name, set) in new[] { ("nebula", onNebula), ("texture", onTexture), ("dark sky", dark) })
        {
            output.WriteLine($"{name}: {set.Length} stars, sky above median {Median(set.Select(static s => s.SkyAbove)):F1}, texture median {Median(set.Select(static s => s.Texture)):F2}");
            set.Length.ShouldBeGreaterThan(5);
        }

        Median(onNebula.Select(static s => s.SkyAbove)).ShouldBeGreaterThan(15f);
        Median(dark.Select(static s => s.SkyAbove)).ShouldBeLessThan(3f);
        Median(onTexture.Select(static s => s.Texture)).ShouldBeGreaterThan(1.3f);
        // A smooth glow is a level, not texture.
        Median(onNebula.Select(static s => s.Texture)).ShouldBeLessThan(1.2f);
        Median(dark.Select(static s => s.Texture)).ShouldBeLessThan(1.2f);
    }

    // The deepest plate pixel within `within` px of (x, y), in sigma under the plate's median over an annulus 6 to 10 px out:
    // how #2449 read a dug core on the store's plates.
    private static double DeepestCorePixel(StarlessPlate plate, double x, double y, double within, int c = 0)
    {
        var plane = plate.Plate.GetChannelSpan(c);
        var ring = new List<float>();
        var deepest = double.PositiveInfinity;
        for (var yy = (int)Math.Floor(y) - 11; yy <= (int)Math.Ceiling(y) + 11; yy++)
        {
            for (var xx = (int)Math.Floor(x) - 11; xx <= (int)Math.Ceiling(x) + 11; xx++)
            {
                var d2 = (xx - x) * (xx - x) + (yy - y) * (yy - y);
                if (d2 >= 6 * 6 && d2 <= 10 * 10)
                {
                    ring.Add(plane[yy * Size + xx]);
                }
                else if (d2 <= within * within)
                {
                    deepest = Math.Min(deepest, plane[yy * Size + xx]);
                }
            }
        }
        ring.Sort();
        return (deepest - ring[ring.Count / 2]) / Sigma;
    }

    // #2449: a star whose peak pixel is lower than its profile, as an undersampled star's is after a stack's interpolation,
    // is over-taken there by its fit, which leaves that one pixel 5 to 11 sigma under the sky: the black pit a 1:1 crop
    // shows. The hole test reads the core's MEAN and passes it. Built without the dug-core trigger the pit is there (the
    // premise); built with it the core is refilled and the row marked inpainted. Faint stars (about 20 sigma, the band
    // where the store's pits are) on the textured patch, where the first fill's per-pixel flag reads the sky map's spread
    // (the ripple's, about twice the pixels' noise) and leaves the pit to the sweep, as a crowd's faint stars do on a real
    // master; and on the ripple's nodes (columns 740 and 760), where it is level round a star and its annulus reads the sky
    // under it. The peak pixel of each is moved by 12 or 15 sigma.
    [Fact(Timeout = 300_000)]
    public async Task AStarWhoseSubtractionDigsItsPeakPixelHasItsCoreRefilled()
    {
        var ct = TestContext.Current.CancellationToken;
        (double X, double Y, double Dent)[] at =
            [(740, 640, -12), (740, 670, -15), (740, 700, -12), (740, 730, -15), (760, 650, -15), (760, 680, -12), (760, 710, -15), (760, 740, -15)];
        var image = MakeField(1, textured: true,
            extra: [.. at.Select(static p => new Injected(p.X, p.Y, 40 * Sigma, false))],
            dents: [.. at.Select(static p => ((int)p.X, (int)p.Y, p.Dent))]).Image;
        var without = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: -1, recorder: null, fillDugCores: false, ct);
        var with = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: -1, recorder: null, fillDugCores: true, ct);

        output.WriteLine($"{with.Statistics.DugCores} stars refilled for a dug core alone, {without.Statistics.DugCores} without the trigger");
        foreach (var (x, y, dent) in at)
        {
            var before = Nearest(without, x, y).ShouldNotBeNull($"the star at ({x}, {y})");
            var after = Nearest(with, x, y).ShouldNotBeNull($"the star at ({x}, {y})");
            var dug = DeepestCorePixel(without, before.X, before.Y, ClassicalStarRemover.DugCoreSearch);
            var deepest = DeepestCorePixel(with, after.X, after.Y, ClassicalStarRemover.DugCoreSearch);
            output.WriteLine($"({x}, {y}) moved {dent}: {after.Outcome} at ({after.X:F2}, {after.Y:F2}) {after.Significance:F1} sigma; "
                + $"without the trigger inpainted {before.Inpainted}, deepest core pixel {dug:F2} sigma; with it inpainted {after.Inpainted}, {deepest:F2} sigma");
            before.Outcome.ShouldBe(StarFitOutcome.Subtracted);
            before.Inpainted.ShouldBeFalse("the first fill leaves the pit to the sweep");
            dug.ShouldBeLessThan(-ClassicalStarRemover.DugPixelSigma, "the subtraction dug the peak pixel");
            after.Outcome.ShouldBe(StarFitOutcome.Subtracted);
            after.Inpainted.ShouldBeTrue("the dug core is refilled, and the row says so");
            deepest.ShouldBeGreaterThan(-ClassicalStarRemover.DugPixelSigma, "no core pixel is left 4 sigma under the sky");
        }
        without.Statistics.DugCores.ShouldBe(0);
        with.Statistics.DugCores.ShouldBeGreaterThanOrEqualTo(at.Length, "each was refilled by the dug-core trigger, which the hole test passed");
    }

    // How many stars each round of the hole sweep refilled for a dug pixel alone, read off the sweep's own debug line.
    private static int[] DugCoresByRound(RecordingLogger log)
        => [.. log.Messages
            .Select(static m => Regex.Match(m, @"hole sweep (\d+): \d+ cores below their sky, (\d+) dug cores"))
            .Where(static m => m.Success)
            .OrderBy(static m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(static m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))];

    // #2449, the sweep's guard: a star refilled for a dug pixel alone is not tested for one again in the later rounds. The
    // fill's grain is drawn at the pixels' noise, and the gate reads a pixel against its annulus's neighbour differences,
    // which noise correlated between neighbours makes smaller than that: by the root of one less the correlation, so 4
    // sigma of them is 2.2 of the pixels' noise at 0.7 and 3.2 at the stack's own 0.35. At 0.7 a pixel of the grain sits
    // under the gate often enough that a retest finds a refilled core dug again and redraws the same disc with the next
    // round's seed; at 0.35 it almost never does, so this field's noise is correlated as far as the kernel goes. A grid
    // of faint stars, each with its peak pixel 4.5 sigma low (over the gate, and under the 5 sigma at which the first
    // fill takes a pixel itself), gives the sweep some two hundred cores to refill, so that a retest redraws a dozen and
    // more. Built with a retest every round, the rounds' refills add up to more than the stars refilled (the premise:
    // some were redrawn); built as every plate is, each star is refilled for a dug pixel in one round only.
    [Fact(Timeout = 300_000)]
    public async Task AStarRefilledForADugPixelAloneIsNotRefilledForOneAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        // Clear of the knot at (700, 300) and of the five saturated stars.
        (int X, int Y)[] at =
            [.. from y in Enumerable.Range(0, 13)
                from x in Enumerable.Range(0, 16)
                let p = (X: 62 + 60 * x, Y: 62 + 70 * y)
                where new[] { (700, 300), (150, 150), (850, 150), (150, 850), (850, 850), (512, 900) }
                    .All(q => Math.Abs(p.X - q.Item1) > 40 || Math.Abs(p.Y - q.Item2) > 40)
                select p];
        var image = MakeField(1, correlation: 0.7,
            extra: [.. at.Select(static p => new Injected(p.X, p.Y, 40 * Sigma, false))],
            dents: [.. at.Select(static p => (p.X, p.Y, -4.5))]).Image;
        var (onceLog, everyLog) = (new RecordingLogger(), new RecordingLogger());
        var once = await ClassicalStarRemover.BuildAsync(image, null, onceLog, tileThreads: -1, recorder: null, fillDugCores: true, ct);
        var every = await ClassicalStarRemover.BuildAsync(image, null, everyLog, tileThreads: -1, recorder: null, fillDugCores: true, retestDugCores: true, ct);
        var (onceRounds, everyRounds) = (DugCoresByRound(onceLog), DugCoresByRound(everyLog));

        output.WriteLine($"{at.Length} stars planted with a low peak pixel");
        output.WriteLine($"as built: {once.Statistics.DugCores} stars refilled for a dug core alone, by round {string.Join(", ", onceRounds)}");
        output.WriteLine($"with a retest every round: {every.Statistics.DugCores} stars, by round {string.Join(", ", everyRounds)}");
        everyRounds.Length.ShouldBeGreaterThan(1, "the sweep ran a later round");
        everyRounds.Sum().ShouldBeGreaterThan(every.Statistics.DugCores + 2, "retested every round, some refilled cores read as dug again and are redrawn");
        once.Statistics.DugCores.ShouldBeGreaterThan(0);
        onceRounds.Sum().ShouldBe(once.Statistics.DugCores, "each star is refilled for a dug pixel in one round only");
    }

    // #2449: a dark pixel the INPUT holds in a star's core (7 sigma under the sky, beside its peak pixel), in the channel where
    // the star has almost no light (a colour field whose third channel takes 0.003 of each star), is the input's own and not
    // one the subtraction dug: the star's model took next to nothing there. The trigger leaves it: nothing in the disc it
    // would refill is inpainted, and every pixel round the star that no fill owns is the same to the bit with the trigger
    // and without it. The pixel still sits more than 4 sigma under the sky, so a trigger that did not ask what the
    // subtraction took would have filled it. (In a channel where the star is bright, its model takes more than 4 sigma at
    // every pixel within 1.5 px of its centre down to the finder's limit, so this is where the question decides.) On smooth
    // sky, where the luminance the first fill flags on holds a third of the pixel's depth, under its flag. Heavy: two
    // colour builds, where one takes 40 s on the x64 leg.
    [Fact(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    public async Task ADarkPixelTheInputHoldsInAStarsCoreIsLeftAlone()
    {
        const int Faint = 2;
        var ct = TestContext.Current.CancellationToken;
        (double X, double Y)[] at = [(300, 250), (600, 220), (450, 780)];
        var image = MakeField(3, gains: [0.8, 1.0, 0.003],
            extra: [.. at.Select(static p => new Injected(p.X, p.Y, 40 * Sigma, false))],
            dents: [.. at.Select(static p => ((int)p.X + 1, (int)p.Y, -7.0))], dentChannel: Faint).Image;
        var without = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: -1, recorder: null, fillDugCores: false, ct);
        var with = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: -1, recorder: null, fillDugCores: true, ct);
        var input = image.GetChannelSpan(Faint);
        var plate = with.Plate.GetChannelSpan(Faint);

        foreach (var (x, y) in at)
        {
            var star = Nearest(with, x, y).ShouldNotBeNull($"the star at ({x}, {y})");
            var (px, py) = ((int)x + 1, (int)y);
            var dark = DeepestCorePixel(with, px, py, 0.1, Faint);
            var took = (input[py * Size + px] - plate[py * Size + px]) / Sigma;
            output.WriteLine($"({x}, {y}): {star.Outcome} at ({star.X:F2}, {star.Y:F2}) {star.Significance:F1} sigma, inpainted {star.Inpainted}; "
                + $"the dark pixel {dark:F2} sigma under the sky, the subtraction took {took:F3} sigma there");
            star.Outcome.ShouldBe(StarFitOutcome.Subtracted);
            star.Inpainted.ShouldBeFalse();
            ((double)(px - star.X) * (px - star.X) + (double)(py - star.Y) * (py - star.Y)).ShouldBeLessThanOrEqualTo(
                ClassicalStarRemover.DugCoreSearch * ClassicalStarRemover.DugCoreSearch, "the trigger looks at the pixel");
            dark.ShouldBeLessThan(-ClassicalStarRemover.DugPixelSigma, "the input's dark pixel is still there");
            took.ShouldBeLessThan(1.0, "the star's model took next to nothing there");
            var (cx, cy) = ((int)Math.Round(star.X), (int)Math.Round(star.Y));
            for (var yy = cy - 3; yy <= cy + 3; yy++)
            {
                for (var xx = cx - 3; xx <= cx + 3; xx++)
                {
                    if ((xx - cx) * (xx - cx) + (yy - cy) * (yy - cy) <= ClassicalStarRemover.DugCoreRadius * ClassicalStarRemover.DugCoreRadius)
                    {
                        with.Inpainted[yy, xx].ShouldBeFalse($"({xx}, {yy}), in the disc the trigger would refill about ({cx}, {cy})");
                    }
                    // A pixel another fill owns moves with any fill of its round, whose grain follows the frame-wide noise
                    // map; what the subtraction left is compared.
                    if (with.Inpainted[yy, xx] || without.Inpainted[yy, xx])
                    {
                        continue;
                    }
                    for (var c = 0; c < 3; c++)
                    {
                        with.Plate.GetChannelSpan(c)[yy * Size + xx].ShouldBe(without.Plate.GetChannelSpan(c)[yy * Size + xx],
                            $"({xx}, {yy}), channel {c}, beside the star at ({x}, {y})");
                    }
                }
            }
        }
    }

    [Fact(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    public async Task TheSameImageGivesTheSamePlate()
    {
        var field = MakeField(1);
        var a = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);
        var b = await ClassicalStarRemover.BuildAsync(MakeField(1).Image, cancellationToken: TestContext.Current.CancellationToken);
        a.Plate.GetChannelSpan(0).SequenceEqual(b.Plate.GetChannelSpan(0)).ShouldBeTrue();
        a.Stars.Length.ShouldBe(b.Stars.Length);
    }

    /// <summary>
    /// The profile a plate is built with comes back with it, and measured alone (a store built before profiles were kept) it
    /// is the same profile to the bit, its luminance beta the report's; through its file it reads back unchanged.
    /// </summary>
    [Fact(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    public async Task TheProfileMeasuredAloneIsThePlatesOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var plate = await ClassicalStarRemover.BuildAsync(MakeField(3).Image, cancellationToken: ct);
        var built = plate.FieldProfile.ShouldNotBeNull();
        var alone = await ClassicalStarRemover.MeasureFieldProfileAsync(MakeField(3).Image, cancellationToken: ct);

        alone.Channels.Length.ShouldBe(3);
        for (var c = 0; c < 3; c++)
        {
            alone.Channels[c].Alpha.ShouldBe(built.Channels[c].Alpha);
            alone.Channels[c].Beta.ShouldBe(built.Channels[c].Beta);
            alone.Channels[c].TableAlpha.ShouldBe(built.Channels[c].TableAlpha);
            alone.Channels[c].Table.SequenceEqual(built.Channels[c].Table).ShouldBeTrue();
        }
        alone.Luminance.Beta.ShouldBe(built.Luminance.Beta);
        ((float)built.Luminance.Beta).ShouldBe(plate.Statistics.FieldBeta);
        built.Luminance.Table.ShouldNotBeEmpty("the field has enough bright stars for a residual table");

        var path = Path.Combine(Path.GetTempPath(), $"tianwen-profile-{Guid.NewGuid():N}.json");
        try
        {
            await StarlessFieldProfile.WriteAsync(path, built, ct);
            var read = (await StarlessFieldProfile.ReadAsync(path, ct)).ShouldNotBeNull();
            read.Luminance.Alpha.ShouldBe(built.Luminance.Alpha);
            read.Channels[1].Table.SequenceEqual(built.Channels[1].Table).ShouldBeTrue();
            (await StarlessFieldProfile.ReadAsync(path + ".absent", ct)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task EachColourChannelLosesItsOwnShareOfTheStar()
    {
        var field = MakeField(3);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        var plain = field.Stars.Where(static s => !s.Saturated && s.Peak >= 10 * Sigma)
            .Where(s => Nearest(plate, s.X, s.Y) is { Outcome: StarFitOutcome.Subtracted, Inpainted: false }).ToArray();
        for (var c = 0; c < 3; c++)
        {
            var errors = plain.Select(s => ErrorRms(plate, field, s.X, s.Y, Fwhm, c)).OrderBy(static e => e).ToArray();
            output.WriteLine($"channel {c}: {errors.Length} stars, subtraction error median {errors[errors.Length / 2]:F3} sigma");
            errors[errors.Length / 2].ShouldBeLessThan(0.5);
        }
    }

    // #2378: the field of the tests below, the plain mono one (which holds three blends fitted as two stars, FitPair) with
    // stars where a tile's work reaches furthest from its tile. Two giants whose plateaus straddle a tile boundary, so the
    // finder's peak (wherever its ties fall on a plateau) and the plateau's centre, where the profile is read and written,
    // can lie in different tiles; a giant half a pixel short of a tile's right edge, whose centre rounds onto the next
    // tile; two bright saturated stars at the FACING edges of two tiles of one phase (columns 0 and 2 of row 1); and a
    // saturated star the frame's edge cuts, beside a tile boundary along that edge. Measured on this seed: four requests
    // ask for more than their ground, all SymmetricProfile reads of the giants (the one at (600, 262) is found in the
    // tile above its plateau's centre, and its ring reads 7 rows past that tile's ground), and 7 pairs of requests of
    // two tiles of one phase overlap (the test's met). No write is among them: a write reaches 104 px and a read 128,
    // so two tiles of a phase, 256 px apart, never have a write meet a request.
    private static Field TileEdgeField(int channels = 1) => MakeField(channels, extra: TileEdgeStars());

    private static Injected[] TileEdgeStars() =>
    [
        new Injected(600.0, 262.0, 1.0e7, true),
        new Injected(262.0, 600.0, 1.0e7, true),
        new Injected(650.0, 513.0, 1.0e7, true),
        new Injected(255.7, 830.0, 1.0e5, true),
        new Injected(253.0, 420.0, 200.0, true),
        new Injected(515.0, 420.0, 200.0, true),
        new Injected(Ring - 1.0, 250.0, 20.0, true),
    ];

    // #2378: the grounds of the tiles of one phase share no pixel, so work held to its ground cannot meet another tile's;
    // and each holds its own tile, with MaxSubtractRadius round it wherever the frame allows.
    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(1000, 700)]
    [InlineData(300, 2000)]
    [InlineData(200, 90)]
    public void TheGroundsOfTheTilesOfOnePhaseShareNoPixelAndEachHoldsItsTile(int width, int height)
    {
        const int Tile = ClassicalStarRemover.TileSize;
        const int Grow = ClassicalStarRemover.MaxSubtractRadius;
        var tilesX = (width + Tile - 1) / Tile;
        var tilesY = (height + Tile - 1) / Tile;
        var frame = new PixelRect(0, 0, width, height);
        for (var phase = 0; phase < 4; phase++)
        {
            var grounds = new List<PixelRect>();
            for (var ty = phase >> 1; ty < tilesY; ty += 2)
            {
                for (var tx = phase & 1; tx < tilesX; tx += 2)
                {
                    var ground = ClassicalStarRemover.TileGround(tx, ty, width, height);
                    var tile = PixelRect.Intersect(frame, new PixelRect(tx * Tile, ty * Tile, Tile, Tile));
                    ground.Contains(tile).ShouldBeTrue($"tile ({tx}, {ty})");
                    frame.Contains(ground).ShouldBeTrue();
                    ground.ShouldBe(PixelRect.Intersect(frame, PixelRect.FromLTRB(tile.Left - Grow, tile.Top - Grow, tx * Tile + Tile + Grow, ty * Tile + Tile + Grow)));
                    grounds.Add(ground);
                }
            }
            for (var a = 0; a < grounds.Count; a++)
            {
                for (var b = a + 1; b < grounds.Count; b++)
                {
                    grounds[a].IntersectsWith(grounds[b]).ShouldBeFalse($"phase {phase}: {grounds[a]} and {grounds[b]}");
                }
            }
        }
    }

    // #2378, the check the issue asked for: every rectangle a routine asks for through Square or Bound, recorded with the
    // ground it was held to, on the field of stars where a tile's work reaches furthest. No requested rectangle, once cut,
    // leaves its ground. That every read and write of the working planes goes through those two is the code review's claim,
    // not this test's. The field is one where work ASKED for more than its ground and where requests of two tiles of one
    // phase would have met without the ground (met > 0, measured 7), so the assertion is not over a field the clip never had to cut. Only a DEBUG build
    // records (Category=DebugOnly runs it on the Debug leg); Release skips.
    [Fact(Timeout = 600_000)]
    [Trait("Category", "DebugOnly")]
    public async Task NoTileReadsOrWritesPastItsGround()
    {
        Assert.SkipUnless(TileAccessRecorder.IsActive, "The recorder is compiled out of Release.");
        var ct = TestContext.Current.CancellationToken;
        var recorder = new TileAccessRecorder();
        var plate = await ClassicalStarRemover.BuildAsync(TileEdgeField().Image, null, null, tileThreads: -1, recorder, fillDugCores: true, ct);
        var accesses = recorder.Accesses.ToArray();
        var tileWork = accesses.Where(static a => a.Pass >= 0).ToArray();
        var pastGround = tileWork.Where(static a => !a.Asked.IsEmpty && !a.Ground.Contains(a.Asked)).ToArray();
        foreach (var a in pastGround)
        {
            output.WriteLine($"  past its ground: {a.Site}, pass {a.Pass} phase {a.Phase}, ground {a.Ground}, asked {a.Asked}, touched {a.Touched}");
        }
        output.WriteLine($"{accesses.Length} rectangles recorded,{tileWork.Length} in the tile passes ({tileWork.Count(static a => a.Write)} writes); "
            + $"{pastGround.Length} asked past their ground: {string.Join(", ", pastGround.GroupBy(static a => a.Site).Select(static g => $"{g.Key} {g.Count()}"))}");

        // What would have met without the ground: two tiles of one phase whose asked rectangles overlap. A write cannot be
        // the one, by the sizes (a write reaches 104 px, a read 128, and two tiles of a phase are 256 px apart), so the
        // meeting that exists is two requests for the same pixels, which is what a clip-less read past a tile would share.
        var met = 0;
        foreach (var phase in tileWork.GroupBy(static a => (a.Pass, a.Phase)))
        {
            var tiles = phase.GroupBy(static a => a.Ground).Select(static g => g.ToArray()).ToArray();
            for (var i = 0; i < tiles.Length; i++)
            {
                for (var j = i + 1; j < tiles.Length; j++)
                {
                    met += tiles[i].Sum(a => tiles[j].Count(b => a.Asked.IntersectsWith(b.Asked)));
                    // And what does meet: nothing, which is the claim itself, read off the touched rectangles.
                    var touchedI = tiles[i].Where(static a => !a.Touched.IsEmpty).ToArray();
                    var touchedJ = tiles[j].Where(static a => !a.Touched.IsEmpty).ToArray();
                    touchedI.Sum(a => touchedJ.Count(b => a.Touched.IntersectsWith(b.Touched)))
                        .ShouldBe(0, $"pass {phase.Key.Pass} phase {phase.Key.Phase}: tiles on {tiles[i][0].Ground} and {tiles[j][0].Ground} touched one pixel");
                }
            }
        }
        output.WriteLine($"{met} pairs of a phase's rectangles would have met without the ground");

        // The premises: a field with each kind of star the issue names, and with work that reached past its ground.
        plate.Stars.ShouldContain(static s => s.Model == StarFitModel.Pair, "a blend fitted as two stars");
        plate.Stars.ShouldContain(static s => s.Model == StarFitModel.Profile, "a giant subtracted by its own profile");
        Nearest(plate, Ring - 1.0, 250.0, 6.0).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted, "the star the edge cuts");
        tileWork.Length.ShouldBeGreaterThan(1000);
        pastGround.ShouldNotBeEmpty("the field has work that reached past its tile's ground");
        // Measured on this field: 7 (the field's comment).
        met.ShouldBeGreaterThan(0, "premise: two tiles of one phase would have met on this field without the ground (measured 7)");

        accesses.Where(static a => !a.Touched.IsEmpty && !a.Ground.Contains(a.Touched))
            .ShouldBeEmpty("every rectangle a routine asks for through Square or Bound is cut to its tile's ground");
    }

    // #2378: with every tile's work held to its ground, the plate is the same whether a phase's tiles run one at a time
    // or all at once, to the bit; on the field where their work reaches furthest. This varies only how many tile passes
    // run at once (a race could still hide in a schedule this run happens not to take); the deterministic proof of the
    // tiles' independence is ThePlateDoesNotDependOnTheOrderOfAPhasesTiles.
    [Fact(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    public async Task ThePlateDoesNotDependOnHowManyTilesRunAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = TileEdgeField().Image;
        var one = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: 1, recorder: null, fillDugCores: true, ct);
        var many = await ClassicalStarRemover.BuildAsync(image, null, null, tileThreads: -1, recorder: null, fillDugCores: true, ct);

        one.Plate.GetChannelSpan(0).SequenceEqual(many.Plate.GetChannelSpan(0)).ShouldBeTrue();
        many.Stars.Length.ShouldBe(one.Stars.Length);
        for (var k = 0; k < one.Stars.Length; k++)
        {
            // A row's amplitudes are an array, which a record compares by reference.
            (many.Stars[k] with { ChannelAmplitudes = default }).ShouldBe(one.Stars[k] with { ChannelAmplitudes = default }, $"row {k}");
            many.Stars[k].ChannelAmplitudes.SequenceEqual(one.Stars[k].ChannelAmplitudes).ShouldBeTrue($"row {k}");
        }
    }

    // N1.3 of #2451: the tiles of a phase are visited forward and reversed on one thread each; if any tile read or wrote
    // what another of its phase does, the order would show in the plate. Mono, and colour (the _lum writes and the colour
    // branch of AddModel and AddProfileAt), on the same stars.
    [Theory(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ThePlateDoesNotDependOnTheOrderOfAPhasesTiles(int channels)
    {
        var ct = TestContext.Current.CancellationToken;
        var image = TileEdgeField(channels).Image;
        var forward = await ClassicalStarRemover.BuildAsync(image, null, null, 1, null, true, false, ClassicalStarRemover.TileOrder.Forward, ct);
        var reversed = await ClassicalStarRemover.BuildAsync(image, null, null, 1, null, true, false, ClassicalStarRemover.TileOrder.Reversed, ct);

        for (var c = 0; c < channels; c++)
        {
            forward.Plate.GetChannelSpan(c).SequenceEqual(reversed.Plate.GetChannelSpan(c)).ShouldBeTrue($"channel {c}");
        }
        reversed.Stars.Length.ShouldBe(forward.Stars.Length);
        for (var k = 0; k < forward.Stars.Length; k++)
        {
            (reversed.Stars[k] with { ChannelAmplitudes = default }).ShouldBe(forward.Stars[k] with { ChannelAmplitudes = default }, $"row {k}");
            reversed.Stars[k].ChannelAmplitudes.SequenceEqual(forward.Stars[k].ChannelAmplitudes).ShouldBeTrue($"row {k}");
        }
    }

    [Fact]
    public async Task AColourMosaicIsRefused()
    {
        var planes = new[] { new float[64, 64] };
        var mosaic = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.RGGB });
        await Should.ThrowAsync<ArgumentException>(() => ClassicalStarRemover.BuildAsync(mosaic, cancellationToken: TestContext.Current.CancellationToken));
    }
}
