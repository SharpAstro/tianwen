using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Observable state of the FITS viewer. Mutated by input handlers,
/// read by the display backend during rendering.
/// </summary>
public sealed class ViewerState
{
    public StretchMode StretchMode { get; set; } = StretchMode.Unlinked;
    public StretchParameters StretchParameters { get; set; } = StretchParameters.Default;
    public ChannelView ChannelView { get; set; } = ChannelView.Composite;
    public DebayerAlgorithm DebayerAlgorithm { get; set; } = DebayerAlgorithm.AHD;
    public bool ShowInfoPanel { get; set; } = true;

    /// <summary>Curves boost amount applied in the display shader (0.0 = off, up to 1.0).</summary>
    public float CurvesBoost { get; set; }

    /// <summary>Curve mode: 0 = power-law boost, 1 = Fritsch-Carlson spline LUT.</summary>
    public int CurvesMode { get; set; }

    /// <summary>33 Fritsch-Carlson knots for the spline LUT (see <see cref="StretchUniforms.CurveData"/>).
    /// Empty when using boost mode.</summary>
    public ImmutableArray<float> CurveData { get; set; } = [];

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

    /// <summary>Whether the WCS coordinate grid overlay is visible.</summary>
    public bool ShowGrid { get; set; }

    /// <summary>Whether deep-sky object overlays (galaxy ellipses, markers, labels) are visible.</summary>
    public bool ShowOverlays { get; set; }

    /// <summary>Whether Tycho-2 photometric color calibration is active.</summary>
    public bool ColorCalibrationEnabled { get; set; }

    /// <summary>Whether background neutralization (pivot1 mode) is active.</summary>
    public bool BackgroundNeutralizationEnabled { get; set; }

    /// <summary>Chosen pivot algorithm for background neutralization. Mean = balanced
    /// photographic average (default), GreenPivot = fix green & scale R/B, MinPivot =
    /// fix darkest channel & scale the rest up. Switching method is cheap: results
    /// cache per document in <see cref="AstroImageDocument.ComputeBackgroundNeutralization"/>.</summary>
    public BackgroundNeutralizationMethod BackgroundNeutralizationMethod { get; set; } = BackgroundNeutralizationMethod.Mean;

    /// <summary>Strength of the background-neutralization effect, 0..1. Acts as a
    /// CPU-side lerp toward identity on the cached gain before the GPU uniform is
    /// written — no pixel work, no extra shader uniform. 1.0 = full effect.</summary>
    public float BackgroundNeutralizationStrength { get; set; } = 1f;

    /// <summary>Manual per-channel white-balance multipliers. (1,1,1) = neutral. Applied <i>on top of</i>
    /// any auto color calibration (Tycho-2 / SPCC): the renderer composes this with
    /// <see cref="AstroImageDocument.ColorCalibration"/> when computing the stretch uniforms, so the auto
    /// WB stays in effect and these sliders nudge it. Lives on the shared <see cref="ViewerState"/>, so the
    /// adjustment applies to any colour source (FITS / TIFF / SER) and the GUI viewer tab inherits it too.
    /// Recompute is cheap (uniforms from cached stats, no pixel pass), so only <see cref="NeedsRedraw"/> is
    /// set on change — never <see cref="NeedsTextureUpdate"/>.</summary>
    public (float R, float G, float B) ManualWhiteBalance { get; set; } = (1f, 1f, 1f);

    /// <summary>Channel (0=R, 1=G, 2=B) of the WB slider currently being dragged, or -1 when idle. Mirrors
    /// the <see cref="IsScrubbing"/> transport-drag pattern: a press begins the drag, mouse-move tracks it,
    /// release clears it.</summary>
    public int WhiteBalanceDragChannel { get; set; } = -1;

    /// <summary>Whether detected star circles are visible.</summary>
    public bool ShowStarOverlay { get; set; }

    /// <summary>Whether the histogram overlay is visible in the upper-right corner.</summary>
    public bool ShowHistogram { get; set; } = true;

    /// <summary>Whether the histogram uses log scale. Auto-set when stretch mode changes; user can override.</summary>
    public bool HistogramLogScale { get; set; } = true;

    /// <summary>Status message to display (e.g. "Plate solving...", "Stretching...").</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Whether the image pipeline needs to be reprocessed.</summary>
    public bool NeedsReprocess { get; set; } = true;

    /// <summary>Whether the display texture needs to be re-uploaded.</summary>
    public bool NeedsTextureUpdate { get; set; } = true;

    /// <summary>
    /// Drop the toolbar + status-bar chrome from the layout and skip rendering them, so the image fills the
    /// whole content region. Used by an embedded live preview (Live Session / guide cam / polar-align) that
    /// wants only the image + overlays, never the standalone-viewer chrome. Overlays (WCS annotation, grid,
    /// stars) and the info panel / histogram are governed by their own flags and unaffected.
    /// </summary>
    public bool HideChrome { get; set; }

    /// <summary>
    /// Freeze the display stretch statistics: while set, a live source reuses its cached median/MAD instead of
    /// rescanning each frame, so the stretch does not re-fire on every exposure. A polar-align correctness
    /// requirement (keeps the field visually stable across the slow refine exposures); a one-shot recompute
    /// fires on the off -&gt; on edge. Honoured by <see cref="LiveFramePreviewSource"/>.
    /// </summary>
    public bool FreezeStretchStats { get; set; }

    /// <summary>
    /// Which camera/OTA index an embedded multi-OTA live preview shows (0-based); -1 = auto (first
    /// available). Unused by the standalone file viewer (single source).
    /// </summary>
    public int SelectedCameraIndex { get; set; } = -1;

    /// <summary>Whether the debayer menu is open.</summary>
    public bool ShowDebayerMenu { get; set; }

    /// <summary>Whether the stretch factor dropdown is open.</summary>
    public bool ShowStretchFactorMenu { get; set; }

    /// <summary>
    /// Shared dropdown overlay for toolbar selectors (stretch mode, channel,
    /// debayer, stretch params, curves boost, HDR). Only one dropdown can be
    /// open at a time, so a single state instance is sufficient. Opened by
    /// left-clicking the corresponding toolbar button; rendered last in
    /// <see cref="ImageRendererBase{TSurface}.Render"/> so its clickables win
    /// hit-test z-order (paint order = z-order).
    /// </summary>
    public DropdownMenuState ToolbarDropdown { get; } = new();

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

    // --- Frame playback (multi-frame sources: SER planetary video; later a live camera stream) ---

    /// <summary>True when the active source is a multi-frame sequence -- gates the transport bar.</summary>
    public bool IsSequence { get; set; }

    /// <summary>Total frames in the active sequence (1 for a still image).</summary>
    public int FrameCount { get; set; } = 1;

    /// <summary>Currently displayed frame index.</summary>
    public int FrameIndex { get; set; }

    /// <summary>Whether sequence playback is running.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>Display/playback rate in frames per second -- the rate frames are actually shown at,
    /// capped to a viewable value (the screen can't show hundreds of fps). User-adjustable.</summary>
    public float PlaybackFps { get; set; } = 30f;

    /// <summary>The source's nominal (capture) frame rate from its timestamps, shown in the transport as
    /// info; null when unknown / not a sequence. Distinct from <see cref="PlaybackFps"/>: a planetary
    /// capture's nominal rate is often hundreds of fps, far above any viewable display rate.</summary>
    public float? SourceFps { get; set; }

    /// <summary>
    /// A one-shot seek request (scrub / step / Home / End). Set by the UI; consumed once by
    /// <see cref="SequencePlayer.Tick"/>, which decodes the target off the render thread. Nullable so a
    /// request and "frame 0" are distinguishable. Decoupling via this field keeps the renderer / input
    /// handlers from reaching into the player's timing state.
    /// </summary>
    public int? RequestedFrame { get; set; }

    /// <summary>True while the user is dragging the transport scrub handle.</summary>
    public bool IsScrubbing { get; set; }

    /// <summary>
    /// When true, a multi-frame source is shown as a <b>live rolling-window stack</b> (lucky imaging)
    /// instead of the raw frame. Only meaningful for a SER sequence with a live-stack source available;
    /// the controller keeps showing the raw frame until the first master is built. Toggled from the
    /// transport bar / the <c>K</c> key. The raw playhead keeps advancing underneath, so the stacked view
    /// follows the current frame.
    /// </summary>
    public bool ShowStacked { get; set; }

    // --- Wavelet sharpening (live stacked view only) ---

    /// <summary>Whether multi-scale wavelet sharpening is applied to the live stacked master. Off = the
    /// stacked view is the pure quality-weighted mean (denoised, not sharpened).</summary>
    public bool WaveletSharpenEnabled { get; set; }

    /// <summary>Per-a-trous-layer sharpening gains, finest scale first (the Registax 6-layer convention).
    /// 1.0 = neutral. Adjusted by the info-panel wavelet sliders; pushed to the live stack source on change.
    /// Defaults to the validated planetary curve so enabling gives a good look immediately.</summary>
    public ImmutableArray<float> WaveletGains { get; set; } = WaveletSharpenOptions.PlanetaryDefault.Gains;

    /// <summary>Layer (0=finest .. 5=coarsest) of the wavelet slider currently being dragged, or -1 when
    /// idle. Mirrors <see cref="WhiteBalanceDragChannel"/>.</summary>
    public int WaveletDragBand { get; set; } = -1;

    /// <summary>Set when the wavelet parameters change so the controller re-pushes them to the live stack
    /// source (which re-sharpens the cached master off-thread, no re-stack). Cleared once pushed.</summary>
    public bool WaveletDirty { get; set; }

    /// <summary>
    /// Builds the live-stack wavelet-sharpen options from the panel sliders, or <c>null</c> when sharpening
    /// is disabled. The single source for every live-stack driver (the <c>tianwen-fits</c>
    /// <c>ViewerController</c> and the GUI planetary capture controller), so they can never sharpen
    /// differently. Reuses the validated planetary fine-scale denoise thresholds so amplified gains don't
    /// pull up limb / sensor grain.
    /// </summary>
    public WaveletSharpenOptions? BuildWaveletOptions()
        => WaveletSharpenEnabled
            ? new WaveletSharpenOptions
            {
                Gains = WaveletGains,
                DenoiseThresholds = WaveletSharpenOptions.PlanetaryDefault.DenoiseThresholds,
            }
            : null;

    /// <summary>Selectable playback rates (fps) cycled by the transport speed control / Up-Down keys.</summary>
    public static readonly float[] PlaybackRates = [1f, 5f, 10f, 15f, 24f, 30f, 50f, 75f, 100f, 150f, 200f];

    // --- File list sidebar ---

    /// <summary>Current folder path being browsed.</summary>
    public string? CurrentFolder { get; set; }

    /// <summary>List of image filenames (name only) in the current folder.</summary>
    public List<string> ImageFileNames { get; set; } = new List<string>();

    /// <summary>Index of the currently loaded file in <see cref="ImageFileNames"/>, or -1 if none.</summary>
    public int SelectedFileIndex { get; set; } = -1;

    /// <summary>Whether the file list sidebar is visible.</summary>
    public bool ShowFileList { get; set; } = true;

    /// <summary>Scroll offset (in items) for the file list.</summary>
    public int FileListScrollOffset { get; set; }

    /// <summary>User-adjusted file list panel width in DPI-independent units. Default 300px;
    /// the actual rendered width is this value multiplied by the DPI scale. Clamped to
    /// <see cref="FileListWidthBaseMin"/>..<see cref="FileListWidthBaseMax"/> when set.</summary>
    public float FileListWidthBase
    {
        get => _fileListWidthBase;
        set => _fileListWidthBase = Math.Clamp(value, FileListWidthBaseMin, FileListWidthBaseMax);
    }
    private float _fileListWidthBase = 300f;

    public const float FileListWidthBaseMin = 180f;
    public const float FileListWidthBaseMax = 900f;

    /// <summary>True while the user is dragging the file list resize handle.</summary>
    public bool IsResizingFileList { get; set; }

    /// <summary>Set by UI to request loading a different file. Consumed by the app loop.</summary>
    public string? RequestedFilePath { get; set; }

    // --- Toolbar hover ---

    /// <summary>Screen position of the mouse cursor, updated each frame.</summary>
    public (float X, float Y) MouseScreenPosition { get; set; }

    /// <summary>Set to true when the UI needs to be redrawn. Cleared after each render.</summary>
    public bool NeedsRedraw { get; set; } = true;
}
