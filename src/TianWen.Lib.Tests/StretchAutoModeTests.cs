using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <see cref="StretchModeExtensions.ResolveAuto"/>: which concrete mode the viewer's default
    /// <see cref="StretchMode.Auto"/> becomes.
    /// </summary>
    /// <remarks>
    /// The rule added here came from a user report phrased as *"the Auto for stretch mode didn't
    /// quite work after applying Enhance"*, and the fault really was the CHOICE rather than the
    /// chosen mode's arithmetic: switching to Linked by hand rendered the frame correctly. Two
    /// separate investigations went past it -- a pedestal double-count (real, fixed, and not this)
    /// and a per-channel MAD spread (refuted: driving the measured stats through the solver in
    /// Unlinked renders the background neutral) -- because both asked what the curve did instead of
    /// which mode should have been picked.
    /// </remarks>
    public class StretchAutoModeTests
    {
        /// <summary>
        /// A gradient-corrected colour frame resolves LINKED even with nothing calibrated, because the
        /// job Unlinked exists to do -- neutralise the background -- has already been done.
        /// </summary>
        [Fact]
        public void AnExtractedBackgroundResolvesLinkedWithoutACalibration()
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: false,
                    colourIsNotPhotometric: false, backgroundAlreadyExtracted: true)
                .ShouldBe(StretchMode.Linked);

        /// <summary>
        /// The case as it was, and still is for an ordinary frame: colour with no calibration
        /// neutralises per channel, asserting no cast.
        /// </summary>
        [Fact]
        public void AnUnextractedColourFrameStillResolvesUnlinked()
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: false)
                .ShouldBe(StretchMode.Unlinked);

        /// <summary>
        /// <b>A non-photometric frame keeps Unlinked even when flattened.</b> The new rule is ordered
        /// after the narrowband guard on purpose: an HOO composite's channels are NOT brought into
        /// agreement by flattening (OIII sits in two of them), and the reason that case avoids Linked
        /// is a white balance fitted to a premise that never held -- which a background extraction
        /// does nothing to repair.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ANonPhotometricFrameResolvesUnlinkedWhateverTheBackground(bool extracted)
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: true,
                    colourIsNotPhotometric: true, backgroundAlreadyExtracted: extracted)
                .ShouldBe(StretchMode.Unlinked);

        /// <summary>Mono has no channels to link, so both answers coincide and it stays Linked.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MonoResolvesLinkedWhateverTheBackground(bool extracted)
            => StretchMode.Auto.ResolveAuto(isColour: false, calibrationActive: false,
                    colourIsNotPhotometric: false, backgroundAlreadyExtracted: extracted)
                .ShouldBe(StretchMode.Linked);

        /// <summary>
        /// Every pre-existing combination is untouched by the new parameter's default, which is what
        /// keeps the Explorer thumbnail (a separate caller that passes neither of the two optional
        /// flags) rendering exactly as before.
        /// </summary>
        [Theory]
        [InlineData(true, true, StretchMode.Linked)]
        [InlineData(true, false, StretchMode.Unlinked)]
        [InlineData(false, true, StretchMode.Linked)]
        [InlineData(false, false, StretchMode.Linked)]
        public void TheDefaultLeavesTheOldAnswersAlone(bool isColour, bool calibrated, StretchMode expected)
            => StretchMode.Auto.ResolveAuto(isColour, calibrated).ShouldBe(expected);

        /// <summary>A mode that is not Auto passes straight through, whatever the flags say.</summary>
        [Theory]
        [InlineData(StretchMode.Linked)]
        [InlineData(StretchMode.Unlinked)]
        [InlineData(StretchMode.Luma)]
        [InlineData(StretchMode.None)]
        public void AnExplicitModeIsNeverOverridden(StretchMode explicitMode)
            => explicitMode.ResolveAuto(isColour: true, calibrationActive: false,
                    colourIsNotPhotometric: false, backgroundAlreadyExtracted: true)
                .ShouldBe(explicitMode);
    }
}
