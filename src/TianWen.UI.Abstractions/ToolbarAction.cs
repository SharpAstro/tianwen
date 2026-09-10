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

    /// <summary>
    /// Show only the part of the frame every sub covered, discarding the ragged canvas ring a stack
    /// leaves. A toggle: pressing it again shows the whole frame.
    /// </summary>
    /// <remarks>
    /// A VIEW crop. No pixel is moved and nothing is discarded, so the readout still reports the frame's
    /// own coordinates, a plate solve still runs on all of it, and turning it off is one press. The
    /// rectangle comes from <see cref="TianWen.Lib.Imaging.Image.LargestCoveredRectangle"/> and is
    /// computed off the render thread, because it reads every channel of every pixel: 52.6 ms on a
    /// 3073 x 3085 x 3 master.
    /// </remarks>
    AutoCrop,

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

    /// <summary>
    /// Steps the context ladder -- <see cref="ViewerOverlayLevel"/> -- one rung: nothing, the WCS
    /// grid, the grid plus catalog objects, then the sky the frame came from drawn behind it.
    /// </summary>
    /// <remarks>
    /// There was a separate <c>Grid</c> button beside this one until the ladder existed, and merging
    /// the two is what paid for the third rung: a toolbar that had run out of room could not add a
    /// button for the sky, and two buttons for two of the three layers was the arrangement that made
    /// the third look like a new feature rather than more of the same one. The grid keeps its own key
    /// (<c>G</c>), which now moves the rung with it -- see <see cref="ViewerState.OverlayLevel"/>.
    /// </remarks>
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
