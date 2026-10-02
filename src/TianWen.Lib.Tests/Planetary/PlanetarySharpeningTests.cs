using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary master's derived sharpening (<see cref="PlanetarySharpening"/>, the enhanced pipeline): a Jupiter rendered through a
/// telescope's diffraction is the truth, and a stack of it is that truth blurred by a seeing it does not know and noised, on a sky
/// above zero as a camera's offset puts it. Derived through the limb's edge, the sharpening must come closer to the truth than the
/// stack, every band summed, and leave no ring below the sky.
/// </summary>
public class PlanetarySharpeningTests
{
    private const int Size = 192;
    private static readonly DateTimeOffset Night = new(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
    private static readonly DiskPlacement Placement = new(95.4, 96.8, 40, 30);
    private static readonly Pupil Telescope = new(0.254, ObstructionRatio: 0.23);
    private const double Wavelength = 650e-9;

    private static double Seeing(double f) => (0.7 * Math.Exp(-2 * Math.PI * Math.PI * 0.9 * 0.9 * f * f)) + (0.3 * Math.Exp(-2 * Math.PI * Math.PI * 4 * 4 * f * f));

    // Belts and, on them, a fine texture down to a couple of degrees (Jupiter's own, the twins' OPAL map's), with features along the
    // belts a fifth as strong as the belts. Two things the fixture must have for the test to be the real case:
    // - a texture at the fine scales, or inside the disk any sharpening only lifts the noise while the gains' disk term still asks for
    //   the limb to be sharpened (on smooth belts the derived gains made the stack worse, 3.18 to 4.56);
    // - longitude features weaker than the belts, since the limb's edge is flattened by the zonal brightness alone (the de-rotation
    //   tests' spotted map, 0.25 against 0.3, read the edge through its texture: 2.2 at 0.3 cycles a pixel where the blur passes 0.03).
    private static PlanetMap BeltedMap()
    {
        var random = new Random(11);
        var grain = new double[360 * 180];
        for (var i = 0; i < grain.Length; i++)
        {
            grain[i] = random.NextDouble() - 0.5;
        }
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                var longitude = (column + 0.5) * Math.PI / 180;
                // The grain smoothed over a 3 by 3 block: a texture at two to three degrees.
                double texture = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        texture += grain[(Math.Clamp(row + dy, 0, 179) * 360) + ((column + dx + 360) % 360)];
                    }
                }
                values[(row * 360) + column] = (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) + (0.05 * Math.Cos(6 * longitude) * Math.Pow(Math.Cos(latitude), 2))
                    + (0.6 * texture / 9));
            }
        }
        return new PlanetMap(values, 360, 180);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(PlanetaryLimbFix.Bounded)]
    [InlineData(PlanetaryLimbFix.Floored)]
    [InlineData(PlanetaryLimbFix.LimbChannel)]
    public async Task ADerivedSharpeningComesCloserToTheTruthThanTheStackAndDoesNotRing(PlanetaryLimbFix fix)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale);

        // The seeing a stack keeps: a core and a halo (R7 part 2), its transfer about what 2022-09-03 Red's stack reads off its limb (0.44
        // at 0.1 cycles a pixel, 0.09 at 0.3; this one 0.61 and 0.17), the noise of a large stack, a sky on the camera's offset. Not a single
        // Gaussian: one of 1.4 px passes 0.03 at 0.3 cycles a pixel, below the noise floor the limb's edge reads there (it read 0.30).
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, Seeing);
        var random = new Random(5);
        var stackPlane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            stackPlane[i / Size, i % Size] = (float)(0.05 + (0.5 * blurred[i]) + (0.002 * PhaseScreen.Gaussian(random)));
        }
        var stack = new Image([stackPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650], Fix = fix };

        var result = await Task.Run(() => PlanetarySharpening.Sharpen(stack, options), TestContext.Current.CancellationToken);

        var sharpened = result.ShouldNotBeNull();
        sharpened.Derived.ShouldBeTrue();
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var fit = PlanetaryLimbFit.Fit(stack, limbOptions).ShouldNotBeNull();
        var disk = MetricDisk.From(fit, limbOptions.AxisRatio);
        var reference = PlanetaryMetrics.Normalise(truth, Size, Size, disk);
        var before = PlanetaryMetrics.Fidelity(PlanetaryMetrics.Normalise(stack.GetChannelSpan(0), Size, Size, disk), reference, Size, Size, disk).Take(4).Sum(b => b.Error);
        var plane = PlanetaryMetrics.Normalise(sharpened.Sharpened.GetChannelSpan(0), Size, Size, disk);
        var after = PlanetaryMetrics.Fidelity(plane, reference, Size, Size, disk).Take(4).Sum(b => b.Error);
        var undershoot = PlanetaryMetrics.LimbUndershoot(plane, Size, Size, disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"{fix}: bands 1 to 4 {before:0.000} stacked, {after:0.000} sharpened; undershoot {undershoot:0.0000}; gains {string.Join(", ", sharpened.Gains.Select(g => g.ToString("0.00")))}; edge at 0.1, 0.3 {sharpened.EdgeAtTenth:0.000}, {sharpened.EdgeAtThreeTenths:0.000} (the blur's own {Seeing(0.1):0.000}, {Seeing(0.3):0.000})");

        // 1.352 to 1.169 bounded (1.172 floored, 1.171 the limb as its own channel) when it was written: the edge reads this
        // fixture's finest band high (0.46 at 0.3 cycles a pixel where the seeing passes 0.17), which holds the gains down; on the
        // twins the same routine halved the error (docs/plans/planetary-restoration.md).
        after.ShouldBeLessThan(before * 0.95);
        undershoot.ShouldBeLessThan(0.02);
        sharpened.Sharpened.Release();
    }

    [Fact(Timeout = 300_000)]
    public async Task TheLiveSlidersSeededWithTheDerivedGainsSharpenAsTheDerivedSharpeningDoes()
    {
        // The live view's Derive (#1159): the gains derived once, then every master sharpened through the sliders, which cannot fit a limb
        // a master. Measured equal on the three twins (docs/plans/planetary-restoration.md, "The live view's derived sharpening"); here,
        // within a hundredth of the batch's floored sharpening on this fixture.
        var ct = TestContext.Current.CancellationToken;
        var (truth, stack) = NoisyStack();

        var (gains, how) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope, [650]), ct);
        gains.Length.ShouldBe(6, how);
        var batch = await Task.Run(() => PlanetarySharpening.Sharpen(stack,
            new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650], Fix = PlanetaryLimbFix.Floored }), ct);
        var floored = batch.ShouldNotBeNull();
        var sliders = WaveletSharpen.Sharpen(stack, PlanetaryBestStack.SliderOptions(gains));

        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var disk = MetricDisk.From(PlanetaryLimbFit.Fit(stack, limbOptions).ShouldNotBeNull(), limbOptions.AxisRatio);
        var reference = PlanetaryMetrics.Normalise(truth, Size, Size, disk);
        double Error(Image image) => PlanetaryMetrics.Fidelity(PlanetaryMetrics.Normalise(image.GetChannelSpan(0), Size, Size, disk), reference, Size, Size, disk).Take(4).Sum(b => b.Error);
        var (batchError, sliderError) = (Error(floored.Sharpened), Error(sliders));
        var undershoot = PlanetaryMetrics.LimbUndershoot(PlanetaryMetrics.Normalise(sliders.GetChannelSpan(0), Size, Size, disk), Size, Size, disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"{how}: the batch, floored, bands 1 to 4 {batchError:0.000}; the sliders {sliderError:0.000}, undershoot {undershoot:0.0000}");

        sliderError.ShouldBe(batchError, 0.01);
        undershoot.ShouldBeLessThan(0.02);
        floored.Sharpened.Release();
        sliders.Release();
    }

    [Fact]
    public void DerivingTheGainsSaysWhyWhenItDerivesNone()
    {
        var (_, stack) = NoisyStack();

        var noTelescope = PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, telescope: null);
        var noPlanet = PlanetaryBestStack.DeriveGains(stack, planet: null, Night, Telescope);
        var noModel = PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Mars, Night, Telescope);

        noTelescope.Gains.ShouldBeEmpty();
        noTelescope.How.ShouldContain("aperture");
        noPlanet.Gains.ShouldBeEmpty();
        noPlanet.How.ShouldContain("Jupiter");
        noModel.Gains.ShouldBeEmpty();
        noModel.How.ShouldContain("Jupiter");
    }

    [Fact]
    public void SaturnIsSharpenedByThePresetAndNotDeRotatedUntilItsRingsAreModelled()
    {
        // The limb fit has no rings: on 2021-12-16's Saturn it swallowed them (a globe half again too large), and the derivation
        // read an edge whose transfer rose with frequency, freed the ring ansae as moons and turned the master green (#1184). Until
        // the rings are modelled, Saturn takes the preset, says why, and is never de-rotated as if its rings lay on its globe.
        var (_, stack) = NoisyStack();

        var gains = PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Saturn, Night, Telescope);
        var (sharpened, how) = PlanetaryBestStack.Sharpen(stack, CatalogIndex.Saturn, Night, Telescope);

        gains.Gains.ShouldBeEmpty();
        gains.How.ShouldContain("#1184");
        how.ShouldStartWith("PlanetaryDefault");
        how.ShouldContain("rings");
        PlanetaryBestStack.DerotationFor(CatalogIndex.Saturn, always: true).ShouldBeNull();
        PlanetaryBestStack.DerotationFor(CatalogIndex.Jupiter).ShouldNotBeNull();
        sharpened.Release();
    }

    // The fixture's truth and its stack: the truth blurred by the seeing, on a sky at 0.05, with a large stack's noise.
    private static (float[] Truth, Image Stack) NoisyStack()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale);
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, Seeing);
        var random = new Random(5);
        var plane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            plane[i / Size, i % Size] = (float)(0.05 + (0.5 * blurred[i]) + (0.002 * PhaseScreen.Gaussian(random)));
        }
        return (truth, new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
    }

    [Fact(Timeout = 300_000)]
    public async Task ATightCropsSharpeningLiftsNoSkyAboveTheStackOutsideTheLimb()
    {
        // The sharpening works in a power-of-two window about the planet: 256 px over this 130 px crop, whose corners lie inside the
        // sky's 2.5 radii (a 200 px PIPP crop of 2022-10-09's 150 px Jupiter, the real-capture validation). Padded with zeros, the
        // window's sky was the padding alone, without noise, so the moons' threshold fell to its floor and the bound freed the sky's own
        // noise peaks (on 2022-10-09, blocks of colour along the frame's edge). With the frame mirrored into the window, this moonless
        // fixture keeps every pixel outside the limb at or under its stack, or at the sky the floor holds a dip below it to.
        const int crop = 130, from = 31;
        var (_, whole) = NoisyStack();
        var plane = new float[crop, crop];
        var source = whole.GetChannelSpan(0);
        for (var i = 0; i < crop * crop; i++)
        {
            plane[i / crop, i % crop] = source[((i / crop) + from) * Size + (i % crop) + from];
        }
        whole.Release();
        var stack = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650] };

        var result = await Task.Run(() => PlanetarySharpening.Sharpen(stack, options), TestContext.Current.CancellationToken);

        var sharpened = result.ShouldNotBeNull().Sharpened;
        var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night));
        var disk = MetricDisk.From(PlanetaryLimbFit.Fit(stack, limbOptions).ShouldNotBeNull(), limbOptions.AxisRatio);
        var before = stack.GetChannelSpan(0);
        var after = sharpened.GetChannelSpan(0);
        var (sky, _) = PlanetaryMetrics.NormalisationLevels(before, crop, crop, disk);
        var (lifted, worst) = (0, 0.0);
        for (var i = 0; i < before.Length; i++)
        {
            var lift = after[i] - Math.Max(before[i], sky);
            if (disk.RadiiAt(i % crop, i / crop) > 1.1 && lift > 1e-5)
            {
                (lifted, worst) = (lifted + 1, Math.Max(worst, lift));
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"outside 1.1 radii: {lifted} pixels lifted above the stack and the sky, the most by {worst:0.0000}");
        lifted.ShouldBe(0);
        sharpened.Release();
    }

    [Fact(Timeout = 600_000)]
    public async Task AColourMastersFinestBandIsKeptAsStackedAndAMonoOnesDerived()
    {
        // #1187: a colour plane samples every second pixel, so its finest band (0.25 to 0.5 cycles a pixel) holds little but noise and
        // the CFA's residue, and a derived gain of 12 to 20 lifted it into a lattice over the disk. A colour master keeps that band as
        // stacked, its other gains fitted around it; a mono master's is derived as before.
        var ct = TestContext.Current.CancellationToken;
        var (_, mono) = NoisyStack();
        var plane = mono.GetChannelSpan(0);
        var colourPlanes = Image.CreateChannelData(3, Size, Size);
        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < plane.Length; i++)
            {
                colourPlanes[c][i / Size, i % Size] = plane[i];
            }
        }
        var colour = new Image(colourPlanes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650] };

        var monoResult = await Task.Run(() => PlanetarySharpening.Sharpen(mono, options), ct);
        var colourResult = await Task.Run(() => PlanetarySharpening.Sharpen(colour, options), ct);

        var (monoGains, colourGains) = (monoResult.ShouldNotBeNull().Gains, colourResult.ShouldNotBeNull().Gains);
        TestContext.Current.TestOutputHelper?.WriteLine($"mono gains {string.Join(", ", monoGains.Select(g => g.ToString("0.00")))}; colour {string.Join(", ", colourGains.Select(g => g.ToString("0.00")))}");
        Math.Abs(monoGains[0] - 1).ShouldBeGreaterThan(0.01, "a mono master's finest band is derived");
        colourGains[0].ShouldBe(1);
        Math.Abs(colourGains[1] - 1).ShouldBeGreaterThan(0.01, "the other gains are still derived, around the held band");
        monoResult.Sharpened.Release();
        colourResult.Sharpened.Release();
    }

    [Fact(Timeout = 300_000)]
    public async Task WithoutATelescopeThePresetSharpensWithTheLimbKeptAsStacked()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale);
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, Seeing);
        var stackPlane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            stackPlane[i / Size, i % Size] = (float)(0.05 + (0.5 * blurred[i]));
        }
        var stack = new Image([stackPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

        var result = await Task.Run(() => PlanetarySharpening.Sharpen(stack, new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, null)), TestContext.Current.CancellationToken);

        var sharpened = result.ShouldNotBeNull();
        sharpened.Derived.ShouldBeFalse();
        sharpened.Gains.ShouldBe(WaveletSharpenOptions.PlanetaryDefault.Gains.Select(g => (double)g));
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var disk = MetricDisk.From(PlanetaryLimbFit.Fit(stack, limbOptions).ShouldNotBeNull(), limbOptions.AxisRatio);
        var plane = PlanetaryMetrics.Normalise(sharpened.Sharpened.GetChannelSpan(0), Size, Size, disk);
        // The preset alone digs a ring of about a fifth of the disk below the sky (R8 follow-up 2); with the limb kept as stacked, none.
        var undershoot = PlanetaryMetrics.LimbUndershoot(plane, Size, Size, disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"preset, the limb kept: undershoot {undershoot:0.0000}; edge at 0.1, 0.3 {sharpened.EdgeAtTenth:0.000}, {sharpened.EdgeAtThreeTenths:0.000}");
        undershoot.ShouldBeLessThan(0.02);
        sharpened.Sharpened.Release();
    }
}
