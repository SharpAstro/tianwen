using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The SPCC / Calibrate toggle must gate the RENDER, not just the measurement. The bug: switching
    /// SPCC off (or pressing W) changed the toolbar highlight and stashed the manual WB, but the shader
    /// still received the calibrated triple, so nothing on screen moved. And an AI enhance re-fitted the
    /// calibration on the enhanced pixels, so a nicely-calibrated frame took on a new cast the moment that
    /// fit landed. Both are properties of what <see cref="AstroImageDocument.ComputeStretchUniforms"/>
    /// writes, so they are pinned here rather than through the GPU.
    /// </summary>
    public sealed class ColorCalibrationToggleTests
    {
        // A frame with a deliberate blue sky cast and a handful of high-SNR stars, so the sky-background
        // calibration (which declares the sky neutral) lands a firmly non-neutral triple -- the Vela
        // fixture's sky is already near-neutral, which is nothing to gate.
        private static Image CastStarField()
        {
            const int W = 200, H = 200;
            var bg = new[] { 200f, 200f, 600f };   // R, G, B background: blue is 3x
            var rng = new Random(42);
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var p = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        p[y, x] = bg[c] + (float)(rng.NextDouble() * 20.0 - 10.0);
                    }
                }
                planes[c] = p;
            }
            // 16 bright stars on a grid, well clear of the edges; the same star in every channel.
            for (var i = 0; i < 16; i++)
            {
                var sx = 20 + i % 4 * 45;
                var sy = 20 + i / 4 * 45;
                for (var dy = -4; dy <= 4; dy++)
                {
                    for (var dx = -4; dx <= 4; dx++)
                    {
                        var g = 6000f * MathF.Exp(-(dx * dx + dy * dy) / (2f * 1.4f * 1.4f));
                        for (var c = 0; c < 3; c++)
                        {
                            planes[c][sy + dy, sx + dx] += g;
                        }
                    }
                }
            }
            var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
                float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image(planes, BitDepth.Float32, maxValue: 8192f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        private static async Task<AstroImageDocument> CalibratedDocAsync(CancellationToken ct)
        {
            var doc = await AstroImageDocument.AdoptImageAsync(CastStarField(), DebayerAlgorithm.None, cancellationToken: ct);
            await doc.DetectStarsAsync(ct);
            doc.Stars.ShouldNotBeNull();
            doc.Stars!.Count.ShouldBeGreaterThanOrEqualTo(5, "the synthetic field must give the sky-bg path enough stars");
            // Sky-background calibration (no catalog needed) declares the cast sky neutral.
            await doc.ComputeColorCalibrationAsync(null!, ct);
            doc.ColorCalibration.ShouldNotBeNull("the cast frame must calibrate to a triple");
            var wb = doc.ColorCalibration!.Value;
            (MathF.Abs(wb.R - 1f) + MathF.Abs(wb.B - 1f)).ShouldBeGreaterThan(0.1f,
                "the whole point is a non-neutral triple to gate");
            return doc;
        }

        [Fact]
        public async Task TheCalibrationToggleGatesTheRenderedWhiteBalance()
        {
            var ct = TestContext.Current.CancellationToken;
            var doc = await CalibratedDocAsync(ct);
            var wb = doc.ColorCalibration!.Value;

            var mode = ViewerActions.DefaultStretchMode;
            var p = new StretchParameters(0.25, -2.8);

            var on = doc.ComputeStretchUniforms(mode, p, applyColorCalibration: true);
            var off = doc.ComputeStretchUniforms(mode, p, applyColorCalibration: false);

            off.WhiteBalance.R.ShouldBe(1f, 1e-6f);
            off.WhiteBalance.G.ShouldBe(1f, 1e-6f);
            off.WhiteBalance.B.ShouldBe(1f, 1e-6f);
            on.WhiteBalance.ShouldNotBe(off.WhiteBalance, "turning the calibration on must change what the shader receives");
            on.WhiteBalance.R.ShouldBe(wb.R, 1e-4f);
            on.WhiteBalance.B.ShouldBe(wb.B, 1e-4f);
        }

        [Fact]
        public async Task TogglingOffThenOnIsBitIdenticalToNeverTogglingBecauseTheTripleIsKept()
        {
            var ct = TestContext.Current.CancellationToken;
            var doc = await CalibratedDocAsync(ct);
            var mode = ViewerActions.DefaultStretchMode;
            var p = new StretchParameters(0.25, -2.8);

            var first = doc.ComputeStretchUniforms(mode, p, applyColorCalibration: true);
            _ = doc.ComputeStretchUniforms(mode, p, applyColorCalibration: false); // "switch off"
            var again = doc.ComputeStretchUniforms(mode, p, applyColorCalibration: true); // "switch on"

            again.WhiteBalance.ShouldBe(first.WhiteBalance, "the measured triple is kept, so re-enabling restores the exact render");
        }

        [Fact]
        public async Task BackgroundNeutralizationHonoursTheToggleSoTheGainsMatchTheAppliedWhiteBalance()
        {
            var ct = TestContext.Current.CancellationToken;
            var doc = await CalibratedDocAsync(ct);

            // The gains are solved for a neutral background AFTER the WB multiply, so they depend on the
            // WB that is actually applied: with the calibration off the applied WB is identity, so the
            // gains must differ from the calibrated-WB ones.
            var withCal = doc.ComputeBackgroundNeutralization(BackgroundNeutralizationMethod.Mean, applyColorCalibration: true);
            var withoutCal = doc.ComputeBackgroundNeutralization(BackgroundNeutralizationMethod.Mean, applyColorCalibration: false);

            withCal.ShouldNotBeNull();
            withoutCal.ShouldNotBeNull();
            withCal!.Value.ShouldNotBe(withoutCal!.Value, "the neutralisation gains depend on the WB that is actually applied");
        }

        [Fact]
        public void AutoIsTheDefaultAndResolvesFromColourAndCalibration()
        {
            ViewerActions.DefaultStretchMode.ShouldBe(StretchMode.Auto);

            // The pure resolution (extension on StretchMode): colour + a calibration to show -> Linked,
            // colour without one -> Unlinked, mono -> Linked, and a non-Auto mode passes through.
            StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: true).ShouldBe(StretchMode.Linked);
            StretchMode.Auto.ResolveAuto(isColour: true, calibrationActive: false).ShouldBe(StretchMode.Unlinked);
            StretchMode.Auto.ResolveAuto(isColour: false, calibrationActive: false).ShouldBe(StretchMode.Linked);
            StretchMode.Luma.ResolveAuto(isColour: true, calibrationActive: false).ShouldBe(StretchMode.Luma);
        }

        [Fact]
        public async Task AutoRendersAsTheConcreteModeItPicksForTheFrame()
        {
            var ct = TestContext.Current.CancellationToken;
            var doc = await CalibratedDocAsync(ct);
            var p = new StretchParameters(0.25, -2.8);

            // Calibrated colour: Auto must render byte-for-byte as Linked, so the measured WB shows.
            var autoOn = doc.ComputeStretchUniforms(StretchMode.Auto, p, applyColorCalibration: true);
            var linked = doc.ComputeStretchUniforms(StretchMode.Linked, p, applyColorCalibration: true);
            autoOn.ShouldBe(linked);

            // Uncalibrated colour: Auto must render as Unlinked, neutralising each channel's background.
            var autoOff = doc.ComputeStretchUniforms(StretchMode.Auto, p, applyColorCalibration: false);
            var unlinked = doc.ComputeStretchUniforms(StretchMode.Unlinked, p, applyColorCalibration: false);
            autoOff.ShouldBe(unlinked);

            autoOn.ShouldNotBe(autoOff, "flipping the calibration on flips Auto from Unlinked to Linked");
        }

        [Fact]
        public async Task AnEnhancedDocumentInheritsTheCalibrationInsteadOfRefittingIt()
        {
            var ct = TestContext.Current.CancellationToken;
            var original = await CalibratedDocAsync(ct);

            // Stand in for the enhance output: a fresh, uncalibrated document from a copy of the pixels.
            var enhanced = await AstroImageDocument.AdoptImageAsync(CastStarField(), DebayerAlgorithm.None, cancellationToken: ct);
            enhanced.ColorCalibration.ShouldBeNull();

            enhanced.InheritColorCalibration(original);

            enhanced.ColorCalibration.ShouldBe(original.ColorCalibration,
                "the enhanced frame carries the triple measured on the original stars, so no re-fit re-casts it");
            enhanced.ColorCalibrationSummary.ShouldBe(original.ColorCalibrationSummary);
        }
    }
}
