using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="SensorTypeEx.FromFITSValue"/>: every pattern string decodes onto the ASCOM
    /// canonical model (RGGB base + offsets, the same model
    /// <see cref="SensorTypeEx.GetBayerPatternMatrix"/> reads back), file offsets COMPOSE with the
    /// pattern's own shift mod 2, and MaxIm DL's <c>BAYERPAT='VALID'</c> -- an assertion that a
    /// Bayer array exists, not a pattern name -- resolves to the base and lets XBAYROFF/YBAYROFF
    /// carry the pattern. Before that mapping an entire iTelescope OSC set read as
    /// <see cref="SensorType.Unknown"/> and would have stacked its CFA mosaic as mono.
    /// </summary>
    public class SensorTypeTests
    {
        [Theory]
        [InlineData("RGGB", 0, 0)]
        [InlineData("GRBG", 1, 0)]
        [InlineData("GBRG", 0, 1)]
        [InlineData("BGGR", 1, 1)]
        // FITS string values arrive space-padded; the decode must not care.
        [InlineData("RGGB    ", 0, 0)]
        [InlineData("bggr", 1, 1)]
        public void FromFITSValue_DecodesThePatternOntoTheRggbBase(string pattern, int expectedX, int expectedY)
            => SensorType.FromFITSValue(null, 1, 0, 0, pattern)
                .ShouldBe((SensorType.RGGB, expectedX, expectedY));

        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(1, 0, 1, 0)]
        [InlineData(0, 1, 0, 1)]
        [InlineData(1, 1, 1, 1)]
        public void FromFITSValue_ValidMeansBaseRggb_TheOffsetsCarryThePattern(int fileX, int fileY, int expectedX, int expectedY)
            => SensorType.FromFITSValue(null, 1, fileX, fileY, "VALID")
                .ShouldBe((SensorType.RGGB, expectedX, expectedY));

        [Fact]
        public void FromFITSValue_FileOffsetsComposeWithThePatternShiftMod2()
            // GRBG carries shift (1,0); a file offset of (1,0) on top wraps back to the base.
            => SensorType.FromFITSValue(null, 1, 1, 0, "GRBG")
                .ShouldBe((SensorType.RGGB, 0, 0));

        [Theory]
        // The shape the archive's oldest data actually has, measured 2026-09-16 over 68 directories
        // and four bodies: SharpCap before 4.0 writes the pattern SHIFTED and the legacy offset card
        // beside it, and the two compose back to RGGB. Its own card says so ("NOTE: Use RGGB on some
        // software (eg PixInsight)"), and the pixels agree -- on a 2021 ASI533MC Pro frame the two
        // greens differ by 0.0 ADU under the RGGB pairing against 684 ADU under the declared GBRG.
        // Honouring either card alone gets every one of those frames wrong, which is why this is a
        // case rather than a comment.
        [InlineData("GBRG", 0, 1)]      // ASI294MC, ASI462MC, ASI533MC Pro: 52 directories
        [InlineData("GRBG", 1, 0)]      // ASI462MC, the same night's other sessions: 16
        public void FromFITSValue_OldSharpCapsShiftedPatternPlusItsLegacyOffsetIsPlainRggb(
            string pattern, int fileOffsetX, int fileOffsetY)
            => SensorType.FromFITSValue(null, 1, fileOffsetX, fileOffsetY, pattern)
                .ShouldBe((SensorType.RGGB, 0, 0));

        [Fact]
        public void FromFITSValue_AnUnknownTokenStaysUnknown_NeverAGuess()
            => SensorType.FromFITSValue(null, 1, 0, 0, "XTRANS")
                .ShouldBe((SensorType.Unknown, 0, 0));

        [Fact]
        public void FromFITSValue_ThreePlanesAreAlreadyColor_WhateverTheProvenanceSays()
            => SensorType.FromFITSValue(true, 3, 1, 1, "RGGB")
                .ShouldBe((SensorType.Color, 0, 0));

        [Fact]
        public void FromFITSValue_NoPatternAtAllIsMonochrome()
            => SensorType.FromFITSValue(null, 1, 0, 0, null, "", " ")
                .ShouldBe((SensorType.Monochrome, 0, 0));

        [Fact]
        public void FromFITSValue_CfaFalseOverridesAStalePattern()
            => SensorType.FromFITSValue(false, 1, 0, 0, "RGGB")
                .ShouldBe((SensorType.Monochrome, 0, 0));
    }
}
