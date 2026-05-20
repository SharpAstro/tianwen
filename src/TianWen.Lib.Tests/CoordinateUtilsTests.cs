using TianWen.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class CoordinateUtilsTests
{

    [Theory]
    [InlineData("05:23:34.5", 5.392916666666667d)]
    [InlineData("23:54:13.2", 23.903666666666666d)]
    [InlineData("23:59:59.9", 23.999972222222222d)]
    [InlineData("12:00:00", 12d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToHoursItReturnsHoursAsDouble(string hms, double expectedDegrees)
    {
        CoordinateUtils.HMSToHours(hms).ShouldBeInRange(expectedDegrees - 0.000000000000001d, expectedDegrees + 0.000000000000001d);
    }

    [Theory]
    [InlineData(5.392916666666667d, "05:23:34.500")]
    [InlineData(23.903666666666666d, "23:54:13.200")]
    [InlineData(23.999972222222222d, "23:59:59.900")]
    [InlineData(12d, "12:00:00")]
    [InlineData(0d, "00:00:00")]
    public void GivenHoursHenConvertToHMSItReturnsHMSAsString(double hours, string expectedHMS)
    {
        CoordinateUtils.HoursToHMS(hours).ShouldBe(expectedHMS);
    }

    [Theory]
    [InlineData("05:23:34.5", 80.89375d)]
    [InlineData("23:54:13.2", 358.555d)]
    [InlineData("23:59:59.9", 359.9995833333333d)]
    [InlineData("12:00:00", 180.0d)]
    [InlineData("12:00", 180.0d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToDegreesItReturnsDegreesAsDouble(string hms, double expectedDegrees)
    {
        CoordinateUtils.HMSToDegree(hms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData("+61:44:24", 61.74d)]
    [InlineData("-12:03:18", -12.055000000000001d)]
    [InlineData("+90:0:0", +90.0d)]
    [InlineData("-89:30:0", -89.5d)]
    [InlineData("-90:0:0", -90.0d)]
    [InlineData("-90:0", -90.0d)]
    [InlineData("0:0:0", 0.0d)]
    [InlineData("-08:11:11", -8.186388888888889d)]
    [InlineData("-00:38:16.5", -0.6379166666666667d)]
    public void GivenDMSWHenConvertToDegreesItReturnsDegreesAsDouble(string dms, double expectedDegrees)
    {
        CoordinateUtils.DMSToDegree(dms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData(61.74d, "+61:44:24")]
    [InlineData(-12.055000000000001d, "-12:03:18")]
    [InlineData(+90.0d, "+90:00:00")]
    [InlineData(-89.5d, "-89:30:00")]
    [InlineData(-90.0d, "-90:00:00")]
    [InlineData(0.0d, "+00:00:00")]
    [InlineData(-8.186388888888889d, "-08:11:11")]
    [InlineData(-31.314123299999999, "-31:18:50.844")]
    [InlineData(+12.8d, "+12:48:00")]
    public void GivenDegreesWhenConvertToDMSItReturnsDMSAsString(double degrees, string expectedDMS)
    {
        CoordinateUtils.DegreesToDMS(degrees).ShouldBe(expectedDMS);
    }

    // Reference cases: known sensor/scope pairs from current TianWen fixtures.
    //  - IMX533 (3.76 μm) on a 600 mm scope -> ~1.293 "/px
    //  - IMX585 (2.9 μm) bin 2 on a 540 mm scope -> ~2.215 "/px (effective 5.8 μm)
    //  - DSLR 4.3 μm on 200 mm -> ~4.435 "/px
    [Theory]
    [InlineData(3.76, 600, 1.292)]
    [InlineData(5.80, 540, 2.215)]
    [InlineData(4.30, 200, 4.435)]
    public void PixelScaleArcsec_KnownInputs_RoundTrip(double pixSizeUm, double focalLenMm, double expectedArcsec)
    {
        var px = CoordinateUtils.PixelScaleArcsec(pixSizeUm, focalLenMm);
        px.ShouldBe(expectedArcsec, tolerance: 0.001);

        // Inverse round-trip: feeding the result back to FocalLengthMm must
        // recover the input focal length within rounding.
        var recovered = CoordinateUtils.FocalLengthMm(pixSizeUm, px);
        recovered.ShouldBe(focalLenMm, tolerance: 1e-6);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(3.76, 0)]
    [InlineData(-1, 600)]
    [InlineData(3.76, -1)]
    public void PixelScaleArcsec_NonPositiveInputs_ReturnNaN(double pixSizeUm, double focalLenMm)
    {
        CoordinateUtils.PixelScaleArcsec(pixSizeUm, focalLenMm).ShouldBe(double.NaN);
    }

    [Theory]
    [InlineData(0, 1.292)]
    [InlineData(3.76, 0)]
    [InlineData(-1, 1.292)]
    [InlineData(3.76, -1)]
    public void FocalLengthMm_NonPositiveInputs_ReturnNaN(double pixSizeUm, double pxScaleArcsec)
    {
        CoordinateUtils.FocalLengthMm(pixSizeUm, pxScaleArcsec).ShouldBe(double.NaN);
    }

    [Fact]
    public void PropagatePm_ZeroPm_ReturnsInputUnchanged()
    {
        var (ra, dec) = CoordinateUtils.PropagatePm(12.345, -45.678, 0.0, 0.0, 26.0);
        ra.ShouldBe(12.345);
        dec.ShouldBe(-45.678);
    }

    [Fact]
    public void PropagatePm_ZeroDt_ReturnsInputUnchanged()
    {
        var (ra, dec) = CoordinateUtils.PropagatePm(12.345, -45.678, 100.0, -50.0, 0.0);
        ra.ShouldBe(12.345);
        dec.ShouldBe(-45.678);
    }

    [Fact]
    public void PropagatePm_BarnardsStarDecDriftMatchesHandComputation()
    {
        // Barnard's Star (TYC 0425-2502-1, HIP 87937), J2000 from Tycho-2:
        // pmRA = -798.8 mas/yr (RA*cos(Dec) form, source field 4)
        // pmDec = +10277.3 mas/yr
        // RA = 17.9636 h, Dec = +4.6933 deg.
        // For dt = 26 yr, ΔDec = 10277.3 * 26 / 3.6e6 = 0.07422972... deg.
        var (_, dec) = CoordinateUtils.PropagatePm(17.9636, 4.6933, -798.8, 10277.3, 26.0);
        var deltaDec = dec - 4.6933;
        deltaDec.ShouldBe(10277.3 * 26.0 / 3.6e6, tolerance: 1e-9);
    }

    [Fact]
    public void PropagatePm_RaDriftScalesByInverseCosDec()
    {
        // pmRA is published as RA*cos(Dec), so the actual ΔRA in hours needs
        // an extra /cos(Dec) factor. Verify the unwind at Dec = 60 deg
        // (cos = 0.5 -> ΔRA doubles vs the same pmRA at the equator).
        var pmRa = 1000.0;
        var dt = 1.0;
        var (raEquator, _)  = CoordinateUtils.PropagatePm(0, 0,  pmRa, 0, dt);
        var (raHighDec, _) = CoordinateUtils.PropagatePm(0, 60, pmRa, 0, dt);
        // ΔRA(60°) should be exactly ΔRA(0°) / cos(60°) = ΔRA(0°) * 2.
        var ratio = raHighDec / raEquator;
        ratio.ShouldBe(2.0, tolerance: 1e-12);
    }

    [Fact]
    public void PropagatePm_RoundTripsThroughForwardThenBackward()
    {
        var (raMid, decMid) = CoordinateUtils.PropagatePm(11.196, -61.35, -20.0, 15.0, 26.0);
        var (raBack, decBack) = CoordinateUtils.PropagatePm(raMid, decMid, -20.0, 15.0, -26.0);
        // Note: this is approximate -- cos(Dec) varies slightly between the
        // start and midpoint epochs, so the inverse propagation isn't bit-exact
        // for non-equatorial declinations. At a typical Dec the error is sub-uas.
        raBack.ShouldBe(11.196, tolerance: 1e-6);
        decBack.ShouldBe(-61.35, tolerance: 1e-9);
    }
}