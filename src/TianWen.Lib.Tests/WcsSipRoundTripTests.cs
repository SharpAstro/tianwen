using nom.tam.fits;
using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class WcsSipRoundTripTests
{
    /// <summary>
    /// Helper: build a WCS centred on a real-looking target with a SIP order-2 polynomial,
    /// matching the shape <see cref="SipPolynomial.Fit"/> emits.
    /// </summary>
    private static WCS BuildSampleSipWcs(bool includeInverse = true)
    {
        var a = new double[3, 3];
        var b = new double[3, 3];
        // Order-2 SIP coefficients sized to the magnitude `solve-field` typically emits
        // for hobby-grade Newtonians (corner residuals ~1 px on a 3000-px master).
        a[1, 0] = 1.234e-6;
        a[0, 1] = -2.345e-6;
        a[2, 0] = 1.5e-7;
        a[1, 1] = -2.5e-7;
        a[0, 2] = 0.75e-7;
        b[1, 0] = -2.0e-6;
        b[0, 1] = 1.0e-6;
        b[2, 0] = -1.2e-7;
        b[1, 1] = 2.4e-7;
        b[0, 2] = -1.0e-7;

        double[,]? ap = null, bp = null;
        if (includeInverse)
        {
            ap = (double[,])a.Clone();
            bp = (double[,])b.Clone();
            // Inverse coefficients are conceptually the negatives at first order (Newton step),
            // close enough to make the test interesting; for round-trip we only care that the
            // values survive the FITS header.
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                {
                    ap[i, j] *= -1.0;
                    bp[i, j] *= -1.0;
                }
        }

        return new WCS(CenterRA: 11.1955, CenterDec: 28.8345)
        {
            CRPix1 = 1500.5,
            CRPix2 = 1000.5,
            CD1_1 = -3.5e-4,
            CD1_2 = 2.5e-6,
            CD2_1 = 2.5e-6,
            CD2_2 = 3.5e-4,
            SipOrder = 2,
            SipA = a,
            SipB = b,
            SipAP = ap,
            SipBP = bp,
        };
    }

    /// <summary>
    /// Write a SIP-bearing WCS to a FITS header, parse it back, assert the
    /// coefficients survive bit-for-bit (FITS doubles round-trip exactly).
    /// </summary>
    [Fact]
    public void GivenSipWcsThenWriteReadRoundTripPreservesCoefficients()
    {
        var original = BuildSampleSipWcs();
        var header = new Header();
        original.WriteToHeader(header);

        var roundtripped = WCS.FromHeader(header);
        roundtripped.ShouldNotBeNull();
        var rt = roundtripped.Value;

        rt.HasCDMatrix.ShouldBeTrue();
        rt.HasSip.ShouldBeTrue();
        rt.HasInverseSip.ShouldBeTrue();
        rt.SipOrder.ShouldBe(original.SipOrder);

        for (var i = 0; i <= 2; i++)
        {
            for (var j = 0; j <= 2 - i; j++)
            {
                if ((i | j) == 0) continue;
                rt.SipA![i, j].ShouldBe(original.SipA![i, j]);
                rt.SipB![i, j].ShouldBe(original.SipB![i, j]);
                rt.SipAP![i, j].ShouldBe(original.SipAP![i, j]);
                rt.SipBP![i, j].ShouldBe(original.SipBP![i, j]);
            }
        }
    }

    /// <summary>
    /// `solve-field` headers usually emit forward-only SIP. Confirm we read
    /// them and that SkyToPixel still works via the Newton-step fallback.
    /// </summary>
    [Fact]
    public void GivenForwardOnlySipThenInverseFlagsAreFalseButSkyToPixelWorks()
    {
        var original = BuildSampleSipWcs(includeInverse: false);
        var header = new Header();
        original.WriteToHeader(header);

        var rt = WCS.FromHeader(header)!.Value;
        rt.HasSip.ShouldBeTrue();
        rt.HasInverseSip.ShouldBeFalse();

        // Forward + inverse round-trip at a non-CRPIX pixel.
        var sky = rt.PixelToSky(2500, 2000);
        sky.ShouldNotBeNull();
        var pix = rt.SkyToPixel(sky.Value.RA, sky.Value.Dec);
        pix.ShouldNotBeNull();
        pix.Value.X.ShouldBe(2500.0, 1e-4);
        pix.Value.Y.ShouldBe(2000.0, 1e-4);
    }

    /// <summary>
    /// With both forward and inverse SIP available, PixelToSky → SkyToPixel must
    /// reproduce the input pixel to high precision at all four corners of a
    /// 3000×2000 frame — the canonical test of distortion-aware coordinate ops.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3000, 1)]
    [InlineData(1, 2000)]
    [InlineData(3000, 2000)]
    [InlineData(1500.5, 1000.5)]
    public void GivenFullSipWcsThenPixelSkyRoundTripIsAccurate(double x, double y)
    {
        var wcs = BuildSampleSipWcs();

        var sky = wcs.PixelToSky(x, y);
        sky.ShouldNotBeNull();
        var pix = wcs.SkyToPixel(sky.Value.RA, sky.Value.Dec);
        pix.ShouldNotBeNull();

        // With consistent forward + inverse polynomials the round-trip is bounded by
        // the polynomial inversion error (i.e. how well AP/BP invert A/B). The
        // synthetic coefficients here are tiny so the residual is well under 0.01 px.
        pix.Value.X.ShouldBe(x, 0.01);
        pix.Value.Y.ShouldBe(y, 0.01);
    }

    /// <summary>
    /// Linear-only WCS continues to round-trip with no -SIP suffix and no
    /// stray SIP fields — ensures we did not regress the existing path.
    /// </summary>
    [Fact]
    public void GivenLinearOnlyWcsThenHeaderCarriesNoSipKeywords()
    {
        var wcs = new WCS(11.1955, 28.8345)
        {
            CRPix1 = 1500.5,
            CRPix2 = 1000.5,
            CD1_1 = -3.5e-4,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = 3.5e-4,
        };
        var header = new Header();
        wcs.WriteToHeader(header);

        header.GetStringValue("CTYPE1").ShouldBe("RA---TAN");
        header.GetStringValue("CTYPE2").ShouldBe("DEC--TAN");
        header.GetIntValue("A_ORDER", -42).ShouldBe(-42);   // sentinel = absent
        header.GetIntValue("B_ORDER", -42).ShouldBe(-42);

        var rt = WCS.FromHeader(header)!.Value;
        rt.HasSip.ShouldBeFalse();
        rt.HasInverseSip.ShouldBeFalse();
        rt.SipOrder.ShouldBe(0);
    }

    /// <summary>
    /// Malformed SIP header (CTYPE claims SIP but no A_ORDER): WCS.FromHeader
    /// returns the linear WCS gracefully rather than producing a broken solution.
    /// </summary>
    [Fact]
    public void GivenSipCtypeButNoOrderThenReadsLinearOnly()
    {
        var header = new Header();
        header.AddCard(new HeaderCard("CTYPE1", "RA---TAN-SIP", null));
        header.AddCard(new HeaderCard("CTYPE2", "DEC--TAN-SIP", null));
        header.AddCard(new HeaderCard("CRVAL1", 167.93, null));
        header.AddCard(new HeaderCard("CRVAL2", 28.83, null));
        header.AddCard(new HeaderCard("CRPIX1", 1500.5, null));
        header.AddCard(new HeaderCard("CRPIX2", 1000.5, null));
        header.AddCard(new HeaderCard("CD1_1", -3.5e-4, null));
        header.AddCard(new HeaderCard("CD1_2", 0.0, null));
        header.AddCard(new HeaderCard("CD2_1", 0.0, null));
        header.AddCard(new HeaderCard("CD2_2", 3.5e-4, null));

        var rt = WCS.FromHeader(header)!.Value;
        rt.HasCDMatrix.ShouldBeTrue();
        rt.HasSip.ShouldBeFalse();
        rt.SipOrder.ShouldBe(0);
    }
}
