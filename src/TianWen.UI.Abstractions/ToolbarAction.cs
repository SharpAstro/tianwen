namespace TianWen.UI.Abstractions;

/// <summary>
/// Actions that can be triggered from the toolbar.
/// </summary>
public enum ToolbarAction
{
    Open,
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
