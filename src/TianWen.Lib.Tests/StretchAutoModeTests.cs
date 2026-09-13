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
                    colourIsNotPhotometric: false, channelsAlreadyAgree: true)
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
        /// <b>A non-photometric frame with a CALIBRATION keeps Unlinked whatever the background.</b>
        /// The reason that case avoids Linked is a white balance fitted to a premise that never held
        /// (an HOO composite puts OIII in two channels, so three gains answer two measurements), and
        /// channel agreement does nothing to repair a bogus fit.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ANonPhotometricFrameResolvesUnlinkedWhateverTheBackground(bool agree)
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: true,
                    colourIsNotPhotometric: true, channelsAlreadyAgree: agree)
                .ShouldBe(StretchMode.Unlinked);

        /// <summary>
        /// <b>...but with NO calibration it resolves Linked, and that is the amendment.</b> The veto
        /// used to fire unconditionally, and its justification is a bogus white balance -- so with none
        /// active there is no fit to protect against, and Unlinked instead fits three curves to the
        /// noise between channels that already agree, discarding the colour the file arrived with.
        /// <para>This is the real case: an Astro Pixel Processor HOO composite, already colour-balanced
        /// by APP, carrying no FILTER or INSTRUME card, with G and B duplicated from OIII so the
        /// duplicate-channel test fires. It opened Unlinked and lost APP's colour. Channel medians
        /// measured 0.1 percent apart, inside the 0.15 percent the rule was built on.</para>
        /// </summary>
        [Fact]
        public void ANonPhotometricFrameWithNoCalibrationResolvesLinkedOnceItsChannelsAgree()
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: false,
                    colourIsNotPhotometric: true, channelsAlreadyAgree: true)
                .ShouldBe(StretchMode.Linked);

        /// <summary>And a non-photometric frame whose channels do NOT agree still resolves Unlinked.</summary>
        [Fact]
        public void ANonPhotometricFrameWhoseChannelsDisagreeStaysUnlinked()
            => StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: false,
                    colourIsNotPhotometric: true, channelsAlreadyAgree: false)
                .ShouldBe(StretchMode.Unlinked);

        /// <summary>Mono has no channels to link, so both answers coincide and it stays Linked.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MonoResolvesLinkedWhateverTheBackground(bool agree)
            => StretchMode.Auto.ResolveAuto(isColour: false, calibrationActive: false,
                    colourIsNotPhotometric: false, channelsAlreadyAgree: agree)
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
                    colourIsNotPhotometric: false, channelsAlreadyAgree: true)
                .ShouldBe(explicitMode);
    }
}
