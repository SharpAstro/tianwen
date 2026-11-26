using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas's object panel keeps its text column clear of the close button at every display scale (#1447): the column's inset
/// is in design units, as the viewer's copy of the panel always had it, so above 100% the rows no longer run up to or under the
/// close. Through the REAL tab render on the CPU surface: the panel and the close are read from the regions the frame registered,
/// the rows from the rect they were painted into, and the title's ink from the surface.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapInfoPanelInsetTests
{
    private const string LongName = "NGC 7000 North America Nebula in Cygnus, a deliberately long object name";

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task TheTextColumnClearsTheCloseButtonAtEveryScale(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = await SkyMapAtlasNightCurveTests.RigAsync(ct, dpiScale);
        rig.Select(LongName, 20.98, 44.33);
        await rig.RenderSettledAsync();

        var regions = rig.Tab.GetRegisteredRegions();
        var border = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "InfoPanelBackground" });
        var close = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "ObjectInfoClose" });
        var rows = rig.Tab.LastPanelTextRect.ShouldNotBeNull("the panel laid no text rows");

        // The border is the panel's rect grown by one pixel on every side.
        var panelLeft = border.X + 1f;
        (rows.X - panelLeft).ShouldBe(ObjectInfoPanel.DesignTextInsetLeft * dpiScale, 0.01f,
            "the rows' left inset is not the design inset at this scale");
        (close.X - (rows.X + rows.Width)).ShouldBeGreaterThanOrEqualTo(
            ((ObjectInfoPanel.DesignTextInsetRight - ObjectInfoPanel.DesignCloseSize) * dpiScale) - 0.01f,
            "the rows run up to or under the close button");
    }

    /// <summary>
    /// What a person sees: a long title, cut to its row with an ellipsis, ends the same gap short of the close button at every scale
    /// (the gap at 1x, scaled), read from the surface. With the column's inset in raw pixels the ellipsis came within 5 px of the
    /// close at 2x, where the gap is 20.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task ALongTitleEndsAGapShortOfTheCloseButton(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = await SkyMapAtlasNightCurveTests.RigAsync(ct, dpiScale);
        rig.Select(LongName, 20.98, 44.33);
        await rig.RenderSettledAsync();

        var close = rig.Tab.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "ObjectInfoClose" });
        var rows = rig.Tab.LastPanelTextRect.ShouldNotBeNull("the panel laid no text rows");

        // The panel's ground, read in the rows' left inset beside the title, where nothing is drawn.
        var pixels = rig.Renderer.Surface.Pixels;
        int At(int x, int y) => ((y * rig.Width) + x) * 4;
        var ground = At((int)rows.X - 2, (int)close.Y + (int)(close.Height / 2));
        var (gr, gg, gb) = (pixels[ground], pixels[ground + 1], pixels[ground + 2]);

        // The rightmost ink in the title's band (the close's height, at the top of the panel), up to the close's left fifth: the
        // "X" is centred in its square, so none of it is drawn there and any ink found is the title's.
        var rightmost = -1;
        var x1 = (int)(close.X + (close.Width / 5f));
        for (var y = (int)MathF.Ceiling(close.Y); y < (int)(close.Y + close.Height); y++)
        {
            for (var x = (int)rows.X; x < x1; x++)
            {
                var i = At(x, y);
                if (x > rightmost
                    && Math.Abs(pixels[i] - gr) + Math.Abs(pixels[i + 1] - gg) + Math.Abs(pixels[i + 2] - gb) > 24)
                {
                    rightmost = x;
                }
            }
        }

        rightmost.ShouldBeGreaterThan((int)rows.X, "no title was drawn");
        // One pixel of slack for the ellipsis' anti-aliased edge.
        (close.X - (rightmost + 1)).ShouldBeGreaterThanOrEqualTo(
            ((ObjectInfoPanel.DesignTextInsetRight - ObjectInfoPanel.DesignCloseSize) * dpiScale) - 1f,
            "the title's ink comes closer to the close button than the gap it keeps at 1x, scaled");
    }
}
