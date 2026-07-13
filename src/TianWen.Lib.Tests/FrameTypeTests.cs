using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Covers <see cref="FrameTypeEx.FromFITSValue"/> / <see cref="FrameTypeEx.IsMasterFITSValue"/> --
    /// in particular the MASTER-prefix handling that lets the dataset builder ingest an already-
    /// integrated FOREIGN master (e.g. N.I.N.A. "MASTERDARK") as its underlying <see cref="FrameType"/>
    /// while still flagging it a master, and the null (excluded) result for non-frame IMAGETYPs like
    /// APP's "BADPIXELMAP".
    /// </summary>
    public class FrameTypeTests
    {
        [Theory]
        [InlineData("Light", FrameType.Light)]
        [InlineData("DARK", FrameType.Dark)]
        [InlineData("Bias", FrameType.Bias)]
        [InlineData("Flat", FrameType.Flat)]
        [InlineData("DARKFLAT", FrameType.DarkFlat)]
        [InlineData("DARK-FLAT", FrameType.DarkFlat)]
        [InlineData("MASTERDARK", FrameType.Dark)]
        [InlineData("MASTERFLAT", FrameType.Flat)]
        [InlineData("MASTERBIAS", FrameType.Bias)]
        [InlineData("MASTERDARKFLAT", FrameType.DarkFlat)]
        [InlineData("Master Dark", FrameType.Dark)]
        public void FromFITSValue_ParsesTypeAndStripsMasterPrefix(string value, FrameType expected)
            => FrameType.FromFITSValue(value).ShouldBe(expected);

        [Theory]
        [InlineData("BADPIXELMAP")]
        [InlineData("MASTER")]
        [InlineData("")]
        [InlineData(null)]
        public void FromFITSValue_ReturnsNullForUnrecognised(string? value)
            => FrameType.FromFITSValue(value!).ShouldBeNull();

        [Theory]
        [InlineData("MASTERDARK", true)]
        [InlineData("Master Flat", true)]
        [InlineData("MASTERBIAS", true)]
        [InlineData("Dark", false)]
        [InlineData("Light", false)]
        [InlineData("BADPIXELMAP", false)]
        [InlineData(null, false)]
        public void IsMasterFITSValue_DetectsMasterPrefix(string? value, bool expected)
            => FrameType.IsMasterFITSValue(value!).ShouldBe(expected);
    }
}
