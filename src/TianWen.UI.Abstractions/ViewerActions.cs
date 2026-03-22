using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Stateless action handlers that mutate <see cref="ViewerState"/> and <see cref="AstroImageDocument"/>.
/// Shared between all display backends.
/// </summary>
public static class ViewerActions
{
    public static void ToggleStretch(ViewerState state)
    {
        state.StretchMode = state.StretchMode is StretchMode.None ? StretchMode.Unlinked : StretchMode.None;
        state.HistogramLogScale = state.StretchMode is StretchMode.None;
        state.NeedsRedraw = true;
        state.StatusMessage = state.StretchMode is StretchMode.None ? "Stretch: Off" : "Stretch: On";
    }

    // Cycle order for stretch link — excludes None
    private static readonly StretchMode[] StretchLinkModes = [StretchMode.Unlinked, StretchMode.Linked, StretchMode.Luma];

    public static void CycleStretchLink(ViewerState state, bool reverse = false)
    {
        var idx = Array.IndexOf(StretchLinkModes, state.StretchMode);
        if (idx < 0) idx = 0;
        var len = StretchLinkModes.Length;
        idx = (idx + (reverse ? len - 1 : 1)) % len;
        state.StretchMode = StretchLinkModes[idx];
        state.NeedsRedraw = true;
        state.StatusMessage = $"Stretch: {state.StretchMode}";
    }

    public static void CycleChannelView(ViewerState state, int channelCount)
    {
        if (channelCount >= 3)
        {
            state.ChannelView = state.ChannelView switch
            {
                ChannelView.Composite => ChannelView.Red,
                ChannelView.Red => ChannelView.Green,
                ChannelView.Green => ChannelView.Blue,
                ChannelView.Blue => ChannelView.Composite,
                _ => ChannelView.Composite
            };
        }
        else if (channelCount > 1)
        {
            var ch = (int)state.ChannelView;
            ch = ch < (int)ChannelView.Channel0 + channelCount - 1 ? ch + 1 : (int)ChannelView.Composite;
            state.ChannelView = (ChannelView)ch;
        }
        state.NeedsTextureUpdate = true;
        state.StatusMessage = $"Channel: {state.ChannelView}";
    }

    private const int DebayerAlgorithmCount = 4; // None, BilinearMono, VNG, AHD

    public static void CycleDebayerAlgorithm(ViewerState state, bool reverse = false)
    {
        var idx = (int)state.DebayerAlgorithm;
        idx = (idx + (reverse ? DebayerAlgorithmCount - 1 : 1)) % DebayerAlgorithmCount;
        state.DebayerAlgorithm = (DebayerAlgorithm)idx;
        state.NeedsRedraw = true;
        state.StatusMessage = $"Debayer (next load): {state.DebayerAlgorithm.DisplayName}";
    }

    public static void CycleCurvesBoost(ViewerState state, bool reverse = false)
    {
        var len = ViewerState.CurvesBoostPresets.Length;
        state.CurvesBoostIndex = reverse
            ? (state.CurvesBoostIndex - 1 + len) % len
            : (state.CurvesBoostIndex + 1) % len;
        state.CurvesBoost = ViewerState.CurvesBoostPresets[state.CurvesBoostIndex];
        state.NeedsRedraw = true;
        state.StatusMessage = state.CurvesBoost > 0f ? $"Curves Boost: {state.CurvesBoost:P0}" : "Curves Boost: Off";
    }

    public static void CycleHdr(ViewerState state, bool reverse = false)
    {
        var len = ViewerState.HdrPresets.Length;
        state.HdrPresetIndex = reverse
            ? (state.HdrPresetIndex - 1 + len) % len
            : (state.HdrPresetIndex + 1) % len;
        var (amount, knee) = ViewerState.HdrPresets[state.HdrPresetIndex];
        state.HdrAmount = amount;
        state.HdrKnee = knee;
        state.NeedsRedraw = true;
        state.StatusMessage = amount > 0f ? $"HDR: {amount:F1} (knee {knee:F2})" : "HDR: Off";
    }

    public static void CycleStretchPreset(ViewerState state, bool reverse = false)
    {
        var presets = StretchParameters.Presets;
        state.StretchPresetIndex = reverse
            ? (state.StretchPresetIndex - 1 + presets.Length) % presets.Length
            : (state.StretchPresetIndex + 1) % presets.Length;
        state.StretchParameters = presets[state.StretchPresetIndex];
        state.NeedsRedraw = true;
        state.StatusMessage = $"Stretch: {state.StretchParameters}";
    }

    /// <summary>
    /// Reprocesses the image pipeline based on current state.
    /// With GPU stretch, this only triggers a texture re-upload from the debayered image.
    /// </summary>
    public static void Reprocess(ViewerState state)
    {
        state.NeedsReprocess = false;
        state.NeedsTextureUpdate = true;
    }

    /// <summary>
    /// Initiates plate solving in the background.
    /// </summary>
    public static async Task PlateSolveAsync(AstroImageDocument document, ViewerState state, IPlateSolverFactory solverFactory, CancellationToken cancellationToken = default)
    {
        if (state.IsPlateSolving)
        {
            return;
        }

        state.IsPlateSolving = true;
        state.StatusMessage = "Plate solving...";

        try
        {
            var solved = await document.PlateSolveAsync(solverFactory, cancellationToken);
            state.StatusMessage = solved ? "Plate solved" : "Plate solve failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.StatusMessage = $"Plate solve error: {ex.Message}";
        }
        finally
        {
            state.IsPlateSolving = false;
        }
    }

    /// <summary>
    /// Updates cursor pixel info when the mouse moves over the image.
    /// </summary>
    public static void UpdateCursorInfo(AstroImageDocument document, ViewerState state, int imageX, int imageY)
    {
        state.CursorImagePosition = (imageX, imageY);
        state.CursorPixelInfo = document.GetPixelInfo(imageX, imageY);
    }

    /// <summary>
    /// Scans a folder for supported image files and populates the file list.
    /// Optionally selects the file matching <paramref name="currentFileName"/>.
    /// </summary>
    public static void ScanFolder(ViewerState state, string folderPath, string? currentFileName = null)
    {
        state.CurrentFolder = folderPath;
        state.ImageFileNames.Clear();
        state.FileListScrollOffset = 0;

        if (!Directory.Exists(folderPath))
        {
            state.SelectedFileIndex = -1;
            return;
        }

        var files = AstroImageDocument.SupportedPatterns
            .SelectMany(p => Directory.EnumerateFiles(folderPath, p, SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        state.ImageFileNames = files;
        state.SelectedFileIndex = currentFileName is not null
            ? files.FindIndex(f => string.Equals(f, currentFileName, StringComparison.OrdinalIgnoreCase))
            : -1;

        // Ensure the selected file is visible
        if (state.SelectedFileIndex >= 0)
        {
            state.FileListScrollOffset = Math.Max(0, state.SelectedFileIndex - 5);
        }
    }

    /// <summary>
    /// Selects a file from the file list by index and sets <see cref="ViewerState.RequestedFilePath"/>.
    /// </summary>
    public static void SelectFile(ViewerState state, int index)
    {
        if (index < 0 || index >= state.ImageFileNames.Count || state.CurrentFolder is null)
        {
            return;
        }

        if (index == state.SelectedFileIndex)
        {
            return;
        }

        state.SelectedFileIndex = index;
        state.RequestedFilePath = Path.Combine(state.CurrentFolder, state.ImageFileNames[index]);
    }

    /// <summary>
    /// Resets zoom to fit the image in the viewport and clears pan offset.
    /// </summary>
    public static void ZoomToFit(ViewerState state)
    {
        state.ZoomToFit = true;
        state.PanOffset = (0f, 0f);
    }

    /// <summary>
    /// Sets zoom to 100% (1 image pixel = 1 screen pixel).
    /// </summary>
    public static void ZoomToActual(ViewerState state) => ZoomTo(state, 1.0f);

    public static void ZoomTo(ViewerState state, float zoom)
    {
        state.ZoomToFit = false;
        state.Zoom = zoom;
        state.PanOffset = (0f, 0f);
    }

    private const float ZoomStepFactor = 1.15f;

    /// <summary>
    /// Zooms in by one step (15%).
    /// </summary>
    public static void ZoomIn(ViewerState state)
    {
        state.ZoomToFit = false;
        state.Zoom = MathF.Max(0.01f, state.Zoom * ZoomStepFactor);
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Zooms out by one step (15%).
    /// </summary>
    public static void ZoomOut(ViewerState state)
    {
        state.ZoomToFit = false;
        state.Zoom = MathF.Max(0.01f, state.Zoom / ZoomStepFactor);
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Scrolls the file list by the given number of items.
    /// </summary>
    public static void ScrollFileList(ViewerState state, int delta)
    {
        var maxScroll = Math.Max(0, state.ImageFileNames.Count - 1);
        state.FileListScrollOffset = Math.Clamp(state.FileListScrollOffset + delta, 0, maxScroll);
    }

    /// <summary>
    /// Begins a pan drag at the given screen position.
    /// </summary>
    public static void BeginPan(ViewerState state, float px, float py)
    {
        state.IsPanning = true;
        state.PanStart = (px, py);
    }

    /// <summary>
    /// Updates pan offset during a drag.
    /// </summary>
    public static void UpdatePan(ViewerState state, float px, float py)
    {
        if (!state.IsPanning)
        {
            return;
        }

        var dx = px - state.PanStart.X;
        var dy = py - state.PanStart.Y;
        state.PanOffset = (state.PanOffset.X + dx, state.PanOffset.Y + dy);
        state.PanStart = (px, py);
    }

    /// <summary>
    /// Ends a pan drag.
    /// </summary>
    public static void EndPan(ViewerState state)
    {
        state.IsPanning = false;
    }

    /// <summary>
    /// Updates cursor pixel info from a screen position, converting to image coordinates.
    /// Returns true if the cursor is over the image.
    /// </summary>
    public static bool UpdateCursorFromScreenPosition(
        AstroImageDocument? document, ViewerState state,
        float px, float py,
        float fileListW, float toolbarH, float areaW, float areaH)
    {
        if (document?.UnstretchedImage is not { } image)
        {
            return false;
        }

        var scale = state.Zoom;
        var drawW = image.Width * scale;
        var drawH = image.Height * scale;
        var offsetX = fileListW + (areaW - drawW) / 2f + state.PanOffset.X;
        var offsetY = toolbarH + (areaH - drawH) / 2f + state.PanOffset.Y;

        var imgX = (int)((px - offsetX) / scale);
        var imgY = (int)((py - offsetY) / scale);

        if (imgX >= 0 && imgX < image.Width && imgY >= 0 && imgY < image.Height)
        {
            UpdateCursorInfo(document, state, imgX, imgY);
            return true;
        }

        state.CursorImagePosition = null;
        state.CursorPixelInfo = null;
        return false;
    }
}
