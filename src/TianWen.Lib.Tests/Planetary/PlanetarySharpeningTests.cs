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
