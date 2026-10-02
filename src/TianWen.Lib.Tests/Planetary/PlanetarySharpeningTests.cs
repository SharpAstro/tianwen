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

    // Belts, with features along them a fifth as strong (Jupiter's own balance): the limb's edge is flattened by the zonal brightness at
    // each limb point's latitude, so a planet whose texture is as strong across longitude as across latitude (the de-rotation tests'
    // spotted map, 0.25 against 0.3) reads its edge through the texture, 2.2 at 0.3 cycles a pixel where the blur passes 0.03.
    private static PlanetMap BeltedMap()
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                var longitude = (column + 0.5) * Math.PI / 180;
                values[(row * 360) + column] = (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) + (0.05 * Math.Cos(6 * longitude) * Math.Pow(Math.Cos(latitude), 2)));
            }
        }
        return new PlanetMap(values, 360, 180);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(PlanetaryLimbFix.Plain, false)]
    [InlineData(PlanetaryLimbFix.Plain, true)]
    [InlineData(PlanetaryLimbFix.LimbChannel, true)]
    public async Task ADerivedSharpeningComesCloserToTheTruthThanTheStackAndDoesNotRing(PlanetaryLimbFix fix, bool nonNegative)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale, supersample: 2);

        // The seeing a stack keeps: a Gaussian of 1.4 px, the noise of a few hundred frames, a sky on the camera's offset.
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 1.4 * 1.4 * f * f));
        var random = new Random(5);
        var stackPlane = new float[Size, Size];
        for (var i = 0; i < blurred.Length; i++)
        {
            stackPlane[i / Size, i % Size] = (float)(0.05 + (0.5 * blurred[i]) + (0.005 * PhaseScreen.Gaussian(random)));
        }
        var stack = new Image([stackPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, Night, Telescope) { WavelengthsNm = [650], Fix = fix, NonNegative = nonNegative };

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
        TestContext.Current.TestOutputHelper?.WriteLine($"{fix}{(nonNegative ? ", non-negative" : "")}: bands 1 to 4 {before:0.000} stacked, {after:0.000} sharpened; undershoot {undershoot:0.0000}; gains {string.Join(", ", sharpened.Gains.Select(g => g.ToString("0.00")))}; edge at 0.1, 0.3 {sharpened.EdgeAtTenth:0.000}, {sharpened.EdgeAtThreeTenths:0.000} (the blur's own {Math.Exp(-2 * Math.PI * Math.PI * 1.96 * 0.01):0.000}, {Math.Exp(-2 * Math.PI * Math.PI * 1.96 * 0.09):0.000})");

        after.ShouldBeLessThan(before * 0.7);
        undershoot.ShouldBeLessThan(0.02);
        sharpened.Sharpened.Release();
    }

    [Fact(Timeout = 300_000)]
    public async Task WithoutATelescopeThePresetSharpensWithTheLimbKeptAsStacked()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        var truth = PlanetaryRender.RenderDiffracted(BeltedMap(), aspect, Placement, Size, Size, 0.95, Telescope, Wavelength, scale, supersample: 2);
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 1.4 * 1.4 * f * f));
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
