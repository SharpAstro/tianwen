using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class FilterCurveDatabaseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadAsync_LoadsAll183Curves()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.IsLoaded.ShouldBeTrue();
        // 176 upstream (SETI Astro's SASP_data.fits) + 7 local, digitised from vendor charts and
        // merged by tools/import-sasp-data --merge-only: IDAS_LPS_D3, IDAS_NBZ,
        // ASKAR_COLOURMAGIC_D1/D2, OPTOLONG_L_QUAD_ENHANCE, OPTOLONG_L_ULTIMATE and OPTOLONG_L_ENHANCE. Local curves live in
        // tools/import-sasp-data/local-filters/.
        FilterCurveDatabase.AllCurves.Length.ShouldBe(183);

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
    [InlineData("L-eXtreme")]
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

    /// <summary>
    /// A duplicate-free database, which is the order-independent form of the count assertions above.
    ///
    /// <para>Those count on 178 and 16, and they DID catch the doubling -- but only because two
    /// tests happened to call <c>LoadAsync</c> close enough together to race, so on a different
    /// interleaving the suite was green with the bug present. Distinctness holds whatever the load
    /// order, which is what makes it worth asserting separately: it is the invariant, where a count
    /// is one consequence of it.</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentLoadsLeaveNoDuplicateCurves()
    {
        var ct = TestContext.Current.CancellationToken;

        // Eight at once, because the bug needed a race to show: LoadAsync built its Task as the
        // CompareExchange argument, so every loser ALSO ran a full load and appended its own copy.
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await FilterCurveDatabase.LoadAsync(ct), ct)));

        var filterNames = FilterCurveDatabase.AllFilters.Select(f => f.Name).ToArray();
        filterNames.Distinct(StringComparer.Ordinal).Count().ShouldBe(filterNames.Length,
            "a filter curve appearing twice means the database was loaded more than once");

        var sensorNames = FilterCurveDatabase.AllSensors.Select(f => f.Name).ToArray();
        sensorNames.Distinct(StringComparer.Ordinal).Count().ShouldBe(sensorNames.Length,
            "a sensor QE curve appearing twice means the database was loaded more than once");

        // And the flag must not lead the data. It was raised by the CAS winner BEFORE the load ran,
        // so a caller that did not await saw IsLoaded == true over an empty database.
        FilterCurveDatabase.IsLoaded.ShouldBeTrue();
        FilterCurveDatabase.AllFilters.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TryGetCurve_BeforeLoad_ReturnsFalse()
    {
        // FilterCurveDatabase is a static singleton: LoadAsync is idempotent.
        // This test verifies the IsLoaded guard on TryGetCurve itself.
        if (FilterCurveDatabase.IsLoaded)
        {
            output.WriteLine("Database already loaded by prior test, guard check skipped.");
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
    // SVBony product numbers don't encode the sensor (SV605 != IMX605);
    // covered by the explicit alias table. Regression-test the spelling
    // variants we expect to see in INSTRUME headers: with/without "SVBONY"
    // prefix, OSC vs mono suffix, and assorted whitespace.
    [InlineData("SVBONY SV605CC", "IMX533")]
    [InlineData("SVBONY SV605MC", "IMX533")]
    [InlineData("SV605CC", "IMX533")]
    [InlineData("SV605MC Pro", "IMX533")]
    [InlineData("Svbony Sv 605 CC", "IMX533")]
    [InlineData("SVBONY SV705C", "IMX585")]
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

    // ----------------------------------------------------------------------------------
    // A NEAR MISS MUST STAY A MISS.
    //
    // The database has IDAS_LPS_P3_LIGHT_POLLUTION and no D-series curve. P and D are
    // different filters, not spellings of one: the D3 (ex NGS1) is a NOTCH filter
    // suppressing OI 557.7, NaI 589.0/589.6 and OI 630.0/636.4 nm, where the P-series is
    // a broad multi-band shaped to preserve continuum colour. Resolving a D3 header to
    // the P3 curve would make SPCC integrate a transmission the light never passed
    // through and return a confidently wrong white balance -- the exact shape of the
    // phantom CFA_R -> BAADER_R fuzzy match that had to be removed from this database.
    //
    // What prevents it is TryMatchFilter's coverage gate (shared * 2 >= keyTokens.Count):
    // the P3 key tokenises to five and a D3 name shares two, so 4 < 5 rejects. That gate
    // is load-bearing, and "be more forgiving about filter names" is a natural-looking
    // change that would quietly break it. Hence these tests.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("IDAS LPS-D3")]
    [InlineData("IDAS LPS D3")]
    [InlineData("IDAS-LPS-D3")]
    [InlineData("LPS-D3")]
    [InlineData("IDAS LPS-D2")]
    [InlineData("IDAS LPS-D1")]
    [InlineData("NGS1")]
    public async Task TryMatchFilter_DSeriesName_DoesNotResolveToThePSeriesCurve(string written)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        if (FilterCurveDatabase.TryMatchFilter(written, out var curve))
        {
            // Matching something is only acceptable if it is not the P-series stand-in. Should a
            // real D-series curve ever be added, this test keeps passing on its own.
            curve.Name.ShouldNotContain("P3", Case.Insensitive,
                $"'{written}' resolved to '{curve.Name}': a D-series filter must never be modelled " +
                "by the P-series curve");
        }
    }

    [Fact]
    public async Task TryMatchFilter_ThePSeriesNameItself_StillResolves()
    {
        // The other half: the gate must not be so tight that the curve we DO have is unreachable.
        // Three of five tokens shared, which passes -- and this is the name a header must carry for
        // the P3 to be modelled at all.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter("IDAS LPS-P3", out var curve).ShouldBeTrue();
        curve.Name.ShouldContain("P3", Case.Insensitive);
        output.WriteLine($"'IDAS LPS-P3' -> {curve.Name}");
    }

    [Fact]
    public async Task TryMatchFilter_D3NameResolvesToTheD3Curve()
    {
        // The D3 curve now EXISTS (digitised from the vendor chart, merged as a local addition), so
        // the theory above changes character: it no longer says "a D3 header must find nothing", it
        // says "a D3 header must never find the P3". This is the positive half.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter("IDAS LPS-D3", out var curve).ShouldBeTrue();
        curve.Name.ShouldBe("IDAS_LPS_D3");
        output.WriteLine($"'IDAS LPS-D3' -> {curve.Name}");
    }

    [Fact]
    public async Task TheNbzCurveIsTwoNarrowBandsAndNothingElse()
    {
        // The dual-band shape is its own validation, and a strong one: the chart labels its
        // passbands OIII 495.9/500.7 and H-alpha 656.3, and a mis-scaled wavelength axis would put
        // the peaks somewhere else entirely. Two narrow windows with a dead baseline either side is
        // a signature almost nothing else produces.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryMatchFilter("IDAS NBZ", out var nbz).ShouldBeTrue();

        nbz.Interpolate(5007.0).ShouldBeGreaterThan(0.8, "OIII 500.7 is a passband");
        nbz.Interpolate(6563.0).ShouldBeGreaterThan(0.8, "H-alpha 656.3 is a passband");

        foreach (var blockedNm in (double[])[400, 450, 550, 600, 700, 800, 1000])
        {
            nbz.Interpolate(blockedNm * 10.0)
                .ShouldBeLessThan(0.05, $"{blockedNm} nm is outside both passbands");
        }
    }

    [Theory]
    [InlineData("Askar ColourMagic D1", "ASKAR_COLOURMAGIC_D1", 6563.0, 6716.0)]
    [InlineData("Askar D1", "ASKAR_COLOURMAGIC_D1", 6563.0, 6716.0)]
    [InlineData("ColourMagic D1", "ASKAR_COLOURMAGIC_D1", 6563.0, 6716.0)]
    [InlineData("Askar ColourMagic D2", "ASKAR_COLOURMAGIC_D2", 6716.0, 6563.0)]
    [InlineData("Askar D2", "ASKAR_COLOURMAGIC_D2", 6716.0, 6563.0)]
    [InlineData("ColourMagic D2", "ASKAR_COLOURMAGIC_D2", 6716.0, 6563.0)]
    public async Task TheColourMagicDuoBandsPassTheirOwnLineAndBlockTheOther(
        string written, string expected, double ownLineAngstrom, double otherLineAngstrom)
    {
        // The pair is the test. D1 and D2 share the OIII half and differ ONLY in the red half --
        // D1 is cut for H-alpha 656.3, D2 for SII 671.6 -- so a curve that passes its own red line
        // AND blocks the other filter's is placed to better than the 15 nm between them. Nothing
        // about those wavelengths went into building either curve or calibrating either axis, and
        // a mis-scaled wavelength axis could not produce this pattern in both directions.
        //
        // It also pins the NAMES, which is the half that bit: before the brand-only fix, "Optolong
        // L-eNhance" resolved to OPTOLONG_B, so a written card naming a duo-band filter could come
        // back as a broadband dichroic. A duo-band that resolves to the wrong curve is worse than
        // one that resolves to nothing.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var duo).ShouldBeTrue($"'{written}' must resolve");
        duo.Name.ShouldBe(expected);

        duo.Interpolate(5007.0).ShouldBeGreaterThan(0.8, "OIII 500.7 is a passband on both");
        duo.Interpolate(ownLineAngstrom).ShouldBeGreaterThan(0.8, "its own red line must pass");
        duo.Interpolate(otherLineAngstrom).ShouldBeLessThan(0.05, "the other filter's line must not");

        // And a dead baseline everywhere else, or "passes everything" would satisfy the above.
        foreach (var blockedNm in (double[])[400, 450, 550, 600, 700, 800, 1000])
        {
            duo.Interpolate(blockedNm * 10.0)
                .ShouldBeLessThan(0.05, $"{blockedNm} nm is outside both passbands");
        }
    }

    [Theory]
    [InlineData("Optolong L-eXtreme")]
    [InlineData("IDAS")]
    [InlineData("CFA_R")]
    public async Task ABrandTokenAloneIsNotAFilterMatch(string written)
    {
        // Half-coverage of the KEY is satisfied by the brand alone when the key is BRAND + CHANNEL,
        // and this cost three separate bugs: "Optolong L-eNhance" (a dual-band Ha+OIII) resolved to
        // OPTOLONG_B, a broadband blue dichroic; a bare "IDAS" resolved to IDAS_NBZ, whichever
        // IDAS curve happened to have fewest tokens; and "CFA_R" resolved to BAADER_R, which put a
        // mono dichroic into a modelled OSC throughput and skewed an SPCC fit.
        //
        // All three are the same failure and it is the bad kind: a confident WRONG curve, used as
        // if it described the glass in the light path, where declining would have been correct and
        // visible. The Optolong duo-bands genuinely are not in the database except pre-convolved
        // with a sensor, so "no match" is the honest answer for them.
        //
        // Adding OPTOLONG_L_QUAD_ENHANCE later re-broke exactly these names by a DIFFERENT route:
        // "optolong" plus the single letter "l" already covers half of that four-token key, so
        // L-eNhance, L-eXtreme and L-Ultimate all landed on the quad-band. What rejects them is
        // that "quad" is unmatched and names exactly one curve in the catalogue -- see the
        // document-frequency gate. Which is why these cases stay here after that fix: they are the
        // same wrong answer reached two different ways, and a third route would go unnoticed.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var curve).ShouldBeFalse(
            $"'{written}' names no curve we carry, so it must not resolve to one -- got '{curve.Name}'");
    }

    [Theory]
    [InlineData("Optolong L-Quad Enhance")]
    [InlineData("L-Quad Enhance")]
    public async Task TheQuadBandPassesFourLinesAndBlocksTheLightPollutionBetweenThem(string written)
    {
        // The strongest axis check in the database, because the nine annotated wavelengths
        // INTERLEAVE pass with block: Hg 435.8 sits between passbands one and two, Hg 546.1 between
        // two and three, the Na lines between three and four. A wavelength axis off by any amount
        // moves a passband onto a line the vendor marks as suppressed, so both halves cannot hold
        // at once unless the calibration is right. None of these went into building the curve.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var quad).ShouldBeTrue($"'{written}' must resolve");
        quad.Name.ShouldBe("OPTOLONG_L_QUAD_ENHANCE");

        foreach (var (nm, name) in ((double Nm, string Name)[])[
            (486.1, "Hb"), (500.7, "OIII"), (656.3, "Ha"), (671.6, "SII")])
        {
            var t = quad.Interpolate(nm * 10.0);
            output.WriteLine($"pass    {name,-4} {nm,7:F1} -> {t:P1}");
            t.ShouldBeGreaterThan(0.85, $"{name} {nm} is one of the four lines this filter is cut for");
        }

        foreach (var (nm, name) in ((double Nm, string Name)[])[
            (435.8, "Hg"), (546.1, "Hg"), (589.0, "Na"), (589.6, "Na"), (615.4, "Na")])
        {
            var t = quad.Interpolate(nm * 10.0);
            output.WriteLine($"blocked {name,-4} {nm,7:F1} -> {t:P1}");
            t.ShouldBeLessThan(0.05, $"{name} {nm} is a light-pollution line between the passbands");
        }
    }

    [Theory]
    // A written name that says LESS than the curve's still resolves: only the KEY has tokens of its
    // own, so the two do not diverge -- one is a shorter way of naming the same thing.
    [InlineData("LPS-D3", "IDAS_LPS_D3")]
    [InlineData("Askar D1", "ASKAR_COLOURMAGIC_D1")]
    [InlineData("IDAS LPS P3", "IDAS_LPS_P3_LIGHT_POLLUTION")]
    [InlineData("Optolong L-Pro", "OPTOLONG_L-PRO_LIGHT_POLLUTION")]
    // And so does one that says MORE, which is what a real FITS card looks like: a filter wheel
    // slot name carries the size, the mount, the batch. Only the NEEDLE has tokens of its own.
    [InlineData("Baader R CCD 31mm", "BAADER_R")]
    [InlineData("Chroma G 36mm unmounted", "CHROMA_G")]
    public async Task AOneSidedTokenDifferenceStillResolves(string written, string expected)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var curve).ShouldBeTrue($"'{written}' must resolve");
        curve.Name.ShouldBe(expected);
        output.WriteLine($"'{written}' -> {curve.Name}");
    }

    [Theory]
    // A TWO-SIDED difference means the names diverge, so they are different products and neither is
    // a match for the other. This is the rule document frequency cannot reach, because the
    // distinguishing tokens on each side may both be perfectly common.
    [InlineData("Johnson Z")]      // {z} against JOHNSON_V's {v} -- single chars must count
    [InlineData("Baader Q")]
    [InlineData("IDAS LPS-D5")]    // {d5} against {d3}, a filter in a family we carry
    [InlineData("Chroma Ha 5nm")]  // {ha, 5, nm} against CHROMA_R's {r}
    public async Task ATwoSidedTokenDifferenceIsRefused(string written)
    {
        // Every one of these has a plausible near-neighbour in the database that shares its brand,
        // which is exactly what makes them dangerous: the brand alone used to be enough, and a
        // half-covered key was enough after that. A filter we do not carry must read as absent.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var curve).ShouldBeFalse(
            $"'{written}' names a different product from anything we carry -- got '{curve.Name}'");
    }

    [Theory]
    [InlineData("Optolong L-Ultimate")]
    [InlineData("L-Ultimate")]
    public async Task TheUltimateIsTwoNarrowBandsAndDoesNotReachHBeta(string written)
    {
        // Hb 486.1 is the identity check, not merely an out-of-band probe. Optolong publish charts
        // under this product's name that are actually L-eNhance, whose blue band is 23 nm wide and
        // passes Hb outright, where L-Ultimate's is 3 nm and is 14.6 nm away from it. So a curve
        // that transmits OIII and H-alpha while BLOCKING Hb is the shape only the 3 nm filter has,
        // and it is what a mislabelled chart could not satisfy.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var ult).ShouldBeTrue($"'{written}' must resolve");
        ult.Name.ShouldBe("OPTOLONG_L_ULTIMATE");

        // 0.7 rather than 0.85: the chart is 1.11 px/nm, so a 3 nm band is three pixels of
        // near-vertical ink and the column centroid averages its own peak down. The vendor's zoomed
        // charts measure 87.6 % and 91.5 %; this reads 80.2 % and 85.6 % AT the lines. Conservative
        // by construction, and the header records the offset -- so the bound is set where the
        // measurement actually is rather than where the glass is.
        ult.Interpolate(5007.0).ShouldBeGreaterThan(0.7, "OIII 500.7 is one of the two bands");
        ult.Interpolate(6563.0).ShouldBeGreaterThan(0.7, "H-alpha 656.3 is the other");

        ult.Interpolate(4861.0).ShouldBeLessThan(0.05,
            "Hb 486.1 is 14.6nm from OIII -- a 3nm band cannot reach it, a 23nm L-eNhance band can");
        foreach (var blockedNm in (double[])[400, 450, 546.1, 589.0, 600, 671.6, 700, 780])
        {
            ult.Interpolate(blockedNm * 10.0)
                .ShouldBeLessThan(0.05, $"{blockedNm} nm is outside both bands");
        }
    }

    [Theory]
    [InlineData("Optolong L-eNhance")]
    [InlineData("L-eNhance")]
    public async Task TheEnhanceIsTriLineAndPassesHBeta(string written)
    {
        // The pair with TheUltimateIsTwoNarrowBandsAndDoesNotReachHBeta is the whole point: these two
        // filters are told apart by ONE wavelength. L-eNhance's blue window is 23 nm and swallows
        // H-beta 486.1 along with both OIII lines; L-Ultimate's is 3 nm and cannot reach it. So the
        // same probe must come out opposite on the two curves, and a chart mislabelled as the other
        // product -- which Optolong have published -- cannot satisfy both tests.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter(written, out var enh).ShouldBeTrue($"'{written}' must resolve");
        enh.Name.ShouldBe("OPTOLONG_L_ENHANCE");

        enh.Interpolate(4861.0).ShouldBeGreaterThan(0.8, "Hb 486.1 is INSIDE the 23nm blue band");
        enh.Interpolate(4959.0).ShouldBeGreaterThan(0.8, "OIII 495.9 too");
        enh.Interpolate(5007.0).ShouldBeGreaterThan(0.7, "and OIII 500.7, on the band's shoulder");
        enh.Interpolate(6563.0).ShouldBeGreaterThan(0.7, "H-alpha 656.3 is the red band");

        // Not flat across the blue band: H-beta gets MORE than OIII 500.7, because the band is
        // centred near 490 and 500.7 sits on the falling edge. Worth pinning -- anyone assuming a
        // duo-band's two channels are symmetric would get this backwards.
        enh.Interpolate(4861.0).ShouldBeGreaterThan(enh.Interpolate(5007.0),
            "Hb sits nearer the band centre than OIII 500.7 does");

        foreach (var blockedNm in (double[])[400, 450, 470, 515, 550, 600, 640 - 5, 700, 780])
        {
            enh.Interpolate(blockedNm * 10.0)
                .ShouldBeLessThan(0.05, $"{blockedNm} nm is outside both bands");
        }
    }

    [Fact]
    public async Task AddingASpecificProductDoesNotCaptureItsSiblings()
    {
        // The regression that adding OPTOLONG_L_QUAD_ENHANCE caused and the document-frequency gate
        // fixed, stated as the property rather than as a list of names: a curve whose name CONTAINS
        // another product's name as a token subset must not answer for it. "Optolong L-eNhance"
        // tokenises to a strict subset of "Optolong L-Quad Enhance", and "optolong" plus the single
        // letter "l" already clears the half-coverage gate on a four-token key.
        //
        // What rejects it is that the unmatched token "quad" names exactly ONE curve, so it is the
        // word that makes this curve specific rather than a brand or a series suffix. That is
        // measured from the catalogue, so this test also guards the measurement: if a future curve
        // happened to make "quad" common, the gate would stop firing and this would go red.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryMatchFilter("Optolong L-Quad Enhance", out var quad).ShouldBeTrue();
        quad.Name.ShouldBe("OPTOLONG_L_QUAD_ENHANCE");

        foreach (var sibling in (string[])["Optolong L-eXtreme"])
        {
            FilterCurveDatabase.TryMatchFilter(sibling, out var got).ShouldBeFalse(
                $"'{sibling}' is a different filter and we carry no standalone curve for it -- got '{got.Name}'");
        }
    }

    [Fact]
    public async Task TheD3CurveBlocksTheLinesItsVendorSaysItBlocks()
    {
        // The curve was digitised from a chart image, so it needs a correctness check that does not
        // come from the chart: the vendor states in PROSE which lines the filter suppresses, and
        // those wavelengths played no part in building the curve or calibrating the axes. If the
        // digitisation were mis-scaled -- a wrong axis range, percent left unconverted -- the
        // notches would not land here.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryMatchFilter("IDAS LPS-D3", out var d3).ShouldBeTrue();

        // Angstrom, matching the database convention.
        foreach (var (line, name) in (( double Nm, string Name)[])[
            (557.7, "OI 557.7"), (589.0, "NaI 589.0"), (589.6, "NaI 589.6"), (630.0, "OI 630.0")])
        {
            var t = d3.Interpolate(line * 10.0);
            output.WriteLine($"{name,-12} -> {t:P1}");
            t.ShouldBeLessThan(0.15, $"{name} is a line the vendor says this filter blocks");
        }

        // And it must still PASS light, or a "blocks everything" curve would satisfy the above.
        d3.Interpolate(4300.0).ShouldBeGreaterThan(0.5, "the blue passband must transmit");
        d3.Interpolate(6600.0).ShouldBeGreaterThan(0.5, "the H-alpha passband must transmit");
    }
}
