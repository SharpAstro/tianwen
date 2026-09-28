using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Equipment tab asks for a profile to be created only once this computer's node has said it runs none (P6: the node
/// holds the profile, and takes seconds to come up). Before that it says it is connecting: the window used to open on
/// "No equipment profile configured" and a Create button for those seconds (the ZWO live check, 2026-09-28).
/// </summary>
[Collection("UI")]
public class EquipmentTabFirstScreenTests
{
    private const uint SurfaceW = 1200;
    private const uint SurfaceH = 800;

    [Fact]
    public void BeforeTheNodeHasAnsweredItSaysItIsConnectingAndOffersNoCreate()
    {
        var appState = new GuiAppState();

        CreateButtons(appState).ShouldBe(0, "the node has not said there is no profile");
        appState.NeedsAProfile.ShouldBeFalse();
        appState.WaitingForLocalNode.ShouldBe("Connecting to this computer's rig...");
    }

    [Fact]
    public void OnceTheNodeSaysItRunsNoneItOffersToCreateOne()
    {
        var appState = new GuiAppState { LocalProfileKnown = true };

        appState.NeedsAProfile.ShouldBeTrue();
        CreateButtons(appState).ShouldBe(1);
    }

    private static int CreateButtons(GuiAppState appState)
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var tab = new EquipmentTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        tab.Render(appState, new RectF32(0f, 0f, SurfaceW, SurfaceH));
        return tab.GetRegisteredRegions().Count(static r => r.Result is HitResult.ButtonHit { Action: "CreateProfile" });
    }
}
