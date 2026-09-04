using nom.tam.fits;
using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The FITS standard centres the first pixel at (1, 1); TianWen's <see cref="WCS"/> lives in the 0-based
    /// frame of a detected centroid. The header is the one place the two meet, and until 2026-09-05 the
    /// numbers crossed it unchanged under a "1-based" comment: every file TianWen solved was one pixel off in
    /// astropy, PixInsight, Siril and ASTAP, and every third-party WCS one pixel off in TianWen. Found by
    /// matching detected peaks to Gaia DR3 on four fields: a constant (+0.95, +0.91) px offset that a
    /// 2.5 px match tolerance had been absorbing, and that put 81 percent of real matches in the 1 to 2 px
    /// ring where quantisation alone allows none beyond 0.71.
    /// </summary>
    [Collection("Astrometry")]
    public class WcsPixelOriginTests
    {
        private const double RaDeg = 82.5;
        private const double DecDeg = -60.0;
        private const double ScaleDeg = 1.6e-3;

        private static WCS Linear(double crpix1, double crpix2) => new WCS(RaDeg / 15.0, DecDeg)
        {
            CRPix1 = crpix1,
            CRPix2 = crpix2,
            CD1_1 = -ScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = ScaleDeg,
        };

        /// <summary>A header as a compliant third-party solver writes it: CRPIX in the standard's 1-based frame.</summary>
        private static Header CompliantHeader(double crpix1, double crpix2)
        {
            var header = new Header();
            header.AddCard(new HeaderCard("CTYPE1", "RA---TAN", null));
            header.AddCard(new HeaderCard("CTYPE2", "DEC--TAN", null));
            header.AddCard(new HeaderCard("CRVAL1", RaDeg, null));
            header.AddCard(new HeaderCard("CRVAL2", DecDeg, null));
            header.AddCard(new HeaderCard("CRPIX1", crpix1, null));
            header.AddCard(new HeaderCard("CRPIX2", crpix2, null));
            header.AddCard(new HeaderCard("CD1_1", -ScaleDeg, null));
            header.AddCard(new HeaderCard("CD1_2", 0.0, null));
            header.AddCard(new HeaderCard("CD2_1", 0.0, null));
            header.AddCard(new HeaderCard("CD2_2", ScaleDeg, null));
            return header;
        }

        [Fact]
        public void ACompliantHeaderPutsTheReferenceSkyOnTheZeroBasedPixel()
        {
            // CRPIX = (1, 1) names the centre of the first pixel in FITS, which is index [0, 0] in memory: the
            // sky at CRVAL must project onto (0, 0), where a centroid of a star on that pixel would land.
            var wcs = WCS.FromHeader(CompliantHeader(1.0, 1.0)).ShouldNotBeNull();

            wcs.CRPix1.ShouldBe(0.0);
            wcs.CRPix2.ShouldBe(0.0);
            var px = wcs.SkyToPixel(wcs.CenterRA, wcs.CenterDec).ShouldNotBeNull();
            px.X.ShouldBe(0.0, 1e-9);
            px.Y.ShouldBe(0.0, 1e-9);
        }

        [Fact]
        public void TheHeaderCarriesCrpixPlusOneAndTheMarker()
        {
            var header = new Header();
            Linear(1532.0, 1554.0).WriteToHeader(header);

            header.GetDoubleValue("CRPIX1").ShouldBe(1533.0);
            header.GetDoubleValue("CRPIX2").ShouldBe(1555.0);
            header.GetIntValue(WCS.PixelOriginCard, -1).ShouldBe(1);
        }

        [Fact]
        public void WriteThenReadIsTheIdentityOnCrpix()
        {
            var header = new Header();
            var original = Linear(1532.53, 1554.54);
            original.WriteToHeader(header);

            var roundTripped = WCS.FromHeader(header).ShouldNotBeNull();
            roundTripped.CRPix1.ShouldBe(original.CRPix1, 1e-9);
            roundTripped.CRPix2.ShouldBe(original.CRPix2, 1e-9);
        }

        [Fact]
        public void ATianWenMasterWrittenBeforeTheMarkerIsReadVerbatim()
        {
            // Before the marker existed the 0-based numbers were written unchanged, so on such a file the
            // header value IS the in-memory value. STACK_N is what says TianWen integrated it.
            var header = CompliantHeader(1532.5, 1554.5);
            header.AddCard(new HeaderCard("STACK_N", 128, "Number of frames combined into this master"));

            var wcs = WCS.FromHeader(header).ShouldNotBeNull();
            wcs.CRPix1.ShouldBe(1532.5);
            wcs.CRPix2.ShouldBe(1554.5);
        }

        [Fact]
        public void ATianWenSwcreateWithoutTheMarkerIsAlsoLegacy()
        {
            var header = CompliantHeader(1532.5, 1554.5);
            header.AddCard(new HeaderCard("SWCREATE", "TianWen.Imaging.Stacking.Integrator", null));

            WCS.FromHeader(header).ShouldNotBeNull().CRPix1.ShouldBe(1532.5);
        }

        [Fact]
        public void TheMarkerBeatsAuthorship()
        {
            // A master TianWen writes from now on is compliant however it is authored, so the shift applies.
            var header = CompliantHeader(1533.0, 1555.0);
            header.AddCard(new HeaderCard("STACK_N", 128, null));
            header.AddCard(new HeaderCard(WCS.PixelOriginCard, 1, null));

            var wcs = WCS.FromHeader(header).ShouldNotBeNull();
            wcs.CRPix1.ShouldBe(1532.0);
            wcs.CRPix2.ShouldBe(1554.0);
        }

        [Fact]
        public void AForeignFileWithoutTheMarkerIsCompliant()
        {
            var header = CompliantHeader(1533.0, 1555.0);
            header.AddCard(new HeaderCard("SWCREATE", "N.I.N.A. 3.1", null));

            WCS.FromHeader(header).ShouldNotBeNull().CRPix1.ShouldBe(1532.0);
        }

        [Fact]
        public void AnAstapIniIsInTheFitsFrame()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tianwen-wcs-origin-{Guid.NewGuid():N}.ini");
            try
            {
                File.WriteAllLines(path,
                [
                    "[astap]",
                    "PLTSOLVD=T",
                    $"CRVAL1={RaDeg}",
                    $"CRVAL2={DecDeg}",
                    "CRPIX1=1533.0",
                    "CRPIX2=1555.0",
                    $"CD1_1={-ScaleDeg}",
                    "CD1_2=0",
                    "CD2_1=0",
                    $"CD2_2={ScaleDeg}",
                ]);

                var wcs = WCS.FromAstapIniFile(path).ShouldNotBeNull();
                wcs.HasCDMatrix.ShouldBeTrue();
                wcs.CRPix1.ShouldBe(1532.0);
                wcs.CRPix2.ShouldBe(1554.0);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
