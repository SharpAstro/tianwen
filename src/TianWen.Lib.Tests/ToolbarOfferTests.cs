using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The toolbar is what the viewer's HOST offers (<see cref="ToolbarOffer"/>, step 3 of P1 in
/// docs/plans/live-session-preview.md): one canonical order and set of labels, which every host picks from, and
/// the keys for an action only a host can run act only where it is offered.
/// </summary>
[Collection("UI")]
public class ToolbarOfferTests
{
    /// <summary>
    /// Every action a bar can show has a button in the one table, so no host can offer one the bar cannot draw.
    /// The colour calibration is the one without: it runs from inside the white-balance popover.
    /// </summary>
    [Fact]
    public void Every_toolbar_action_but_the_popovers_calibration_has_one_button()
    {
        var buttonActions = Enum.GetValues<ToolbarAction>().Where(action => action is not ToolbarAction.ColorCalibrate).ToArray();

        var buttons = ToolbarOffer.Of(Enum.GetValues<ToolbarAction>()).Buttons.Select(button => button.Action).ToArray();

        buttons.Order().ToArray().ShouldBe(buttonActions.Order().ToArray(), "one button an action");
        buttons[^1].ShouldBe(ToolbarAction.Shortcuts, "help is the last button of every bar");
    }

    /// <summary>A host chooses what is on the bar, never its order: an offer comes out in the viewer's order.</summary>
    [Fact]
    public void An_offer_is_laid_out_in_the_viewers_order_whatever_order_it_names_them_in()
    {
        var offer = ToolbarOffer.Of(ToolbarAction.Shortcuts, ToolbarAction.Zoom, ToolbarAction.StretchToggle, ToolbarAction.FileList);

        offer.Buttons.Select(button => button.Action).ToArray().ShouldBe(new[]
        {
            ToolbarAction.FileList, ToolbarAction.StretchToggle, ToolbarAction.Zoom, ToolbarAction.Shortcuts,
        });
    }

    /// <summary>
    /// The default is the plain file viewer's bar, what a host that says nothing always got: no sky and no
    /// Enhance, which need a map and an AI pipeline, and the one Zoom control rather than Fit and 1:1.
    /// </summary>
    [Fact]
    public void The_file_viewers_offer_is_the_plain_bar()
    {
        var offer = ToolbarOffer.FileViewer;

        foreach (var action in new[] { ToolbarAction.SkyBackdrop, ToolbarAction.Enhance, ToolbarAction.ZoomFit, ToolbarAction.ZoomActual })
        {
            offer.Offers(action).ShouldBeFalse($"{action} is not on the plain bar");
        }
        foreach (var action in new[] { ToolbarAction.Open, ToolbarAction.Save, ToolbarAction.PlateSolve, ToolbarAction.Zoom, ToolbarAction.Shortcuts })
        {
            offer.Offers(action).ShouldBeTrue($"{action} is");
        }
        offer.With(ToolbarAction.Enhance).Offers(ToolbarAction.Enhance).ShouldBeTrue();
        offer.Offers(ToolbarAction.Enhance).ShouldBeFalse("With makes a new offer and leaves this one as it was");
    }

    /// <summary>
    /// The planetary view shows its eight buttons and nothing else, all of them live on its source, which is
    /// never a document.
    /// </summary>
    [Fact]
    public void The_planetary_view_shows_its_eight_buttons_all_live_on_a_live_frame()
    {
        using var renderer = new RgbaImageRenderer(2400, 700);
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus()) { Offer = ToolbarOffer.Planetary };
        var source = new LiveFramePreviewSource();
        source.AcceptFrame(LiveFrame(), freezeStats: false).ShouldBeTrue();

        viewer.Render(source, new ViewerState { ShowFileList = false, ShowInfoPanel = false, ShowHistogram = false });

        var expected = new[]
        {
            ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
            ToolbarAction.Channel, ToolbarAction.Debayer, ToolbarAction.Tone, ToolbarAction.ZoomFit, ToolbarAction.ZoomActual,
        };
        viewer.PaintedToolbarButtons.Select(button => button.Action).ToArray().ShouldBe(expected);
        foreach (var action in expected.Where(action => action is not ToolbarAction.Debayer))
        {
            viewer.TryGetPaintedToolbarRect(action, out _).ShouldBeTrue($"{action} is live: a press on it does something");
        }
    }

    /// <summary>
    /// A key for an action only a host can run presses its button, and only where the host offers it: where
    /// nothing offered a solve, P posted one that nothing ran. Ctrl+O never falls through to O, the annotation.
    /// The host says the solve can run here, since a key also follows its button's enabled state and no frame
    /// is on show: what differs between the two cases is the offer alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_key_for_a_hosts_action_acts_only_where_the_host_offers_it(bool offered)
    {
        using var renderer = new RgbaImageRenderer(600, 400);
        var bus = new SignalBus();
        var viewer = new ViewerE2E.Surface(renderer, bus)
        {
            Offer = offered ? ToolbarOffer.FileViewer : ToolbarOffer.Planetary,
            HostCanRun = action => action is ToolbarAction.PlateSolve ? true : null,
        };
        var state = new ViewerState();
        viewer.Render(null, state);
        var presses = new List<ToolbarAction>();
        viewer.ToolbarPressPolicy = (_, action, _) =>
        {
            presses.Add(action);
            return true;
        };
        var overlay = state.OverlayLevel;

        viewer.HandleInput(new InputEvent.KeyDown(InputKey.P, InputModifier.None));
        viewer.HandleInput(new InputEvent.KeyDown(InputKey.O, InputModifier.Ctrl));
        bus.ProcessPending();

        presses.ToArray().ShouldBe(offered ? new[] { ToolbarAction.PlateSolve, ToolbarAction.Open } : [],
            "a key presses its button, where its button is offered");
        state.OverlayLevel.ShouldBe(overlay, "Ctrl+O never falls through to O");
    }

    private static Image LiveFrame()
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[30, 40];
            for (var y = 0; y < 30; y++)
            {
                for (var x = 0; x < 40; x++)
                {
                    plane[y, x] = 100f * (c + 1) + x + (y * 40);
                }
            }
            planes[c] = plane;
        }

        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, BitDepth.Float32, maxValue: 4000f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }
}
