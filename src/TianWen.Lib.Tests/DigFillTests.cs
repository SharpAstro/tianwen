using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="DigFill"/> (#2204): the step after TianWen's own star remover. A hole dug under a removed star is filled to
/// the sky around it, light left standing is lowered within its cap, and nothing else moves. Its agreement with the Python
/// it was ported from is measured on whole masters by <c>training/star-removal/dig_fill_parity.py</c>, not here.
/// </summary>
[Collection("Imaging")]
public class DigFillTests
{
    private const int Size = 128;
    private const float Sky = 0.1f;
    private const float Noise = 1e-3f;
    private const int Cx = 70, Cy = 58;
    private const float StarPeak = 50 * Noise;

    // Sky and noise with one star on it (a Gaussian of 2 px), and what a remover made of it: the same sky and noise, with
    // `dig` noise sigmas dug out under the star (a Gaussian of 3 px) and `left` of the star's light still standing.
    private static (float[,] Frame, float[,] Model) Scene(float dig = 0f, float left = 0f, int seed = 3, float level = Sky)
    {
        var rng = new Random(seed);
        var frame = new float[Size, Size];
        var model = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                var star = (float)(StarPeak * Math.Exp(-r2 / (2.0 * 2 * 2)));
                var hole = (float)(dig * Noise * Math.Exp(-r2 / (2.0 * 3 * 3)));
                var ground = level + (float)(Noise * StarInjection.Gaussian(rng));
                frame[y, x] = ground + star;
                model[y, x] = ground - hole + (left * star);
            }
        }
        return (frame, model);
    }

    private static Image ImageOf(params float[][,] planes) => new(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

    // The plate's mean over the sky, in noise sigmas, within `radius` px of the star.
    private static double MeanAtStar(Image plate, int channel = 0, int radius = 3, float level = Sky)
    {
        double sum = 0;
        var count = 0;
        for (var y = Cy - radius; y <= Cy + radius; y++)
        {
            for (var x = Cx - radius; x <= Cx + radius; x++)
            {
                if (((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy)) <= radius * radius)
                {
                    sum += (plate[channel, y, x] - level) / Noise;
                    count++;
                }
            }
        }
        return sum / count;
    }

    [Fact]
    public void AHoleDugUnderARemovedStarIsFilledToItsSurroundings()
    {
        var (frame, model) = Scene(dig: 4f);
        var starless = ImageOf(model);
        MeanAtStar(starless).ShouldBeLessThan(-2.5, "the scene's premise: the model dug under the star");

        var (plate, share) = DigFill.Apply(ImageOf(frame), starless);

        MeanAtStar(plate).ShouldBeInRange(-0.75, 0.75, "the dig is filled to the sky around it");
        share.ShouldBeInRange(0.005, 0.05, "only the star's own pixels could change");
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // Noise standing over the sky counts as light left, so a pixel may come down, by no more than the cap on
                // the largest take there is (the star's peak and the dig under it).
                (model[y, x] - plate[0, y, x]).ShouldBeLessThanOrEqualTo(DigFill.MaxLoweringOfTaken * (StarPeak + (5 * Noise)), "a lowering stays within its cap");
                if (Math.Abs(x - Cx) > 12 || Math.Abs(y - Cy) > 12)
                {
                    plate[0, y, x].ShouldBe(model[y, x], "away from the star the plate is the model's, bit for bit");
                }
            }
        }
    }

    [Fact]
    public void LightLeftStandingIsLoweredWithinTheCap()
    {
        var (frame, model) = Scene(left: 0.1f);
        var starless = ImageOf(model);
        var before = MeanAtStar(starless, radius: 2);

        var (plate, _) = DigFill.Apply(ImageOf(frame), starless);

        // The largest take anywhere is under the star's whole peak, so no pixel may come down by more than this.
        var cap = DigFill.MaxLoweringOfTaken * StarPeak;
        var most = 0f;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                most = Math.Max(most, model[y, x] - plate[0, y, x]);
            }
        }
        most.ShouldBeLessThanOrEqualTo(cap, "a lowering is bounded by a share of what the model took");
        most.ShouldBeGreaterThan(Noise, "and the residue did come down");
        MeanAtStar(plate, radius: 2).ShouldBeLessThan(before - 0.5);
    }

    [Fact]
    public void APlateWithNoStarTakenIsHandedBackAsItIs()
    {
        var (frame, _) = Scene();
        var input = ImageOf(frame);
        var starless = ImageOf(Scene().Frame);

        var (plate, share) = DigFill.Apply(input, starless);

        plate.ShouldBeSameAs(starless);
        share.ShouldBe(0);
    }

    [Fact]
    public void AbsentPixelsStayAbsentAndAreHeldOutOfTheFill()
    {
        var (frame, model) = Scene(dig: 4f);
        // A canvas ring absent in both, a patch absent in the input only, and one in the plate only, right beside the star.
        for (var i = 0; i < Size; i++)
        {
            for (var k = 0; k < 4; k++)
            {
                frame[k, i] = frame[Size - 1 - k, i] = frame[i, k] = frame[i, Size - 1 - k] = float.NaN;
                model[k, i] = model[Size - 1 - k, i] = model[i, k] = model[i, Size - 1 - k] = float.NaN;
            }
        }
        frame[30, 30] = float.NaN;
        model[Cy + 2, Cx + 2] = float.NaN;

        var (plate, _) = DigFill.Apply(ImageOf(frame), ImageOf(model));

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var absent = float.IsNaN(frame[y, x]) || float.IsNaN(model[y, x]);
                float.IsNaN(plate[0, y, x]).ShouldBe(absent, $"({x}, {y})");
            }
        }
        MeanAtStar(plate, radius: 1).ShouldBeInRange(-1.0, 1.0, "the dig is still filled, the absent pixel beside it lending nothing");
    }

    [Fact]
    public void EveryChannelOfAColourPlateIsFilledAtItsOwnLevel()
    {
        float[] levels = [0.05f, 0.1f, 0.2f];
        var frames = new float[3][,];
        var models = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            (frames[c], models[c]) = Scene(dig: 4f, seed: 10 + c, level: levels[c]);
        }
        // Absent in one channel only: that channel stays absent there, the others keep the model's value.
        models[1][20, 20] = float.NaN;

        var (plate, _) = DigFill.Apply(ImageOf(frames), ImageOf(models));

        plate.ChannelCount.ShouldBe(3);
        for (var c = 0; c < 3; c++)
        {
            MeanAtStar(ImageOf(models), c, level: levels[c]).ShouldBeLessThan(-2.5);
            MeanAtStar(plate, c, level: levels[c]).ShouldBeInRange(-0.75, 0.75, $"channel {c}");
        }
        float.IsNaN(plate[1, 20, 20]).ShouldBeTrue();
        plate[0, 20, 20].ShouldBe(models[0][20, 20]);
        plate[2, 20, 20].ShouldBe(models[2][20, 20]);
    }

    /// <summary>Returns a fixed plate on whatever scale it is handed the input on; takes the dig-fill step or does not.</summary>
    private sealed class FixedRemover(float[,] model, float inputPeak, bool takesDigFill) : IStarRemover
    {
        public string Name => "Test/Fixed";

        public bool TakesDigFill => takesDigFill;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var peak = 0f;
            foreach (var v in input.GetChannelSpan(0))
            {
                peak = Math.Max(peak, v);
            }
            var scale = peak / inputPeak;
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

    private static float PeakOf(float[,] plane)
    {
        var peak = 0f;
        foreach (var v in plane)
        {
            peak = Math.Max(peak, v);
        }
        return peak;
    }

    private static void ShouldBeBitForBit(Image actual, Image expected)
    {
        var a = actual.GetChannelSpan(0);
        var e = expected.GetChannelSpan(0);
        a.Length.ShouldBe(e.Length);
        for (var i = 0; i < a.Length; i++)
        {
            BitConverter.SingleToInt32Bits(a[i]).ShouldBe(BitConverter.SingleToInt32Bits(e[i]), $"pixel {i}");
        }
    }

    [Fact]
    public async Task TheSplitFillsDigsOnlyForARemoverThatTakesItAndOnlyWhenOn()
    {
        var (frame, model) = Scene(dig: 4f);
        var input = ImageOf(frame);
        var peak = PeakOf(frame);
        var ct = TestContext.Current.CancellationToken;
        var own = new FixedRemover(model, peak, takesDigFill: true);
        var raw = (await NarrowbandCombination.StarlessAsync([input], own, ct))[0];
        MeanAtStar(raw).ShouldBeLessThan(-2.5);

        var (on, onStars, _, onShare) = await NarrowbandCombination.SplitStarsAsync([input], own, protectCores: null, fillDigs: true, ct);
        var (off, _, _, offShare) = await NarrowbandCombination.SplitStarsAsync([input], own, protectCores: null, fillDigs: false, ct);
        var (other, _, _, otherShare) = await NarrowbandCombination.SplitStarsAsync(
            [input], new FixedRemover(model, peak, takesDigFill: false), protectCores: null, fillDigs: true, ct);

        MeanAtStar(on[0]).ShouldBeInRange(-0.75, 0.75, "on, and the remover takes it: filled");
        onShare.ShouldNotBeNull();
        onShare[0].ShouldBeGreaterThan(0);

        ShouldBeBitForBit(off[0], raw);
        offShare.ShouldBeNull("switched off: the model's output, bit for bit");
        ShouldBeBitForBit(other[0], raw);
        otherShare.ShouldBeNull("a remover that does not take it (RC-Astro) is never touched");

        // The stars are the input less the FILLED plate, so plate plus stars is still the input.
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                onStars[0][0, y, x].ShouldBe(frame[y, x] - on[0][0, y, x]);
            }
        }
    }

    [Fact]
    public async Task TheSplitFillsAgainstTheOriginalInputAfterCoreProtection()
    {
        // A galaxy (a Gaussian of 60 px at 400 sigma) with a compact nucleus, as ExtendedCoreMaskTests builds it, and one
        // dug star far from it. The model took the nucleus (a remover that takes everything compact) and dug under the star.
        const int size = 512, galaxyX = 256, galaxyY = 240, starX = 70, starY = 70;
        const float sky = 0.02f, noise = 1e-4f;
        var rng = new Random(1);
        var frame = new float[size, size];
        var model = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var gr2 = ((x - galaxyX) * (x - galaxyX)) + ((y - galaxyY) * (y - galaxyY));
                var sr2 = ((x - starX) * (x - starX)) + ((y - starY) * (y - starY));
                var smooth = sky + (float)(400 * noise * Math.Exp(-gr2 / (2.0 * 60 * 60)));
                var nucleus = (float)(200 * noise * Math.Exp(-gr2 / (2.0 * 1.2 * 1.2)));
                var star = (float)(300 * noise * Math.Exp(-sr2 / (2.0 * 1.2 * 1.2)));
                var hole = (float)(4 * noise * Math.Exp(-sr2 / (2.0 * 3 * 3)));
                var n = (float)(noise * StarInjection.Gaussian(rng));
                frame[y, x] = smooth + nucleus + star + n;
                model[y, x] = smooth + n - hole;
            }
        }
        var input = ImageOf(frame);
        var ct = TestContext.Current.CancellationToken;
        var own = new FixedRemover(model, PeakOf(frame), takesDigFill: true);

        var (starless, stars, cores, share) = await NarrowbandCombination.SplitStarsAsync([input], own, ExtendedCoreMask.Options.Default, fillDigs: true, ct);

        cores.ShouldNotBeNull();
        cores[0].Cores.Length.ShouldBe(1, "the nucleus is an extended core, the lone star is not");
        share.ShouldNotBeNull();
        starless[0][0, galaxyY, galaxyX].ShouldBe(frame[galaxyY, galaxyX], "the core is the plane's, bit for bit, and the fill leaves it alone");
        stars[0][0, galaxyY, galaxyX].ShouldBe(0f, "no stars in the core");

        double sum = 0;
        var count = 0;
        for (var y = starY - 3; y <= starY + 3; y++)
        {
            for (var x = starX - 3; x <= starX + 3; x++)
            {
                if (((x - starX) * (x - starX)) + ((y - starY) * (y - starY)) <= 9)
                {
                    sum += (starless[0][0, y, x] - sky) / noise;
                    count++;
                }
            }
        }
        (sum / count).ShouldBeInRange(-0.75, 0.75, "the dug star is filled to the sky");
        stars[0][0, starY, starX].ShouldBe(frame[starY, starX] - starless[0][0, starY, starX]);
    }
}
