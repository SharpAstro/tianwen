using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
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
[Collection("Imaging")]
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

    private static Field MakeField(int channels, int seed = 7)
    {
        var rng = new Random(seed);
        var alpha = Fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0));
        var noise = CorrelatedNoise(rng, Size, Size, 0.35);
        var knot = (X: 700.0, Y: 300.0, Peak: 40 * Sigma, Fwhm: 3 * Fwhm);

        var truth = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            truth[c] = new float[Size * Size];
            var cNoise = c == 0 ? noise : CorrelatedNoise(rng, Size, Size, 0.35);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var nebula = 0.03 * Math.Exp(-((x - 400.0) * (x - 400.0) + (y - 600.0) * (y - 600.0)) / (2 * 120.0 * 120.0));
                    var gradient = 0.01 * x / Size;
                    var ks = knot.Fwhm / 2.3548;
                    var knotValue = knot.Peak * Math.Exp(-((x - knot.X) * (x - knot.X) + (y - knot.Y) * (y - knot.Y)) / (2 * ks * ks));
                    truth[c][y * Size + x] = (float)(Sky + nebula + gradient + knotValue + Sigma * cNoise[y * Size + x]);
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

        var planes = new float[channels][,];
        var gains = channels == 3 ? new[] { 0.8, 1.0, 0.6 } : new[] { 1.0 };
        for (var c = 0; c < channels; c++)
        {
            var plane = (float[])truth[c].Clone();
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

    private static void Render(float[] plane, double cx, double cy, double peak, double alpha, double beta)
    {
        var reach = alpha * Math.Sqrt(Math.Pow(peak / (0.01 * Sigma), 1.0 / beta) - 1.0);
        var r = (int)Math.Ceiling(reach);
        for (var y = Math.Max(0, (int)cy - r); y <= Math.Min(Size - 1, (int)cy + r); y++)
        {
            for (var x = Math.Max(0, (int)cx - r); x <= Math.Min(Size - 1, (int)cx + r); x++)
            {
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
    public async Task AClosePairIsTwoStarsAndTheStarBesideTheRingIsTaken()
    {
        var field = MakeField(1);
        var plate = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);

        Nearest(plate, 300.0, 450.0, 1.2).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        Nearest(plate, 304.0, 450.0, 1.2).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        Nearest(plate, Ring + 4.0, 500.0, 1.5).ShouldNotBeNull().Outcome.ShouldBe(StarFitOutcome.Subtracted);
        var pairError = ErrorRms(plate, field, 302.0, 450.0, 5.0);
        output.WriteLine($"close pair: error {pairError:F3} sigma; {string.Join("; ", plate.Stars.Where(static s => Math.Abs(s.X - 302) < 6 && Math.Abs(s.Y - 450) < 4).Select(static s => $"({s.X:F2},{s.Y:F2}) {s.Outcome} w={s.WidthScale:F2} A={s.Amplitude:E2}"))}");
        pairError.ShouldBeLessThan(1.0);
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

    [Fact(Timeout = 600_000)]
    public async Task TheSameImageGivesTheSamePlate()
    {
        var field = MakeField(1);
        var a = await ClassicalStarRemover.BuildAsync(field.Image, cancellationToken: TestContext.Current.CancellationToken);
        var b = await ClassicalStarRemover.BuildAsync(MakeField(1).Image, cancellationToken: TestContext.Current.CancellationToken);
        a.Plate.GetChannelSpan(0).SequenceEqual(b.Plate.GetChannelSpan(0)).ShouldBeTrue();
        a.Stars.Length.ShouldBe(b.Stars.Length);
    }

    [Fact(Timeout = 300_000)]
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

    [Fact]
    public async Task AColourMosaicIsRefused()
    {
        var planes = new[] { new float[64, 64] };
        var mosaic = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.RGGB });
        await Should.ThrowAsync<ArgumentException>(() => ClassicalStarRemover.BuildAsync(mosaic, cancellationToken: TestContext.Current.CancellationToken));
    }
}
