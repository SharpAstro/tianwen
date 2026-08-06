using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;
using nom.tam.fits;
using nom.tam.util;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins that an Astro Pixel Processor integration is recognised as a stacking PRODUCT and never
    /// re-ingested as a raw sub.
    ///
    /// <para>Measured on the real archive, not imagined: APP marks calibration masters with
    /// <c>IMAGETYP='MASTERFLAT'</c> / <c>'MASTERDARK'</c> / <c>'MASTERBIAS'</c> /
    /// <c>'MASTERDARKFLAT'</c>, which <see cref="FrameType.IsMasterFITSValue"/> already catches. A
    /// LIGHT integration is the one that slips through: APP keeps <c>IMAGETYP='LIGHT'</c>, adds its
    /// own <c>INTEGRAT</c> card, and then copies the reference sub's header verbatim -- including
    /// <c>SWCREATE='N.I.N.A. ...'</c>. So the master flag says no, and
    /// <see cref="IntegrationFitsWriter.IsTianWenProduct"/> says no, and the frame reads as a raw
    /// sub. <c>NUMFRAME</c> is on all five APP product kinds and is what closes it.</para>
    /// </summary>
    [Collection("Imaging")]
    public class FitsIntegrationMarkerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fitsint-" + Guid.NewGuid().ToString("N")[..8]);

        public FitsIntegrationMarkerTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// Writes a minimal 4x4 float frame carrying the supplied extra cards, mirroring the header
        /// shape of a real APP product.
        /// </summary>
        private string WriteFrame(string name, params (string Key, object Value)[] cards)
        {
            var path = Path.Combine(_dir, name);
            var data = new float[4, 4];
            var fits = new Fits();
            var hdu = FitsFactory.HDUFactory(data);
            foreach (var (key, value) in cards)
            {
                switch (value)
                {
                    case int i: hdu.AddValue(key, i, ""); break;
                    case double d: hdu.AddValue(key, d, ""); break;
                    default: hdu.AddValue(key, value.ToString(), ""); break;
                }
            }
            fits.AddHDU(hdu);
            using (var bf = new BufferedFile(path, FileAccess.ReadWrite, FileShare.None))
            {
                fits.Write(bf);
                bf.Flush();
            }
            return path;
        }

        [Fact]
        public void AppLightIntegration_IsRecognisedAsAProduct_ViaNumframe()
        {
            // The exact shape read off Vela_SNR_Panel_4-Multi-NB-Oxygen_III.fits.
            var path = WriteFrame("app-light-integration.fits",
                ("IMAGETYP", "LIGHT"),
                ("INTEGRAT", "Integration"),
                ("NUMFRAME", 140),
                ("EXPTIME", 8400.0),
                ("EXPOSURE", 60.0),
                ("SOFTWARE", "Astro Pixel Processor by Aries Productions"),
                ("SWCREATE", "N.I.N.A. 3.2.0.9001 (x64)"));

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();

            // The two pre-existing markers genuinely do NOT fire -- that is the bug, kept explicit
            // here so a future change cannot "fix" this test by making one of them true instead.
            info.IsMaster.ShouldBeFalse();
            IntegrationFitsWriter.IsTianWenProduct(info.Meta.SWCreator).ShouldBeFalse();
            info.FrameType.ShouldBe(FrameType.Light);

            info.StackedFrameCount.ShouldBe(140);
        }

        [Theory]
        [InlineData("MASTERFLAT", 40)]
        [InlineData("MASTERDARK", 60)]
        [InlineData("MASTERBIAS", 144)]
        [InlineData("MASTERDARKFLAT", 50)]
        public void AppCalibrationMasters_CarryNumframeToo(string imageType, int numFrame)
        {
            var path = WriteFrame($"app-{imageType}.fits",
                ("IMAGETYP", imageType),
                ("NUMFRAME", numFrame),
                ("SOFTWARE", "Astro Pixel Processor by Aries Productions"));

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            // These were already caught by the master flag; NUMFRAME is belt and braces.
            info.IsMaster.ShouldBeTrue();
            info.StackedFrameCount.ShouldBe(numFrame);
        }

        [Fact]
        public void RawSub_HasNoStackedFrameCount()
        {
            var path = WriteFrame("raw-sub.fits",
                ("IMAGETYP", "LIGHT"),
                ("EXPTIME", 60.0),
                ("EXPOSURE", 60.0),
                ("SWCREATE", "N.I.N.A. 3.2.0.9001 (x64)"));

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.IsMaster.ShouldBeFalse();
            info.StackedFrameCount.ShouldBe(0);
        }

        [Fact]
        public void StackN_StillWins_ForOurOwnMasters()
        {
            // TianWen's own products keep working off STACK_N; NUMFRAME is a fallback, not a
            // replacement, so a file carrying both reports STACK_N.
            var path = WriteFrame("tianwen-master.fits",
                ("IMAGETYP", "LIGHT"),
                ("STACK_N", 37),
                ("NUMFRAME", 999));

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.StackedFrameCount.ShouldBe(37);
        }
    }
}
