using System;
using System.Collections.Generic;
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
/// A planetary master's picture in numbers (<see cref="PlanetaryPicture"/>, <c>tianwen planetary compare</c>): each region read as it
/// was put in. A Jupiter rendered through a telescope's diffraction is the truth, and its light just past the limb is the glow the
/// planet's model through the pupil expects; the same Jupiter through a seeing it does not know carries more. A sky with a known noise, gradient and
/// level reads them back, and its outliers come in at a Gaussian's count.
/// </summary>
public class PlanetaryPictureTests
{
    private const int Size = 256;
    private const double Sky = 0.05;
    private const double Disk = 0.5;
    private static readonly DateTimeOffset Night = new(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
    private static readonly DiskPlacement Placement = new(128.3, 127.6, 40, 30);
    private static readonly Pupil Telescope = new(0.254, ObstructionRatio: 0.23);
    private const double WavelengthNm = 650;

    private static double Seeing(double f) => (0.7 * Math.Exp(-2 * Math.PI * Math.PI * 0.9 * 0.9 * f * f)) + (0.3 * Math.Exp(-2 * Math.PI * Math.PI * 4 * 4 * f * f));

    // The planet through the telescope alone, unit brightness on no sky.
    private static (float[] Truth, PlanetAspect Aspect) Truth()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var map = new float[36 * 18];
        Array.Fill(map, 1f);
        var scale = aspect.AngularDiameterArcsec / 2 / Placement.EquatorialRadius;
        return (PlanetaryRender.RenderDiffracted(new PlanetMap(map, 36, 18), aspect, Placement, Size, Size, 0.95, Telescope, WavelengthNm * 1e-9, scale), aspect);
    }

    // `light` on the sky with white noise of `noise`, plus a gradient of `gradient` across the frame in x.
    private static float[] OnSky(ReadOnlySpan<float> light, double noise, double gradient, int seed)
    {
        var random = new Random(seed);
        var plane = new float[light.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = (float)(Sky + (Disk * light[i]) + (gradient * (i % Size) / Size) + (noise * PhaseScreen.Gaussian(random)));
        }
        return plane;
    }

    private static Image ToImage(float[] plane)
    {
        var data = new float[Size, Size];
        Buffer.BlockCopy(plane, 0, data, 0, plane.Length * sizeof(float));
        return new Image([data], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    [Fact(Timeout = 300_000)]
    public async Task TheTruthsGlowIsTheGlowExpectedAndASeeingBlurredStackCarriesMore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (truth, aspect) = Truth();
        var truthPlane = OnSky(truth, 0.0005, 0, 3);
        var stackPlane = OnSky(PlanetaryInverse.Apply(truth, Size, Size, Seeing), 0.0005, 0, 4);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var fit = (await Task.Run(() => PlanetaryLimbFit.Fit(ToImage(truthPlane), limbOptions), ct)).ShouldNotBeNull();
        var disk = MetricDisk.From(fit, limbOptions.AxisRatio);
        var expected = PlanetaryPicture.Diffracted(fit, limbOptions, aspect, Telescope, WavelengthNm, Size, Size);

        var ofTruth = PlanetaryPicture.Measure(truthPlane, Size, Size, disk, expected);
        var ofStack = PlanetaryPicture.Measure(stackPlane, Size, Size, disk, expected, glowNoise: ofTruth.SkyNoise, moons: []);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"fit R {fit.EquatorialRadius:0.000} k {fit.LimbDarkening:0.000}; truth: glow {ofTruth.GlowFound:0.0} of {ofTruth.GlowExpected:0.0} expected, to 1.1 radii {ofTruth.GlowNearFound:0.00} of {ofTruth.GlowNearExpected:0.00}; " +
            $"stack: glow {ofStack.GlowFound:0.0}, {ofStack.GlowBrighter}/{ofStack.GlowDarker} off by 3 noise");

        ofTruth.GlowNearFound.ShouldBe(ofTruth.GlowNearExpected, ofTruth.GlowNearExpected * 0.05, "the truth's light just past the limb is the diffraction glow the model expects");
        ofTruth.GlowFound.ShouldBe(ofTruth.GlowExpected, ofTruth.GlowExpected * 0.05, "and so is its light out to 1.5 radii, the wing held on both sides (#1213)");
        ofStack.GlowFound.ShouldBeGreaterThan(ofTruth.GlowExpected * 2, "a seeing-blurred stack spreads the disk's light past the limb");
        ofStack.GlowBrighter.ShouldBeGreaterThan(ofTruth.GlowBrighter * 10);
    }

    [Fact(Timeout = 300_000)]
    public async Task AMastersLimbEdgeOverThePupilIsOneOnTheTruthAndTheSeeingOnAStackThroughIt()
    {
        // #1367: a master's edge over the pupil's own transfer (PlanetaryFinestBand.MasterEdge) reads one where only the telescope blurred
        // it, and the seeing's own transfer where a known seeing blurred it too, within #1139's 0.05. The truth is held to 0.3 cycles a
        // pixel since its reference takes a detector's pixel (#1468: against the limb fit's model alone it read 0.935 there); the stack only
        // to 0.2, since at 0.3 so small a disk's edge is mostly noise (0.121 against the seeing's 0.166).
        var ct = TestContext.Current.CancellationToken;
        var (truth, aspect) = Truth();
        var truthPlane = OnSky(truth, 0.0005, 0, 8);
        var stackPlane = OnSky(PlanetaryInverse.Apply(truth, Size, Size, Seeing), 0.0005, 0, 9);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var fit = (await Task.Run(() => PlanetaryLimbFit.Fit(ToImage(truthPlane), limbOptions), ct)).ShouldNotBeNull();

        var ofTruth = PlanetaryFinestBand.MasterEdge(truthPlane, Size, Size, fit, limbOptions, aspect, Telescope, WavelengthNm);
        var ofStack = PlanetaryFinestBand.MasterEdge(stackPlane, Size, Size, fit, limbOptions, aspect, Telescope, WavelengthNm);
        foreach (var f in PlanetaryChannelPicture.EdgeFrequencies)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f:0.0} cycles a pixel: truth {ofTruth.TransferAt(f):0.000}, stack {ofStack.TransferAt(f):0.000} (the seeing {Seeing(f):0.000})");
        }
        foreach (var f in PlanetaryChannelPicture.EdgeFrequencies)
        {
            ofTruth.TransferAt(f).ShouldBe(1, 0.05, $"the truth at {f}");
        }
        foreach (var f in new[] { 0.1, 0.2 })
        {
            ofStack.TransferAt(f).ShouldBe(Seeing(f), 0.05, $"the stack at {f}");
        }
    }

    [Fact]
    public void TheSkysNoiseGradientAndOutliersAreReadAsPutIn()
    {
        var (truth, _) = Truth();
        const double noise = 0.002;
        const double gradient = 0.01;
        var plane = OnSky(truth, noise, gradient, 5);
        var disk = new MetricDisk(Placement.CenterX, Placement.CenterY, Placement.EquatorialRadius);
        var (_, scale) = PlanetaryMetrics.NormalisationLevels(plane, Size, Size, disk);

        var picture = PlanetaryPicture.Measure(plane, Size, Size, disk, moons: []);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"noise {picture.SkyNoise:0.00000} (put in {noise / scale:0.00000}), gradient {picture.SkyGradient:0.00000} (put in {gradient / scale:0.00000}), " +
            $"block scatter {picture.SkyBlockScatter:0.00}, outliers {picture.SkyBright}/{picture.SkyDark} of {picture.SkyOutliersExpected:0} expected, {picture.SkyPixels} read");

        picture.SkyNoise.ShouldBe(noise / scale, noise / scale * 0.05);
        picture.SkyGradient.ShouldBe(gradient / scale, gradient / scale * 0.1);
        picture.SkyBlockScatter.ShouldBeInRange(0.7, 1.4, "white noise on a plane gives its block means no more scatter than it implies");
        picture.SkyBright.ShouldBeInRange((int)(picture.SkyOutliersExpected * 0.6), (int)(picture.SkyOutliersExpected * 1.4) + 1);
        picture.SkyDark.ShouldBeInRange((int)(picture.SkyOutliersExpected * 0.6), (int)(picture.SkyOutliersExpected * 1.4) + 1);
        picture.Moons.ShouldBe(0);
    }

    [Fact]
    public void TheSkysNoiseBandByBandAndTheGrainAreReadAsPutIn()
    {
        // #1367: white noise read through the a trous bands past 2.5 radii gives Starck's noise per band for the B3 starlet, and the grain
        // is band 1's over the globe's median level inside half a radius, a ratio the disk units cancel out of. The sky carries a gradient,
        // which the transform's mirrored edges would fold into the coarse bands if its plane were left in (band 4 a tenth high on 256 px).
        // A 512 px frame: on 256 px band 4's sky holds so few independent samples that it scatters 3.4 % from seed to seed, against 2 % here.
        const int size = 512;
        double[] starck = [0.889, 0.200, 0.086, 0.041];
        const double noise = 0.002;
        var disk = new MetricDisk(size / 2.0 + 0.3, size / 2.0 - 0.4, 40);
        var random = new Random(7);
        var plane = new float[size * size];
        var globe = new List<double>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // A limb-darkened disk, so the globe's median is not simply its brightest level.
                var r = disk.RadiiAt(x, y);
                var light = r <= 1 ? Disk * Math.Pow(1 - (r * r), 0.25) : 0;
                plane[(y * size) + x] = (float)(Sky + light + (0.01 * x / size) + (noise * PhaseScreen.Gaussian(random)));
                if (r <= PlanetaryPicture.GrainRadii)
                {
                    globe.Add(light);
                }
            }
        }
        globe.Sort();
        var level = globe[globe.Count / 2];
        var (_, scale) = PlanetaryMetrics.NormalisationLevels(plane, size, size, disk);

        var picture = PlanetaryPicture.Measure(plane, size, size, disk, moons: []);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"bands {string.Join(", ", picture.SkyBandNoise.Select((b, j) => $"{b * scale / noise:0.000} of {starck[j]:0.000}"))}; grain {picture.Grain:0.00000} (put in {starck[0] * noise / level:0.00000})");

        picture.SkyBandNoise.Length.ShouldBe(starck.Length);
        for (var j = 0; j < starck.Length; j++)
        {
            picture.SkyBandNoise[j].ShouldBe(starck[j] * noise / scale, starck[j] * noise / scale * 0.05, $"band {j + 1}");
        }
        picture.Grain.ShouldBe(starck[0] * noise / level, starck[0] * noise / level * 0.05);
    }

    [Fact]
    public void ADiskClippedAtWhiteSaysSo()
    {
        var (truth, _) = Truth();
        var plane = OnSky(truth, 0.0005, 0, 6);
        var disk = new MetricDisk(Placement.CenterX, Placement.CenterY, Placement.EquatorialRadius);
        var ceiling = (float)(Sky + (0.8 * Disk));
        var clipped = Array.ConvertAll(plane, v => Math.Min(v, ceiling));

        var asIs = PlanetaryPicture.Measure(plane, Size, Size, disk, moons: []);
        var atWhite = PlanetaryPicture.Measure(clipped, Size, Size, disk, moons: []);

        asIs.AtPeak.ShouldBeLessThan(3);
        atWhite.AtPeak.ShouldBeGreaterThan(100, "every pixel the ceiling cut sits at the image's peak");
        atWhite.Contrast.ShouldBeLessThan(asIs.Contrast);
    }

    [Fact(Timeout = 600_000)]
    public async Task TheSharpeningsGlowIsTheRenderedTruthsRingByRing()
    {
        // #1213: the glow the derived sharpening draws past the limb (the fit's sharp model through the pupil, over its window's reach)
        // against the renderer's truth, both in the undiffracted disk's own brightness. On 128-sample PSF grids the renderer cut the glow to
        // nothing past 1.6 radii and the model left it 12 to 25 % short; grown to their reach they agree within 2.5 % out to 3 radii.
        var ct = TestContext.Current.CancellationToken;
        var (truth, aspect) = Truth();
        var map = new float[36 * 18];
        Array.Fill(map, 1f);
        var sharp = PlanetaryRender.Render(new PlanetMap(map, 36, 18), aspect, Placement, Size, Size, 0.95);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        var fit = (await Task.Run(() => PlanetaryLimbFit.Fit(ToImage(OnSky(truth, 0, 0, 3)), limbOptions), ct)).ShouldNotBeNull();
        var disk = MetricDisk.From(fit, limbOptions.AxisRatio);
        var model = PlanetaryPicture.Diffracted(fit, limbOptions, aspect, Telescope, WavelengthNm, Size, Size);
        double brightness = 0;
        var count = 0;
        for (var i = 0; i < sharp.Length; i++)
        {
            if (disk.RadiiAt(i % Size, i / Size) < 0.8)
            {
                brightness += sharp[i];
                count++;
            }
        }
        brightness /= count;

        for (var r0 = 1.0; r0 < 2.5; r0 += 0.1)
        {
            double inTruth = 0, inModel = 0;
            for (var i = 0; i < truth.Length; i++)
            {
                var r = disk.RadiiAt(i % Size, i / Size);
                if (r > r0 && r <= r0 + 0.1)
                {
                    inTruth += truth[i] / brightness;
                    inModel += model[i];
                }
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"{r0:0.0} to {r0 + 0.1:0.0} radii: truth {inTruth:0.000}, the sharpening's glow {inModel:0.000}");
            inModel.ShouldBe(inTruth, inTruth * 0.04, $"the glow from {r0:0.0} radii");
        }
    }
}
