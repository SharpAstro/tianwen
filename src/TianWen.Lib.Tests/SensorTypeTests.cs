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
