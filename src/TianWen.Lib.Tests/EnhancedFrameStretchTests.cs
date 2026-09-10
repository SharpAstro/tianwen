using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The stretch curve an ENHANCED frame's own statistics produce, driven from numbers measured on
    /// a real one rather than from a synthetic fixture.
    /// </summary>
    /// <remarks>
    /// <para>For viewer-prerelease-fixes P30: after Enhance, with no SPCC, a 10P drizzle master
    /// renders as a flat crimson wash in <see cref="StretchMode.Auto"/> (which resolves to Unlinked
    /// uncalibrated). Zeroing the pedestal fixed one real fault -- the pedestal was being counted
    /// twice, once folded into the collected median and again at shadow placement -- and the cast
    /// SURVIVED it.</para>
    /// <para>The inputs below are what the viewer logged after that fix: pedestal 0, three medians
    /// within 0.15 percent of each other, and MADs spanning 1.7x. They look healthy, which is exactly
    /// the difficulty -- so this drives the pure solver with them and looks at what comes out, no GUI
    /// and no three-minute enhance in the loop.</para>
    /// </remarks>
    public class EnhancedFrameStretchTests
    {
        private readonly ITestOutputHelper _output;

        public EnhancedFrameStretchTests(ITestOutputHelper output) => _output = output;

        // Measured 2026-09-10, post-WithZeroPedestal, from ViewerController.LogStretchBasis.
        private static ChannelStretchStats[] EnhancedStats() =>
        [
            new ChannelStretchStats(0f, 0.0201114f, 3.10806E-05f),
            new ChannelStretchStats(0f, 0.0200809f, 3.4637E-05f),
            new ChannelStretchStats(0f, 0.0200809f, 5.32075E-05f),
        ];

        // The same frame BEFORE the enhance, which renders correctly -- the control. Note the MAD
        // spread here is WIDER (1.83x) than the enhanced frame's 1.71x, so a per-channel MAD spread
        // cannot by itself be what breaks the render.
        private static ChannelStretchStats[] RawStats() =>
        [
            new ChannelStretchStats(0f, 0.00872816f, 0.00012225f),
            new ChannelStretchStats(0f, 0.0320287f, 0.000223673f),
            new ChannelStretchStats(0f, 0.0195926f, 0.000223881f),
        ];

        /// <summary>
        /// Reports the curve each set produces, and what the background renders as per channel. A cast
        /// IS the three channels' rendered backgrounds disagreeing, so that is the number to compare.
        /// </summary>
        [Fact]
        public void TheEnhancedCurveIsReportedBesideTheRawOne()
        {
            var parameters = new StretchParameters(0.1f, 5f);

            foreach (var (label, stats) in new[] { ("raw", RawStats()), ("enhanced", EnhancedStats()) })
            {
                var u = StretchSolver.ComputeStretchUniforms(
                    StretchMode.Unlinked, parameters, stats, lumaStats: null, imageMaxValue: 1f);

                _output.WriteLine($"--- {label}");
                _output.WriteLine($"shadows  {u.Shadows.R:G6} / {u.Shadows.G:G6} / {u.Shadows.B:G6}");
                _output.WriteLine($"midtones {u.Midtones.R:G6} / {u.Midtones.G:G6} / {u.Midtones.B:G6}");
                _output.WriteLine($"rescale  {u.Rescale.R:G6} / {u.Rescale.G:G6} / {u.Rescale.B:G6}");

                // What the sky itself renders as, per channel: the median through that channel's own
                // curve. Three equal numbers is a neutral background; a cast is these disagreeing.
                var r = Image.StretchValue(stats[0].Median, 1f, 0f, u.Shadows.R, u.Midtones.R, u.Rescale.R);
                var g = Image.StretchValue(stats[1].Median, 1f, 0f, u.Shadows.G, u.Midtones.G, u.Rescale.G);
                var b = Image.StretchValue(stats[2].Median, 1f, 0f, u.Shadows.B, u.Midtones.B, u.Rescale.B);
                _output.WriteLine($"background renders as R={r:G6} G={g:G6} B={b:G6}");
            }
        }

        /// <summary>
        /// <b>The claim under test:</b> an Unlinked stretch maps every channel's own median to the same
        /// place, so the background renders neutral whatever the channels' levels were. That is what
        /// makes Unlinked the uncalibrated default, and it holds on the raw frame.
        /// </summary>
        [Fact]
        public void AnUnlinkedStretchRendersTheBackgroundNeutral()
        {
            var parameters = new StretchParameters(0.1f, 5f);

            foreach (var (label, stats) in new[] { ("raw", RawStats()), ("enhanced", EnhancedStats()) })
            {
                var u = StretchSolver.ComputeStretchUniforms(
                    StretchMode.Unlinked, parameters, stats, lumaStats: null, imageMaxValue: 1f);

                var r = Image.StretchValue(stats[0].Median, 1f, 0f, u.Shadows.R, u.Midtones.R, u.Rescale.R);
                var g = Image.StretchValue(stats[1].Median, 1f, 0f, u.Shadows.G, u.Midtones.G, u.Rescale.G);
                var b = Image.StretchValue(stats[2].Median, 1f, 0f, u.Shadows.B, u.Midtones.B, u.Rescale.B);

                _output.WriteLine($"{label}: R={r:G6} G={g:G6} B={b:G6}");

                // Generous: a cast visible on screen is a gross difference, not a subtle one.
                g.ShouldBe(r, 0.02, $"{label}: green against red");
                b.ShouldBe(r, 0.02, $"{label}: blue against red");
            }
        }
    }
}
