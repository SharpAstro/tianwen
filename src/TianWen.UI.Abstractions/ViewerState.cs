using System.Collections.Generic;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Observable state of the FITS viewer. Mutated by input handlers,
/// read by the display backend during rendering.
/// </summary>
public sealed class ViewerState
{
    public StretchMode StretchMode { get; set; } = StretchMode.None;
    public StretchParameters StretchParameters { get; set; } = StretchParameters.Default;
    public ChannelView ChannelView { get; set; } = ChannelView.Composite;
    public DebayerAlgorithm DebayerAlgorithm { get; set; } = DebayerAlgorithm.VNG;
    public bool ShowInfoPanel { get; set; } = true;

    /// <summary>Curves boost amount applied in the display shader (0.0 = off, up to 1.0).</summary>
    public float CurvesBoost { get; set; }

    /// <summary>Index into <see cref="CurvesBoostPresets"/>.</summary>
    public int CurvesBoostIndex { get; set; }

    /// <summary>Available curves boost presets.</summary>
    public static readonly float[] CurvesBoostPresets = [0f, 0.25f, 0.50f, 1.0f, 1.5f];

    /// <summary>HDR compression amount (0.0 = off). Applied in the display shader.</summary>
    public float HdrAmount { get; set; }

    /// <summary>HDR knee point — values above this are compressed.</summary>
    public float HdrKnee { get; set; } = 0.8f;

    /// <summary>Index into <see cref="HdrPresets"/>.</summary>
    public int HdrPresetIndex { get; set; }

    /// <summary>Available HDR presets: (amount, knee).</summary>
    public static readonly (float Amount, float Knee)[] HdrPresets =
    [
        (0f, 0.8f),
        (0.5f, 0.85f),
        (1.0f, 0.8f),
        (1.5f, 0.75f),
        (2.0f, 0.7f),
    ];

    /// <summary>Current mouse position in image coordinates (0-based), or <c>null</c> if outside.</summary>
    public (int X, int Y)? CursorImagePosition { get; set; }

    /// <summary>Pixel info at the current cursor position.</summary>
    public PixelInfo? CursorPixelInfo { get; set; }

    /// <summary>Whether a plate solve is currently in progress.</summary>
    public bool IsPlateSolving { get; set; }

    /// <summary>Status message to display (e.g. "Plate solving...", "Stretching...").</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Whether the image pipeline needs to be reprocessed.</summary>
    public bool NeedsReprocess { get; set; } = true;

    /// <summary>Whether the display texture needs to be re-uploaded.</summary>
    public bool NeedsTextureUpdate { get; set; } = true;

    /// <summary>Whether the debayer menu is open.</summary>
    public bool ShowDebayerMenu { get; set; }

    /// <summary>Whether the stretch factor dropdown is open.</summary>
    public bool ShowStretchFactorMenu { get; set; }

    /// <summary>Index into <see cref="StretchParameters.Presets"/> for the selected stretch preset.</summary>
    public int StretchPresetIndex { get; set; } = 0; // (0.1, -5.0) default

    /// <summary>Whether to automatically fit the image to the viewport.</summary>
    public bool ZoomToFit { get; set; } = true;

    /// <summary>Zoom as actual display scale: 1.0 = 100% (1 image pixel = 1 screen pixel).</summary>
    public float Zoom { get; set; } = 1.0f;

    /// <summary>Pan offset in screen pixels.</summary>
    public (float X, float Y) PanOffset { get; set; }

    /// <summary>Whether a mouse drag (pan) is in progress.</summary>
    public bool IsPanning { get; set; }

    /// <summary>Last mouse position during panning.</summary>
    public (float X, float Y) PanStart { get; set; }

    // --- File list sidebar ---

    /// <summary>Current folder path being browsed.</summary>
    public string? CurrentFolder { get; set; }

    /// <summary>List of FITS filenames (name only) in the current folder.</summary>
    public List<string> FitsFileNames { get; set; } = new List<string>();

    /// <summary>Index of the currently loaded file in <see cref="FitsFileNames"/>, or -1 if none.</summary>
    public int SelectedFileIndex { get; set; } = -1;

    /// <summary>Whether the file list sidebar is visible.</summary>
    public bool ShowFileList { get; set; } = true;

    /// <summary>Scroll offset (in items) for the file list.</summary>
    public int FileListScrollOffset { get; set; }

    /// <summary>Set by UI to request loading a different file. Consumed by the app loop.</summary>
    public string? RequestedFilePath { get; set; }

    // --- Toolbar hover ---

    /// <summary>Screen position of the mouse cursor, updated each frame.</summary>
    public (float X, float Y) MouseScreenPosition { get; set; }

    /// <summary>Set to true when the UI needs to be redrawn. Cleared after each render.</summary>
    public bool NeedsRedraw { get; set; } = true;
}
