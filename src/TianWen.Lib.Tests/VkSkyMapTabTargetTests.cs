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
}
