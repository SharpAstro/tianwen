using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

        /// <summary>
        /// Drives every row of the panel's ROOT page and reports the ones that opened a URL.
        /// </summary>
        /// <remarks>
        /// A FRESH viewer per row, because the panel is a menu now: two of its rows change the page
        /// rather than opening a link, and once the page has changed the same indices mean the rows of
        /// a different list. Driving one viewer down the whole list therefore stops testing the root
        /// after the first navigation row -- which is exactly how this read when the menu landed, as
        /// two links that had silently stopped being reachable.
        /// </remarks>
        private static List<(int Index, string Url)> OpenedLinksPerRow()
        {
            var opened = new List<(int, string)>();
            for (var i = 0; i < RootRowCount(); i++)
            {
                var bus = new SignalBus();
                var urls = new List<string>();
                bus.Subscribe<OpenUrlSignal>(sig => urls.Add(sig.Url));

                using var renderer = new RgbaImageRenderer(900, 700);
                var viewer = new HelpViewer(renderer) { Bus = bus };

                // BUILD first: a row index only means anything once the page it belongs to has been
                // built, which is what assigns them -- and in the app a panel is always built before
                // it can be clicked.
                viewer.BuildHelpLines();
                viewer.HandleHelpSelection(i);
                bus.ProcessPending(); // Post enqueues; delivery is the frame's job
                foreach (var url in urls)
                {
                    opened.Add((i, url));
                }
            }

            return opened;
        }

        private static int RootRowCount()
        {
            using var renderer = new RgbaImageRenderer(900, 700);
            var lines = new HelpViewer(renderer).BuildHelpLines();
            lines.Length.ShouldBeGreaterThan(0);
            return lines.Length;
        }

        /// <summary>
        /// The two navigation rows open a PAGE rather than a link, and the page's first row comes back.
        /// </summary>
        /// <remarks>
        /// The panel became a menu because it had grown past a laptop screen -- about 35 rows -- and
        /// the one screen someone opens when the viewer has misbehaved is the worst to have running off
        /// the bottom. What that trades away is a flat list, so this pins the navigation: forward into
        /// each page, and back out of it.
        /// </remarks>
        [Fact]
        public void TheLongSectionsArePagesReachedFromTheRootAndReturnedFrom()
        {
            using var renderer = new RgbaImageRenderer(900, 700);
            var viewer = new HelpViewer(renderer);

            var root = viewer.BuildHelpLines();
            root.Length.ShouldBeLessThan(12, "the root page is a menu, not the whole panel");

            var shortcutsRow = IndexOfRowContaining(root, "Keyboard shortcuts");
            viewer.HandleHelpSelection(shortcutsRow);
            var shortcuts = viewer.BuildHelpLines();
            shortcuts[0].ShouldContain("Back");
            shortcuts.ShouldContain(l => l.Contains("Fullscreen"), "the shortcut list is what this page is");

            // Row 0 of any sub-page is the way back, so the handler needs no per-page bookkeeping.
            viewer.HandleHelpSelection(0);
            viewer.BuildHelpLines().ShouldBe(root);

            var aiRow = IndexOfRowContaining(root, "AI enhancement");
            viewer.HandleHelpSelection(aiRow);
            viewer.BuildHelpLines()[0].ShouldContain("Back");

            viewer.HandleHelpSelection(0);
            viewer.BuildHelpLines().ShouldBe(root);
        }

        private static int IndexOfRowContaining(ImmutableArray<string> lines, string text)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(text, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new Xunit.Sdk.XunitException($"no row contains '{text}'");
        }

        /// <summary>
        /// Every row of the panel that opens something, and that each opens its OWN thing.
        /// </summary>
        /// <remarks>
        /// The count is deliberately exact and this test is meant to go red when a row is added --
        /// which is what it did when the install-folder row became clickable, correctly, on the run
        /// that added it. Note the fourth destination is a local PATH rather than a URL: it travels on
        /// the same <c>OpenUrlSignal</c> because the host's handler is the platform shell call
        /// (Explorer / xdg-open / open), all of which take a directory, and introducing a second way to
        /// ask the OS to open something is the thing worth avoiding here.
        /// </remarks>
        [Fact]
        public void ExactlyFourRowsOpenSomethingAndEachOpensItsOwn()
        {
            var opened = OpenedLinksPerRow();

            opened.Count.ShouldBe(4, "the ? panel has four action rows; a new one needs a case here");

            // In panel order: the install folder, then guide, report, tip jar.
            opened[0].Url.ShouldBe(TianWen.Lib.BuildInfo.InstallFolder);
            opened[1].Url.ShouldBe(BugReportLink.DocumentationUrl);
            opened[2].Url.ShouldStartWith("https://github.com/SharpAstro/tianwen/issues/new");
            opened[3].Url.ShouldBe(BugReportLink.SupportUrl);

            // The folder is a filesystem path, not a web address -- stated because the two share a
            // signal and a row that quietly started opening a browser would still pass everything above.
            opened[0].Url.ShouldNotStartWith("http");

            // Distinct rows: the whole failure mode this guards is two rows sharing an index.
            opened[0].Index.ShouldBeLessThan(opened[1].Index);
            opened[1].Index.ShouldBeLessThan(opened[2].Index);
            opened[2].Index.ShouldBeLessThan(opened[3].Index);
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
