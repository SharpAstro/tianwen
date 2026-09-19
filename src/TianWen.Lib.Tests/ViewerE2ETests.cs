using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// A link on the object panel opens its page, once, through this host. The standalone host's router had
    /// no <c>OpenUrl</c> subscriber, so a press on a link took the hand pointer, was consumed and opened
    /// nothing, while the same panel in the GUI worked (2026-09-18).
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public async Task APressOnThePanelsAtlasLinkOpensThePageOnce(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);
        var opened = new List<string>();
        e2e.Bus.Subscribe<OpenUrlSignal>(sig => opened.Add(sig.Url));

        var link = SelectAndFindTheAtlasLink(e2e);
        e2e.Click(link);

        // Once: the link opens the page and the panel's own callback only says so on the status line.
        opened.ShouldHaveSingleItem().ShouldStartWith(SkyAtlasLink.BaseUrl);
        e2e.State.StatusMessage.ShouldBe("Opening the sky atlas...");
    }

    /// <summary>
    /// Crossing onto a panel link asks for a frame, so the link lights under the pointer, and moving on
    /// inside it does not. This host used to route motion around the router, which is what repaints a
    /// hover, so nothing drew the hover until something unrelated forced a frame; and the router, keyed on
    /// a node remembered from an earlier frame, then asked for a frame on every move inside a lit control
    /// (SharpAstro/DIR.Lib#90).
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public async Task CrossingOntoAPanelLinkRepaintsAndMovingWithinItDoesNot(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);
        var link = SelectAndFindTheAtlasLink(e2e);
        var y = link.Y + (link.Height / 2f);

        e2e.Host.HandlePointer(new InputEvent.MouseMove(link.X - 2f, y));
        e2e.Frame();
        var pixel = e2e.State.CursorImagePosition;

        e2e.Host.HandlePointer(new InputEvent.MouseMove(link.X + 2f, y));
        e2e.State.CursorImagePosition.ShouldBe(pixel,
            "both probes are over one picture pixel, so the readout cannot be what asks for the frame");
        e2e.State.NeedsRedraw.ShouldBeTrue("the pointer crossed onto the link");
        e2e.Frame();

        e2e.Host.HandlePointer(new InputEvent.MouseMove(link.X + 3f, y));
        e2e.State.CursorImagePosition.ShouldBe(pixel);
        e2e.State.NeedsRedraw.ShouldBeFalse("moving within the lit link changes nothing on screen");
    }

    /// <summary>
    /// A popover's dial follows a DRAG through this host, not only the press. The host routed presses
    /// through the router, which arms a dial's drag there, and sent the moves and the release straight to
    /// the viewer, so the armed drag never saw a move: a dial jumped to where it was pressed and stayed
    /// (2026-09-18, the tone popover's Boost). The popover tests drove a copy of the routing that sent
    /// every event through a router, and passed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public async Task APopoverDialFollowsADragAcrossItsTrack(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        // The soft clip's Amount rather than Boost: Boost wants stars, which this synthetic frame has none
        // of. Every dial arms its drag the same way. White balance's R dial is the popover's other kind.
        foreach (var (action, dial) in new[]
                 {
                     (ToolbarAction.Tone, e2e.Viewer.ToneAmountSliderState),
                     (ToolbarAction.WhiteBalance, e2e.Viewer.WhiteBalanceSliderState(0)),
                 })
        {
            e2e.Click(action);
            var track = e2e.Region(hit => hit is HitResult.SliderStateHit { State: var state } && ReferenceEquals(state, dial),
                $"the {action} dial's track");
            var y = track.Y + (track.Height / 2f);

            e2e.Drag(track.X + (track.Width * 0.25f), y, track.X + (track.Width * 0.75f), y);

            // The press alone lands a quarter of the way along; only the moves carry it to three quarters.
            ((dial.Value - dial.Min) / (dial.Max - dial.Min)).ShouldBe(0.75f, 0.05f,
                $"the {action} dial follows the drag to its end at dpi {dpiScale}");
            e2e.Key(InputKey.Escape);
        }
    }

    /// <summary>
    /// Selects an object with no catalogue entry, whose panel therefore has exactly one link, the sky atlas's,
    /// and returns where it was painted.
    /// </summary>
    private static RectF32 SelectAndFindTheAtlasLink(ViewerE2E e2e)
    {
        e2e.State.SelectedObject = SkyMapInfoPanelData.FromPosition(
            "M 42", 5.588, -5.391, double.NaN, double.NaN, DateTimeOffset.UnixEpoch, default);
        e2e.Frame();
        return e2e.Region(hit => hit is HitResult.LinkHit, "the panel's sky-atlas link");
    }

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

    /// <summary>
    /// An open toolbar menu hangs from its button while the window changes size. The anchor was taken once,
    /// at the press, so a "?" menu opened in a small window stayed where that window's right edge had been
    /// after it was maximised, and one opened in a large window hung past the edge of a smaller one
    /// (2026-09-19). The "?" button is pinned to the right edge; Zoom's moves down a row when the bar wraps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public async Task AnOpenToolbarMenuFollowsItsButtonWhenTheWindowResizes(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);

        // Narrower than the window the menu opened in, then narrower than the "?" menu ITSELF (as wide as its
        // install-path line, about 1000 design units: it was pushed to x = 0 and ran off the far side, away
        // from its right-edge button), then wider. Scaled with the DPI so each case is the same case at both.
        var sizes = new[]
        {
            ((uint)(1100 * dpiScale), 700u),
            ((uint)(700 * dpiScale), 700u),
            ((uint)(1800 * dpiScale), 1000u),
        };
        foreach (var action in new[] { ToolbarAction.Shortcuts, ToolbarAction.Zoom })
        {
            e2e.Click(action);
            foreach (var (width, height) in sizes)
            {
                e2e.Resize(width, height);
                e2e.State.ToolbarDropdown.IsOpen.ShouldBeTrue($"{action}'s menu stays open across a resize");

                var button = e2e.ToolbarButton(action);
                var row = DropdownRows.First(e2e.Viewer);
                row.Y.ShouldBe(button.Y + button.Height, 0.5f,
                    $"{action}'s menu hangs from where its button is at {width}x{height}, dpi {dpiScale}");
                // Staying on screen is not enough, and was never what went wrong: the stale menu stayed on
                // screen, under where the button HAD been, so it must span the button it hangs from.
                (row.X <= button.X + button.Width && row.X + row.Width >= button.X).ShouldBeTrue(
                    $"{action}'s menu [{row.X:F0}, {row.X + row.Width:F0}] spans its button "
                    + $"[{button.X:F0}, {button.X + button.Width:F0}] at {width}x{height}, dpi {dpiScale}");
                row.X.ShouldBeGreaterThanOrEqualTo(-0.5f, $"{action}'s menu starts inside the window at {width}x{height}");
                (row.X + row.Width).ShouldBeLessThanOrEqualTo(width + 0.5f,
                    $"{action}'s menu ends inside the window at {width}x{height}, dpi {dpiScale}");
            }

            e2e.Key(InputKey.Escape);
        }
    }

    /// <summary>
    /// The wheel over a menu too long for the window scrolls the menu, not the picture under it. DIR.Lib
    /// 10.2 made a declared dropdown a scroll container the router hands the wheel to, and this host sent
    /// the wheel straight to the viewer, around the router, so it zoomed the image beneath the open "?"
    /// menu instead (2026-09-19, live, on the keyboard-shortcuts page).
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public async Task TheWheelOverALongMenuScrollsTheMenuNotThePicture(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        using var e2e = ViewerE2E.Start(dpiScale, width: 1600, height: 500);
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);
        e2e.Click(ToolbarAction.Shortcuts);

        // The root page's "Keyboard shortcuts" row; a page change reopens the menu on the next frame.
        e2e.Click(e2e.Region(h => h is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId, Index: 3 },
            "the root page's keyboard-shortcuts row"));
        e2e.Frame();
        MenuRowIndices(e2e).Min().ShouldBe(0, "the shortcuts page opens at its top");
        var zoom = (e2e.State.Zoom, e2e.State.ZoomToFit);

        var first = DropdownRows.First(e2e.Viewer);
        e2e.Host.HandlePointer(new InputEvent.Scroll(-3f, first.X + (first.Width / 2f), first.Y + (first.Height * 4f)));
        e2e.Frame();

        MenuRowIndices(e2e).Min().ShouldBeGreaterThan(0, $"the wheel scrolled the menu at dpi {dpiScale}");
        (e2e.State.Zoom, e2e.State.ZoomToFit).ShouldBe(zoom, "the picture under the menu did not zoom");
    }

    private static int[] MenuRowIndices(ViewerE2E e2e)
        => [.. e2e.Viewer.GetRegisteredRegions()
            .Select(r => r.Result)
            .OfType<HitResult.ListItemHit>()
            .Where(h => h.ListId == DropdownMenuState<string>.ListId)
            .Select(h => h.Index)];

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
