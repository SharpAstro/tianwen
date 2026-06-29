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

        [Fact]
        public void No_frame_yet_yields_a_linear_passthrough()
        {
            var src = new LiveFramePreviewSource();
            var u = src.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
            u.Mode.ShouldBe(StretchMode.None);
        }
    }
}
