using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-fits</c> end to end, through the host the window runs (see <see cref="ViewerE2E"/>): what a
/// user does in the first minute with a file open, at an ordinary and a high DPI scale.
/// </summary>
/// <remarks>
/// <para>
/// Each case here is something that shipped broken or was checked by hand in the live inspector because
/// nothing else could: toolbar presses that hit-tested and did nothing under a router, menus drawn at the
/// square of the DPI scale, a file-list row that did not open its file. Two scales are the whole point of
/// the theory: every older viewer test ran at 1x, where applying the scale twice changes nothing.
/// </para>
/// <para>
/// Assertions read STATE and the painted REGIONS, never pixels: the CPU surface does not draw the picture
/// (that is the GPU's), and a region is what a click and the inspector both resolve against.
/// </para>
/// </remarks>
[Collection("Viewer")]
public class ViewerE2ETests
{
    public static TheoryData<float> Scales => new TheoryData<float> { 1f, 1.5f };

    // Every toolbar button that opens a menu on a colour frame with no plate solution. Debayer is absent
    // because it is enabled only for a raw mosaic.
    private static readonly ToolbarAction[] MenuButtons =
    [
        ToolbarAction.Save,
        ToolbarAction.StretchLink,
        ToolbarAction.StretchParams,
        ToolbarAction.Channel,
        ToolbarAction.Zoom,
        ToolbarAction.BackgroundNeutralize,
        ToolbarAction.Shortcuts,
    ];

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task EveryToolbarMenuOpensAtTheButtonsScaleAndEscapeClosesIt(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        foreach (var action in MenuButtons)
        {
            var button = e2e.ToolbarButton(action);
            e2e.Click(button);
            e2e.State.ToolbarDropdown.IsOpen.ShouldBeTrue($"{action} opens its menu");

            // A row is the toolbar font times 1.8 and a button is the bar's height less its spacing, which
            // come out within 2 percent of each other at every scale. Applying the scale twice put the rows
            // at 1.5 times the button at 1.5x, so this ratio is the DPI regression in one number.
            var row = DropdownRows.First(e2e.Viewer);
            (row.Height / button.Height).ShouldBeInRange(0.9f, 1.1f,
                $"{action}'s rows are the height of the button that opened them at {dpiScale}x");

            e2e.Key(InputKey.Escape);
            e2e.State.ToolbarDropdown.IsOpen.ShouldBeFalse($"Escape closes {action}'s menu");
        }

        e2e.ExitRequests.ShouldBe(0, "an open menu takes Escape, so it never reaches the exit binding");
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task TheToneAndWhiteBalancePopoversOpenAndEscapeClosesThem(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        foreach (var (action, popover) in new[]
                 {
                     (ToolbarAction.Tone, e2e.State.TonePopover),
                     (ToolbarAction.WhiteBalance, e2e.State.WhiteBalancePopover),
                 })
        {
            e2e.Click(action);
            popover.IsOpen.ShouldBeTrue($"{action} opens its popover");

            e2e.Key(InputKey.Escape);
            popover.IsOpen.ShouldBeFalse($"Escape closes the {action} popover");
        }

        e2e.ExitRequests.ShouldBe(0, "an open popover takes Escape, so it never reaches the exit binding");
    }

    [Fact]
    public void EscapeWithNothingOpenAsksToExit()
    {
        using var e2e = ViewerE2E.Start(1f);

        e2e.Key(InputKey.Escape);

        e2e.ExitRequests.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task TheToggleButtonsChangeWhatTheyToggle(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        var stretch = e2e.State.StretchMode;
        e2e.Click(ToolbarAction.StretchToggle);
        e2e.State.StretchMode.ShouldNotBe(stretch, "the stretch button toggles the stretch");

        var showFileList = e2e.State.ShowFileList;
        e2e.Click(ToolbarAction.FileList);
        e2e.State.ShowFileList.ShouldBe(!showFileList, "the file-list button toggles the list");
        e2e.Click(ToolbarAction.FileList);

        // One of the four regions that swallowed its press before this host routed presses.
        var collapsed = e2e.State.InfoPanelStatisticsCollapsed;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ToggleStatistics" }, "the statistics heading"));
        e2e.State.InfoPanelStatisticsCollapsed.ShouldBe(!collapsed, "the statistics heading folds the table");
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task TheZoomKeysReachTheViewer(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        e2e.Key(InputKey.R);
        e2e.State.ZoomToFit.ShouldBeFalse("R leaves fit");
        e2e.State.Zoom.ShouldBe(1f, "R is 1:1");

        e2e.Key(InputKey.D2, InputModifier.Ctrl);
        e2e.State.Zoom.ShouldBe(0.5f, "Ctrl+2 is 1:2");

        e2e.Key(InputKey.F);
        e2e.State.ZoomToFit.ShouldBeTrue("F fits the frame again");
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task AFileListRowOpensItsFile(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        var first = e2e.WriteColourFits("a.fits");
        var second = e2e.WriteColourFits("b.fits", level: 500f);
        await e2e.OpenAsync(first, ct);

        var index = e2e.State.ImageFileNames.IndexOf("b.fits");
        index.ShouldBeGreaterThanOrEqualTo(0, "the folder scan lists the second file");
        var row = e2e.Region(h => h is HitResult.ListItemHit { ListId: ImageRendererBase<RgbaImage>.FileListId } item
            && item.Index == index, "the second file's row");

        e2e.Click(row);
        await e2e.PumpUntilAsync(() => e2e.IsShowing(second), "the clicked row's file to open", ct);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task ARightPressOnThePictureOpensItsContextMenu(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        // The fallback the router hands an unclaimed press to, which exists only in this host.
        e2e.Click(e2e.Viewer.ImageArea, MouseButton.Right);

        e2e.State.ToolbarDropdown.IsOpen.ShouldBeTrue("a right press on the picture opens its context menu");
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public async Task ALeftDragOnThePicturePansIt(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);
        e2e.Key(InputKey.R);

        var area = e2e.Viewer.ImageArea;
        var (cx, cy) = (area.X + (area.Width / 2f), area.Y + (area.Height / 2f));
        var before = e2e.State.PanOffset;

        // The other half of the same fallback: an unclaimed left press starts the pan.
        e2e.Drag(cx, cy, cx + 40f, cy + 30f);

        e2e.State.PanOffset.ShouldNotBe(before, "dragging the picture pans it");
    }
}
