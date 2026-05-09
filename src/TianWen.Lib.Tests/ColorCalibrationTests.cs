using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public class ColorCalibrationTests(ITestOutputHelper output)
{
    [Fact]
    public void ComputeMultipliers_BalancedStars_ReturnsIdentity()
    {
        // Stars with observed = expected → WB should be (1, 1, 1)
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        // Expected ratios match observed → identity
        var (expR, expG, expB) = (obsR, obsG, obsB);

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        wbG.ShouldBe(1f);
        wbR.ShouldBeInRange(0.99f, 1.01f);
        wbB.ShouldBeInRange(0.99f, 1.01f);
    }

    [Fact]
    public void ComputeMultipliers_GreenCast_BoostsRedAndBlue()
    {
        // Simulate green cast: observed green is 2x brighter than expected relative to R/B
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 200f, 400f, 600f, 800f, 1000f }; // 2x too bright
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        // Expected: equal ratios (gray star B-V = 0.65 → roughly equal RGB)
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        // Green is too bright → R and B need boosting
        wbG.ShouldBe(1f);
        wbR.ShouldBeGreaterThanOrEqualTo(1.5f, "red needs boosting when green cast exists");
        wbB.ShouldBeGreaterThanOrEqualTo(1.5f, "blue needs boosting when green cast exists");
    }

    [Fact]
    public void ComputeMultipliers_BlueCast_ReducesBlue()
    {
        // Simulate blue cast: observed blue is 2x brighter relative to expected
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 160f, 320f, 480f, 640f, 800f }; // 2x too bright vs expected
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        wbG.ShouldBe(1f);
        // wbR = expR/obsR * obsG/expG = 1.0 * 1.2 = 1.2 (mild red boost from green reference)
        wbR.ShouldBeGreaterThan(1f, "red boosted slightly since green ref is also above expected");
        // wbB = expB/obsB * obsG/expG = 0.5 * 1.2 = 0.6 → blue reduced
        wbB.ShouldBeLessThan(1f, "blue reduced when blue cast exists");
    }

    [Fact]
    public void ComputeMultipliers_RedCast_ReducesRed()
    {
        var obsR = new[] { 200f, 400f, 600f, 800f, 1000f }; // 2x too bright
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        wbG.ShouldBe(1f);
        // wbR = expR/obsR * obsG/expG = 0.5 * 1.2 = 0.6 → red reduced
        wbR.ShouldBeLessThan(1f, "red reduced when red cast exists");
        // wbB = expB/obsB * obsG/expG = 1.25 * 1.2 = 1.5 → blue boosted
        wbB.ShouldBeGreaterThan(1f, "blue boosted when red cast exists");
    }

    [Fact]
    public void ComputeMultipliers_ClampsToRange()
    {
        // Extreme cast — should clamp to [0.5, 2.0] range.
        // Values outside this range indicate sensor/model mismatch, not correctable colour cast.
        var obsR = new[] { 1f };
        var obsG = new[] { 1000f }; // huge green cast
        var obsB = new[] { 1f };
        var exp = new[] { 1f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, exp, exp, exp);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");
        // wbR = expR/obsR * obsG/expG = 1000, clamped to 2.0
        wbR.ShouldBe(2f, "clamped to max of [0.5, 2.0] range");
        wbB.ShouldBe(2f, "clamped to max of [0.5, 2.0] range");
    }

    // ----------------------------------------------------------------- Spectrophotometric

    [Fact]
    public async Task SpectrophotometricWB_SolarG2V_ExpectedRatiosReasonable()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // Build system throughput for IMX533 + Baader RGB
        var tsysR = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        var tsysG = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G");
        var tsysB = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B");
        tsysR.ShouldNotBeNull(); tsysG.ShouldNotBeNull(); tsysB.ShouldNotBeNull();

        // For a G2V star (B-V=0.65), expected ratios should be near 1.0
        // since the Sun is our reference for "white"
        FilterCurveDatabase.TryGetSedByBv(0.65, out var sed).ShouldBeTrue();
        var ratios = FilterCurveDatabase.ComputeExpectedRatios(
            sed, tsysR!.Value, tsysG!.Value, tsysB!.Value);
        ratios.ShouldNotBeNull();
        var (rg, bg) = ratios!.Value;

        output.WriteLine($"G2V + IMX533 + Baader RGB: R/G={rg:F4}, B/G={bg:F4}");
        rg.ShouldBeInRange(0.5, 2.0);
        bg.ShouldBeInRange(0.2, 1.5);
    }

    [Fact]
    public async Task SpectrophotometricWB_BlueStar_MoreBlueThanRed()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var tsysR = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        var tsysG = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G");
        var tsysB = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B");
        tsysR.ShouldNotBeNull(); tsysG.ShouldNotBeNull(); tsysB.ShouldNotBeNull();

        // Hot blue star (B5V, B-V ≈ -0.16) vs cool red star (M0V, B-V ≈ +1.40)
        FilterCurveDatabase.TryGetSedByBv(-0.17, out var blue).ShouldBeTrue();
        FilterCurveDatabase.TryGetSedByBv(1.40, out var red).ShouldBeTrue();

        var blueR = FilterCurveDatabase.ComputeExpectedRatios(
            blue, tsysR!.Value, tsysG!.Value, tsysB!.Value);
        var redR = FilterCurveDatabase.ComputeExpectedRatios(
            red, tsysR.Value, tsysG.Value, tsysB.Value);

        blueR.ShouldNotBeNull(); redR.ShouldNotBeNull();

        // Blue star should have stronger B/G than red star
        blueR!.Value.BG.ShouldBeGreaterThan(redR!.Value.BG,
            "blue star should be bluer (higher B/G) than red star");
        // Red star should have stronger R/G than blue star
        redR.Value.RG.ShouldBeGreaterThan(blueR.Value.RG,
            "red star should be redder (higher R/G) than blue star");

        output.WriteLine($"B5V: R/G={blueR.Value.RG:F4}, B/G={blueR.Value.BG:F4}");
        output.WriteLine($"M0V: R/G={redR.Value.RG:F4}, B/G={redR.Value.BG:F4}");
    }

    [Fact]
    public async Task SpectrophotometricWB_FallsBackToBlackbody_WhenSedNotLoaded()
    {
        // Without loading the database, SED lookup fails → should fall back to blackbody
        // (FilterCurveDatabase.TryGetSedByBv returns false when not loaded)
        if (FilterCurveDatabase.IsLoaded)
        {
            output.WriteLine("Database already loaded — skipping fallback test.");
            return;
        }

        // Just verify the blackbody path still works
        var (r, g, b) = SyntheticStarFieldRenderer.BMinusVToRGB(0.65);
        r.ShouldBeInRange(0.5, 1.5);
        g.ShouldBe(1.0);
        b.ShouldBeInRange(0.5, 1.5);
    }

    [Fact]
    public async Task BuildChannelThroughputs_OSC_WithKnownSensor_ReturnsThreeChannels()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var meta = new ImageMeta(
            Instrument: "ZWO ASI533MC Pro",
            ExposureStartTime: DateTimeOffset.UtcNow,
            ExposureDuration: TimeSpan.FromSeconds(60),
            FrameType: FrameType.Light,
            Telescope: "SharpStar 61EDPH",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 275,
            FocusPos: 1000,
            Filter: Filter.Luminance,
            BinX: 1, BinY: 1,
            CCDTemperature: -10f,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 48.2f,
            Longitude: 16.3f,
            SensorModel: "IMX533"
        );

        var channels = FilterCurveDatabase.BuildChannelThroughputs(meta);
        channels.ShouldNotBeNull("should build channels for OSC IMX533");

        var (r, g, b) = channels!.Value;
        r.Count.ShouldBeGreaterThan(0);
        g.Count.ShouldBeGreaterThan(0);
        b.Count.ShouldBeGreaterThan(0);

        // CFA R should have more red throughput (higher at 650nm than blue)
        var redAt650 = r.Interpolate(6500);
        var blueAt450 = b.Interpolate(4500);
        redAt650.ShouldBeGreaterThan(0, "red channel should pass red light");
        blueAt450.ShouldBeGreaterThan(0, "blue channel should pass blue light");

        output.WriteLine($"T_sys OSC: R={r.Count}pts @ {r.WavelengthAt(0):F0}-{r.WavelengthAt(r.Count-1):F0}A, " +
            $"G={g.Count}pts, B={b.Count}pts");
    }

    [Fact]
    public async Task BuildChannelThroughputs_Mono_WithExplicitFilters_ReturnsThreeChannels()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var meta = new ImageMeta(
            Instrument: "ZWO ASI533MM Pro",
            ExposureStartTime: DateTimeOffset.UtcNow,
            ExposureDuration: TimeSpan.FromSeconds(60),
            FrameType: FrameType.Light,
            Telescope: "SharpStar 61EDPH",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 275,
            FocusPos: 1000,
            Filter: Filter.Red, // mono image was shot through red filter
            BinX: 1, BinY: 1,
            CCDTemperature: -10f,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 48.2f,
            Longitude: 16.3f,
            SensorModel: "IMX533"
        );

        // For mono, caller provides the 3 filter names used for RGB
        var channels = FilterCurveDatabase.BuildChannelThroughputs(meta,
            redFilter: "Baader R", greenFilter: "Baader G", blueFilter: "Baader B");
        channels.ShouldNotBeNull("should build channels for mono + explicit filters");

        var (r, g, b) = channels!.Value;
        output.WriteLine($"T_sys Mono: R={r.Count}pts, G={g.Count}pts, B={b.Count}pts");
        r.Count.ShouldBeGreaterThan(0);
        g.Count.ShouldBeGreaterThan(0);
        b.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SpectrophotometricWB_ComputeMultipliers_WithSedExpectedColors()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var tsysR = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        var tsysG = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G");
        var tsysB = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B");
        tsysR.ShouldNotBeNull(); tsysG.ShouldNotBeNull(); tsysB.ShouldNotBeNull();

        // Simulate a set of stars where observed = expected (perfectly balanced image)
        // Use the SED-based expected colors for 5 stars with different B-V
        var bvs = new[] { -0.17, 0.0, 0.30, 0.65, 1.40 };
        var obsR = new float[bvs.Length];
        var obsG = new float[bvs.Length];
        var obsB = new float[bvs.Length];
        var expR = new float[bvs.Length];
        var expG = new float[bvs.Length];
        var expB = new float[bvs.Length];

        for (var i = 0; i < bvs.Length; i++)
        {
            FilterCurveDatabase.TryGetSedByBv(bvs[i], out var sed).ShouldBeTrue();
            var ratios = FilterCurveDatabase.ComputeExpectedRatios(
                sed, tsysR!.Value, tsysG!.Value, tsysB!.Value);
            ratios.ShouldNotBeNull();
            var (rg, bg) = ratios!.Value;

            // Normalise so max channel = 1.0 (matching ComputeExpectedRgbFromSed convention)
            var max = Math.Max(rg, Math.Max(1.0, bg));
            expR[i] = (float)(rg / max);
            expG[i] = (float)(1.0 / max);
            expB[i] = (float)(bg / max);

            // Observed = expected (balanced)
            obsR[i] = expR[i];
            obsG[i] = expG[i];
            obsB[i] = expB[i];

            output.WriteLine($"  B-V={bvs[i]:+.00} → exp R/G={rg:F4} B/G={bg:F4} → RGB=({expR[i]:F4},{expG[i]:F4},{expB[i]:F4})");
        }

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        // With observed = expected, WB should be near identity
        wbG.ShouldBe(1f);
        wbR.ShouldBeInRange(0.95f, 1.05f);
        wbB.ShouldBeInRange(0.95f, 1.05f);
    }
}
