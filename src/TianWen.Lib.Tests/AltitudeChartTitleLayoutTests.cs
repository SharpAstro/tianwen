using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The chart title had two independent geometry bugs, both provable without a repro run and both
/// fixed here: it was drawn from x = 0 rather than areaX (so a centre-aligned title landed at
/// areaW / 2 and slid out of its own column, which only shows up when the chart is NOT at x = 0),
/// and it was anchored downward from the top while the twilight-zone labels are anchored upward
/// from the plot, with nothing reserving space for both.
/// </summary>
public class AltitudeChartTitleLayoutTests
{
    private static readonly DateTimeOffset NightStart =
        new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset NightEnd =
        new DateTimeOffset(2026, 6, 22, 4, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Records every DrawText call's text + layout rect, and still draws it.</summary>
    private sealed class TextCapturingRenderer(uint w, uint h) : RgbaImageRenderer(w, h)
    {
        public List<(string Text, RectInt Rect)> Texts { get; } = [];

        public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
            RGBAColor32 fontColor, in RectInt layout,
            TextAlign horizAlignment = TextAlign.Center, TextAlign vertAlignment = TextAlign.Near)
        {
            Texts.Add((text.ToString(), layout));
            base.DrawText(text, fontFamily, fontSize, fontColor, layout, horizAlignment, vertAlignment);
        }
    }

    // A full twilight set, so every zone label row is drawn (Civil / Naut. / Astro on both sides).
    private static PlannerState BuildState() => new PlannerState
    {
        AstroDark = NightStart,
        AstroTwilight = NightEnd,
        CivilSet = NightStart - TimeSpan.FromMinutes(60),
        NauticalSet = NightStart - TimeSpan.FromMinutes(30),
        NauticalRise = NightEnd + TimeSpan.FromMinutes(30),
        CivilRise = NightEnd + TimeSpan.FromMinutes(60),
        SiteLatitude = -37.8,
        SiteLongitude = 145.0,
        SiteTimeZone = TimeSpan.FromHours(10),
        MinHeightAboveHorizon = 20,
    };

    private static (string Text, RectInt Rect) Title(TextCapturingRenderer r) =>
        r.Texts.Where(t => t.Text.StartsWith("Observation Schedule", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(733)]
    public void Title_IsCentredOnItsOwnColumn_NotOnTheSurface(int areaX)
    {
        // areaX = 0 is the case that hid the bug: with the chart at the left edge, 0 and areaX agree.
        const int areaW = 800, areaH = 600;
        using var renderer = new TextCapturingRenderer((uint)(areaX + areaW), areaH);
        AltitudeChartRenderer.Render(renderer, BuildState(), FontResolver.ResolveSystemFont(),
            areaX, 0, areaW, areaH);

        var (_, rect) = Title(renderer);
        var centre = (rect.UpperLeft.X + rect.LowerRight.X) / 2;
        Math.Abs(centre - (areaX + areaW / 2)).ShouldBeLessThanOrEqualTo(1,
            "the title is centre-aligned inside its rect, so the rect must span the chart column");
        rect.UpperLeft.X.ShouldBeGreaterThanOrEqualTo(areaX, "the title must not start left of its column");
        rect.LowerRight.X.ShouldBeLessThanOrEqualTo(areaX + areaW, "the title must not run past its column");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(900)]
    [InlineData(1440)]
    [InlineData(2400)]
    public void Title_ClearsTheTwilightZoneLabels_AtEveryRealisticHeight(int areaH)
    {
        // The old arithmetic (title from areaY + 2 down, labels from plotY - 38 up, top margin
        // max(30, h / 22)) only separated the two above roughly 2370 px, so they overlapped at
        // every size a chart is actually drawn at. 2400 is included to cover the size where the
        // old code happened to work, so the reservation is exercised on both sides of it.
        using var renderer = new TextCapturingRenderer(1000, (uint)areaH);
        AltitudeChartRenderer.Render(renderer, BuildState(), FontResolver.ResolveSystemFont(),
            0, 0, 1000, areaH);

        var (_, title) = Title(renderer);
        var zoneLabelTops = renderer.Texts
            .Where(t => t.Text is "Civil" or "Naut." or "Astro")
            .Select(t => t.Rect.UpperLeft.Y)
            .ToArray();

        zoneLabelTops.ShouldNotBeEmpty("the twilight labels must draw, or this proves nothing");
        zoneLabelTops.Min().ShouldBeGreaterThanOrEqualTo(title.LowerRight.Y,
            $"at {areaH} px the twilight labels start above the title's bottom edge, i.e. they overlap");
    }

    [Fact]
    public void Title_IsDroppedRatherThanOverlapped_WhenTheChartIsTooShort()
    {
        // The portrait planner layout renders the chart about 117 px tall (PlannerTabLayoutTests
        // pins that), which cannot hold the title band, the label band, the axis labels and the
        // legend at once. The plot is what has to survive, so the title is not drawn at all.
        const int areaH = 117;
        using var renderer = new TextCapturingRenderer(1000, areaH);
        AltitudeChartRenderer.Render(renderer, BuildState(), FontResolver.ResolveSystemFont(),
            0, 0, 1000, areaH);

        renderer.Texts.ShouldNotContain(t => t.Text.StartsWith("Observation Schedule", StringComparison.Ordinal),
            "a title that cannot fit its band must be dropped, not drawn over the zone labels");

        // ...and the plot keeps a usable height, which is the reason the title yields rather than
        // the plot. A negative plot height would invert the altitude axis.
        var layout = AltitudeChartRenderer.GetChartPlotLayout(BuildState(), 0, 0, 1000, areaH);
        layout.PlotH.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetChartPlotLayout_AgreesWithTheDrawnChart()
    {
        // The public getter (used for the time shade + mouse follower drawn over a cached chart
        // texture) restated the margin arithmetic; it now shares VerticalLayout with Render. The
        // twilight labels sit immediately above plotY, so their rows locate the drawn plot.
        const int areaH = 700;
        using var renderer = new TextCapturingRenderer(1000, areaH);
        var state = BuildState();
        AltitudeChartRenderer.Render(renderer, state, FontResolver.ResolveSystemFont(), 0, 0, 1000, areaH);

        var layout = AltitudeChartRenderer.GetChartPlotLayout(state, 0, 0, 1000, areaH);
        var lowestZoneLabelBottom = renderer.Texts
            .Where(t => t.Text is "Civil" or "Naut." or "Astro")
            .Max(t => t.Rect.LowerRight.Y);

        lowestZoneLabelBottom.ShouldBeLessThanOrEqualTo(layout.PlotY,
            "the getter's PlotY must match the plot the renderer drew below those labels");
        (layout.PlotY - lowestZoneLabelBottom).ShouldBeLessThanOrEqualTo(12,
            "the label band sits immediately above the plot, so the two must nearly touch");
    }
}
