using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Smoke tests for <see cref="CieReferenceData"/>. The values are transcribed
/// from CIE 015:2018 (matching functions) and ISO/CIE 11664-2:2022 (D65) — any
/// typo would silently skew the camera→sRGB matrix derivation, so we check
/// a handful of standard identities that pin the numerical content.
/// </summary>
public class CieReferenceDataTests
{
    [Theory]
    [InlineData(nameof(CieReferenceData.X1931))]
    [InlineData(nameof(CieReferenceData.Y1931))]
    [InlineData(nameof(CieReferenceData.Z1931))]
    [InlineData(nameof(CieReferenceData.D65))]
    public void Curve_HasCanonicalShape(string curveName)
    {
        var curve = curveName switch
        {
            nameof(CieReferenceData.X1931) => CieReferenceData.X1931,
            nameof(CieReferenceData.Y1931) => CieReferenceData.Y1931,
            nameof(CieReferenceData.Z1931) => CieReferenceData.Z1931,
            nameof(CieReferenceData.D65)   => CieReferenceData.D65,
            _ => throw new System.InvalidOperationException(),
        };

        // 81 samples (380-780 nm @ 5 nm = (780-380)/5 + 1 = 81).
        curve.Count.ShouldBe(81);

        // Wavelengths span the visible range in Angstroms, monotonic increasing.
        curve.Wavelengths[0].ShouldBe(3800.0);
        curve.Wavelengths[^1].ShouldBe(7800.0);
        for (var i = 1; i < curve.Count; i++)
            curve.Wavelengths[i].ShouldBeGreaterThan(curve.Wavelengths[i - 1]);
    }

    [Fact]
    public void Y1931_PeaksAt555nm()
    {
        // Photopic V(lambda) peak is exactly 1.0 at 555 nm by definition of the
        // CIE 1931 normalisation. A typo in the YBar values would shift this.
        var yBar = CieReferenceData.Y1931;
        var peakIdx = 0;
        for (var i = 1; i < yBar.Count; i++)
            if (yBar.Throughputs[i] > yBar.Throughputs[peakIdx]) peakIdx = i;

        yBar.Wavelengths[peakIdx].ShouldBe(5550.0); // 555 nm = 5550 Å
        yBar.Throughputs[peakIdx].ShouldBe(1.0);
    }

    [Fact]
    public void D65_PeaksNear560nm()
    {
        // D65 SPD is normalised to 100 at 560 nm by ISO/CIE 11664-2 convention.
        var d65 = CieReferenceData.D65;
        var idx560 = 36; // (560 - 380) / 5
        d65.Wavelengths[idx560].ShouldBe(5600.0);
        d65.Throughputs[idx560].ShouldBe(100.0);
    }

    [Fact]
    public void X1931_PeakInRedRegion()
    {
        // X-bar's primary peak is at ~600 nm with value ~1.062 (long-wavelength
        // hump). Spot-check the peak magnitude.
        var xBar = CieReferenceData.X1931;
        var max = 0.0;
        foreach (var v in xBar.Throughputs) if (v > max) max = v;
        max.ShouldBe(1.0622, tolerance: 0.001);
    }
}
