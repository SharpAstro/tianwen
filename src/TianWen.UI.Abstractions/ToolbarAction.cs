namespace TianWen.UI.Abstractions;

/// <summary>
/// Actions that can be triggered from the toolbar.
/// </summary>
public enum ToolbarAction
{
    /// <summary>Show or hide the file list down the left side. A toggle, mirroring the <c>L</c> key.</summary>
    /// <remarks>
    /// It exists because the list could only be dismissed by a key nothing on screen mentioned --
    /// the same gap the toolbar hover state and the sky palette rows were: reachable, and invisible.
    /// First in the run and in a group of its own, because it is the only button that changes the
    /// WINDOW rather than the picture or the file.
    /// </remarks>
    FileList,

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

    /// <summary>
    /// Open the white-balance popover: the R / G / B sliders, Auto and Reset that used to be a section
    /// of the docked info strip. A mark-only button (three colour discs), lit while the effective
    /// white balance is anything but neutral.
    /// </summary>
    /// <remarks>
    /// Moved off the strip at the user's request: three sliders and two buttons that most frames never
    /// touch were standing open under thirteen rows of statistics. A popover keeps them one click
    /// away and gives the strip back to what it reports. The button's highlight is the one thing the
    /// strip's section could not do: say from across the bar that a white balance is in force.
    /// </remarks>
    WhiteBalance,
    StretchParams,
    Channel,
    Debayer,
    Tone,
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
    /// The annotation: nothing, or the WCS grid plus the catalog objects. A plain activation
    /// TOGGLES between those two (<see cref="ViewerActions.ToggleOverlayLevel"/>); reverse -- a
    /// right-click, or <c>Shift+O</c> -- walks the <see cref="ViewerOverlayLevel"/> ladder a rung
    /// instead, which is the only way the grid-only rung is still reached from here.
    /// </summary>
    /// <remarks>
    /// There was a separate <c>Grid</c> button beside this one until the ladder existed, and merging
    /// the two is what paid for the room this bar needed. The grid keeps its own key (<c>G</c>),
    /// which moves the rung with it -- see <see cref="ViewerState.OverlayLevel"/>. The sky behind the
    /// frame was briefly a fourth rung here and is now <see cref="SkyBackdrop"/>, for the reasons on
    /// <see cref="ViewerOverlayLevel"/>.
    /// </remarks>
    Overlays,

    /// <summary>
    /// Draws the sky the frame was taken from BEHIND it -- star field, constellations, the milky way
    /// and, where the frame says where and when it was shot, the horizon -- with the photograph
    /// composited on top at its solved place, scale and rotation. The <c>Y</c> key.
    /// </summary>
    /// <remarks>
    /// <para><b>Its own button because it is not more annotation.</b> As the top rung of
    /// <see cref="Overlays"/> it was unreachable without stepping through two annotation states on
    /// the way, it shared one mark with three other meanings, and on an unsolved frame the press did
    /// nothing with nothing to say why. All three are things a button can fix and a rung cannot.</para>
    /// <para>Enabled only on a frame with a CD matrix, for the reason the backdrop itself gives:
    /// without a solution there is no answer to where the photograph is, so a sky drawn behind it
    /// would be a picture of the wrong part of the sky rather than a missing feature. Dim rather than
    /// hidden, the house reading -- a control that vanishes reads as a bug, one that is dim reads as
    /// a precondition -- with the tooltip saying to solve the frame.</para>
    /// </remarks>
    SkyBackdrop,
    PlateSolve,
    Stars,
    ColorCalibrate,
    BackgroundNeutralize,
    Enhance,

    /// <summary>Toggle the before/after split. Right-click re-pins the current display settings.</summary>
    Compare,

    /// <summary>Open the full keyboard-shortcut list. The home for every shortcut that has no button
    /// of its own to carry it in a tooltip.</summary>
    Shortcuts,
}
