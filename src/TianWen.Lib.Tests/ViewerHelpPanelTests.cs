using System;
using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The "?" panel's action rows are addressed by INDEX, and this pins which index does what.
    /// </summary>
    /// <remarks>
    /// <para>The panel is a dropdown of mostly inert facts with a few live rows in it, and
    /// <c>HandleHelpSelection</c> keys on the row index rather than the label because the labels are
    /// ellipsized to fit the window. That is the right call -- comparing trimmed strings would work
    /// until the day someone runs a narrow window -- but it means <b>inserting a row silently
    /// renumbers every row after it</b>, and a misrouted row is not a crash: it is the wrong link
    /// opening, which nothing else would catch.</para>
    /// <para>So this drives EVERY index rather than the three it knows about, and asserts the complete
    /// set of rows that do anything. A row added without a case here fails the count.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerHelpPanelTests
    {
        private sealed class HelpViewer : ImageRendererBase<RgbaImage>
        {
            public HelpViewer(RgbaImageRenderer renderer)
                : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = 1f;
                FontPath = FontResolver.ResolveSystemFont();
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float rotationRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;
        }

        /// <summary>Drives every row of the panel and reports the ones that opened a URL.</summary>
        private static List<(int Index, string Url)> OpenedLinksPerRow()
        {
            var bus = new SignalBus();
            var urls = new List<string>();
            bus.Subscribe<OpenUrlSignal>(sig => urls.Add(sig.Url));

            using var renderer = new RgbaImageRenderer(900, 700);
            var viewer = new HelpViewer(renderer) { Bus = bus };

            var lines = viewer.BuildHelpLines();
            lines.Length.ShouldBeGreaterThan(0);

            var opened = new List<(int, string)>();
            for (var i = 0; i < lines.Length; i++)
            {
                urls.Clear();
                viewer.HandleHelpSelection(i);
                bus.ProcessPending(); // Post enqueues; delivery is the frame's job
                foreach (var url in urls)
                {
                    opened.Add((i, url));
                }
            }

            return opened;
        }

        [Fact]
        public void ExactlyThreeRowsOpenALinkAndEachOpensItsOwn()
        {
            var opened = OpenedLinksPerRow();

            opened.Count.ShouldBe(3, "the ? panel has three action rows; a new one needs a case here");

            // In panel order: guide, report, tip jar.
            opened[0].Url.ShouldBe(BugReportLink.DocumentationUrl);
            opened[1].Url.ShouldStartWith("https://github.com/SharpAstro/tianwen/issues/new");
            opened[2].Url.ShouldBe(BugReportLink.SupportUrl);

            // Distinct rows: the whole failure mode this guards is two rows sharing an index.
            opened[0].Index.ShouldBeLessThan(opened[1].Index);
            opened[1].Index.ShouldBeLessThan(opened[2].Index);
        }

        /// <summary>
        /// The tip jar is last. Not cosmetic: the panel is what someone opens when the viewer has just
        /// misbehaved, and an ask placed above "report a problem" would meet them at the worst moment.
        /// </summary>
        [Fact]
        public void TheTipJarIsTheLastActionRow()
        {
            var opened = OpenedLinksPerRow();

            opened[^1].Url.ShouldBe(BugReportLink.SupportUrl);
        }

        /// <summary>
        /// A row index nothing claims must do nothing at all -- the panel is mostly inert facts, and a
        /// stray click on the build string or a shortcut line must not open anything.
        /// </summary>
        [Fact]
        public void AnInertRowOpensNothing()
        {
            var opened = OpenedLinksPerRow();
            var live = new HashSet<int>();
            foreach (var (index, _) in opened)
            {
                live.Add(index);
            }

            // Row 0 is the build string, which is a fact and not a link.
            live.ShouldNotContain(0);
        }
    }
}
