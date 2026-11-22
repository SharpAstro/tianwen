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

    // #2209: the lift's cap had no test, and taking it out left every scene here passing. No scene that reaches it was
    // found (see DigFill.Capped for why), so the rule is pinned where it is stated.
    [Theory]
    [InlineData(10f, 0f, 2f, 4f, 0f)]      // a lift is at most twice what the model took
    [InlineData(3f, 0f, 2f, 3f, 0f)]       // and is itself when it is under that
    [InlineData(-5f, 0f, 2f, 0f, 0f)]      // a wish to go down is no lift
    [InlineData(0f, 10f, 2f, 0f, 0.1f)]    // light left standing comes down by at most a twentieth of the take
    [InlineData(0f, 0.05f, 2f, 0f, 0.05f)] // and by itself when it is under that
    [InlineData(10f, 10f, 0f, 0f, 0f)]     // where the model took nothing, nothing is corrected
    public void EachCorrectionIsHeldToItsShareOfWhatTheModelTook(float wantedLift, float standing, float taken, float up, float down)
    {
        var capped = DigFill.Capped(wantedLift, standing, taken);

        capped.Up.ShouldBe(up, 1e-6f);
        capped.Down.ShouldBe(down, 1e-6f);
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

    // Scene's star and dig on a ground that carries real structure: a ripple of `ripple` noise sigmas (its scatter is half
    // that), in the frame and in the model alike, so the model took the star and dug under it and nothing else. The
    // ripple's periods are 24 and 30 px: coarse enough that the noise reading (a 5 px high-pass) does not take it for noise
    // (at 16 and 20 px it read the noise 1.75 times too high, and the scatter in noise units fell under the bar), fine enough
    // that the 4 px Gaussian of the sky still sees its swing (a scatter of 4.5 to 6.9 sigmas over the star's pixels).
    private static (float[,] Frame, float[,] Model) StructuredScene(float ripple, float level, float noise, float dig = 6f, int seed = 5)
    {
        var rng = new Random(seed);
        var frame = new float[Size, Size];
        var model = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                var star = (float)(50 * noise * Math.Exp(-r2 / (2.0 * 2 * 2)));
                var hole = (float)(dig * noise * Math.Exp(-r2 / (2.0 * 3 * 3)));
                var structure = (float)(ripple * noise * Math.Sin(2 * Math.PI * x / 24.0) * Math.Sin(2 * Math.PI * y / 30.0));
                var ground = level + structure + (float)(noise * StarInjection.Gaussian(rng));
                frame[y, x] = ground + star;
                model[y, x] = ground - hole;
            }
        }
        return (frame, model);
    }

    private static int PixelsChanged(Image plate, float[,] model)
    {
        var changed = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (plate[0, y, x] != model[y, x])
                {
                    changed++;
                }
            }
        }
        return changed;
    }

    [Fact]
    public void ADigInStructureTheSkyCannotBeTrustedOnIsLeftAlone()
    {
        // #2243: inside bright, structured nebula the sky under a removed star is not known, and filling to an interpolated
        // one drew flat patches over real dark structure. The same dig is filled on flat sky and left alone here.
        var (flatFrame, flatModel) = StructuredScene(ripple: 0f, level: Sky, noise: Noise);
        var (flat, _) = DigFill.Apply(ImageOf(flatFrame), ImageOf(flatModel));
        PixelsChanged(flat, flatModel).ShouldBeGreaterThan(20, "the premise: on flat sky this dig is filled");

        // A ripple of 12 sigmas scatters by 6: over the bar at which nothing is corrected.
        var (frame, model) = StructuredScene(ripple: 3 * DigFill.UntrustedSkySpread, level: Sky, noise: Noise);
        var (plate, share) = DigFill.Apply(ImageOf(frame), ImageOf(model));

        share.ShouldBeGreaterThan(0, "the star was taken, so its pixels could have changed");
        PixelsChanged(plate, model).ShouldBe(0, "where the sky cannot be trusted the plate is the model's, bit for bit");
    }

    [Fact]
    public void TheSkysScatterIsReadRightOnAVeryBrightGround()
    {
        // The scatter is the mean of squares less the squared mean. At a level of 25,000 noise sigmas those two differ in
        // the ninth digit, so in single precision the structure reads as none and the dig would be filled as on flat sky.
        const float noise = 2e-5f;
        var (frame, model) = StructuredScene(ripple: 3 * DigFill.UntrustedSkySpread, level: 0.5f, noise: noise);

        var (plate, _) = DigFill.Apply(ImageOf(frame), ImageOf(model));

        PixelsChanged(plate, model).ShouldBe(0, "the structure is seen at any level");

        // And flat sky at that level is still trusted, so its dig is still filled.
        var (flatFrame, flatModel) = StructuredScene(ripple: 0f, level: 0.5f, noise: noise);
        var (flat, _) = DigFill.Apply(ImageOf(flatFrame), ImageOf(flatModel));
        PixelsChanged(flat, flatModel).ShouldBeGreaterThan(20);
    }

    // The light of a grid of stars about the scene's centre at (x, y), in noise sigmas: each a Gaussian of 1.5 px and 60
    // sigmas, `pitch` px apart, out to `reach` px from the centre along each axis.
    private static double Cluster(int x, int y, int pitch, int reach)
    {
        var sum = 0.0;
        for (var sy = Cy - reach; sy <= Cy + reach; sy += pitch)
        {
            for (var sx = Cx - reach; sx <= Cx + reach; sx += pitch)
            {
                var d2 = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
                if (d2 < 100)
                {
                    sum += 60 * Math.Exp(-d2 / (2.0 * 1.5 * 1.5));
                }
            }
        }
        return sum;
    }

    // The largest fall and the largest rise of the plate against the model within `radius` px of the centre, noise sigmas.
    private static (double Lowered, double Lifted) MostMoved(Image plate, float[,] model, int radius)
    {
        var (lowered, lifted) = (0.0, 0.0);
        for (var y = Cy - radius; y <= Cy + radius; y++)
        {
            for (var x = Cx - radius; x <= Cx + radius; x++)
            {
                if (((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy)) <= radius * radius)
                {
                    lowered = Math.Max(lowered, (model[y, x] - plate[0, y, x]) / Noise);
                    lifted = Math.Max(lifted, (plate[0, y, x] - model[y, x]) / Noise);
                }
            }
        }
        return (lowered, lifted);
    }

    [Fact]
    public void AKnotUnderCrowdedStarsIsNotLoweredToTheFarSky()
    {
        // #2252: stars 5 px apart make one set, so the sky under it is spread in from its far edge, where it is flat and so
        // trusted. A knot of nebula between the star cores stands over that sky and was lowered toward it in the squares of
        // the cap. The set as a whole standing far over its sky is what says the light is the scene's.
        (float[,] Frame, float[,] Model) Knot(double height)
        {
            var rng = new Random(9);
            var frame = new float[Size, Size];
            var model = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                    // The model took the stars whole and left the knot: a Gaussian of 6 px, well inside the set's 17 px.
                    var ground = Sky + (float)(Noise * (StarInjection.Gaussian(rng) + (height * Math.Exp(-r2 / (2.0 * 6 * 6)))));
                    frame[y, x] = ground + (float)(Noise * Cluster(x, y, pitch: 5, reach: 15));
                    model[y, x] = ground;
                }
            }
            return (frame, model);
        }

        // The premise: light standing a sigma and a half over the sky is taken for residue and lowered.
        var (faintFrame, faintModel) = Knot(height: 1.5);
        var (faint, share) = DigFill.Apply(ImageOf(faintFrame), ImageOf(faintModel));
        share.ShouldBeGreaterThan(0.05, "the stars make one set of over a thousand pixels");
        MostMoved(faint, faintModel, radius: 8).Lowered.ShouldBeGreaterThan(0.75, "a set that stands near its sky is lowered to it");

        // A knot of 20 sigmas: the set stands 14 sigmas over its sky at the centre and still over 7 at 8 px.
        var (frame, model) = Knot(height: 20);
        var (plate, _) = DigFill.Apply(ImageOf(frame), ImageOf(model));
        for (var y = Cy - 8; y <= Cy + 8; y++)
        {
            for (var x = Cx - 8; x <= Cx + 8; x++)
            {
                if (((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy)) <= 64)
                {
                    plate[0, y, x].ShouldBe(model[y, x], $"the knot is the model's, bit for bit, at ({x}, {y})");
                }
            }
        }
    }

    [Fact]
    public void ADarkGlobuleUnderStarsIsNotFilledAndADigOfTheSameShapeIs()
    {
        // #2252, the fill's side. One plate, two inputs. The plate lies under its far sky in a dip of 14 sigmas (a Gaussian
        // of 6 px) inside a set made one by a broad take of 2.5 sigmas and the stars on it. Where the INPUT holds that dip
        // between its stars it is a globule and stays; where the input is level it is a hole the model dug and is filled.
        // (At 8 sigmas the input's floor about the centre star reads 3.6 under the sky, inside the fade, and a fifth of
        // the fill comes through: the dip is made deep enough to be clear of the bars.)
        (float[,] Frame, float[,] Model) Dip(bool inTheInput)
        {
            var rng = new Random(13);
            var frame = new float[Size, Size];
            var model = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                    var r = Math.Sqrt(r2);
                    var dip = 14 * Math.Exp(-r2 / (2.0 * 6 * 6));
                    var broadTake = 2.5 * (r <= 18 ? 1.0 : Math.Exp(-((r - 18) * (r - 18)) / (2.0 * 2 * 2)));
                    var ground = Sky + (float)(Noise * StarInjection.Gaussian(rng));
                    // Stars 10 px apart: over two fifths of a 15 px window lies between them.
                    frame[y, x] = ground + (float)(Noise * (Cluster(x, y, pitch: 10, reach: 20) - (inTheInput ? dip : 0)));
                    model[y, x] = ground - (float)(Noise * (dip + broadTake));
                }
            }
            return (frame, model);
        }

        var (globuleFrame, model) = Dip(inTheInput: true);
        var (dugFrame, sameModel) = Dip(inTheInput: false);
        sameModel.ShouldBe(model, "the two scenes share one plate");

        var (globule, _) = DigFill.Apply(ImageOf(globuleFrame), ImageOf(model));
        var (dug, _) = DigFill.Apply(ImageOf(dugFrame), ImageOf(model));

        MostMoved(globule, model, radius: 4).Lifted.ShouldBe(0.0, "a globule the input shows too is left as dark as it is");
        double sum = 0;
        var count = 0;
        for (var y = Cy - 4; y <= Cy + 4; y++)
        {
            for (var x = Cx - 4; x <= Cx + 4; x++)
            {
                sum += (dug[0, y, x] - Sky) / Noise;
                count++;
            }
        }
        (sum / count).ShouldBeInRange(-1.5, 1.5, "the same dip, where the input is level, is a dig and is filled to the sky");
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

        // Off, the fill leaves the model's output as it is; the split then only holds it at or under the input (#2433), which
        // here moves a pixel the scale's round trip left a rounding error over the input.
        var held = NarrowbandCombination.AtOrUnderPlane(raw, input);
        ShouldBeBitForBit(off[0], held);
        offShare.ShouldBeNull("switched off: the model's output, held at or under the input");
        ShouldBeBitForBit(other[0], held);
        otherShare.ShouldBeNull("a remover that does not take it (RC-Astro) is never filled");

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

        // The order pin. Cores first, the nucleus is back in the plate before the fill reads it, so its pixels are not in the
        // set of removed stars. Fill first (or no protection) and the nucleus the model took (200 sigma) is in that set, so the
        // share is larger. Plate and stars match in either order, so only the share tells them apart.
        var unprotected = new FixedRemover(model, PeakOf(frame), takesDigFill: true);
        var (_, _, _, unprotectedShare) = await NarrowbandCombination.SplitStarsAsync([input], unprotected, protectCores: null, fillDigs: true, ct);
        unprotectedShare.ShouldNotBeNull();
        share[0].ShouldBeLessThan(unprotectedShare[0], "the fill reads the plate after the cores went back, so the nucleus is not counted as a removed star");

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

    // The scatter of one channel under one Gaussian, the plain way: whole planes in double, down the columns and then
    // along the rows, each sum from the centre tap and then the pairs from the farthest in.
    private static double[] PlainGaussian(double[] src, int width, int height, double sigma)
    {
        var kernel = DigFill.GaussianKernel(sigma);
        var radius = kernel.Length / 2;
        var mid = new double[src.Length];
        var dst = new double[src.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = src[(y * width) + x] * kernel[radius];
                for (var j = radius; j >= 1; j--)
                {
                    sum += (src[(DigFill.Mirror(y - j, height) * width) + x] + src[(DigFill.Mirror(y + j, height) * width) + x]) * kernel[radius - j];
                }
                mid[(y * width) + x] = sum;
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = mid[(y * width) + x] * kernel[radius];
                for (var j = radius; j >= 1; j--)
                {
                    sum += (mid[(y * width) + DigFill.Mirror(x - j, width)] + mid[(y * width) + DigFill.Mirror(x + j, width)]) * kernel[radius - j];
                }
                dst[(y * width) + x] = sum;
            }
        }
        return dst;
    }

    [Fact]
    public void TheTrustReadInRunsOfPixelsIsThePlainWholeFrameOne()
    {
        // #2254: the scatter is summed row by row and only over runs of the set's pixels, to hold no whole plane in double.
        // It has to be, to the bit, what whole planes give, and the sums are compared as the doubles they are: the trust
        // made of them is clamped and narrowed to float, which would hide a last digit. The frame is narrower than the far
        // Gaussian reaches to either side (48 px), so that one's single run on a row mirrors at both edges; the near one
        // (16 px) is the one that splits a row into runs, on the rows whose set pixels lie over 32 px apart. The set has
        // pixels on the border, lone ones and wide blocks.
        const int width = 70, height = 90, n = width * height, channels = 2;
        var rng = new Random(21);
        var plates = new float[channels][];
        var sigma = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            plates[c] = new float[n];
            sigma[c] = new float[n];
            for (var i = 0; i < n; i++)
            {
                // Noise of one sigma, and a ripple that grows from nothing at the left edge to eight sigmas at the right,
                // so the scatter runs from under the trusted bar to over the untrusted one across the frame.
                var x = i % width;
                plates[c][i] = 0.3f + (0.1f * c) + (float)(1e-3 * StarInjection.Gaussian(rng)) + (0.008f * x / width * MathF.Sin(x / 5f));
                sigma[c][i] = 1e-3f * (1f + (0.1f * c));
            }
        }
        var set = new bool[n];
        var sky = new float[n];
        var nearWeight = new float[n];
        for (var i = 0; i < n; i++)
        {
            var (x, y) = (i % width, i / width);
            // A block deep enough for the far Gaussian, a strip along the left edge, a corner, and scattered single pixels.
            set[i] = (x is >= 20 and < 50 && y is >= 30 and < 60) || (x < 3 && y % 4 != 0) || (x >= width - 2 && y >= height - 2) || rng.Next(40) == 0;
            sky[i] = set[i] ? 0f : 1f;
            // Which Gaussian answers: the far one deep in the block, as the step chooses it by the near one's weight.
            nearWeight[i] = x is >= 28 and < 42 && y is >= 38 and < 52 ? 0.001f : 0.5f;
        }

        var spread = new double[n];
        var trust = DigFill.SkyTrust(plates, sky, sigma, set, nearWeight, hasFar: true, width, height, spread);

        var expected = new double[n];
        var wideSky = Array.ConvertAll(sky, v => (double)v);
        foreach (var (blur, far) in new[] { (DigFill.SkyBlurPx, false), (DigFill.FarSkyBlurPx, true) })
        {
            var total = PlainGaussian(wideSky, width, height, blur);
            for (var c = 0; c < channels; c++)
            {
                var first = new double[n];
                var second = new double[n];
                for (var i = 0; i < n; i++)
                {
                    first[i] = (double)plates[c][i] * sky[i];
                    second[i] = (double)plates[c][i] * plates[c][i] * sky[i];
                }
                var sum = PlainGaussian(first, width, height, blur);
                var squares = PlainGaussian(second, width, height, blur);
                for (var i = 0; i < n; i++)
                {
                    if (set[i] && (nearWeight[i] < DigFill.ThinSkyWeight) == far)
                    {
                        var share = Math.Max(total[i], 1e-6);
                        var mean = sum[i] / share;
                        expected[i] = Math.Max(expected[i], Math.Sqrt(Math.Max((squares[i] / share) - (mean * mean), 0.0)) / sigma[c][i]);
                    }
                }
            }
        }
        var checkedPixels = 0;
        var between = 0;
        var splitRows = 0;
        for (var y = 0; y < height; y++)
        {
            // A row the near Gaussian reads in more than one run: two of its pixels over 32 px apart with none between.
            var last = -1;
            var split = false;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (set[i] && nearWeight[i] >= DigFill.ThinSkyWeight)
                {
                    split |= last >= 0 && x - last > 32;
                    last = x;
                }
            }
            splitRows += split ? 1 : 0;
        }
        splitRows.ShouldBeGreaterThan(10, "the scene holds rows that are read in several runs");
        for (var i = 0; i < n; i++)
        {
            if (!set[i])
            {
                continue;
            }
            spread[i].ShouldBe(expected[i], $"the scatter itself, to the bit, at pixel ({i % width}, {i / width})");
            var plain = (float)Math.Clamp((DigFill.UntrustedSkySpread - expected[i]) / (DigFill.UntrustedSkySpread - DigFill.TrustedSkySpread), 0.0, 1.0);
            trust[i].ShouldBe(plain, $"pixel ({i % width}, {i / width})");
            checkedPixels++;
            between += plain is > 0f and < 1f ? 1 : 0;
        }
        checkedPixels.ShouldBeGreaterThan(900);
        between.ShouldBeGreaterThan(20, "the scene holds pixels whose trust is neither 0 nor 1, so the comparison is of real values");
    }
}
