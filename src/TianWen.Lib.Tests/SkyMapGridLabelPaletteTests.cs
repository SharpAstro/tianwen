using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A grid label is not drawn where the layer palette covers it. The palette floats against the right
/// edge, where the grid's edge-anchored labels land on almost every pan, and at rest it recedes to
/// 40 percent, so a label under it was read through its rows: "15h" across "Objects" on the deployed
/// atlas, 2026-09-17.
/// </summary>
[Collection("Astrometry")]
public sealed partial class SkyMapGridLabelPaletteTests
{
    /// <summary>Records every DrawText call with its size and layout box, and still draws it.</summary>
    private sealed class TextCapturingRenderer(uint w, uint h) : RgbaImageRenderer(w, h)
    {
        public List<(string Text, float FontSize, string Font, RectInt Rect)> Texts { get; } = [];

        public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
            RGBAColor32 fontColor, in RectInt layout,
            TextAlign horizAlignment = TextAlign.Center, TextAlign vertAlignment = TextAlign.Near)
        {
            Texts.Add((text.ToString(), fontSize, fontFamily, layout));
            base.DrawText(text, fontFamily, fontSize, fontColor, layout, horizAlignment, vertAlignment);
        }
    }

    // Publishes the view matrix each frame the way the GPU pipelines do, so the grid labels' edge
    // crossings are projected with a matrix that agrees with the centre.
    private sealed class GridTestSkyMapTab(TextCapturingRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site, phase);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }
    }

    // "14h", "1.5h", "+60°", "-30°": what DrawGridLabels prints, and nothing else the tab draws.
    [GeneratedRegex(@"^([0-9]+(\.[0-9])?h|[+-][0-9]+°)$")]
    private static partial Regex GridLabel();

    private static (string Text, float X, float Y, float InkW, float H)[] GridLabels(TextCapturingRenderer r)
        => r.Texts
            .Where(t => GridLabel().IsMatch(t.Text))
            .Select(t => (t.Text, (float)t.Rect.UpperLeft.X, (float)t.Rect.UpperLeft.Y,
                r.MeasureText(t.Text.AsSpan(), t.Font, t.FontSize).Width, (float)t.Rect.Height))
            .ToArray();

    // One pixel of slack: the layout box reaches the renderer rounded to whole pixels.
    private static bool Overlaps((string Text, float X, float Y, float InkW, float H) label, RectF32 rect)
        => label.X < rect.Right - 1f && label.X + label.InkW > rect.X + 1f
            && label.Y < rect.Bottom - 1f && label.Y + label.H > rect.Y + 1f;

    [Fact]
    public async Task AGridLabelUnderTheLayerPaletteIsNotDrawn()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        const int w = 900, h = 900;
        using var renderer = new TextCapturingRenderer(w, h);
        var tab = new GridTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var state = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 48.0,
            SiteLongitude = 11.0,
            SiteTimeZone = TimeSpan.FromHours(1),
            PlanningDate = new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.FromHours(2)),
        };
        var time = new FakeTimeProviderWrapper(state.PlanningDate.Value);
        var content = new RectF32(0, 0, w, h);

        // The first render homes the view on the pole. At 30 degrees the hour lines leave it every two
        // hours, 30 degrees apart, so three of them cross the right-hand edge.
        tab.Render(state, content, time);
        tab.State.FieldOfViewDeg = 30.0;

        // Control, palette hidden: find a label on the right-hand edge.
        tab.State.ShowLayerPalette = false;
        renderer.Texts.Clear();
        tab.Render(state, content, time);
        var control = GridLabels(renderer);
        var onRightEdge = control
            .Where(l => l.X > w - 120 && l.Y > 250 && l.Y < h - 250)
            .OrderBy(l => Math.Abs(l.Y - h / 2f))
            .ToArray();
        onRightEdge.ShouldNotBeEmpty(
            "a grid label on the right-hand edge; drawn: " + string.Join(", ", control.Select(l => $"{l.Text}@({l.X},{l.Y})")));
        var target = onRightEdge[0];

        // Move the palette down the right edge until it covers that label, then show it. Two renders:
        // the first arranges the palette at its new place, and the labels read where it landed last.
        tab.State.LayerPalette.OffsetAlong = target.Y - 60f;
        tab.State.ShowLayerPalette = true;
        tab.Render(state, content, time);
        renderer.Texts.Clear();
        tab.Render(state, content, time);

        var palette = tab.State.LayerPalette.PanelRect;
        Overlaps(target, palette).ShouldBeTrue(
            $"the setup must put the palette over '{target.Text}' at ({target.X}, {target.Y}), or the assertion below proves nothing; palette {palette}");

        var drawn = GridLabels(renderer);
        drawn.ShouldNotBeEmpty("the grid's other labels still draw");
        drawn.Where(l => Overlaps(l, palette)).ShouldBeEmpty("no grid label may be drawn under the layer palette");

        // Hiding the palette gives the label back.
        tab.State.ShowLayerPalette = false;
        renderer.Texts.Clear();
        tab.Render(state, content, time);
        GridLabels(renderer).ShouldContain(l => l.Text == target.Text && Overlaps(l, palette));
    }
}
