using nom.tam.fits;
using nom.tam.util;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the FITS header parse against the conventions of FOREIGN calibration masters (Astro Pixel
    /// Processor's MD-IG / MF-IG files), which differ from N.I.N.A.'s in two ways that used to silently
    /// drop an otherwise-perfect master out of dataset calibration selection:
    /// <list type="number">
    /// <item><b>CFAIMAGE holds the Bayer PATTERN string</b> ("RGGB"), not a logical T/F. Read as a
    /// boolean it yields <c>false</c>, which forced <see cref="SensorType.Monochrome"/> and made
    /// <c>DimensionCompatible</c> reject the master vs an RGGB light.</item>
    /// <item><b>GAIN is a float card</b> ("121.0"). <c>GetIntValue</c> won't coerce it (returns -1), so
    /// the master's gain read as "unknown" and lost the gain-scored match.</item>
    /// </list>
    /// Both are exercised end-to-end through <see cref="Image.TryReadFitsHeader"/> (the exact path the
    /// archive scan uses), plus a N.I.N.A.-style control so the fix can't regress normal files.
    /// </summary>
    [Collection("Imaging")]
    public class AppMasterHeaderParseTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "appmaster-" + Guid.NewGuid().ToString("N")[..8]);

        public AppMasterHeaderParseTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Theory]
        [InlineData("RGGB", true)]   // APP: Bayer pattern string => it IS a CFA image
        [InlineData("GRBG", true)]
        [InlineData("GBRG", true)]
        [InlineData("BGGR", true)]
        [InlineData("T", true)]
        [InlineData("t", true)]
        [InlineData("1", true)]
        [InlineData("True", true)]
        [InlineData("F", false)]
        [InlineData("0", false)]
        [InlineData("False", false)]
        [InlineData("NONE", false)]  // unknown sentinel keeps the old safe not-CFA default
        [InlineData("2", false)]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        public void ParseCfaImageFlag_MapsBooleanAndPatternForms(string? raw, bool? expected)
        {
            Image.ParseCfaImageFlag(raw).ShouldBe(expected);
        }

        [Fact]
        public void AppStyleMasterDark_ParsesAsRggbMasterWithGain()
        {
            // A foreign APP master dark: IMAGETYP=MASTERDARK (no FRAMETYP), CFAIMAGE as the pattern
            // STRING, and GAIN as a FLOAT -- the exact shape that used to parse Monochrome / gain -1.
            var path = Path.Combine(_dir, "MD-IG_appstyle.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "MASTERDARK", "");
                h.AddValue("BAYERPAT", "RGGB", "");
                h.AddValue("CFAIMAGE", "RGGB", "");   // string pattern, not a boolean
                h.AddValue("GAIN", 121.0, "");        // float card
                h.AddValue("OFFSET", 20, "");
                h.AddValue("CCD-TEMP", -4.9, "");
                h.AddValue("EXPTIME", 60.0, "");
                h.AddValue("INSTRUME", "ZWO ASI533MC Pro", "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.FrameType.ShouldBe(FrameType.Dark);
            info.IsMaster.ShouldBeTrue();
            info.Meta.SensorType.ShouldBe(SensorType.RGGB);   // was Monochrome before the CFAIMAGE fix
            info.Meta.Gain.ShouldBe((short)121);              // was -1 before the float-GAIN fix
        }

        [Fact]
        public void NinaStyleRawDark_StillParses_NotRegressed()
        {
            // N.I.N.A. raw dark: IMAGETYP=DARK, no CFAIMAGE, GAIN as an INT. Must be unaffected by the
            // foreign-master parse tolerance -- RGGB from BAYERPAT, integer gain, not a master.
            var path = Path.Combine(_dir, "nina_raw_dark.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "DARK", "");
                h.AddValue("BAYERPAT", "RGGB", "");
                h.AddValue("GAIN", 121, "");          // int card
                h.AddValue("OFFSET", 20, "");
                h.AddValue("CCD-TEMP", -10.0, "");
                h.AddValue("EXPTIME", 120.0, "");
                h.AddValue("INSTRUME", "ZWO ASI533MC Pro", "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.FrameType.ShouldBe(FrameType.Dark);
            info.IsMaster.ShouldBeFalse();
            info.Meta.SensorType.ShouldBe(SensorType.RGGB);
            info.Meta.Gain.ShouldBe((short)121);
        }

        [Theory]
        [InlineData(false, "RGGB", SensorType.Monochrome)] // explicit not-CFA beats a stale BAYERPAT
        [InlineData(true, "RGGB", SensorType.RGGB)]
        public void LogicalCfaImageCard_IsHonoured_OverStaleBayerpat(bool cfa, string bayerpat, SensorType expected)
        {
            // A genuine FITS LOGICAL card (unquoted T/F, the ASCOM form) is written via the bool
            // AddValue overload -- GetStringValue returns null for it, so the parse must fall back
            // to the boolean read instead of silently deferring to BAYERPAT.
            var path = Path.Combine(_dir, $"logical_cfa_{cfa}.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "LIGHT", "");
                h.AddValue("CFAIMAGE", cfa, "");      // logical card, not a string
                h.AddValue("BAYERPAT", bayerpat, "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.SensorType.ShouldBe(expected);
        }

        [Fact]
        public void CfaImageAsSolePatternSource_ParsesRggbWithOffset()
        {
            // No BAYERPAT/COLORTYP at all: the pattern must come from CFAIMAGE alone ('GRBG' maps
            // to RGGB + X offset 1). Guards the cfaPattern slot in the FromFITSValue candidates.
            var path = Path.Combine(_dir, "cfa_sole_source.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "MASTERDARK", "");
                h.AddValue("CFAIMAGE", "GRBG", "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.SensorType.ShouldBe(SensorType.RGGB);
            info.Meta.BayerOffsetX.ShouldBe(1);
            info.Meta.BayerOffsetY.ShouldBe(0);
        }

        [Fact]
        public void BooleanStringCfaImage_WithNoPattern_StaysMonochrome()
        {
            // CFAIMAGE='T' (quoted string) declares CFA but carries no pattern; it must NOT leak
            // into the pattern switch (which would yield SensorType.Unknown -> spurious debayer).
            var path = Path.Combine(_dir, "cfa_bool_string.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "LIGHT", "");
                h.AddValue("CFAIMAGE", "T", "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.SensorType.ShouldBe(SensorType.Monochrome);
        }

        [Fact]
        public void ThreeChannelImage_WithStaleCfaPattern_ParsesAsColor()
        {
            // An already-debayered 3-plane integration that still carries its CFA provenance tags
            // (APP keeps CFAIMAGE/BAYERPAT on RGB outputs) is Color, never RGGB -- classifying it
            // as a mosaic would run a real debayer downstream and discard two of three channels.
            var path = Path.Combine(_dir, "rgb_stale_cfa.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "LIGHT", "");
                h.AddValue("CFAIMAGE", "RGGB", "");
                h.AddValue("BAYERPAT", "RGGB", "");
            }, channelCount: 3);

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.ChannelCount.ShouldBe(3);
            info.Meta.SensorType.ShouldBe(SensorType.Color);
        }

        [Fact]
        public void NonFiniteGainCard_ReadsAsUnknown_NotZero()
        {
            // Double.Parse accepts "NaN"; (short)Math.Round(NaN) would be 0 -- a plausible real
            // gain. A non-finite GAIN card must fall back to the -1 unknown sentinel instead.
            var path = Path.Combine(_dir, "gain_nan.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "LIGHT", "");
                h.AddValue("GAIN", "NaN", "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Gain.ShouldBe((short)-1);
        }

        [Fact]
        public void FloatOffsetCard_ParsesLikeFloatGain()
        {
            // The float-card tolerance applies to the whole OFFSET/BLKLEVEL/CAMOFFS chain, not just
            // GAIN -- an APP-style 'OFFSET = 20.0' must read 20, not fall to the -1 sentinel.
            var path = Path.Combine(_dir, "offset_float.fits");
            WriteFits(path, extra: h =>
            {
                h.AddValue("IMAGETYP", "MASTERDARK", "");
                h.AddValue("OFFSET", 20.0, "");
            });

            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
            info.ShouldNotBeNull();
            info.Meta.Offset.ShouldBe(20);
        }

        /// <summary>Writes a minimal float FITS with a valid DATE-OBS (so the header read doesn't
        /// trip the missing-date guard) plus whatever cards <paramref name="extra"/> adds. A
        /// <paramref name="channelCount"/> above 1 writes a 3-axis cube (jagged planes), matching
        /// the shape of a debayered RGB integration.</summary>
        private static void WriteFits(string path, Action<Header> extra, int channelCount = 1)
        {
            object data = channelCount == 1
                ? new float[4, 4]
                : Enumerable.Range(0, channelCount).Select(_ => new float[4, 4]).ToArray();
            var fits = new Fits();
            var hdu = FitsFactory.HDUFactory(data);
            hdu.Header.AddValue("DATE-OBS", FitsDate.GetFitsDateString(new DateTime(2025, 12, 20, 5, 27, 6, DateTimeKind.Utc)), "UT");
            extra(hdu.Header);
            fits.AddHDU(hdu);
            using var bf = new BufferedFile(path, FileAccess.ReadWrite, FileShare.Read, 16 * 2880);
            fits.Write(bf);
            bf.Flush();
            bf.Close();
        }
    }
}
