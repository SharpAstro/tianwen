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
/// Stateless action handlers that mutate <see cref="ViewerState"/> and <see cref="FitsDocument"/>.
/// Shared between all display backends.
/// </summary>
public static class ViewerActions
{
    public static void ToggleStretch(ViewerState state)
    {
        state.StretchMode = state.StretchMode is StretchMode.None ? StretchMode.Unlinked : StretchMode.None;
        state.NeedsReprocess = true;
        state.StatusMessage = state.StretchMode is StretchMode.None ? "Stretch: Off" : "Stretch: On";
    }

    public static void CycleStretchLink(ViewerState state)
    {
        state.StretchMode = state.StretchMode switch
        {
            StretchMode.Unlinked => StretchMode.Linked,
            _ => StretchMode.Unlinked,
        };
        state.NeedsReprocess = true;
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

    public static void CycleDebayerAlgorithm(ViewerState state)
    {
        state.DebayerAlgorithm = state.DebayerAlgorithm switch
        {
            DebayerAlgorithm.None => DebayerAlgorithm.BilinearMono,
            DebayerAlgorithm.BilinearMono => DebayerAlgorithm.VNG,
            DebayerAlgorithm.VNG => DebayerAlgorithm.AHD,
            DebayerAlgorithm.AHD => DebayerAlgorithm.None,
            _ => DebayerAlgorithm.VNG
        };
        state.NeedsReprocess = true;
        state.StatusMessage = $"Debayer: {state.DebayerAlgorithm}";
    }

    public static void CycleStretchPreset(ViewerState state)
    {
        var presets = StretchParameters.Presets;
        state.StretchPresetIndex = (state.StretchPresetIndex + 1) % presets.Length;
        state.StretchParameters = presets[state.StretchPresetIndex];
        if (state.StretchMode is not StretchMode.None)
        {
            state.NeedsReprocess = true;
        }
        state.StatusMessage = $"Stretch: {state.StretchParameters}";
    }

    /// <summary>
    /// Reprocesses the image pipeline (debayer + stretch) based on current state.
    /// </summary>
    public static async Task ReprocessAsync(FitsDocument document, ViewerState state, CancellationToken cancellationToken = default)
    {
        state.StatusMessage = "Processing...";
        state.NeedsReprocess = false;

        await document.ApplyDebayerAsync(state.DebayerAlgorithm, cancellationToken);
        await document.ApplyStretchAsync(state.StretchMode, state.StretchParameters, cancellationToken);

        state.NeedsTextureUpdate = true;
        state.StatusMessage = null;
    }

    /// <summary>
    /// Initiates plate solving in the background.
    /// </summary>
    public static async Task PlateSolveAsync(FitsDocument document, ViewerState state, IPlateSolverFactory solverFactory, CancellationToken cancellationToken = default)
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
    public static void UpdateCursorInfo(FitsDocument document, ViewerState state, int imageX, int imageY)
    {
        state.CursorImagePosition = (imageX, imageY);
        state.CursorPixelInfo = document.GetPixelInfo(imageX, imageY);
    }

    /// <summary>
    /// Scans a folder for FITS files and populates the file list.
    /// Optionally selects the file matching <paramref name="currentFileName"/>.
    /// </summary>
    public static void ScanFolder(ViewerState state, string folderPath, string? currentFileName = null)
    {
        state.CurrentFolder = folderPath;
        state.FitsFileNames.Clear();
        state.FileListScrollOffset = 0;

        if (!Directory.Exists(folderPath))
        {
            state.SelectedFileIndex = -1;
            return;
        }

        string[] fitsPatterns = ["*.fit", "*.fits", "*.fts"];
        var files = fitsPatterns
            .SelectMany(p => Directory.EnumerateFiles(folderPath, p, SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        state.FitsFileNames = files;
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
        if (index < 0 || index >= state.FitsFileNames.Count || state.CurrentFolder is null)
        {
            return;
        }

        if (index == state.SelectedFileIndex)
        {
            return;
        }

        state.SelectedFileIndex = index;
        state.RequestedFilePath = Path.Combine(state.CurrentFolder, state.FitsFileNames[index]);
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
    public static void ZoomToActual(ViewerState state)
    {
        state.ZoomToFit = false;
        state.Zoom = 1.0f;
        state.PanOffset = (0f, 0f);
    }

    /// <summary>
    /// Scrolls the file list by the given number of items.
    /// </summary>
    public static void ScrollFileList(ViewerState state, int delta)
    {
        var maxScroll = Math.Max(0, state.FitsFileNames.Count - 1);
        state.FileListScrollOffset = Math.Clamp(state.FileListScrollOffset + delta, 0, maxScroll);
    }
}
