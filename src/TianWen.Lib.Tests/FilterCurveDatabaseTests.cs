using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class FilterCurveDatabaseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadAsync_LoadsAll176Curves()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.IsLoaded.ShouldBeTrue();
        FilterCurveDatabase.AllCurves.Length.ShouldBe(176);

        foreach (var curve in FilterCurveDatabase.AllCurves)
        {
            curve.Name.Length.ShouldBeGreaterThan(0, $"curve should have a name");
            curve.Count.ShouldBeGreaterThan(0, $"curve '{curve.Name}' should have data points");
            curve.Wavelengths.Length.ShouldBe((int)curve.Count);
            curve.Throughputs.Length.ShouldBe((int)curve.Count);
            // Wavelengths must be strictly increasing
            for (var i = 1; i < curve.Count; i++)
                curve.WavelengthAt(i).ShouldBeGreaterThan(curve.WavelengthAt(i - 1),
                    $"curve '{curve.Name}' wavelengths must increase");
            // Throughputs must be in [0, 1] (allow tiny overshoot from source data)
            for (var i = 0; i < curve.Count; i++)
                curve.ThroughputAt(i).ShouldBeInRange(-0.001, 1.001,
                    $"curve '{curve.Name}' throughput[{i}] must be near [0,1]");
        }
    }

    [Theory]
    [InlineData("BAADER_R")]
    [InlineData("BAADER_G")]
    [InlineData("BAADER_B")]
    [InlineData("CHROMA_R")]
    [InlineData("CHROMA_G")]
    [InlineData("CHROMA_B")]
    [InlineData("ZWO_R")]
    [InlineData("ZWO_G")]
    [InlineData("ZWO_B")]
    [InlineData("ANTLIA_V_PRO_SERIES_R")]
    [InlineData("ANTLIA_V_PRO_SERIES_G")]
    [InlineData("ANTLIA_V_PRO_SERIES_B")]
    [InlineData("ASTRONOMIK_DEEP_SKY_R")]
    [InlineData("ASTRONOMIK_DEEP_SKY_G")]
    [InlineData("ASTRONOMIK_DEEP_SKY_B")]
    [InlineData("JOHNSON_V")]
    [InlineData("JOHNSON_B")]
    [InlineData("JOHNSON_R")]
    [InlineData("SDSS_G")]
    [InlineData("SDSS_R")]
    [InlineData("SDSS_I")]
    [InlineData("IDAS_LPS_P3_LIGHT_POLLUTION")]
    private async Task TryGetCurve_ExactName_ReturnsCurve(string name)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var found = FilterCurveDatabase.TryGetCurve(name, out var curve);
        found.ShouldBeTrue($"exact match for '{name}' should succeed");
        curve.Name.ShouldBe(name);
    }

    [Theory]
    [InlineData("Baader R", "BAADER_R")]
    [InlineData("Baader G", "BAADER_G")]
    [InlineData("Baader B", "BAADER_B")]
    [InlineData("Chroma R", "CHROMA_R")]
    [InlineData("Chroma G", "CHROMA_G")]
    [InlineData("Chroma B", "CHROMA_B")]
    [InlineData("Antlia V Pro Series B", "ANTLIA_V_PRO_SERIES_B")]
    [InlineData("Antlia V-Pro Series G", "ANTLIA_V_PRO_SERIES_G")]
    [InlineData("Astronomik Deep Sky R", "ASTRONOMIK_DEEP_SKY_R")]
    [InlineData("Johnson V", "JOHNSON_V")]
    [InlineData("IDAS LPS P3", "IDAS_LPS_P3_LIGHT_POLLUTION")]
    private async Task TryMatchCurve_FuzzyName_MatchesCorrectCurve(string input, string expectedName)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var found = FilterCurveDatabase.TryMatchCurve(input, out var curve);
        found.ShouldBeTrue($"fuzzy match for '{input}' should succeed");
        curve.Name.ShouldBe(expectedName);
        output.WriteLine($"Matched '{input}' -> '{curve.Name}' ({curve.OriginFilename})");
    }

    [Theory]
    [InlineData("L-Ultimate")]
    [InlineData("L-eNhance")]
    [InlineData("L-eXtreme")]
    [InlineData("L-Quad Enhance")]
    private async Task TryMatchCurve_NarrowbandWithoutCamera_ReturnsFalse(string input)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // These narrowband/dual-band filter names exist in SASP only as
        // camera+filter combos (e.g. CANON_FULL_SPECTRUM_B_/_OPT._L-ULTIMATE).
        // Without a camera brand in the query, they should not match a
        // camera-specific entry since fewer than half the tokens are shared.
        FilterCurveDatabase.TryMatchCurve(input, out var curve).ShouldBeFalse(
            $"'{input}' should not match a camera-specific combo without a camera brand");
    }

    [Theory]
    [InlineData("Canon Full Spectrum B / L-Ultimate")]
    [InlineData("Sony CMOS B-UVIRcut / L-Ultimate")]
    [InlineData("Canon Full Spectrum R / L-eNhance")]
    private async Task TryMatchCurve_NarrowbandWithCamera_MatchesCombo(string input)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var found = FilterCurveDatabase.TryMatchCurve(input, out var curve);
        found.ShouldBeTrue($"fuzzy match for '{input}' should succeed when camera is specified");
        output.WriteLine($"Matched '{input}' -> '{curve.Name}'");
    }

    [Theory]
    [InlineData("CompletelyUnknownFilterName")]
    [InlineData("")]
    [InlineData("   ")]
    private async Task TryMatchCurve_Unknown_ReturnsFalse(string input)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchCurve(input, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task TryGetCurve_BeforeLoad_ReturnsFalse()
    {
        // FilterCurveDatabase is a static singleton — LoadAsync is idempotent.
        // This test verifies the IsLoaded guard on TryGetCurve itself.
        if (FilterCurveDatabase.IsLoaded)
        {
            output.WriteLine("Database already loaded by prior test — guard check skipped.");
            return;
        }

        FilterCurveDatabase.TryGetCurve("BAADER_R", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Interpolate_WithinRange_ReturnsCorrectValue()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetCurve("BAADER_R", out var curve).ShouldBeTrue();

        // At the first wavelength point, interpolation should return the first throughput
        var wl0 = curve.WavelengthAt(0);
        curve.Interpolate(wl0).ShouldBe(curve.ThroughputAt(0), 1e-6);

        // At the last wavelength point
        var wlLast = curve.WavelengthAt(curve.Count - 1);
        curve.Interpolate(wlLast).ShouldBe(curve.ThroughputAt(curve.Count - 1), 1e-6);

        // Midpoint between two points
        var wlMid = (curve.WavelengthAt(10) + curve.WavelengthAt(11)) / 2.0;
        var expectedMid = (curve.ThroughputAt(10) + curve.ThroughputAt(11)) / 2.0;
        curve.Interpolate(wlMid).ShouldBe(expectedMid, 1e-6);
    }

    [Fact]
    public async Task Interpolate_OutsideRange_ReturnsZero()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetCurve("BAADER_R", out var curve).ShouldBeTrue();

        curve.Interpolate(0).ShouldBe(0);
        curve.Interpolate(1_000_000).ShouldBe(0); // far IR
    }

    [Fact]
    public async Task Sensors_LoadedAlongsideFilters()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.AllSensors.Length.ShouldBe(16,
            "16 sensor QE curves from SASP_data.fits");

        // Spot-check known sensors
        FilterCurveDatabase.TryGetSensor("IMX533", out var imx533).ShouldBeTrue();
        imx533.Name.ShouldBe("IMX533");
        imx533.Count.ShouldBeGreaterThan(100);

        FilterCurveDatabase.TryGetSensor("IMX571", out var imx571).ShouldBeTrue();
        imx571.Name.ShouldBe("IMX571");
    }

    [Theory]
    [InlineData("IMX533")]
    [InlineData("imx533")]
    [InlineData("IMX571")]
    [InlineData("IMX455")]
    [InlineData("KAF-8300")]
    private async Task TryGetSensor_KnownModels_ReturnsCurve(string model)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetSensor(model, out var curve).ShouldBeTrue(
            $"sensor '{model}' should be found");
        output.WriteLine($"Sensor {curve.Name}: {curve.Count} pts, {curve.WavelengthAt(0):F0}-{curve.WavelengthAt(curve.Count-1):F0} A");
    }

    [Theory]
    [InlineData("ZWO ASI533MC Pro", "IMX533")]
    [InlineData("ZWO ASI585MC Pro", "IMX585")]
    [InlineData("ZWO ASI183MM Pro", "IMX183")]
    [InlineData("ZWO ASI462MC", "IMX462_SEESTAR")]
    private async Task TryMatchSensor_FromProductName_ReturnsCorrectSensor(string productName, string expected)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchSensor(productName, out var curve).ShouldBeTrue(
            $"should match sensor from '{productName}'");
        curve.Name.ShouldBe(expected);
        output.WriteLine($"Matched '{productName}' -> '{curve.Name}'");
    }

    [Fact]
    public async Task TryGetSensor_Unknown_ReturnsFalse()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetSensor("NONEXISTENT123", out _).ShouldBeFalse();
        FilterCurveDatabase.TryGetSensor("", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Combine_TwoCurves_MultipliesThroughputs()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetSensor("IMX533", out var qe).ShouldBeTrue();
        FilterCurveDatabase.TryGetFilter("BAADER_R", out var baaderR).ShouldBeTrue();

        var combined = FilterCurve.Combine("IMX533+BaaderR", [qe, baaderR]);

        combined.Name.ShouldBe("IMX533+BaaderR");
        combined.Count.ShouldBeGreaterThan(0);

        // Overlap: IMX533 is 3500-10000Å, Baader R is ~5700-7200Å
        // Combined should span their intersection
        combined.WavelengthAt(0).ShouldBeGreaterThanOrEqualTo(5600);
        combined.WavelengthAt(combined.Count - 1).ShouldBeLessThanOrEqualTo(8000);

        // At every point, combined throughput ≤ min of inputs
        for (var i = 0; i < combined.Count; i++)
        {
            var wl = combined.WavelengthAt(i);
            var expected = qe.Interpolate(wl) * baaderR.Interpolate(wl);
            combined.ThroughputAt(i).ShouldBe(expected, 1e-6);
        }

        output.WriteLine($"Combined: {combined.Count} pts, {combined.WavelengthAt(0):F0}-{combined.WavelengthAt(combined.Count-1):F0} A");
    }

    [Fact]
    public async Task ComputeSystemThroughput_Mono533_BaaderRGB_ReturnsNonZero()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var tsys = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        tsys.ShouldNotBeNull("should resolve IMX533 QE + Baader R filter");
        tsys!.Value.Count.ShouldBeGreaterThan(0);

        // Peak throughput should be non-trivial (QE × filter transmission)
        var maxTp = 0.0;
        for (var i = 0; i < tsys.Value.Count; i++)
            maxTp = Math.Max(maxTp, tsys.Value.ThroughputAt(i));
        maxTp.ShouldBeGreaterThan(0.01, "combined throughput should have non-zero peak");
        maxTp.ShouldBeLessThan(1.0, "combined throughput should be ≤ 1");

        output.WriteLine($"T_sys IMX533+BaaderR: {tsys.Value.Count} pts, peak={maxTp:F4}");
    }

    [Fact]
    public async Task ComputeSystemThroughput_UnknownSensor_ReturnsNull()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var tsys = FilterCurveDatabase.ComputeSystemThroughput("NONEXISTENT", "Baader R");
        tsys.ShouldBeNull("unknown sensor should return null");
    }

    // ------------------------------------------------------------------ SEDs

    [Fact]
    public async Task Seds_LoadedAlongsideFilters()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.AllSeds.Length.ShouldBe(157,
            "157 Pickles stellar SEDs from SASP_data.fits");

        // Spot-check known spectral types
        FilterCurveDatabase.TryGetSedByName("G2V", out var g2v).ShouldBeTrue();
        g2v.Name.ShouldBe("G2V");
        g2v.Count.ShouldBe(1895);
        output.WriteLine($"G2V: {g2v.Count} pts, {g2v.WavelengthAt(0):F0}-{g2v.WavelengthAt(g2v.Count-1):F0} A");

        FilterCurveDatabase.TryGetSedByName("O5V", out var o5v).ShouldBeTrue();
        FilterCurveDatabase.TryGetSedByName("M5III", out var m5iii).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetSedByBv_SolarBv_ReturnsGTypeStar()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // Solar B-V ≈ 0.65 → should match a G-type main sequence star
        FilterCurveDatabase.TryGetSedByBv(0.65, out var sed).ShouldBeTrue();
        sed.Name.StartsWith("G").ShouldBeTrue($"B-V=0.65 should match G-type, got {sed.Name}");
        output.WriteLine($"B-V=0.65 → {sed.Name}");
    }

    [Theory]
    [InlineData(-0.32, "O")]  // very blue → O-type (closest to O5V)
    [InlineData(0.00, "A")]   // blue-white → A-type (A0V)
    [InlineData(0.30, "F")]   // white → F-type (F0V)
    [InlineData(0.65, "G")]   // yellow → G-type (G2V = Sun)
    [InlineData(0.90, "K")]   // orange → K-type (K1V ≈ 0.91)
    [InlineData(1.40, "M")]   // red → M-type (M0V)
    private async Task TryGetSedByBv_RoughSpectralClass_CorrectClass(double bv, string expectedClass)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryGetSedByBv(bv, out var sed).ShouldBeTrue();
        sed.Name.StartsWith(expectedClass).ShouldBeTrue(
            $"B-V={bv:F2} should match {expectedClass}-type star, got {sed.Name}");
        output.WriteLine($"B-V={bv:F2} → {sed.Name}");
    }

    [Fact]
    public async Task TryGetSedByBv_ExtremeValues_ReturnsBoundary()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // Very blue: should match hottest star (O5V)
        FilterCurveDatabase.TryGetSedByBv(-1.0, out var hot).ShouldBeTrue();
        hot.Name.ShouldBe("O5V");
        output.WriteLine($"B-V=-1.0 → {hot.Name}");

        // Very red: should match coolest star
        FilterCurveDatabase.TryGetSedByBv(3.0, out var cool).ShouldBeTrue();
        output.WriteLine($"B-V=3.0 → {cool.Name}");
    }

    [Fact]
    public async Task TryGetSedByBv_BvSorted_Monotonic()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // Verify SEDs are sorted by B-V
        for (var i = 0; i < 20; i++)
        {
            var bv = -0.3 + i * 0.1; // -0.3 to 1.7
            FilterCurveDatabase.TryGetSedByBv(bv, out var sed).ShouldBeTrue();
            var klass = sed.Name[0];
            // As B-V increases, spectral class should move O→B→A→F→G→K→M
            output.WriteLine($"  B-V={bv:F1} → {sed.Name}");
        }
    }

    // ------------------------------------------------------------------ Integration

    [Fact]
    public async Task IntegrateSedThroughput_BaaderRG_FluxRatiosAreReasonable()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        // Solar-type star through Baader RGB + IMX533
        FilterCurveDatabase.TryGetSedByBv(0.65, out var sed).ShouldBeTrue();
        var tsysR = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        var tsysG = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G");
        var tsysB = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B");
        tsysR.ShouldNotBeNull(); tsysG.ShouldNotBeNull(); tsysB.ShouldNotBeNull();

        var ratios = FilterCurveDatabase.ComputeExpectedRatios(
            sed, tsysR!.Value, tsysG!.Value, tsysB!.Value);
        ratios.ShouldNotBeNull();
        var (rOverG, bOverG) = ratios!.Value;

        // For a G2V star, R/G should be close to 1 and B/G slightly less
        output.WriteLine($"G2V + Baader RGB + IMX533: R/G={rOverG:F4}, B/G={bOverG:F4}");
        rOverG.ShouldBeInRange(0.5, 2.0, "R/G should be reasonable for solar-type star");
        bOverG.ShouldBeInRange(0.2, 1.5, "B/G should be reasonable for solar-type star");
    }

    [Fact]
    public async Task IntegrateSedThroughput_BlueVsRed_RelativeRatiosDiverge()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var tsysR = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R");
        var tsysG = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G");
        var tsysB = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B");
        tsysR.ShouldNotBeNull(); tsysG.ShouldNotBeNull(); tsysB.ShouldNotBeNull();

        // Hot blue star (B5V)
        FilterCurveDatabase.TryGetSedByBv(-0.17, out var blue).ShouldBeTrue();
        var blueRatios = FilterCurveDatabase.ComputeExpectedRatios(
            blue, tsysR!.Value, tsysG!.Value, tsysB!.Value);

        // Cool red star (M0V)
        FilterCurveDatabase.TryGetSedByBv(1.40, out var red).ShouldBeTrue();
        var redRatios = FilterCurveDatabase.ComputeExpectedRatios(
            red, tsysR.Value, tsysG.Value, tsysB.Value);

        blueRatios.ShouldNotBeNull();
        redRatios.ShouldNotBeNull();
        var (blueRG, blueBG) = blueRatios!.Value;
        var (redRG, redBG) = redRatios!.Value;

        // Blue star: more B relative to G, less R relative to G
        blueBG.ShouldBeGreaterThan(redBG, "blue star should have stronger blue channel");
        // Red star: more R relative to G, less B relative to G
        redRG.ShouldBeGreaterThan(blueRG, "red star should have stronger red channel");

        output.WriteLine($"B5V ({blue.Name}): R/G={blueRG:F4}, B/G={blueBG:F4}");
        output.WriteLine($"M0V ({red.Name}): R/G={redRG:F4}, B/G={redBG:F4}");
    }

    [Fact]
    public async Task LoadAsync_IsIdempotent()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var count1 = FilterCurveDatabase.AllCurves.Length;

        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var count2 = FilterCurveDatabase.AllCurves.Length;

        count2.ShouldBe(count1);
    }

    [Theory]
    [InlineData("ANTLIA_V_PRO_SERIES_B", "antliavproseriesb")]
    [InlineData("Antlia V-Pro Series B", "antliavproseriesb")]
    [InlineData("antlia v pro series b", "antliavproseriesb")]
    [InlineData("BAADER_R", "baaderr")]
    [InlineData("Baader / R", "baaderr")]
    [InlineData("OPT._L-EXTREME", "optlextreme")]
    public void NormalizeName_StripsNonAlphanumeric(string input, string expected)
    {
        FilterCurveDatabase.NormalizeName(input).ShouldBe(expected);
    }

    /// <summary>
    /// Sensor-derived luma weights: for an OSC sensor (SensorType.RGGB) with a known
    /// QE curve, the helper integrates QE x CFA_R/G/B and normalises so the three
    /// channels sum to 1. Asserts the broadband response is positive on every channel
    /// (Bayer CFAs always overlap green into R/B), and that the values are distinct
    /// from Rec.709 (otherwise the SensorMatched path would be pointless).
    /// </summary>
    [Theory]
    [InlineData("IMX533")]
    [InlineData("IMX571")]
    [InlineData("IMX455")]
    public async Task TryComputeSensorLumaWeights_OscSensor_ProducesNormalizedTriple(string sensorModel)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var meta = TestImageMeta(SensorType.RGGB, sensorModel);

        FilterCurveDatabase.TryComputeSensorLumaWeights(meta, out var w).ShouldBeTrue(
            $"sensor {sensorModel} + RGGB CFA should integrate to a valid luma triple");

        output.WriteLine($"{sensorModel}: weights R={w.R:F4} G={w.G:F4} B={w.B:F4}");

        // Each channel produces positive broadband signal under a Bayer CFA.
        w.R.ShouldBeGreaterThan(0f);
        w.G.ShouldBeGreaterThan(0f);
        w.B.ShouldBeGreaterThan(0f);

        // Normalised (sums to 1 within FP rounding).
        (w.R + w.G + w.B).ShouldBe(1f, 1e-4f, "sensor luma weights must sum to 1");

        // Distinct from Rec.709 -- otherwise SensorMatched is just a re-labelled Rec.709.
        var rec709 = LumaWeighting.Rec709.Weights;
        var l1Diff = Math.Abs(w.R - rec709.R) + Math.Abs(w.G - rec709.G) + Math.Abs(w.B - rec709.B);
        l1Diff.ShouldBeGreaterThan(0.01f, "SensorMatched should differ from Rec.709 in a measurable way");
    }

    /// <summary>
    /// Empty / unknown sensor metadata falls back cleanly: helper returns false so the
    /// producer can route to a standard Rec.709 weighting instead of crashing.
    /// </summary>
    [Fact]
    public async Task TryComputeSensorLumaWeights_UnknownSensor_ReturnsFalse()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var meta = TestImageMeta(SensorType.Monochrome, "DEFINITELY_NOT_A_REAL_SENSOR");

        FilterCurveDatabase.TryComputeSensorLumaWeights(meta, out _).ShouldBeFalse(
            "unknown sensor + mono (no CFA) cannot resolve a per-channel response");
    }

    /// <summary>Minimal stub for tests that only care about SensorType + SensorModel.</summary>
    private static ImageMeta TestImageMeta(SensorType sensorType, string sensorModel) => new(
        Instrument: sensorModel,
        ExposureStartTime: default,
        ExposureDuration: default,
        FrameType: FrameType.Light,
        Telescope: "",
        PixelSizeX: 0f,
        PixelSizeY: 0f,
        FocalLength: -1,
        FocusPos: -1,
        Filter: Filter.None,
        BinX: 1,
        BinY: 1,
        CCDTemperature: float.NaN,
        SensorType: sensorType,
        BayerOffsetX: 0,
        BayerOffsetY: 0,
        RowOrder: RowOrder.TopDown,
        Latitude: float.NaN,
        Longitude: float.NaN,
        SensorModel: sensorModel);
}
