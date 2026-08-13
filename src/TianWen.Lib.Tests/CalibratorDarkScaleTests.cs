using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins dark scaling: a dark whose exposure does not match the light's has its THERMAL
    /// component rescaled by t_light/t_dark, while its electronic offset stays put.
    ///
    /// <para>Measured motivation: 47 of the 64 sessions in the reference dataset were calibrated
    /// with a mismatched dark, 28 of them 60s lights against a 120s dark, and the sub-PSF residue
    /// in the stacked masters concentrated in exactly those sessions.</para>
    ///
    /// <para>The invariant that constrains the design, documented at the two production call sites
    /// and easy to break: a master dark built from RAW darks already carries the sensor's offset,
    /// so <c>Bias</c> is deliberately never passed alongside it. The bias here is used ONLY to
    /// split the dark and is not subtracted from the light a second time.</para>
    /// </summary>
    public class CalibratorDarkScaleTests
    {
        private const int W = 5, H = 3;

        private static Image Mono(float value)
        {
            var data = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    data[y, x] = value;
                }
            }
            var meta = new ImageMeta("t", DateTime.UnixEpoch, TimeSpan.FromSeconds(60),
                FrameType.Light, "", 3.76f, 3.76f, 130, -1, Filter.Unknown, 1, 1,
                -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Float32, 1f, 0f, 0f, meta);
        }

        /// <summary>
        /// The 120s-dark-on-60s-lights case, which is 28 of the reference dataset's sessions.
        /// Offset 0.10, dark thermal 0.20 over 120s. Halving the exposure must halve ONLY the
        /// thermal part: 0.10 + 0.20*0.5 = 0.20, so a 0.50 light calibrates to 0.30. Subtracting
        /// the dark unscaled would give 0.20 and over-subtract by a third of the signal.
        /// </summary>
        [Fact]
        public void HalvingTheExposureHalvesTheThermalPartAndLeavesTheOffset()
        {
            var scaled = new Calibrator(
                Dark: Mono(0.30f), DarkBias: Mono(0.10f), DarkScale: 0.5f).EnsureValid().Apply(Mono(0.50f));
            var unscaled = new Calibrator(Dark: Mono(0.30f)).Apply(Mono(0.50f));

            scaled[0, 0, 0].ShouldBe(0.30f, tolerance: 1e-6f);
            unscaled[0, 0, 0].ShouldBe(0.20f, tolerance: 1e-6f);
        }

        /// <summary>Scaling up is the same arithmetic in the other direction, and must not touch
        /// the offset either: 0.10 + 0.20*2 = 0.50.</summary>
        [Fact]
        public void DoublingTheExposureDoublesOnlyTheThermalPart()
        {
            var result = new Calibrator(
                Dark: Mono(0.30f), DarkBias: Mono(0.10f), DarkScale: 2f).EnsureValid().Apply(Mono(0.90f));
            result[0, 0, 0].ShouldBe(0.40f, tolerance: 1e-6f);
        }

        /// <summary>A scale of 1 must be an exact no-op, so every existing caller is byte-identical
        /// and a bias is not required to reach the old path.</summary>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.00001f)]
        public void ScaleOfOneIsAnExactNoOpAndNeedsNoBias(float scale)
        {
            var withScale = new Calibrator(Dark: Mono(0.30f), DarkScale: scale).EnsureValid().Apply(Mono(0.50f));
            var plain = new Calibrator(Dark: Mono(0.30f)).Apply(Mono(0.50f));
            withScale[0, 0, 0].ShouldBe(plain[0, 0, 0], tolerance: 1e-6f);
        }

        /// <summary>Fails where the mistake is made, not per pixel and not silently: a scale with
        /// nothing to split the dark cannot be applied, and ignoring it would emit mis-calibrated
        /// frames, which is the exact failure this parameter exists to end.</summary>
        [Fact]
        public void ScalingWithoutABiasIsRejectedAtConstruction()
        {
            Should.Throw<ArgumentException>(() =>
                new Calibrator(Dark: Mono(0.30f), DarkScale: 0.5f).EnsureValid());
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-0.5f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void ANonPositiveOrNonFiniteScaleIsRejected(float scale)
        {
            Should.Throw<ArgumentException>(() =>
                new Calibrator(Dark: Mono(0.30f), DarkBias: Mono(0.10f), DarkScale: scale).EnsureValid());
        }

        /// <summary>The tile path feeds the integrator and the whole-frame path feeds one-off
        /// calibration, so they must agree exactly; the scaling arithmetic is written twice (once
        /// materialised, once inline) and this is what stops the two drifting.</summary>
        [Fact]
        public void TheTileAndWholeFramePathsAgree()
        {
            var cal = new Calibrator(Dark: Mono(0.30f), DarkBias: Mono(0.10f), DarkScale: 0.5f).EnsureValid();
            var whole = cal.Apply(Mono(0.50f));

            var lightTile = new float[W * H];
            Array.Fill(lightTile, 0.50f);
            var dst = new float[W * H];
            cal.ApplyTile(lightTile, channel: 0, regionX: 0, regionY: 0, regionWidth: W, regionHeight: H, dst);

            for (var i = 0; i < dst.Length; i++)
            {
                dst[i].ShouldBe(whole[0, i / W, i % W], tolerance: 1e-6f);
            }
        }

        /// <summary>The bias must NOT be subtracted from the light a second time. The scaled dark
        /// still carries the offset, so passing a DarkBias may only ever change the dark, never add
        /// an independent subtraction; at scale 1 the result must equal the no-bias result.</summary>
        [Fact]
        public void TheDarkBiasIsNeverSubtractedFromTheLightOnItsOwn()
        {
            var withDarkBias = new Calibrator(
                Dark: Mono(0.30f), DarkBias: Mono(0.10f), DarkScale: 1f).EnsureValid().Apply(Mono(0.50f));
            var withoutIt = new Calibrator(Dark: Mono(0.30f)).Apply(Mono(0.50f));

            withDarkBias[0, 0, 0].ShouldBe(withoutIt[0, 0, 0], tolerance: 1e-6f);
            withDarkBias[0, 0, 0].ShouldBe(0.20f, tolerance: 1e-6f);
        }
    }
}
