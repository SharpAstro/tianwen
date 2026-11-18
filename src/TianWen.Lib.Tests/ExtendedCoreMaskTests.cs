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
