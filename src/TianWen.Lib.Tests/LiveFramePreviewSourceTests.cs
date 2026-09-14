using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pure tests for <see cref="LiveFramePreviewSource"/> -- the lightweight live-frame
    /// <see cref="IPreviewSource"/> the full viewer uses for the Live Session / guide / polar preview. Covers
    /// the two behaviours that make it cheaper than a per-frame document: [0,1] normalisation on accept, and
    /// the freeze-stats lock (reuse cached median/MAD instead of rescanning) with its off -&gt; on one-shot edge.
    /// </summary>
    public sealed class LiveFramePreviewSourceTests
    {
        private const float Max = 1000f;

        // Mono float[,] image; px(x, y) gives the raw sample (0..Max). Image channel is [y, x] row-major.
        private static Image MonoImage(int w, int h, Func<int, int, float> px, SensorType sensor = SensorType.Monochrome)
        {
            var ch = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    ch[y, x] = px(x, y);
                }
            }

            var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
                float.NaN, sensor, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([ch], BitDepth.Float32, maxValue: Max, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        // The same, as a single-plane Bayer mosaic: SensorType.RGGB names the CFA and the offsets carry
        // the pattern (1, 0 = GRBG), which is the convention the whole codebase uses.
        private static Image MosaicImage(int w, int h, Func<int, int, float> px)
        {
            var ch = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    ch[y, x] = px(x, y);
                }
            }

            var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
                float.NaN, SensorType.RGGB, 1, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([ch], BitDepth.Float32, maxValue: Max, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        [Fact]
        public void Accept_mono_frame_normalizes_channel_to_unit_and_reports_geometry()
        {
            var src = new LiveFramePreviewSource();
            // 2x2 raw [0, 250, 500, 1000] (MaxValue 1000); flat row-major -> normalised [0, .25, .5, 1].
            var raw = new[] { 0f, 250f, 500f, 1000f };
            src.AcceptFrame(MonoImage(2, 2, (x, y) => raw[y * 2 + x]), freezeStats: false);

            src.Width.ShouldBe(2);
            src.Height.ShouldBe(2);
            src.ChannelCount.ShouldBe(1);
            src.SensorType.ShouldBe(SensorType.Monochrome);
            src.FrameCount.ShouldBe(1);

            var data = src.GetChannelData(0);
            data.Length.ShouldBe(4);
            data[0].ShouldBe(0f, 1e-6f);
            data[1].ShouldBe(0.25f, 1e-6f);
            data[2].ShouldBe(0.5f, 1e-6f);
            data[3].ShouldBe(1.0f, 1e-6f);
        }

        [Fact]
        public void GetChannelData_out_of_range_returns_empty()
        {
            var src = new LiveFramePreviewSource();
            src.AcceptFrame(MonoImage(4, 4, (_, _) => 500f), freezeStats: false);

            src.GetChannelData(3).IsEmpty.ShouldBeTrue();
            src.GetChannelData(-1).IsEmpty.ShouldBeTrue();
        }

        [Fact]
        public void Rggb_single_channel_frame_is_a_bayer_mosaic_with_offsets()
        {
            var src = new LiveFramePreviewSource();
            var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.None, 1, 1,
                float.NaN, SensorType.RGGB, 1, 0, RowOrder.TopDown, float.NaN, float.NaN);
            var ch = new float[4, 4];
            var image = new Image([ch], BitDepth.Float32, maxValue: Max, minValue: 0f, pedestal: 0f, imageMeta: meta);

            src.AcceptFrame(image, freezeStats: false);

            src.SensorType.ShouldBe(SensorType.RGGB);
            src.ChannelCount.ShouldBe(1);
            src.BayerOffsetX.ShouldBe(1);
            src.BayerOffsetY.ShouldBe(0);
        }

        [Fact]
        public void Geometry_change_reallocates_buffers()
        {
            var src = new LiveFramePreviewSource();
            src.AcceptFrame(MonoImage(4, 4, (_, _) => 200f), freezeStats: false);
            src.GetChannelData(0).Length.ShouldBe(16);

            src.AcceptFrame(MonoImage(8, 8, (_, _) => 200f), freezeStats: false);
            src.Width.ShouldBe(8);
            src.Height.ShouldBe(8);
            src.GetChannelData(0).Length.ShouldBe(64);
            src.FrameCount.ShouldBe(2);
        }

        [Fact]
        public void Freeze_reuses_stats_across_frames_but_unfrozen_tracks_brightness()
        {
            // Checkerboard so MAD is non-zero; brightness varies the median, hence the stretch uniforms.
            static Func<int, int, float> Level(float lo, float hi) => (x, y) => (x + y) % 2 == 0 ? lo : hi;

            var src = new LiveFramePreviewSource();

            // A: dim. Unfrozen -> stats from A.
            src.AcceptFrame(MonoImage(16, 16, Level(150f, 250f)), freezeStats: false);
            var uA = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);

            // B: bright, freeze turning ON -> one-shot recompute, stats now from B.
            src.AcceptFrame(MonoImage(16, 16, Level(550f, 650f)), freezeStats: true);
            var uB = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);

            // C: different brightness, still frozen -> stats stay at B, uniforms unchanged.
            src.AcceptFrame(MonoImage(16, 16, Level(800f, 950f)), freezeStats: true);
            var uC = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);

            uA.ShouldNotBe(uB);  // brightness moved the median -> different stretch
            uB.ShouldBe(uC);     // frozen: C's brighter pixels did not move the stats

            // D: same bright frame as C but UNFROZEN -> stats recompute -> now differs from the frozen uB.
            src.AcceptFrame(MonoImage(16, 16, Level(800f, 950f)), freezeStats: false);
            var uD = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
            uD.ShouldNotBe(uB);
        }

        [Fact]
        public void Provides_per_channel_background_so_renderer_post_stretch_background_does_not_crash()
        {
            // Regression: the renderer (VkImageRenderer.RenderImageQuad) calls
            // stretch.ComputePostStretchBackground(source.PerChannelBackground, source.LumaBackground) every
            // frame; an empty PerChannelBackground threw IndexOutOfRange (GetChannelBg reads [0]).
            var src = new LiveFramePreviewSource();
            src.AcceptFrame(MonoImage(8, 8, (_, _) => 300f), freezeStats: false);

            src.PerChannelBackground.ShouldNotBeEmpty();

            var u = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
            var bg = u.ComputePostStretchBackground(src.PerChannelBackground, src.LumaBackground);
            bg.ShouldBeInRange(0f, 1f);
        }

        /// <summary>
        /// <b>The live preview solves a mosaic from its three colours, as a document does.</b> It used
        /// to take ONE statistic over the whole mosaic and hand the solver three copies, so Unlinked --
        /// which Auto resolves a mosaic to, precisely so each channel's background levels -- rendered
        /// exactly what Linked rendered, and background neutralisation had nothing to level. The GUI's
        /// live session and guider previews are the only viewers that come through here, so this was
        /// the one place left where the same OSC frame looked different from the file on disk.
        /// </summary>
        [Fact]
        public void A_bayer_mosaic_is_solved_from_three_colours_so_unlinked_differs_from_linked()
        {
            // GRBG at offsets (1, 0): red at odd x / even y, blue at even x / odd y, green elsewhere.
            // Three separated levels plus a checkerboard wobble, so every colour has a real MAD.
            static float Cfa(int x, int y)
            {
                var wobble = (x + y) % 2 == 0 ? 0f : 40f;
                var isRed = (x & 1) == 1 && (y & 1) == 0;
                var isBlue = (x & 1) == 0 && (y & 1) == 1;
                return (isRed ? 200f : isBlue ? 700f : 450f) + wobble;
            }

            var src = new LiveFramePreviewSource();
            src.AcceptFrame(MosaicImage(32, 32, Cfa), freezeStats: false);

            src.ChannelCount.ShouldBe(1, "the frame really is a one-plane mosaic");
            src.PerChannelBackground.Length.ShouldBe(3, "one background per colour, as the document path gives");

            var unlinked = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
            var linked = src.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default);

            unlinked.Shadows.R.ShouldNotBe(unlinked.Shadows.B, "Unlinked: each colour's curve sits on its own median");
            linked.Shadows.R.ShouldBe(linked.Shadows.B, "Linked: one curve for all three");
            unlinked.ShouldNotBe(linked);
        }

        /// <summary>A mono frame keeps solving from its one statistic, broadcast -- the mosaic path must
        /// not leak into it.</summary>
        [Fact]
        public void A_mono_frame_still_solves_one_curve_for_every_channel()
        {
            var src = new LiveFramePreviewSource();
            src.AcceptFrame(MonoImage(16, 16, (x, y) => (x + y) % 2 == 0 ? 300f : 380f), freezeStats: false);

            var u = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);

            u.Shadows.R.ShouldBe(u.Shadows.G);
            u.Shadows.G.ShouldBe(u.Shadows.B);
        }

        [Fact]
        public void No_frame_yet_yields_a_linear_passthrough()
        {
            var src = new LiveFramePreviewSource();
            var u = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
            u.Mode.ShouldBe(StretchMode.None);
        }
    }
}
