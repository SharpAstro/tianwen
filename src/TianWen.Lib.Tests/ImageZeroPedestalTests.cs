using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <see cref="Image.WithZeroPedestal"/>: the rewrap a display stretch needs for a
    /// background-EXTRACTED frame, whose pedestal no longer describes its own pixels.
    /// </summary>
    /// <remarks>
    /// The case it exists for, measured on a 10P drizzle master through the viewer's own log: an
    /// enhance took the pedestal from 0 to 0.019361 while the medians collapsed to about 0.00072, so
    /// the pedestal ended TWENTY-FIVE TIMES the median. A stretch places its shadow point at the
    /// pedestal-subtracted median, which put every channel negative, and the frame rendered as a flat
    /// crimson wash (viewer-prerelease-fixes P30).
    /// </remarks>
    public class ImageZeroPedestalTests
    {
        private static Image Frame(float minValue, float pedestal)
        {
            var data = new float[1][,];
            data[0] = new float[4, 4];
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    data[0][y, x] = 0.25f;
                }
            }

            return new Image(data, BitDepth.Float32, 1f, minValue, pedestal, new ImageMeta(), true);
        }

        /// <summary>A raw frame is handed straight back, so the common path allocates nothing.</summary>
        [Fact]
        public void AFrameWithNoPedestalIsReturnedUnchanged()
        {
            var raw = Frame(minValue: 0f, pedestal: 0f);

            raw.WithZeroPedestal().ShouldBeSameAs(raw);
        }

        /// <summary>
        /// <b>A non-zero pedestal is neutralised even when the MINIMUM is zero</b>, which is the whole
        /// of the widened guard.
        /// </summary>
        /// <remarks>
        /// The original private helper on <c>MasterPreviewRenderer</c> returned early on
        /// <c>MinValue == 0</c> alone and never looked at the pedestal -- so a frame shaped like the
        /// enhanced master (a floor of zero, a pedestal well above the median) slipped through the very
        /// guard written for it. Hoisting the method is what surfaced that; this is the case that
        /// separates the two versions, and it fails against the old condition.
        /// </remarks>
        [Fact]
        public void APedestalIsNeutralisedEvenWhenTheMinimumIsAlreadyZero()
        {
            var enhanced = Frame(minValue: 0f, pedestal: 0.019361f);

            var display = enhanced.WithZeroPedestal();

            display.ShouldNotBeSameAs(enhanced);
            display.Pedestal.ShouldBe(0f);
            display.MinValue.ShouldBe(0f);
        }

        /// <summary>
        /// The rewrap SHARES its pixels, so it is cheap per render -- and is a view rather than a
        /// second owner, which is why nothing may release both.
        /// </summary>
        [Fact]
        public void TheRewrapSharesItsPixelsRatherThanCopyingThem()
        {
            var enhanced = Frame(minValue: 0.001f, pedestal: 0.019361f);

            var display = enhanced.WithZeroPedestal();

            display.GetChannelArray(0).ShouldBeSameAs(enhanced.GetChannelArray(0));
        }

        /// <summary>Everything else about the frame survives the rewrap.</summary>
        [Fact]
        public void TheRewrapKeepsTheRestOfTheFrame()
        {
            var enhanced = Frame(minValue: 0.001f, pedestal: 0.019361f);

            var display = enhanced.WithZeroPedestal();

            display.MaxValue.ShouldBe(enhanced.MaxValue);
            display.BitDepth.ShouldBe(enhanced.BitDepth);
            display.ChannelCount.ShouldBe(enhanced.ChannelCount);
            display.SamplesAreUnitReferred.ShouldBe(enhanced.SamplesAreUnitReferred);
        }
    }
}
