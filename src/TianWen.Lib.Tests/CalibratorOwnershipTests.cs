using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the ownership contract P1 of <c>docs/plans/frame-lifecycle.md</c> gave
    /// <see cref="Calibrator.Apply"/>: it CONSUMES the light it is handed and the caller owns the
    /// result, whatever the configuration.
    /// </summary>
    /// <remarks>
    /// <para>Before P1 this method was the load-bearing example of "identity or copy, decided at
    /// runtime": with no masters it returned its own input, so ownership of the return value was a
    /// function of which masters happened to be configured and fourteen call sites carried
    /// <c>if (!ReferenceEquals(calibrated, raw)) raw.Release();</c> by hand. Getting that backwards
    /// is silent -- it recycles pixels another holder is still reading -- which is why the contract
    /// is worth a test rather than a comment.</para>
    /// <para>Asserted through a <see cref="ChannelBuffer"/> release counter rather than the
    /// DEBUG-only leak tracker, so these run in every configuration including CI's Release leg. The
    /// counter is also the thing that actually matters: it is the camera's or the pool's array coming
    /// back, not a bookkeeping entry.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class CalibratorOwnershipTests
    {
        private const int Size = 4;

        /// <summary>A light whose single channel carries a recycled buffer, plus the release counter.</summary>
        private static (Image Light, int[] Releases) BufferedLight(float value)
        {
            var releases = new int[1];
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    data[y, x] = value;
                }
            }

            var buffer = new ChannelBuffer(data, onRelease: _ => releases[0]++);
            var channel = new Channel(data, default, 0f, value, 0) { Buffer = buffer };
            return (new Image([channel], BitDepth.Float32, 0f, new ImageMeta()), releases);
        }

        private static Image PlainMaster(float value)
        {
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    data[y, x] = value;
                }
            }

            return new Image([new Channel(data, default, 0f, value, 0)], BitDepth.Float32, 0f, new ImageMeta());
        }

        [Fact]
        public void WithNoMastersTheInputIsReturnedAndItsOwnershipGoesWithIt()
        {
            var (light, releases) = BufferedLight(100f);

            var calibrated = new Calibrator().Apply(light);

            // Still the same instance -- that part did not change, and it is why the old guard
            // existed. What changed is that the caller no longer has to ask: it owns whatever came
            // back and releases exactly that.
            calibrated.ShouldBeSameAs(light);
            releases[0].ShouldBe(0, "nothing consumed it, so the array must still be out on loan");

            calibrated.Release();
            releases[0].ShouldBe(1, "one release by the owner, and the array goes home");
        }

        [Fact]
        public void WithABiasTheConsumedInputIsHandedBackWithoutTheCallerAskingForIt()
        {
            var (light, releases) = BufferedLight(100f);

            var calibrated = new Calibrator(Bias: PlainMaster(10f)).Apply(light);

            calibrated.ShouldNotBeSameAs(light);
            releases[0].ShouldBe(1, "Apply consumed the light, so its array went back at the subtract");
            calibrated.GetChannel(0).Buffer.ShouldBeNull("the fresh destination is not the camera's array");
            calibrated[0, 0, 0].ShouldBe(90f);

            // The caller releasing what it owns must not double-count the input's handback.
            calibrated.Release();
            releases[0].ShouldBe(1);
        }

        [Fact]
        public void EveryMasterTogetherStillReleasesTheInputExactlyOnce()
        {
            var (light, releases) = BufferedLight(100f);

            var calibrated = new Calibrator(
                Bias: PlainMaster(10f),
                Dark: PlainMaster(20f),
                Flat: PlainMaster(1f)).Apply(light);

            // Three chained transforms, one consumed input: the intermediates carry no buffer, so
            // their releases are no-ops and only the light's array is a real handback.
            calibrated.ShouldNotBeSameAs(light);
            releases[0].ShouldBe(1);

            calibrated.Release();
            releases[0].ShouldBe(1);
        }

        [Fact]
        public void AnIntermediateStaysReadableAfterApplyReleasesIt()
        {
            // Apply releases what each step consumed without asking whether the frame carries a
            // buffer. For an ordinary unbuffered intermediate that release is a no-op and the frame
            // remains fully readable (convention 2) -- which is what makes the unconditional release
            // safe rather than merely convenient.
            var light = PlainMaster(100f);

            var calibrated = new Calibrator(Bias: PlainMaster(10f), Dark: PlainMaster(20f)).Apply(light);

            calibrated[0, 0, 0].ShouldBe(70f);
            light[0, 0, 0].ShouldBe(100f, "an unbuffered release is a no-op, so the input is still readable");
        }
    }
}
