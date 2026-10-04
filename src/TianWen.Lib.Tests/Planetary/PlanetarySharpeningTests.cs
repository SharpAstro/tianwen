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
    [InlineData(PlanetaryLimbFix.ModelFeathered)]
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

        var (gains, how, _) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope, [650]), ct);
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

    [Fact(Timeout = 300_000)]
    public async Task TheLiveDialsAreDrawnOutsideTheLimbAsTheBatchDrawsIt()
    {
        // #1201: Derive keeps the limb it fitted, and every later master, sharpened by the dials, is drawn outside the limb as the batch's
        // derived sharpening draws it (the planet's model through the pupil, feathered back to the stack, no sharpening). On the master the
        // limb was fitted on, the two must agree outside the limb to within 1e-4 of the disk's level, and inside it within a hundredth of
        // band error, as the dials alone did (docs/plans/planetary-restoration.md, "The feathered limb on the live view", rule 1).
        var ct = TestContext.Current.CancellationToken;
        var (truth, stack) = NoisyStack();

        var (gains, how, kept) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope, [650]), ct);
        var limb = kept.ShouldNotBeNull(how);
        var batch = (await Task.Run(() => PlanetarySharpening.Sharpen(stack, new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650] }), ct))
            .ShouldNotBeNull();
        batch.Fix.ShouldBe(PlanetaryLimbFix.ModelFeathered);
        var dials = WaveletSharpen.Sharpen(stack, PlanetaryBestStack.SliderOptions(gains));
        limb.FollowedTo(stack).ShouldBeSameAs(limb, "the disk has not moved");
        var live = limb.Draw(stack, dials);

        var disk = limb.Disk;
        var (outside, inside) = (LargestOutsideTheLimb(batch.Sharpened, live, disk), BandError(truth, live, disk) - BandError(truth, batch.Sharpened, disk));
        var floored = LargestOutsideTheLimb(batch.Sharpened, dials, disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"outside the limb the live drawing is {outside:E2} of the disk's level from the batch's (the dials alone {floored:0.0000}); inside, band error {inside:+0.000;-0.000} against the batch's");

        outside.ShouldBeLessThan(1e-4);
        inside.ShouldBe(0, 0.01);
        floored.ShouldBeGreaterThan(1e-3, "the dials alone sharpen past the limb, or this test would show nothing");
        foreach (var image in new[] { batch.Sharpened, dials, live })
        {
            image.Release();
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AKeptLimbFollowsTheDiskWhereItMovesAndIsFittedAgainWhereItGrows()
    {
        // The live master's disk moves when the stack takes another reference or the mount is recentred (#1201): the kept limb follows it by
        // the fit's own start, the model drawn again at the new centre, and is fitted again, as the batch fits, where the disk changed size.
        var ct = TestContext.Current.CancellationToken;
        var (_, stack) = NoisyStack();
        var (_, how, kept) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope, [650]), ct);
        var limb = kept.ShouldNotBeNull(how);
        var (dx, dy) = (3.4, -2.1);
        var (_, moved) = NoisyStack(Placement with { CenterX = Placement.CenterX + dx, CenterY = Placement.CenterY + dy });
        var (_, grown) = NoisyStack(Placement with { EquatorialRadius = Placement.EquatorialRadius * 1.1 });

        var there = limb.FollowedTo(moved).ShouldNotBeNull();
        var bigger = (await Task.Run(() => limb.FollowedTo(grown), ct)).ShouldNotBeNull();
        var refit = PlanetaryLimbFit.Fit(grown, PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night))).ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"moved: centre ({there.Fit.CenterX - limb.Fit.CenterX:0.000}, {there.Fit.CenterY - limb.Fit.CenterY:0.000}) for ({dx}, {dy}); grown: radius {bigger.Fit.EquatorialRadius:0.000} against a cold fit's {refit.EquatorialRadius:0.000}, centre off it by ({bigger.Fit.CenterX - refit.CenterX:0.000}, {bigger.Fit.CenterY - refit.CenterY:0.000})");

        there.ShouldNotBeSameAs(limb);
        (there.Fit.CenterX - limb.Fit.CenterX).ShouldBe(dx, 0.1);
        (there.Fit.CenterY - limb.Fit.CenterY).ShouldBe(dy, 0.1);
        there.Fit.EquatorialRadius.ShouldBe(limb.Fit.EquatorialRadius);
        bigger.Fit.EquatorialRadius.ShouldBe(refit.EquatorialRadius, 1e-9);
        bigger.Fit.CenterX.ShouldBe(refit.CenterX, 1e-9);
        bigger.Fit.CenterY.ShouldBe(refit.CenterY, 1e-9);
    }

    // The largest difference between two masters past the disk's limb, in the disk's level above the sky (the first one's).
    private static double LargestOutsideTheLimb(Image a, Image b, MetricDisk disk)
    {
        var planeA = a.GetChannelSpan(0);
        var planeB = b.GetChannelSpan(0);
        var (_, scale) = PlanetaryMetrics.NormalisationLevels(planeA, a.Width, a.Height, disk);
        double largest = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (disk.RadiiAt(x, y) > 1)
                {
                    largest = Math.Max(largest, Math.Abs(planeA[(y * a.Width) + x] - planeB[(y * a.Width) + x]) / scale);
                }
            }
        }
        return largest;
    }

    // A master's error against the truth, bands 1 to 4 inside 0.9 radii.
    private static double BandError(float[] truth, Image master, MetricDisk disk)
        => PlanetaryMetrics.Fidelity(PlanetaryMetrics.Normalise(master.GetChannelSpan(0), Size, Size, disk), PlanetaryMetrics.Normalise(truth, Size, Size, disk), Size, Size, disk)
            .Take(4).Sum(b => b.Error);

    [Fact]
    public void DerivingTheGainsSaysWhyWhenItDerivesNone()
    {
        var (_, stack) = NoisyStack();

        var noTelescope = PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, telescope: null);
        var noPlanet = PlanetaryBestStack.DeriveGains(stack, planet: null, Night, Telescope);
        var noModel = PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Mars, Night, Telescope);

        noTelescope.Gains.ShouldBeEmpty();
        noTelescope.How.ShouldContain("aperture");
        noTelescope.Limb.ShouldBeNull("no telescope, no model through its pupil to draw the limb with");
        noPlanet.Gains.ShouldBeEmpty();
        noPlanet.How.ShouldContain("Jupiter");
        noModel.Gains.ShouldBeEmpty();
        noModel.How.ShouldContain("Jupiter");
    }

    [Fact]
    public void SaturnIsDeRotatedAsJupiterIs()
    {
        // Saturn's globe turns under rings that stay where they lie (S5, #1234, SaturnDerotationTests), so a capture of either planet gets
        // its de-rotation; a planet with no rotation model gets none.
        PlanetaryBestStack.DerotationFor(CatalogIndex.Saturn, always: true).ShouldNotBeNull().Planet.ShouldBe(CatalogIndex.Saturn);
        PlanetaryBestStack.DerotationFor(CatalogIndex.Jupiter).ShouldNotBeNull();
        PlanetaryBestStack.DerotationFor(CatalogIndex.Mars).ShouldBeNull();
    }

    // The fixture's truth and its stack: the truth blurred by the seeing, on a sky at 0.05, with a large stack's noise. The planet where
    // `placement` puts it, at the fixture's plate scale (the fixture's own placement when none).
    private static (float[] Truth, Image Stack) NoisyStack(DiskPlacement? placement = null)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, placement ?? Placement, Size, Size, 0.95, Telescope, Wavelength, scale);
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, Seeing);
        var random = new Random(5);
        var plane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            plane[i / Size, i % Size] = (float)(0.05 + (0.5 * blurred[i]) + (0.002 * PhaseScreen.Gaussian(random)));
        }
        return (truth, new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
    }

    [Fact(Timeout = 600_000)]
    public async Task AColourLiveViewIsBalancedAsTheBatchBalancesItsMaster()
    {
        // #1212: one balance for planetary-stack, the viewer's Best stack and the live view. Derive reads the balance the batch would give
        // the master on show and keeps it with the limb, and every master the live view draws is given it last, as the batch gives its
        // sharpened master: the same colour on the disk, and a master that says it is balanced (the flag the planetary stretch reads, #1229).
        var ct = TestContext.Current.CancellationToken;
        var (_, mono) = NoisyStack();
        var grey = mono.GetChannelSpan(0);
        (float Gain, float Sky)[] camera = [(0.9f, 0.04f), (0.6f, 0.03f), (0.35f, 0.05f)];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var i = 0; i < grey.Length; i++)
            {
                planes[c][i / Size, i % Size] = (camera[c].Gain * (grey[i] - 0.05f)) + camera[c].Sky;
            }
        }
        var stack = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

        var (gains, how, kept) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope), ct);
        var limb = kept.ShouldNotBeNull(how);
        var balance = limb.Balance.ShouldNotBeNull(how);
        how.ShouldContain(balance.Describe());
        var dials = WaveletSharpen.Sharpen(stack, PlanetaryBestStack.SliderOptions(gains));
        var live = limb.FollowedTo(stack).ShouldNotBeNull().Draw(stack, dials);

        // The batch's order: its colours moved onto green (the stacker's, #1202; nothing to move here, which still resamples), sharpened,
        // then balanced by the same routine at the same saturation.
        var (onGreen, _) = PlanetaryChannelAlignment.Align(stack, PlanetaryFrameLayout.Rgb, PlanetaryChannelAlignment.LimbOptionsFor(CatalogIndex.Jupiter, Night));
        var batch = (await Task.Run(() => PlanetarySharpening.Sharpen(onGreen, new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [610, 530, 460] }), ct))
            .ShouldNotBeNull();
        var (batchBalance, batchHow) = await Task.Run(() => PlanetaryColourBalance.For(onGreen, CatalogIndex.Jupiter, Night), ct);
        var balancedBatch = batchBalance.ShouldNotBeNull(batchHow).Apply(batch.Sharpened);

        var disk = limb.Disk;
        LinearRgb Mean(Image image)
        {
            var r = image.GetChannelSpan(0);
            var g = image.GetChannelSpan(1);
            var b = image.GetChannelSpan(2);
            return PlanetaryColour.DiskMean(r, g, b, Size, Size, disk, PlanetaryColour.Sky(r, g, b, Size, Size, disk));
        }
        var (asCaptured, byLive, byBatch) = (Mean(stack), Mean(live), Mean(balancedBatch));
        TestContext.Current.TestOutputHelper?.WriteLine($"{how}; the disk's colour (r, g): as captured {asCaptured.ChromaR:0.0000}, {asCaptured.ChromaG:0.0000}; live {byLive.ChromaR:0.0000}, {byLive.ChromaG:0.0000}; batch {byBatch.ChromaR:0.0000}, {byBatch.ChromaG:0.0000}");

        (balance.Gains.R, balance.Gains.B).ShouldBe((batchBalance.Gains.R, batchBalance.Gains.B), "one balance, read the batch's way");
        live.ImageMeta.IsColourBalanced.ShouldBeTrue();
        byLive.ChromaDistance(byBatch).ShouldBeLessThan(0.002, "the live view's disk takes the batch's colour");
        asCaptured.ChromaDistance(byBatch).ShouldBeGreaterThan(0.05, "the camera's colour is far from it, or this test would show nothing");
        foreach (var image in new[] { dials, live, batch.Sharpened, balancedBatch, onGreen })
        {
            image.Release();
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AColourLiveViewMovesItsColoursOntoGreenAsTheBatchMovesItsMaster()
    {
        // #1202: the atmosphere's dispersion leaves a live master's colours apart (6.4 px red to blue on 2022-10-09), where the batch moves
        // its master's onto green by their limbs. Derive reads them the batch's way on the master on show and keeps the reading with the
        // limb, and every later master is moved by it before it is sharpened and drawn: its colours then lie on green, as the batch's do.
        var ct = TestContext.Current.CancellationToken;
        var (_, mono) = NoisyStack();
        var grey = mono.GetChannelSpan(0);
        (float Gain, float Sky, int Dy)[] camera = [(0.9f, 0.04f, -2), (0.6f, 0.03f, 0), (0.35f, 0.05f, 3)];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                // The colour's content sits Dy rows from green's (PlanetaryChannelShift), the frame's edge row repeated past it.
                var from = Math.Clamp(y - camera[c].Dy, 0, Size - 1);
                for (var x = 0; x < Size; x++)
                {
                    planes[c][y, x] = (camera[c].Gain * (grey[(from * Size) + x] - 0.05f)) + camera[c].Sky;
                }
            }
        }
        var stack = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        var limbOptions = PlanetaryChannelAlignment.LimbOptionsFor(CatalogIndex.Jupiter, Night);

        var (gains, how, kept) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope), ct);
        var limb = kept.ShouldNotBeNull(how);
        var read = limb.Channels.ShouldNotBeNull(how);
        how.ShouldContain(read.Describe());

        // A later master as the live view draws it: moved, sharpened by the dials, drawn outside the limb; and as it drew one before.
        limb.TryAlign(stack, out var moved).ShouldBeTrue();
        var dials = WaveletSharpen.Sharpen(moved, PlanetaryBestStack.SliderOptions(gains));
        var live = limb.FollowedTo(moved).ShouldNotBeNull().Draw(moved, dials);
        var unmovedDials = WaveletSharpen.Sharpen(stack, PlanetaryBestStack.SliderOptions(gains));
        var unmoved = limb.FollowedTo(stack).ShouldNotBeNull().Draw(stack, unmovedDials);
        var (_, after) = await Task.Run(() => PlanetaryChannelAlignment.Align(live, PlanetaryFrameLayout.Rgb, limbOptions), ct);
        var (_, before) = await Task.Run(() => PlanetaryChannelAlignment.Align(unmoved, PlanetaryFrameLayout.Rgb, limbOptions), ct);
        var (onGreen, wasApart) = (after.ShouldNotBeNull(), before.ShouldNotBeNull());
        TestContext.Current.TestOutputHelper?.WriteLine($"{how}; the drawn master's colours: moved, red {onGreen.Red}, blue {onGreen.Blue}; as before, red {wasApart.Red}, blue {wasApart.Blue}");

        read.Reading.ShouldBe(PlanetaryChannelReading.Limb, "read by their limbs, as the batch reads its master's");
        (read.Red.Dx, read.Red.Dy, read.Blue.Dx, read.Blue.Dy).ShouldSatisfyAllConditions(
            r => r.Item1.ShouldBe(0, 0.15), r => r.Item2.ShouldBe(-2, 0.15), r => r.Item3.ShouldBe(0, 0.15), r => r.Item4.ShouldBe(3, 0.15));
        onGreen.Red.Length.ShouldBeLessThan(0.15, "every master drawn has its red on green");
        onGreen.Blue.Length.ShouldBeLessThan(0.15, "and its blue");
        // Unmoved, the model pasted outside green's limb pulls the reading in (2.45 px of 3), but the dispersion stays.
        wasApart.Blue.Dy.ShouldBeGreaterThan(2, "a master drawn unmoved keeps the dispersion, or this test would show nothing");
        limb.FollowedTo(moved).ShouldNotBeNull().TryAlign(stack, out var again).ShouldBeTrue("a followed limb keeps the reading");
        foreach (var image in new[] { moved, dials, live, unmovedDials, unmoved, again })
        {
            image.Release();
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AMoonBeyondTheSharpeningWindowIsSharpenedAsOneInsideItInTheBatchAndTheLiveView()
    {
        // #1211: the sharpening works in a window about the planet, 256 px here, and left the frame beyond it as stacked, a moon there too,
        // while a moon inside it kept its sharpening (#1181). The same planet on a wider frame with a moon each side of the window's edge:
        // both must come out as the dials, the same gains over the whole master, sharpen them, in the batch and in the live view's drawing,
        // and the frame beyond the window and every moon's reach stays the stack exactly.
        const int width = 448, height = 320;
        var ct = TestContext.Current.CancellationToken;
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var planet = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale);
        var truth = new float[width * height];
        for (var y = 0; y < Size; y++)
        {
            planet.AsSpan(y * Size, Size).CopyTo(truth.AsSpan(y * width, Size));
        }
        // The window spans x -33 to 223 and y -31 to 225 about the planet at (95.4, 96.8): one moon inside it, one far beyond.
        (int X, int Y)[] moons = [(170, 150), (390, 260)];
        foreach (var (mx, my) in moons)
        {
            for (var y = my - 5; y <= my + 5; y++)
            {
                for (var x = mx - 5; x <= mx + 5; x++)
                {
                    truth[(y * width) + x] += (float)Math.Exp(-(((x - mx) * (x - mx)) + ((y - my) * (y - my))) / (2 * 0.9 * 0.9));
                }
            }
        }
        var blurred = PlanetaryInverse.Apply(truth, width, height, Seeing);
        var random = new Random(5);
        var stackPlane = new float[height, width];
        for (var i = 0; i < blurred.Length; i++)
        {
            stackPlane[i / width, i % width] = (float)(0.05 + (0.5 * blurred[i]) + (0.002 * PhaseScreen.Gaussian(random)));
        }
        var stack = new Image([stackPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

        var (gains, how, kept) = await Task.Run(() => PlanetaryBestStack.DeriveGains(stack, CatalogIndex.Jupiter, Night, Telescope, [650]), ct);
        var limb = kept.ShouldNotBeNull(how);
        var batch = (await Task.Run(() => PlanetarySharpening.Sharpen(stack, new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650] }), ct))
            .ShouldNotBeNull();
        var dials = WaveletSharpen.Sharpen(stack, PlanetaryBestStack.SliderOptions(gains));
        var live = limb.FollowedTo(stack).ShouldNotBeNull().Draw(stack, dials);

        var disk = limb.Disk;
        double Peak(Image image, (int X, int Y) at) => PlanetaryMetrics.SourcePeak(PlanetaryMetrics.Normalise(image.GetChannelSpan(0), width, height, disk), width, height, at.X, at.Y).Peak;
        foreach (var moon in moons)
        {
            var (stacked, byDials, byBatch, byLive) = (Peak(stack, moon), Peak(dials, moon), Peak(batch.Sharpened, moon), Peak(live, moon));
            TestContext.Current.TestOutputHelper?.WriteLine($"the moon at {moon}: peak {stacked:0.0000} stacked, {byDials:0.0000} by the dials, {byBatch:0.0000} batch, {byLive:0.0000} live");
            byBatch.ShouldBeGreaterThan(1.5 * stacked, $"the moon at {moon} is sharpened, not left as stacked");
            byBatch.ShouldBe(byDials, 0.1 * byDials, $"the moon at {moon} as the dials sharpen it");
            byLive.ShouldBe(byBatch, 0.02 * byBatch, $"the moon at {moon} drawn live as the batch sharpens it");
        }
        // Beyond the window (x 223 on, or y 225 on) and every moon's reach, the batch is the stack itself.
        var sharpened = batch.Sharpened.GetChannelSpan(0);
        var original = stack.GetChannelSpan(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if ((x >= 223 || y >= 225) && moons.All(m => ((x - m.X) * (x - m.X)) + ((y - m.Y) * (y - m.Y)) > PlanetaryDering.MoonReachPx * PlanetaryDering.MoonReachPx))
                {
                    sharpened[(y * width) + x].ShouldBe(original[(y * width) + x], $"({x}, {y}) beyond the window and the moons");
                }
            }
        }
        foreach (var image in new[] { batch.Sharpened, dials, live })
        {
            image.Release();
        }
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
        // Bounded's moon finder is what the zero padding fooled; the default fix draws no sharpening outside the limb at all.
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650], Fix = PlanetaryLimbFix.Bounded };

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
