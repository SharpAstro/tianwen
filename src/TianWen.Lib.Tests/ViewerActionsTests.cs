using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure state-mutation tests for <see cref="ViewerActions"/>.
/// No SDL, no DI, no async I/O, just <see cref="ViewerState"/>.
/// </summary>
public class ViewerActionsTests
{
    // --- ToggleStretch ---

    [Fact]
    public void ToggleStretch_WhenUnlinked_SetsNone()
    {
        var state = new ViewerState { StretchMode = StretchMode.Unlinked };

        ViewerActions.ToggleStretch(state);

        state.StretchMode.ShouldBe(StretchMode.None);
        state.HistogramLogScale.ShouldBeTrue();
        state.NeedsRedraw.ShouldBeTrue();
    }

    [Fact]
    public void ToggleStretch_WhenNone_SetsTheDefaultMode()
    {
        var state = new ViewerState { StretchMode = StretchMode.None };

        ViewerActions.ToggleStretch(state);

        // Re-enabling lands on whatever a fresh viewer would show, not on a mode named here: the
        // default was Unlinked, then Linked, now Auto, and stating it twice is how the two drift.
        state.StretchMode.ShouldBe(ViewerActions.DefaultStretchMode);
        state.HistogramLogScale.ShouldBeFalse();
    }

    [Fact]
    public void DefaultStretchMode_IsAuto()
    {
        // Named explicitly ONCE, because it is a deliberate behavioural choice and not an
        // implementation detail: Auto resolves per frame to Linked when a colour calibration is
        // active (so the WB shows) and Unlinked otherwise (so an uncalibrated frame has no cast to
        // guess at). The default was Unlinked (SPCC looked like a no-op), then Linked (an
        // uncalibrated frame could open on a cast); Auto picks the right one of the two per frame.
        ViewerActions.DefaultStretchMode.ShouldBe(StretchMode.Auto);
        new ViewerState().StretchMode.ShouldBe(StretchMode.Auto);
        DisplayControls.Defaults.StretchMode.ShouldBe(StretchMode.Auto);
    }

    // --- CycleStretchLink ---

    [Fact]
    public void CycleStretchLink_Forward_VisitsEveryModeInOrderAndWrapsAround()
    {
        // Asserted against the cycle table rather than against three named modes, so re-ordering the
        // table (which is what changing the default does) does not need this test rewritten.
        var modes = ViewerActions.StretchLinkModes;
        var state = new ViewerState { StretchMode = modes[0] };

        for (var i = 1; i <= modes.Length; i++)
        {
            ViewerActions.CycleStretchLink(state);
            state.StretchMode.ShouldBe(modes[i % modes.Length]);
        }
    }

    [Fact]
    public void CycleStretchLink_Reverse_WalksTheTableBackwards()
    {
        var modes = ViewerActions.StretchLinkModes;
        var state = new ViewerState { StretchMode = modes[0] };

        for (var i = 1; i <= modes.Length; i++)
        {
            ViewerActions.CycleStretchLink(state, reverse: true);
            state.StretchMode.ShouldBe(modes[(modes.Length - i) % modes.Length]);
        }
    }

    [Fact]
    public void CycleStretchLink_WhenNone_StartsFromTheTableHead()
    {
        // None is not in the cycle, so an unknown index falls back to slot 0 and one step lands on
        // slot 1 -- the first mode a user reaches from a linear view.
        var modes = ViewerActions.StretchLinkModes;
        var state = new ViewerState { StretchMode = StretchMode.None };

        ViewerActions.CycleStretchLink(state);

        state.StretchMode.ShouldBe(modes[1]);
    }

    // --- CycleCurvesBoost ---

    [Fact]
    public void CycleCurvesBoost_Forward_WrapsAroundPresets()
    {
        var state = new ViewerState();
        state.CurvesBoostIndex.ShouldBe(0);

        for (var i = 0; i < ViewerState.CurvesBoostPresets.Length; i++)
        {
            ViewerActions.CycleCurvesBoost(state);
        }

        // After cycling through all presets, wraps back to 0
        state.CurvesBoostIndex.ShouldBe(0);
        state.CurvesBoost.ShouldBe(ViewerState.CurvesBoostPresets[0]);
    }

    [Fact]
    public void CycleCurvesBoost_Reverse_WrapsToLastPreset()
    {
        var state = new ViewerState();

        ViewerActions.CycleCurvesBoost(state, reverse: true);

        state.CurvesBoostIndex.ShouldBe(ViewerState.CurvesBoostPresets.Length - 1);
        state.CurvesBoost.ShouldBe(ViewerState.CurvesBoostPresets[^1]);
    }

    // --- CycleHdr ---

    [Fact]
    public void CycleHdr_SetsAmountAndKneeFromPresets()
    {
        var state = new ViewerState();

        ViewerActions.CycleHdr(state);

        var expected = ViewerState.HdrPresets[1];
        state.HdrAmount.ShouldBe(expected.Amount);
        state.HdrKnee.ShouldBe(expected.Knee);
        state.HdrPresetIndex.ShouldBe(1);
    }

    [Fact]
    public void CycleHdr_WrapsAround()
    {
        var state = new ViewerState();

        for (var i = 0; i < ViewerState.HdrPresets.Length; i++)
        {
            ViewerActions.CycleHdr(state);
        }

        state.HdrPresetIndex.ShouldBe(0);
        state.HdrAmount.ShouldBe(ViewerState.HdrPresets[0].Amount);
    }

    // --- SelectFile ---

    [Fact]
    public void SelectFile_WithValidIndex_SetsRequestedFilePath()
    {
        var state = new ViewerState
        {
            CurrentFolder = "/test",
            ImageFileNames = ["a.fits", "b.fits", "c.fits"],
            SelectedFileIndex = 0
        };

        ViewerActions.SelectFile(state, 1);

        state.SelectedFileIndex.ShouldBe(1);
        state.RequestedFilePath.ShouldBe(Path.Combine("/test", "b.fits"));
    }

    [Fact]
    public void SelectFile_SameIndex_DoesNothing()
    {
        var state = new ViewerState
        {
            CurrentFolder = "/test",
            ImageFileNames = ["a.fits"],
            SelectedFileIndex = 0
        };

        ViewerActions.SelectFile(state, 0);

        state.RequestedFilePath.ShouldBeNull();
    }

    [Fact]
    public void SelectFile_OutOfRange_DoesNothing()
    {
        var state = new ViewerState
        {
            CurrentFolder = "/test",
            ImageFileNames = ["a.fits"]
        };

        ViewerActions.SelectFile(state, 5);

        state.RequestedFilePath.ShouldBeNull();
        state.SelectedFileIndex.ShouldBe(-1);
    }

    [Fact]
    public void SelectFile_NoFolder_DoesNothing()
    {
        var state = new ViewerState
        {
            CurrentFolder = null,
            ImageFileNames = ["a.fits"]
        };

        ViewerActions.SelectFile(state, 0);

        state.RequestedFilePath.ShouldBeNull();
    }

    // --- HandleToolbarAction ---

    [Theory]
    [InlineData(ToolbarAction.StretchToggle)]
    [InlineData(ToolbarAction.StretchLink)]
    [InlineData(ToolbarAction.StretchParams)]
    [InlineData(ToolbarAction.Debayer)]
    [InlineData(ToolbarAction.CurvesBoost)]
    [InlineData(ToolbarAction.Hdr)]
    [InlineData(ToolbarAction.Grid)]
    [InlineData(ToolbarAction.Overlays)]
    [InlineData(ToolbarAction.Stars)]
    [InlineData(ToolbarAction.ZoomFit)]
    [InlineData(ToolbarAction.ZoomActual)]
    public void HandleToolbarAction_PureActions_ReturnTrue(ToolbarAction action)
    {
        var state = new ViewerState();

        ViewerActions.HandleToolbarAction(state, document: null, action).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ToolbarAction.Open)]
    [InlineData(ToolbarAction.Save)]
    [InlineData(ToolbarAction.PlateSolve)]
    public void HandleToolbarAction_DIDependentActions_ReturnFalse(ToolbarAction action)
    {
        var state = new ViewerState();

        ViewerActions.HandleToolbarAction(state, document: null, action).ShouldBeFalse();
    }

    // --- ScanFolder ---

    [Fact]
    public void ScanFolder_WithMatchingFiles_PopulatesListAndSelectsCurrent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ViewerActionsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "star1.fits"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "star2.fit"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "notes.txt"), []);

            var state = new ViewerState();
            ViewerActions.ScanFolder(state, tempDir, "star2.fit");

            state.ImageFileNames.Count.ShouldBe(2);
            state.ImageFileNames.ShouldContain("star1.fits");
            state.ImageFileNames.ShouldContain("star2.fit");
            state.SelectedFileIndex.ShouldBeGreaterThanOrEqualTo(0);
            state.ImageFileNames[state.SelectedFileIndex].ShouldBe("star2.fit");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanFolder_NonExistentPath_ClearsListAndSetsIndexMinusOne()
    {
        var state = new ViewerState
        {
            ImageFileNames = ["old.fits"],
            SelectedFileIndex = 0
        };

        ViewerActions.ScanFolder(state, "/nonexistent/path");

        state.ImageFileNames.ShouldBeEmpty();
        state.SelectedFileIndex.ShouldBe(-1);
    }

    // --- HandleFileDrop ---

    [Fact]
    public void HandleFileDrop_UnsupportedExtension_DoesNothing()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(tempFile, []);
        try
        {
            var state = new ViewerState();
            ViewerActions.HandleFileDrop(state, tempFile);

            state.RequestedFilePath.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void HandleFileDrop_SupportedFile_SetsRequestedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ViewerActionsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fitsFile = Path.Combine(tempDir, "image.fits");
        File.WriteAllBytes(fitsFile, []);
        try
        {
            var state = new ViewerState();
            ViewerActions.HandleFileDrop(state, fitsFile);

            state.RequestedFilePath.ShouldBe(fitsFile);
            state.NeedsRedraw.ShouldBeTrue();
            state.ImageFileNames.ShouldContain("image.fits");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- Zoom operations ---

    [Fact]
    public void ZoomToFit_SetsFlagAndClearsPanOffset()
    {
        var state = new ViewerState { ZoomToFit = false, PanOffset = (10f, 20f) };

        ViewerActions.ZoomToFit(state);

        state.ZoomToFit.ShouldBeTrue();
        state.PanOffset.ShouldBe((0f, 0f));
    }

    [Fact]
    public void ZoomIn_IncreasesZoomAndClearsZoomToFit()
    {
        var state = new ViewerState { Zoom = 1.0f, ZoomToFit = true };

        ViewerActions.ZoomIn(state);

        state.Zoom.ShouldBeGreaterThan(1.0f);
        state.ZoomToFit.ShouldBeFalse();
        state.NeedsRedraw.ShouldBeTrue();
    }

    [Fact]
    public void ZoomOut_DecreasesZoom()
    {
        var state = new ViewerState { Zoom = 1.0f };

        ViewerActions.ZoomOut(state);

        state.Zoom.ShouldBeLessThan(1.0f);
        state.ZoomToFit.ShouldBeFalse();
    }

    [Fact]
    public void ZoomToActual_SetsZoomToOne()
    {
        var state = new ViewerState { Zoom = 2.5f, ZoomToFit = true };

        ViewerActions.ZoomToActual(state);

        state.Zoom.ShouldBe(1.0f);
        state.ZoomToFit.ShouldBeFalse();
    }

    // The pan gesture (Begin/Update/EndPan + IsPanning/PanStart) and the cursor-anchored wheel zoom now
    // live in the DIR.Lib PanZoomController (the renderer seeds it from ViewerState and writes the
    // display transform back); begin/update/end + anchor math coverage moved to
    // DIR.Lib.Tests.PanZoomControllerTests.

    // File-list scroll clamping / wheel accumulation now lives in the DIR.Lib ListScrollController (the
    // renderer owns the offset); ScrollFileList + FileListScrollOffset were removed, and the clamp/accumulate
    // coverage moved to DIR.Lib.Tests.ListScrollControllerTests.
    // --- Wheel over a multi-option toolbar button (TryHandleToolbarWheel) ---

    /// <summary>
    /// The gesture as asked for: scroll up on the boost button boosts UP. The presets are an ascending
    /// ladder ([0, 0.25, 0.50, 1.0, 1.5]), so the direction has a meaning the click does not have.
    /// </summary>
    [Fact]
    public void ToolbarWheel_UpOnBoost_StepsToTheNextStrongerPreset()
    {
        var state = new ViewerState();

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.CurvesBoost, steps: 1).ShouldBeTrue();

        state.CurvesBoostIndex.ShouldBe(1);
        state.CurvesBoost.ShouldBe(ViewerState.CurvesBoostPresets[1]);
        state.NeedsRedraw.ShouldBeTrue();
    }

    /// <summary>
    /// The reason the ramps clamp instead of wrapping, and the case worth pinning: one notch past the
    /// top must NOT land back on zero. A wrap there turns the effect off in response to a request for
    /// more of it, which reads as a broken control rather than as a cycle.
    /// </summary>
    [Fact]
    public void ToolbarWheel_UpAtTheStrongestBoost_StaysThereRatherThanWrappingToOff()
    {
        var top = ViewerState.CurvesBoostPresets.Length - 1;
        var state = new ViewerState();
        ViewerActions.SetCurvesBoostIndex(state, top);

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.CurvesBoost, steps: 1).ShouldBeTrue();

        state.CurvesBoostIndex.ShouldBe(top);
        state.CurvesBoost.ShouldBe(ViewerState.CurvesBoostPresets[top]);
    }

    [Fact]
    public void ToolbarWheel_DownAtZeroBoost_StaysAtZero()
    {
        var state = new ViewerState();

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.CurvesBoost, steps: -1).ShouldBeTrue();

        state.CurvesBoostIndex.ShouldBe(0);
        state.CurvesBoost.ShouldBe(ViewerState.CurvesBoostPresets[0]);
    }

    /// <summary>HDR is the other ascending ladder, so it clamps for the same reason.</summary>
    [Fact]
    public void ToolbarWheel_UpAtTheStrongestHdr_StaysThereRatherThanWrappingToOff()
    {
        var top = ViewerState.HdrPresets.Length - 1;
        var state = new ViewerState();
        ViewerActions.SetHdrPresetIndex(state, top);

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Hdr, steps: 1).ShouldBeTrue();

        state.HdrPresetIndex.ShouldBe(top);
        state.HdrAmount.ShouldBe(ViewerState.HdrPresets[top].Amount);
    }

    /// <summary>
    /// An unordered set wraps, because clamping would strand the user at an end with no way onward.
    /// Three modes, so a fourth notch returns to the first.
    /// </summary>
    [Fact]
    public void ToolbarWheel_OnStretchLink_WrapsThroughEveryMode()
    {
        var state = new ViewerState { StretchMode = ViewerActions.StretchLinkModes[0] };

        for (var i = 0; i < ViewerActions.StretchLinkModes.Length; i++)
        {
            ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.StretchLink, steps: 1).ShouldBeTrue();
        }

        state.StretchMode.ShouldBe(ViewerActions.StretchLinkModes[0]);
    }

    /// <summary>
    /// Scrolling down must undo scrolling up exactly, for every starting point. This is what the added
    /// reverse branch has to satisfy, and asserting the round trip pins it without restating the
    /// forward order in the test (which would just be the implementation written twice).
    /// </summary>
    [Theory]
    [InlineData(ChannelView.Composite)]
    [InlineData(ChannelView.Red)]
    [InlineData(ChannelView.Green)]
    [InlineData(ChannelView.Blue)]
    public void CycleChannelView_ReverseIsTheExactInverseOfForward(ChannelView start)
    {
        var state = new ViewerState { ChannelView = start };

        ViewerActions.CycleChannelView(state, channelCount: 3);
        ViewerActions.CycleChannelView(state, channelCount: 3, reverse: true);

        state.ChannelView.ShouldBe(start);
    }

    /// <summary>
    /// The whole reason this is not <c>HandleToolbarAction(reverse: !up)</c>. These three overload
    /// <c>reverse</c> to mean a different action, so the wheel must not reach them: a scroll down on
    /// Compare would re-pin the before image and discard the comparison baseline, on Zoom it would
    /// toggle Fit/1:1, and Grid is a plain toggle with no cycle to step. Reporting NOT handled is what
    /// leaves the event for whatever claims it next.
    /// </summary>
    [Theory]
    [InlineData(ToolbarAction.Compare)]
    [InlineData(ToolbarAction.Grid)]
    [InlineData(ToolbarAction.Open)]
    [InlineData(ToolbarAction.Enhance)]
    public void ToolbarWheel_OnAnActionWhoseReverseMeansSomethingElse_IsNotHandled(ToolbarAction action)
    {
        // NeedsRedraw defaults to TRUE (a fresh state wants its first paint), so it has to be cleared
        // for the assertion below to mean anything -- asserting it false on a fresh state would be
        // checking a default rather than an effect.
        var state = new ViewerState { NeedsRedraw = false };
        var boostBefore = state.CurvesBoostIndex;

        ViewerActions.TryHandleToolbarWheel(state, document: null, action, steps: -1).ShouldBeFalse();

        state.ShowGrid.ShouldBeFalse();
        state.CurvesBoostIndex.ShouldBe(boostBefore);
        state.NeedsRedraw.ShouldBeFalse();
    }
    /// <summary>A single event carrying several notches moves several options, rather than one.</summary>
    [Fact]
    public void ToolbarWheel_WithAMultiNotchDelta_StepsThatManyOptions()
    {
        var state = new ViewerState();

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.CurvesBoost, steps: 3).ShouldBeTrue();

        state.CurvesBoostIndex.ShouldBe(3);
    }

    /// <summary>
    /// The partial-notch case a trackpad produces: the caller has accumulated less than one notch, so it
    /// passes 0. That must be reported HANDLED (the wheel does drive this button, so the event is claimed
    /// and must not fall through to the image zoom) while changing nothing.
    /// </summary>
    [Fact]
    public void ToolbarWheel_WithZeroSteps_IsHandledAndChangesNothing()
    {
        var state = new ViewerState { NeedsRedraw = false };
        ViewerActions.SetCurvesBoostIndex(state, 2);
        state.NeedsRedraw = false;

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.CurvesBoost, steps: 0).ShouldBeTrue();

        state.CurvesBoostIndex.ShouldBe(2);
    }

    /// <summary>Multi-notch on a WRAPPING cycler applies the cycle repeatedly, so a full lap returns to
    /// the start rather than landing somewhere an index-offset would have put it.</summary>
    [Fact]
    public void ToolbarWheel_MultiNotchOnStretchLink_WrapsRatherThanClamping()
    {
        var len = ViewerActions.StretchLinkModes.Length;
        var state = new ViewerState { StretchMode = ViewerActions.StretchLinkModes[0] };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.StretchLink, steps: len).ShouldBeTrue();

        state.StretchMode.ShouldBe(ViewerActions.StretchLinkModes[0]);
    }
    // --- Wheel over the Zoom button: a ratio ladder whose axis runs backwards ---

    /// <summary>Up means zoom IN, which on this control means a SMALLER denominator. Getting the sign
    /// wrong is invisible in a build and obvious in the hand.</summary>
    [Fact]
    public void ToolbarWheel_UpOnZoom_MovesTowardOneToOne()
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 1f / 4f };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: 1).ShouldBeTrue();

        state.Zoom.ShouldBe(1f / 3f, 0.0001f);
        state.ZoomToFit.ShouldBeFalse();
    }

    [Fact]
    public void ToolbarWheel_DownOnZoom_MovesAwayFromOneToOne()
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 1f / 4f };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: -1).ShouldBeTrue();

        state.Zoom.ShouldBe(1f / 5f, 0.0001f);
    }

    [Theory]
    [InlineData(1, 1)]                                             // at 1:1, up stays
    [InlineData(9, -1)]                                            // at 1:9, down stays
    public void ToolbarWheel_AtEitherEndOfTheZoomLadder_Clamps(int denominator, int steps)
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 1f / denominator };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps).ShouldBeTrue();

        state.Zoom.ShouldBe(1f / denominator, 0.0001f);
    }

    /// <summary>
    /// The wheel over the IMAGE zooms by a 1.15 factor, so the zoom is routinely BETWEEN rungs. One
    /// notch from there must land on the adjacent rung in the direction asked for, not skip past it:
    /// 43% up is 1:2 (50%), and 43% down is 1:3 (33%).
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(-1, 3)]
    public void ToolbarWheel_FromAnOffRungZoom_SnapsTowardTheDirectionOfTravel(int steps, int expectedDenominator)
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 0.43f };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps).ShouldBeTrue();

        state.Zoom.ShouldBe(1f / expectedDenominator, 0.0001f);
    }

    /// <summary>
    /// A zoom magnified past 1:1 is above the ladder, so an up-notch must do NOTHING. Clamping to the
    /// top rung would zoom OUT in answer to a zoom-IN request -- the same surprise the boost ramp clamps
    /// to avoid, in the one place where the clamp itself would cause it.
    /// </summary>
    [Fact]
    public void ToolbarWheel_UpFromAZoomBeyondOneToOne_DoesNotClampBackDownToIt()
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 2f };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: 1).ShouldBeTrue();

        state.Zoom.ShouldBe(2f, 0.0001f);
    }

    /// <summary>Down from a magnified zoom DOES rejoin the ladder at the top rung, which is the correct
    /// direction and the reason the guard above is one-sided.</summary>
    [Fact]
    public void ToolbarWheel_DownFromAZoomBeyondOneToOne_RejoinsTheLadderAtOneToOne()
    {
        var state = new ViewerState { ZoomToFit = false, Zoom = 2f };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: -1).ShouldBeTrue();

        state.Zoom.ShouldBe(1f, 0.0001f);
    }

    /// <summary>
    /// Fit is a mode, not a rung: it has no number to step from (ZoomToFit does not write Zoom at all --
    /// the renderer derives the fit scale) and no fixed place on the ladder, since for a large image it
    /// sits below 1:9 and for a small one above 1:1. So up leaves it for the one rung that means
    /// something absolute, and down stays put rather than guessing.
    /// </summary>
    [Fact]
    public void ToolbarWheel_UpFromFit_LandsOnOneToOne()
    {
        var state = new ViewerState { ZoomToFit = true };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: 1).ShouldBeTrue();

        state.ZoomToFit.ShouldBeFalse();
        state.Zoom.ShouldBe(1f, 0.0001f);
    }

    [Fact]
    public void ToolbarWheel_DownFromFit_StaysOnFit()
    {
        var state = new ViewerState { ZoomToFit = true };

        ViewerActions.TryHandleToolbarWheel(state, document: null, ToolbarAction.Zoom, steps: -1).ShouldBeTrue();

        state.ZoomToFit.ShouldBeTrue();
    }
}
