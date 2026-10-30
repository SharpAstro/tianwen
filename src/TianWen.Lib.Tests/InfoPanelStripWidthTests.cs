using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The statistics table is never trimmed (a cut number is a wrong one), so at the strip's real width every cell, a SER's peak and
/// its clip mark included, has to be painted inside the panel (#1874). Read off the frame the viewer painted
/// (<see cref="PixelWidgetBase{TSurface}.GetCapturedLayout"/>), with the face it ships with (<see cref="OrreryTests.AppFace"/>,
/// the same file on every OS: <see cref="BundledFonts"/> finds no bundled face beside the tests and answers with the platform's
/// monospace default, which passed on Windows and overflowed on Linux); the strip's inner edge is the painted Statistics
/// heading, whose row spans it.
/// </summary>
[Collection("Viewer")]
public class InfoPanelStripWidthTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheInfoPanelTableOfAClippingColourCapturePaintsInsideTheStrip(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        e2e.Viewer.FontPath = OrreryTests.AppFace;
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(e2e.Folder, "clipping.ser");
        const int n = 16;
        using (var writer = new SerWriter(path, n, n, SerColorId.BayerRGGB, 16, "", "", ""))
        {
            var samples = new ushort[n * n];
            Array.Fill(samples, (ushort)1000);
            samples[0] = 65535; // red photosite (0, 0) at full scale
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            writer.AppendFrame(bytes, new DateTimeOffset(2026, 10, 7, 21, 14, 3, TimeSpan.Zero));
        }
        e2e.Host.HandleDropFile(path);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == path, "the capture to open", ct);

        // Opened as the heading's click opens it: at DPI 1.5 the window has no room for the table and rolls it up.
        e2e.State.InfoPanelStatisticsPinned = true;
        e2e.Frame();
        var strip = PaintedStrip(e2e.Viewer);

        var clip = strip.Texts.Where(a => Text(a).EndsWith(" clip", StringComparison.Ordinal)).ShouldHaveSingleItem("the clip mark is painted");
        Right(clip).ShouldBeLessThanOrEqualTo(strip.Inner + 0.5f, "the clip mark is inside the strip");
        strip.ShouldFitInside("no cell runs past the strip");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheInfoPanelTableOfAFitsColourFramePaintsInsideTheStrip(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        e2e.Viewer.FontPath = OrreryTests.AppFace;
        await e2e.OpenAsync(e2e.WriteColourFits("colour.fits"), TestContext.Current.CancellationToken);

        e2e.State.InfoPanelStatisticsPinned = true;
        e2e.Frame();
        var strip = PaintedStrip(e2e.Viewer);

        strip.ShouldFitInside("no cell runs past the strip");
        // In the bundled face mean, median and MAD fit beside the labels and the background does not: the table breaks at the
        // first column that runs past the strip, never sooner.
        var row = strip.Texts.Where(a => Text(a) is "mean" or "med" or "MAD").Select(a => a.Bounds.Y).Distinct().ToArray();
        var top = row.ShouldHaveSingleItem("the first table holds every column that fits beside the labels");
        strip.Texts.Single(a => Text(a) == "bg").Bounds.Y.ShouldBeGreaterThan(top, "the background is a table under the first");
    }

    /// <summary>What the last frame painted in the info strip: its text leaves, the heading's right edge and the widest text's.</summary>
    internal sealed record Strip(IReadOnlyList<Layout.ArrangedNode<float>> Texts, float Inner, float Widest)
    {
        /// <summary>
        /// Every painted text ends inside the strip. A failure names each text that runs past it and where it ends: a table's
        /// cell is as wide as its column, so a column that runs past names every cell in it.
        /// </summary>
        internal void ShouldFitInside(string why)
        {
            var past = Texts.Where(a => Right(a) > Inner + 0.5f).Select(a => $"\"{Text(a)}\" to {Right(a):0.0}");
            Widest.ShouldBeLessThanOrEqualTo(Inner + 0.5f,
                $"{why}: past the strip's inner edge at {Inner:0.0}, {string.Join(", ", past)}");
        }

        /// <summary>The painted words of a line whose label is <paramref name="label"/>, in reading order.</summary>
        internal IReadOnlyList<Layout.ArrangedNode<float>> LineAfter(string label)
        {
            var at = Texts.Select((a, i) => (a, i)).First(p => Text(p.a) == label).i;
            var line = new List<Layout.ArrangedNode<float>>();
            for (var i = at + 1; i < Texts.Count && !Text(Texts[i]).EndsWith(':'); i++)
            {
                line.Add(Texts[i]);
            }
            return line;
        }
    }

    internal static Strip PaintedStrip(ImageRendererBase<RgbaImage> viewer)
    {
        var panel = viewer.CurrentInfoPanelRect;
        panel.Width.ShouldBeGreaterThan(0f, "the strip has to be painted for this to observe anything");
        var inside = viewer.GetCapturedLayout()
            .Where(a => a.Bounds.X >= panel.X && a.Bounds.X < panel.X + panel.Width && a.Bounds.Y >= panel.Y && a.Bounds.Y < panel.Y + panel.Height)
            .ToArray();
        var heading = inside.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "ToggleStatistics" }).Bounds;
        var inner = heading.X + heading.Width;
        inner.ShouldBeLessThanOrEqualTo(panel.X + panel.Width, "the heading is inside the panel");
        var texts = inside.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ToArray();
        texts.Any(a => Text(a) == "bg").ShouldBeTrue("the table is open");
        var widest = texts.Max(Right);
        TestContext.Current.SendDiagnosticMessage($"panel right {panel.X + panel.Width}, inner edge {inner}, widest text {widest}");
        return new Strip(texts, inner, widest);
    }

    internal static string Text(Layout.ArrangedNode<float> node)
        => node.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var text } } ? text : string.Empty;

    internal static float Right(Layout.ArrangedNode<float> node) => node.Bounds.X + node.Bounds.Width;
}
