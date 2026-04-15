using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure state-mutation tests for <see cref="ViewerActions"/>.
/// No SDL, no DI, no async I/O — just <see cref="ViewerState"/>.
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
    public void ToggleStretch_WhenNone_SetsUnlinked()
    {
        var state = new ViewerState { StretchMode = StretchMode.None };

        ViewerActions.ToggleStretch(state);

        state.StretchMode.ShouldBe(StretchMode.Unlinked);
        state.HistogramLogScale.ShouldBeFalse();
    }

    // --- CycleStretchLink ---

    [Fact]
    public void CycleStretchLink_Forward_CyclesUnlinkedLinkedLuma()
    {
        var state = new ViewerState { StretchMode = StretchMode.Unlinked };

        ViewerActions.CycleStretchLink(state);
        state.StretchMode.ShouldBe(StretchMode.Linked);

        ViewerActions.CycleStretchLink(state);
        state.StretchMode.ShouldBe(StretchMode.Luma);

        ViewerActions.CycleStretchLink(state);
        state.StretchMode.ShouldBe(StretchMode.Unlinked);
    }

    [Fact]
    public void CycleStretchLink_Reverse_CyclesBackward()
    {
        var state = new ViewerState { StretchMode = StretchMode.Unlinked };

        ViewerActions.CycleStretchLink(state, reverse: true);
        state.StretchMode.ShouldBe(StretchMode.Luma);

        ViewerActions.CycleStretchLink(state, reverse: true);
        state.StretchMode.ShouldBe(StretchMode.Linked);
    }

    [Fact]
    public void CycleStretchLink_WhenNone_StartsFromUnlinked()
    {
        var state = new ViewerState { StretchMode = StretchMode.None };

        ViewerActions.CycleStretchLink(state);

        state.StretchMode.ShouldBe(StretchMode.Linked);
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

    // --- Pan operations ---

    [Fact]
    public void BeginPan_SetsIsPanningAndPanStart()
    {
        var state = new ViewerState();

        ViewerActions.BeginPan(state, 100f, 200f);

        state.IsPanning.ShouldBeTrue();
        state.PanStart.ShouldBe((100f, 200f));
    }

    [Fact]
    public void UpdatePan_WhenPanning_UpdatesPanOffset()
    {
        var state = new ViewerState { IsPanning = true, PanStart = (100f, 100f), PanOffset = (0f, 0f) };

        ViewerActions.UpdatePan(state, 110f, 120f);

        state.PanOffset.ShouldBe((10f, 20f));
        state.PanStart.ShouldBe((110f, 120f));
    }

    [Fact]
    public void UpdatePan_WhenNotPanning_DoesNothing()
    {
        var state = new ViewerState { IsPanning = false, PanOffset = (5f, 5f) };

        ViewerActions.UpdatePan(state, 999f, 999f);

        state.PanOffset.ShouldBe((5f, 5f));
    }

    [Fact]
    public void EndPan_ClearsIsPanning()
    {
        var state = new ViewerState { IsPanning = true };

        ViewerActions.EndPan(state);

        state.IsPanning.ShouldBeFalse();
    }

    // --- ScrollFileList ---

    [Fact]
    public void ScrollFileList_ClampsToValidRange()
    {
        var state = new ViewerState
        {
            ImageFileNames = ["a.fits", "b.fits", "c.fits"],
            FileListScrollOffset = 1
        };

        ViewerActions.ScrollFileList(state, 100);
        state.FileListScrollOffset.ShouldBe(2);

        ViewerActions.ScrollFileList(state, -100);
        state.FileListScrollOffset.ShouldBe(0);
    }
}
