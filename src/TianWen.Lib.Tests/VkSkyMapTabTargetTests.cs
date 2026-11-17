using Shouldly;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the sky pass restores its viewport and scissor to (<see cref="VkSkyMapTab.FullTargetExtent"/>; docs/plans/atlas-planet-detail.md,
/// A5, "The backdrop"): a window keeps its SWAPCHAIN's extent, which on the published 7.64 renderer is not the renderer's size when the
/// window is maximised to the work area (#1574: 1920 x 1233 against 1920 x 1200), and an offscreen frame, whose context has no swapchain
/// (0 x 0), takes the renderer's.
/// </summary>
public sealed class VkSkyMapTabTargetTests
{
    [Theory]
    [InlineData(1920u, 1200u, 1920u, 1233u, 1920u, 1200u)]
    [InlineData(0u, 0u, 900u, 600u, 900u, 600u)]
    [InlineData(1280u, 800u, 1280u, 800u, 1280u, 800u)]
    public void AWindowKeepsItsSwapchainAndAnOffscreenFrameTakesTheRenderers(
        uint swapchainWidth, uint swapchainHeight, uint rendererWidth, uint rendererHeight, uint width, uint height)
        => VkSkyMapTab.FullTargetExtent(swapchainWidth, swapchainHeight, rendererWidth, rendererHeight).ShouldBe((width, height));

    /// <summary>
    /// A frame that repaints only the top bar's clock leaves the map's pipeline draws out (<see cref="VkSkyMapTab.RegionMissesMap"/>):
    /// on a software rasteriser their vertices are shaded whether or not the scissor keeps a fragment. The map here is the atlas at
    /// 1200x800: 48,33 to the window's corner. A region with one pixel inside the map does not miss it, or the hover's repaint and
    /// the info strip's would lose the sky behind them.
    /// </summary>
    [Theory]
    [InlineData(995, 0, 201u, 33u, true)]     // the clock, ending where the map begins
    [InlineData(995, 0, 201u, 34u, false)]    // one row into the map
    [InlineData(0, 300, 48u, 100u, true)]     // the sidebar, ending at the map's left edge
    [InlineData(0, 300, 49u, 100u, false)]    // one column into the map
    [InlineData(500, 400, 60u, 60u, false)]   // a hover's rectangle inside the map
    [InlineData(1200, 33, 10u, 10u, true)]    // past the right edge
    [InlineData(48, 800, 10u, 10u, true)]     // past the bottom edge
    [InlineData(0, 0, 1200u, 800u, false)]    // the whole window
    public void AFrameThatRepaintsNoPartOfTheMapLeavesItsDrawsOut(int x, int y, uint width, uint height, bool misses)
        => VkSkyMapTab.RegionMissesMap(x, y, width, height, mapX: 48f, mapY: 33f, mapWidth: 1152f, mapHeight: 767f).ShouldBe(misses);

    // A map that begins part way into a pixel row owns that row: at a display scale of 1.15 it begins at 32.2, and a
    // region that ends at row 32 misses it where one that ends at 33 does not.
    [Theory]
    [InlineData(32u, true)]
    [InlineData(33u, false)]
    public void AMapThatBeginsInsideAPixelRowOwnsThatRow(uint regionHeight, bool misses)
        => VkSkyMapTab.RegionMissesMap(995, 0, 201u, regionHeight, mapX: 59.8f, mapY: 32.2f, mapWidth: 1140.2f, mapHeight: 767.8f)
            .ShouldBe(misses);

    [Fact]
    public void AMapWithNoAreaIsMissedByEveryFrame()
        => VkSkyMapTab.RegionMissesMap(0, 0, 1200u, 800u, mapX: 48f, mapY: 33f, mapWidth: 0f, mapHeight: 767f).ShouldBeTrue();
}
