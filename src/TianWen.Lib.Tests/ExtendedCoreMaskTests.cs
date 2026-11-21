using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="ExtendedCoreMask"/> (#1658): the brightest part of a galaxy or nebula kept out of the stars layer whichever
/// remover runs, and nothing else touched.
/// </summary>
[Collection("Imaging")]
public class ExtendedCoreMaskTests(ITestOutputHelper output)
{
    private const int Size = 512;
    private const float Sky = 0.02f;
    private const float Noise = 1e-4f;
    private const int Cx = 256, Cy = 240;

    // A frame of sky and noise, a galaxy (a Gaussian of 60 px at peakSigma noise sigmas) with a compact nucleus on it, and
    // field stars; the smooth model a remover that takes everything compact would return; and where the stars are.
    private static (float[,] Frame, float[,] Model, (int X, int Y)[] Stars) Scene(double peakSigma, bool galaxy = true, int seed = 1)
    {
        var rng = new Random(seed);
        var frame = new float[Size, Size];
        var model = new float[Size, Size];
        var stars = Enumerable.Range(0, 30).Select(_ => (X: rng.Next(20, Size - 20), Y: rng.Next(20, Size - 20)))
            .Where(s => Math.Abs(s.X - Cx) > 60 || Math.Abs(s.Y - Cy) > 60).ToArray();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                var smooth = Sky + (galaxy ? (float)(peakSigma * Noise * Math.Exp(-r2 / (2.0 * 60 * 60))) : 0f);
                var compact = galaxy ? (float)(200 * Noise * Math.Exp(-r2 / (2.0 * 1.2 * 1.2))) : 0f;
                foreach (var (sx, sy) in stars)
                {
                    var d2 = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
                    if (d2 < 100)
                    {
                        compact += (float)(300 * Noise * Math.Exp(-d2 / (2.0 * 1.2 * 1.2)));
                    }
                }
                var n = (float)(Noise * StarInjection.Gaussian(rng));
                frame[y, x] = smooth + compact + n;
                model[y, x] = smooth + n;
            }
        }
        return (frame, model, stars);
    }

    private static Image ImageOf(float[,] plane) => new([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

    [Fact]
    public void AGalaxysNucleusIsKeptAndTheStarsAroundItAreNot()
    {
        var (frame, model, stars) = Scene(peakSigma: 400);
        var input = ImageOf(frame);

        var mask = ExtendedCoreMask.Find(input);

        output.WriteLine($"sigma {mask.SkySigma:E3}; cores: {string.Join("; ", mask.Cores.Select(c => $"({c.X}, {c.Y}) peak {c.PeakSigma:F0} sigma, core {c.CoreAreaPx} of {c.SourceAreaPx} px"))}");
        mask.Cores.Length.ShouldBe(1);
        var core = mask.Cores[0];
        Math.Abs(core.X - Cx).ShouldBeLessThanOrEqualTo(ExtendedCoreMask.BlockPx);
        Math.Abs(core.Y - Cy).ShouldBeLessThanOrEqualTo(ExtendedCoreMask.BlockPx);
        core.CoreAreaPx.ShouldBeLessThanOrEqualTo((int)(0.01 * core.SourceAreaPx) + (ExtendedCoreMask.BlockPx * ExtendedCoreMask.BlockPx));
        mask.SkySigma.ShouldBe(Noise, Noise * 0.1);

        mask.Weight[(Cy * Size) + Cx].ShouldBe(1f, "a core is fed back whole; its feather lies outside it");
        var kept = mask.Apply(input, ImageOf(model));
        kept[0, Cy, Cx].ShouldBe(frame[Cy, Cx], 1e-7f, "the nucleus is the input's");
        foreach (var (sx, sy) in stars)
        {
            kept[0, sy, sx].ShouldBe(model[sy, sx], 1e-7f, "a field star is removed as the remover removed it");
        }
    }

    [Fact]
    public void AStarFieldHasNoCoreAndAFaintNebulaGetsNone()
    {
        ExtendedCoreMask.Find(ImageOf(Scene(peakSigma: 0, galaxy: false).Frame)).Cores.ShouldBeEmpty("stars are not an extended source");
        var faint = ExtendedCoreMask.Find(ImageOf(Scene(peakSigma: 20).Frame));
        faint.Cores.ShouldBeEmpty("a source under the minimum peak is never taken as a star");
        faint.Weight.ShouldAllBe(w => w == 0f);
    }

    [Fact]
    public void AQuantisedFrameStillReadsItsNoiseAndFindsTheCore()
    {
        // An 8-bit frame: most neighbour differences are zero, so their median is too.
        var (frame, _, _) = Scene(peakSigma: 400);
        var quantum = 2.5f * Noise;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                frame[y, x] = MathF.Round(frame[y, x] / quantum) * quantum;
            }
        }

        var mask = ExtendedCoreMask.Find(ImageOf(frame));

        mask.SkySigma.ShouldBeGreaterThan(0.0);
        mask.Cores.Length.ShouldBe(1);
    }

    // A flat sky with noise and a list of Gaussians (centre, sigma px, amplitude in noise sigmas) added to it.
    private static float[,] Painted(params (double X, double Y, double Sigma, double Amp)[] blobs)
    {
        var rng = new Random(7);
        var frame = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double v = Sky + (Noise * StarInjection.Gaussian(rng));
                foreach (var (bx, by, s, amp) in blobs)
                {
                    var d2 = ((x - bx) * (x - bx)) + ((y - by) * (y - by));
                    if (d2 < 9 * s * s)
                    {
                        v += amp * Noise * Math.Exp(-d2 / (2.0 * s * s));
                    }
                }
                frame[y, x] = (float)v;
            }
        }
        return frame;
    }

    [Fact]
    public void ABrightStarWithAWideHaloGetsNoCore()
    {
        // The halo alone (150 sigma, 20 px) makes an extended source over MinPeakSigma; the star on it stands far over the map.
        var mask = ExtendedCoreMask.Find(ImageOf(Painted((256, 256, 20, 150), (256, 256, 1.5, 6000))));

        mask.Cores.ShouldBeEmpty("a star's halo fills the median window at a small share of its height, so its peak ratio is far over the bar and it is no extended source");
    }

    [Fact]
    public void AStripeAlongTheBorderGetsNoCore()
    {
        var frame = Painted();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                frame[y, x] += 200 * Noise;
            }
        }

        var mask = ExtendedCoreMask.Find(ImageOf(frame));

        mask.Cores.ShouldBeEmpty("a source peaking within the median window's reach of the border is a coverage edge, not a source");
    }

    [Fact]
    public void ASmoothBulgeWithACompactNucleusKeepsItsCore()
    {
        // The M31 kind: a smooth bulge and a compact nucleus on it.
        var mask = ExtendedCoreMask.Find(ImageOf(Painted((Cx, Cy, 30, 300), (Cx, Cy, 2, 400))));

        output.WriteLine(string.Join("; ", mask.Cores.Select(c => $"({c.X}, {c.Y}) ratio {c.PeakRatio:F2} edge {c.EdgeDistancePx}")));
        mask.Cores.Length.ShouldBe(1, "an extended source's core is the map: the nucleus must not read as a star");
        mask.Cores[0].PeakRatio.ShouldBeLessThan(ExtendedCoreMask.Options.Default.MaxPeakRatio, "the nucleus sits on a bulge that is the map, so the ratio is under the bar");
    }

    [Fact]
    public void ANebulaKnotWithStarsInsideItKeepsItsCore()
    {
        var mask = ExtendedCoreMask.Find(ImageOf(Painted((Cx, Cy, 50, 300), (Cx + 25, Cy, 1.5, 2000), (Cx - 25, Cy + 10, 1.5, 2000),
            (Cx, Cy - 28, 1.5, 2000), (Cx + 10, Cy + 30, 1.5, 2000))));

        output.WriteLine(string.Join("; ", mask.Cores.Select(c => $"({c.X}, {c.Y}) ratio {c.PeakRatio:F2}")));
        mask.Cores.Length.ShouldBe(1, "the stars inside a knot are not in the box about its peak, so the knot keeps its core");
    }

    // A clipped star added to a frame: a flat top of the given radius at clipSigmas noise sigmas, and wings falling as a
    // power of the distance beyond it (what a profile cut off at the sensor's ceiling looks like).
    private static float[,] WithClippedStar(float[,] frame, int cx, int cy, double topRadius, double power, double clipSigmas)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                frame[y, x] += (float)(clipSigmas * Noise * (r <= topRadius ? 1.0 : Math.Pow(topRadius / r, power)));
            }
        }
        return frame;
    }

    private static readonly ExtendedCoreMask.Options ClippedStarRuleOff = ExtendedCoreMask.Options.Default with { MaxCoreOverFlatTop = 0 };

    [Fact]
    public void AClippedStarWideEnoughToReadAsACoreGetsNone()
    {
        // The Alcyone kind (#2360): a top 40 px wide, so the 56 px median under it is most of the clip level and the star
        // rule's ratio reads low however bright the star is.
        var frame = WithClippedStar(Painted(), 256, 256, topRadius: 20, power: 4, clipSigmas: 20000);

        var without = ExtendedCoreMask.Find(ImageOf(frame), ClippedStarRuleOff);
        output.WriteLine(string.Join("; ", without.Cores.Select(c =>
            $"({c.X}, {c.Y}) ratio {c.PeakRatio:F2}, core {c.CoreAreaPx} px, top {c.FlatTopPx} px round {c.FlatTopRoundness:F2} fall {c.FlatTopFall:F3}")));
        without.Cores.Length.ShouldBe(1, "the star rule alone takes this star for an extended source: that is the defect");
        var core = without.Cores[0];
        core.PeakRatio.ShouldBeLessThan(ExtendedCoreMask.Options.Default.MaxPeakRatio, "a clipped top this wide holds the ratio under the bar");
        core.FlatTopPx.ShouldBeInRange(1150, 1400, "the top is the disc that was painted (1,257 px) and the first pixels of its wings");
        core.FlatTopRoundness.ShouldBeGreaterThan(0.9);
        core.FlatTopFall.ShouldBeLessThan(0.1, "two radii out the light is a sixteenth of the top's");
        core.CoreAreaPx.ShouldBeLessThan(9 * core.FlatTopPx, "the core is no larger than the star's own disc");

        ExtendedCoreMask.Find(ImageOf(frame)).Cores.ShouldBeEmpty("a source whose core is a clipped star's flat top is the star");
    }

    [Fact]
    public void AClippedStarOnARealCoreLeavesTheCoreItsProtection()
    {
        // The eta Carinae kind: a wide bright nebula, and a clipped star at its brightest part. The top is a star's by every
        // measure, and the core is many times the top, so the core stays.
        var frame = WithClippedStar(Painted((256, 256, 80, 3000)), 256, 256, topRadius: 4.5, power: 6, clipSigmas: 40000);

        var mask = ExtendedCoreMask.Find(ImageOf(frame));

        output.WriteLine(string.Join("; ", mask.Cores.Select(c =>
            $"({c.X}, {c.Y}) ratio {c.PeakRatio:F2}, core {c.CoreAreaPx} px, top {c.FlatTopPx} px round {c.FlatTopRoundness:F2} fall {c.FlatTopFall:F3}")));
        mask.Cores.Length.ShouldBe(1, "a clipped star on a real core does not take the core's protection away");
        var core = mask.Cores[0];
        core.FlatTopPx.ShouldBeGreaterThanOrEqualTo(50, "the top is wide enough for the rule to read");
        core.FlatTopRoundness.ShouldBeGreaterThan(0.75);
        core.FlatTopFall.ShouldBeLessThan(0.18, "the top is a star's by its fall too");
        core.CoreAreaPx.ShouldBeGreaterThan(9 * core.FlatTopPx, "what keeps the core is its size against the star's top");
    }

    [Fact]
    public void ABurntOutNebulaCoreKeepsItsCore()
    {
        // The Orion kind: a nebula's own light clipped into a flat top, here two knots side by side, so the top is not round.
        var frame = Painted((238, 256, 20, 600), (274, 256, 20, 600));
        var ceiling = Sky + (400 * Noise);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                frame[y, x] = Math.Min(frame[y, x], ceiling);
            }
        }

        var mask = ExtendedCoreMask.Find(ImageOf(frame));

        output.WriteLine(string.Join("; ", mask.Cores.Select(c =>
            $"({c.X}, {c.Y}) ratio {c.PeakRatio:F2}, core {c.CoreAreaPx} px, top {c.FlatTopPx} px round {c.FlatTopRoundness:F2} fall {c.FlatTopFall:F3}")));
        mask.Cores.Length.ShouldBe(1, "a clipped nebula core is still a core");
        mask.Cores[0].FlatTopPx.ShouldBeGreaterThan(1000, "the clipped light is one wide top");
        mask.Cores[0].FlatTopRoundness.ShouldBeLessThan(0.75, "which is not round, so it is not a star's");
    }

    /// <summary>Returns a fixed plate on whatever scale it is handed the input on.</summary>
    private sealed class FixedRemover(float[,] model, float inputPeak) : IStarRemover
    {
        public string Name => "Test/Fixed";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var scale = input.GetChannelSpan(0).ToArray().Where(float.IsFinite).Max() / inputPeak;
            var plane = new float[input.Height, input.Width];
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    plane[y, x] = model[y, x] * scale;
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    [Fact]
    public async Task TheSplitKeepsTheCoreOutOfTheStarsOnlyWhenAsked()
    {
        var (frame, model, _) = Scene(peakSigma: 400);
        var input = ImageOf(frame);
        var peak = frame.Cast<float>().Max();
        var ct = TestContext.Current.CancellationToken;

        var (_, plain) = await NarrowbandCombination.SplitStarsAsync([input], new FixedRemover(model, peak), ct);
        var (_, kept, cores, _) = await NarrowbandCombination.SplitStarsAsync([input], new FixedRemover(model, peak), ExtendedCoreMask.Options.Default, fillDigs: true, ct);

        plain[0][0, Cy, Cx].ShouldBeGreaterThan(100 * Noise, "without protection the remover's take of the nucleus is in the stars");
        kept[0][0, Cy, Cx].ShouldBe(0f, 1e-6f, "with it the nucleus stays in the starless plate");
        cores.ShouldNotBeNull();
        cores[0].Cores.Length.ShouldBe(1);
    }
}
