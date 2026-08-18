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
