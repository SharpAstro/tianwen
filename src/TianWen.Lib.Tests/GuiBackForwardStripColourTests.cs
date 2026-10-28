using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The rail strip's arrows as PAINTED (#1821), never as handed over: DIR.Lib draws a disabled icon at the midpoint of its
/// colour and the ground, so a contrast figure computed from <see cref="GuiNavigationStrip{TSurface}.ColorsFor"/> was 2 to 3
/// times the one on screen. The ink is read off the strip painted on a CPU surface and held against the rail's own ground.
/// </summary>
// In the "UI" collection: it calls GuiTheme.Apply, which mutates PROCESS-WIDE state (see GuiThemeTests).
[Collection("UI")]
public class GuiBackForwardStripColourTests
{
    private sealed class Navigation(bool back, bool forward) : IGuiNavigation
    {
        public bool CanGoBack => back;
        public bool CanGoForward => forward;
        public bool GoBack() => false;
        public bool GoForward() => false;
    }

    public static TheoryData<UiThemeState, double> EnabledFloors => new()
    {
        { UiThemeState.Light, 3.0 },
        { UiThemeState.Dark, 3.0 },
        { UiThemeState.Night, 3.0 },
    };

    // Night is held to what its palette can reach: red ink with blue at zero, halved by the engine, tops out at 2.9:1.
    public static TheoryData<UiThemeState, double> DimmedFloors => new()
    {
        { UiThemeState.Light, 3.0 },
        { UiThemeState.Dark, 3.0 },
        { UiThemeState.Night, 2.8 },
    };

    [Theory]
    [MemberData(nameof(EnabledFloors))]
    public void AnEnabledArrowKeepsThreeToOneOnTheRail(UiThemeState state, double floor)
    {
        try
        {
            GuiTheme.Apply(state, desktopIsDark: state != UiThemeState.Light);

            var peak = PaintedPeakContrast(canBack: true, canForward: true);

            peak.ShouldBeGreaterThanOrEqualTo(floor, $"{state}: the painted arrow is {peak:F1}:1 on the rail");
        }
        finally
        {
            // Static app state: hand it back the way every other test expects to find it.
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    [Theory]
    [MemberData(nameof(DimmedFloors))]
    public void ADimmedArrowStaysAnIconButReadsDimmerThanALitOne(UiThemeState state, double floor)
    {
        try
        {
            GuiTheme.Apply(state, desktopIsDark: state != UiThemeState.Light);

            var lit = PaintedPeakContrast(canBack: true, canForward: true);
            var dimmed = PaintedPeakContrast(canBack: false, canForward: false);

            dimmed.ShouldBeGreaterThanOrEqualTo(floor, $"{state}: the painted dimmed arrow is {dimmed:F1}:1 on the rail");
            dimmed.ShouldBeLessThan(lit, $"{state}: a dimmed arrow must read dimmer than a lit one");
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    /// <summary>The strongest contrast of any ink pixel against the rail's ground, over a strip painted at design scale.</summary>
    private static double PaintedPeakContrast(bool canBack, bool canForward)
    {
        const int width = 52;
        var height = (int)GuiNavigationStrip<RgbaImage>.BaseHeight;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var strip = new GuiNavigationStrip<RgbaImage>(renderer) { Navigation = new Navigation(canBack, canForward) };
        strip.Render(new RectF32(0, 0, width, height));

        var ground = GuiTheme.Palette.PanelBg;
        var pixels = renderer.Surface.Pixels;
        var peak = 1.0;
        for (var i = 0; i < width * height * 4; i += 4)
        {
            var ink = new RGBAColor32(pixels[i], pixels[i + 1], pixels[i + 2], 255);
            if (ink != ground)
            {
                peak = Math.Max(peak, Contrast(ink, ground));
            }
        }

        // No ink at all would read as 1:1 and fail the floors above, which is the right answer to a strip that painted nothing.
        return peak;
    }

    private static double Linear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(RGBAColor32 c) => 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
