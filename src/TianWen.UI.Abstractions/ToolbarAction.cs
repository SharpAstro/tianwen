namespace TianWen.UI.Abstractions;

/// <summary>
/// Actions that can be triggered from the toolbar.
/// </summary>
public enum ToolbarAction
{
    Open,

    /// <summary>
    /// Write the image AS SEEN -- the current stretch, white balance, curves, HDR and channel view --
    /// to a file the user picks.
    /// </summary>
    /// <remarks>
    /// Not a screenshot: the raster is rendered at the IMAGE's size through
    /// <see cref="TianWen.Lib.Imaging.DisplayRasterExport"/>, so a 9576x6388 master saves at
    /// 9576x6388 from a 1280-pixel window, and nothing drawn OVER the image (grid, star markers,
    /// object labels, the A/B split) is included. The container comes from the extension the user
    /// chooses in the dialog, which is what makes one button enough for every format.
    /// </remarks>
    Save,

    StretchToggle,
    StretchLink,
    StretchParams,
    Channel,
    Debayer,
    CurvesBoost,
    Hdr,
    ZoomFit,
    ZoomActual,

    /// <summary>
    /// The viewer's single zoom control: it SHOWS the current zoom and opens a menu to change it.
    /// </summary>
    /// <remarks>
    /// Replaces a Fit button beside a 1:1 button. Two buttons could only ever say which of two zooms was
    /// active, so every other zoom -- the whole range the wheel reaches -- was invisible on a toolbar that
    /// had run out of room saying it. One button that reads "Fit" / "1:1" / "43%" says strictly more in
    /// less space, and its menu carries the 1:N ratios that were keyboard-only (Ctrl+2..9) and so
    /// undiscoverable. <see cref="ZoomFit"/> and <see cref="ZoomActual"/> remain as ACTIONS -- the
    /// keyboard and the planetary tab's own toolbar still dispatch them.
    /// </remarks>
    Zoom,
    Grid,
    Overlays,
    PlateSolve,
    Stars,
    ColorCalibrate,
    BackgroundNeutralize,
    SpccCalibrate,
    Enhance,

    /// <summary>Toggle the before/after split. Right-click re-pins the current display settings.</summary>
    Compare,

    /// <summary>Open the full keyboard-shortcut list. The home for every shortcut that has no button
    /// of its own to carry it in a tooltip.</summary>
    Shortcuts,
}
