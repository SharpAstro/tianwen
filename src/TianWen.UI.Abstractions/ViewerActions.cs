using DIR.Lib;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Stateless action handlers that mutate <see cref="ViewerState"/> and <see cref="AstroImageDocument"/>.
/// Shared between all display backends.
/// </summary>
public static class ViewerActions
{
    public static void ToggleStretch(ViewerState state)
    {
        state.StretchMode = state.StretchMode is StretchMode.None ? DefaultStretchMode : StretchMode.None;
        state.HistogramLogScale = state.StretchMode is StretchMode.None;
        state.NeedsRedraw = true;
    }

    // Cycle order for stretch link: excludes None. Internal so the dropdown
    // selector in <see cref="ImageRendererBase{TSurface}"/> can reuse the same
    // mode list (single source of truth: cycle order matches dropdown order).
    // Auto leads, and is the default (see DefaultStretchMode).
    internal static readonly StretchMode[] StretchLinkModes =
        [StretchMode.Auto, StretchMode.Linked, StretchMode.Unlinked, StretchMode.Luma];

    /// <summary>
    /// What a viewer shows when it has to pick a stretch for itself: first entry of
    /// <see cref="StretchLinkModes"/>, which is <see cref="StretchMode.Auto"/>.
    ///
    /// <para>Auto resolves per frame (<see cref="StretchModeExtensions.ResolveAuto"/>, in TianWen.Lib so the
    /// Explorer thumbnail resolves it the same way): a colour frame with an
    /// active colour calibration renders LINKED, so the measured white balance shows as colour; a
    /// colour frame without one renders UNLINKED, which neutralises each channel's background for a
    /// clean look with no cast to guess at; a mono frame renders LINKED (Linked and Unlinked coincide
    /// on one channel). So running SPCC on an Auto frame flips it to Linked on its own and the
    /// calibration is visible immediately, which is the decision a user otherwise had to make by hand.</para>
    ///
    /// <para>The default used to be Unlinked, which made a photometric calibration look like a no-op on
    /// a fresh viewer; then Linked, which opened an uncalibrated frame on a possible colour cast. Auto
    /// picks the right one of the two per frame. Named once here so the several places that need a
    /// default -- the toggle above, ViewerState's initialiser, DisplayControls.Defaults, the CLI view
    /// command -- cannot drift apart again.</para>
    /// </summary>
    public static StretchMode DefaultStretchMode => StretchLinkModes[0];

    public static void CycleStretchLink(ViewerState state, bool reverse = false)
    {
        var idx = Array.IndexOf(StretchLinkModes, state.StretchMode);
        if (idx < 0) idx = 0;
        var len = StretchLinkModes.Length;
        idx = (idx + (reverse ? len - 1 : 1)) % len;
        state.StretchMode = StretchLinkModes[idx];
        state.NeedsRedraw = true;
    }

    public static void CycleChannelView(ViewerState state, int channelCount, bool reverse = false)
    {
        if (channelCount >= 3)
        {
            state.ChannelView = reverse
                ? state.ChannelView switch
                {
                    ChannelView.Composite => ChannelView.Blue,
                    ChannelView.Blue => ChannelView.Green,
                    ChannelView.Green => ChannelView.Red,
                    ChannelView.Red => ChannelView.Composite,
                    _ => ChannelView.Composite
                }
                : state.ChannelView switch
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
            // The exact inverse of the forward walk rather than a re-derivation: forward climbs to
            // `last` then wraps to Composite, so reverse descends to Composite then wraps to `last`.
            var last = (int)ChannelView.Channel0 + channelCount - 1;
            var ch = (int)state.ChannelView;
            ch = reverse
                ? (ch > (int)ChannelView.Composite ? ch - 1 : last)
                : (ch < last ? ch + 1 : (int)ChannelView.Composite);
            state.ChannelView = (ChannelView)ch;
        }
        state.NeedsTextureUpdate = true;
    }

    // The demosaics the viewer OFFERS, in selector order. Internal so the toolbar dropdown in
    // <see cref="ImageRendererBase{TSurface}"/> reads the same list the D key cycles, exactly as
    // StretchLinkModes above does for the stretch selector: one order, so a cycle can never land on
    // something the dropdown does not show.
    //
    // AHD is deliberately absent, and the reason is honesty rather than quality. The GPU shader has
    // no AHD, so choosing it displayed MHC while a Save wrote AHD -- the one thing a viewer whose
    // whole contract is "the file is what you were looking at" may not do. It also cost 1258 ms of
    // every save, an order more than anything else here. This removes it from the SELECTOR only:
    // DebayerAlgorithm still has it, the batch paths (stacking, dataset export) still ask for it by
    // name, GpuDebayerMode still answers for it, and a state that has AHD in it keeps rendering
    // exactly as it did.
    //
    // Auto leads, because it is the default and the only entry that is right for every input rather
    // than for one. It is a UI intent, not a GPU mode: the upload path and the save path both resolve
    // it through DebayerAlgorithmExtensions.ResolveAuto before anything acts on it.
    internal static readonly DebayerAlgorithm[] DebayerAlgorithms =
        [DebayerAlgorithm.Auto, DebayerAlgorithm.None, DebayerAlgorithm.BilinearMono, DebayerAlgorithm.MHC, DebayerAlgorithm.VNG];

    /// <summary>
    /// What a viewer demosaics with when nobody has chosen: <see cref="DebayerAlgorithm.Auto"/>, which
    /// resolves a still CFA mosaic to <see cref="DebayerAlgorithm.VNG"/>, a SER stream to
    /// <see cref="DebayerAlgorithm.MHC"/>, a mono frame to <see cref="DebayerAlgorithm.BilinearMono"/>
    /// and an already-colour frame to <see cref="DebayerAlgorithm.None"/>. The measurement below is why
    /// VNG is the answer for the still-CFA case specifically.
    ///
    /// <para>VNG because of what it does at a star, which is the thing this application is looking at.
    /// Measured on a real CR3 over four stars, the rim of the halo sits 0.36% BELOW the local sky for
    /// VNG and 2.86% ABOVE it for MHC -- MHC draws a faint dark ring around every bright core, which
    /// is visible the moment two renders are blinked against each other. Bilinear has no ring either
    /// but spreads colour twice as far (fringe 3.89% against VNG's 1.35%).</para>
    ///
    /// <para>The default used to be AHD, which is not in the selector at all any more (see
    /// <see cref="DebayerAlgorithms"/>): it rings less than MHC but still rings, has no GPU
    /// implementation, and cost 1258 ms of every save against VNG's 63 ms.</para>
    ///
    /// <para>Named once here so the places that need a default -- ViewerState's initialiser, the
    /// document open/cache entry points, the CLI view command -- cannot drift apart.</para>
    /// </summary>
    public const DebayerAlgorithm DefaultDebayerAlgorithm = DebayerAlgorithm.Auto;

    public static void CycleDebayerAlgorithm(ViewerState state, bool reverse = false)
    {
        // A state holding something the selector no longer offers (a persisted AHD) is not in the
        // ring at all; IndexOf answers -1 and the first step lands on entry 0 instead of throwing.
        var idx = Array.IndexOf(DebayerAlgorithms, state.DebayerAlgorithm);
        var len = DebayerAlgorithms.Length;
        idx = idx < 0 ? 0 : (idx + (reverse ? len - 1 : 1)) % len;
        state.DebayerAlgorithm = DebayerAlgorithms[idx];
        // RawBayer (SER / raw Bayer FITS) re-derives the GPU demosaic mode in UploadDocumentTextures,
        // so the switch between the shader's bilinear / MHC / VNG branches is live (a CPU-debayered
        // colour FITS is unaffected).
        state.NeedsTextureUpdate = true;
    }

    /// <summary>
    /// Applies a curves-boost preset by index, CLAMPED to the preset range. The single place that
    /// writes the boost state, so a wrapping caller (a click, which has no direction) and a clamping
    /// one (a wheel notch, which does) cannot drift in what else they update.
    /// </summary>
    public static void SetCurvesBoostIndex(ViewerState state, int index)
    {
        state.CurvesBoostIndex = Math.Clamp(index, 0, ViewerState.CurvesBoostPresets.Length - 1);
        state.CurvesBoost = ViewerState.CurvesBoostPresets[state.CurvesBoostIndex];
        state.NeedsRedraw = true;
    }

    public static void CycleCurvesBoost(ViewerState state, bool reverse = false)
    {
        var len = ViewerState.CurvesBoostPresets.Length;
        SetCurvesBoostIndex(state, reverse
            ? (state.CurvesBoostIndex - 1 + len) % len
            : (state.CurvesBoostIndex + 1) % len);
    }

    /// <summary>Cycles between power-law boost (mode 0) and spline LUT (mode 1).</summary>
    public static void CycleCurvesMode(ViewerState state)
    {
        state.CurvesMode = state.CurvesMode == 0 ? 1 : 0;
        if (state.CurvesMode == 1 && state.CurveData.IsDefault)
        {
            // Use an S-curve preset: lift shadows, preserve mids, compress highlights
            var spline = new FritschCarlsonSpline([(0f, 0f), (0.15f, 0.22f), (0.4f, 0.5f), (0.7f, 0.72f), (1f, 1f)]);
            state.CurveData = spline.ComputeKnots33();
        }
        state.NeedsRedraw = true;
        state.StatusMessage = state.CurvesMode == 1 ? "Curve: Spline LUT" : "Curve: Boost";
    }

    /// <summary>Applies an HDR preset by index, CLAMPED to the preset range. Counterpart of
    /// <see cref="SetCurvesBoostIndex"/> and the single writer of the HDR state.</summary>
    public static void SetHdrPresetIndex(ViewerState state, int index)
    {
        state.HdrPresetIndex = Math.Clamp(index, 0, ViewerState.HdrPresets.Length - 1);
        var (amount, knee) = ViewerState.HdrPresets[state.HdrPresetIndex];
        state.HdrAmount = amount;
        state.HdrKnee = knee;
        state.NeedsRedraw = true;
        state.StatusMessage = amount > 0f ? $"HDR: {amount:F1} (knee {knee:F2})" : "HDR: Off";
    }

    public static void CycleHdr(ViewerState state, bool reverse = false)
    {
        var len = ViewerState.HdrPresets.Length;
        SetHdrPresetIndex(state, reverse
            ? (state.HdrPresetIndex - 1 + len) % len
            : (state.HdrPresetIndex + 1) % len);
    }

    public static void CycleStretchPreset(ViewerState state, bool reverse = false)
    {
        var presets = StretchParameters.Presets;
        state.StretchPresetIndex = reverse
            ? (state.StretchPresetIndex - 1 + presets.Length) % presets.Length
            : (state.StretchPresetIndex + 1) % presets.Length;
        state.StretchParameters = presets[state.StretchPresetIndex];
        state.NeedsRedraw = true;
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
    /// <remarks>
    /// The CALLER claims <see cref="ViewerState.IsPlateSolving"/> before handing this to the tracker
    /// and clears it in the tracker's onFinally. Claiming it here instead would leave the check and the
    /// set on opposite sides of a thread hand-off, which is exactly wide enough for a second press to
    /// slip through and start a concurrent solve. Exceptions and cancellation are routed by
    /// <see cref="BackgroundTaskTracker.RunGuarded"/>, so this body only handles the solver answering
    /// "not solved", which is a result rather than a fault.
    /// </remarks>
    public static async Task PlateSolveAsync(AstroImageDocument document, ViewerState state, IPlateSolverFactory solverFactory, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var solved = await document.PlateSolveAsync(solverFactory, cancellationToken);
        state.StatusMessage = solved ? "Plate solved" : "Plate solve failed";
        if (!solved)
        {
            // Logged, not just shown. A StatusMessage is transient -- the next status overwrites it --
            // so routing the only account of a failure through it means that by the time anyone looks
            // for the reason it is gone, and the log has no record that a solve was even attempted.
            // A THROWN failure is logged by the tracker instead; this branch is the solver answering
            // "no", which is not an exception.
            logger?.LogWarning("Plate solve failed for {File}", document.FilePath);
        }
    }

    /// <summary>
    /// Updates cursor pixel info when the mouse moves over the image.
    /// </summary>
    public static void UpdateCursorInfo(AstroImageDocument document, ViewerState state, int imageX, int imageY)
    {
        state.CursorImagePosition = (imageX, imageY);
        state.CursorPixelInfo = document.GetPixelInfo(imageX, imageY,
            state.ChannelView.DisplayedSourceChannel(document.UnstretchedImage.ChannelCount));
    }

    /// <summary>
    /// Scans a folder for supported image files and populates the file list.
    /// Optionally selects the file matching <paramref name="currentFileName"/>.
    /// </summary>
    public static void ScanFolder(ViewerState state, string folderPath, string? currentFileName = null)
    {
        state.CurrentFolder = folderPath;
        state.ImageFileNames.Clear();
        state.PendingFileListScrollTop = 0;

        if (!Directory.Exists(folderPath))
        {
            state.SelectedFileIndex = -1;
            return;
        }

        // One directory-index pass matched by extension suffix; the old per-pattern loop listed the
        // folder once per supported extension and followed reparse points into wherever they led.
        var files = FileEnumeration.EnumerateFiles(folderPath, AstroImageDocument.SupportedExtensions, recursive: false)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        state.ImageFileNames = files;
        state.SelectedFileIndex = currentFileName is not null
            ? files.FindIndex(f => string.Equals(f, currentFileName, StringComparison.OrdinalIgnoreCase))
            : -1;

        // Ensure the selected file is visible (applied to the scroll controller on the next render).
        if (state.SelectedFileIndex >= 0)
        {
            state.PendingFileListScrollTop = Math.Max(0, state.SelectedFileIndex - 5);
        }
    }

    /// <summary>
    /// Selects a file from the file list by index and sets <see cref="ViewerState.RequestedFilePath"/>.
    /// </summary>
    /// <summary>
    /// Turn the automatic colour calibration on or off, moving the manual white-balance triple out of
    /// and back into the way.
    ///
    /// The two slots multiply (auto x manual), so an automatic answer measured from star photometry
    /// has to take the manual slot to identity -- a hand-dragged slider or the gray-world Auto button
    /// on top of it is a second correction of an already-corrected image. What was missing is the
    /// other direction: nothing restored the user's triple when the calibration was switched off, so
    /// the sliders sat at 1.00 and the adjustment was simply gone.
    ///
    /// Only the auto slot scales the per-channel stats, which is what holds the background neutral
    /// under an Unlinked or linear stretch -- so the calibration is never MOVED into the manual
    /// sliders as a friendlier-looking alternative. It is parked and put back.
    /// </summary>
    public static void SetColorCalibrationEnabled(ViewerState state, bool enabled)
    {
        if (enabled)
        {
            // ??= so a second calibration cannot overwrite the stash with the identity triple the
            // first one installed. Re-calibrating must not consume the user's original.
            state.ManualWhiteBalanceBeforeCalibration ??= state.ManualWhiteBalance;
            state.ManualWhiteBalance = (1f, 1f, 1f);
        }
        else
        {
            state.ManualWhiteBalance = state.ManualWhiteBalanceBeforeCalibration ?? (1f, 1f, 1f);
            state.ManualWhiteBalanceBeforeCalibration = null;
        }

        state.ColorCalibrationEnabled = enabled;
    }

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

        // The selection moved, so the list has to follow it: a blink, an arrow step or a snap back to the
        // blink anchor can all walk the loaded file off-screen, and the selected row is the only thing on
        // screen that says which file is being shown. One-shot, consumed by the next paint.
        state.PendingFileListEnsureVisible = index;
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
    /// Snaps <see cref="ViewerState.PlaybackFps"/> to the next (<paramref name="faster"/>) or previous
    /// entry in <see cref="ViewerState.PlaybackRates"/>. Used by the transport speed control and Up/Down
    /// keys while a sequence is loaded.
    /// </summary>
    public static void CyclePlaybackSpeed(ViewerState state, bool faster)
    {
        var rates = ViewerState.PlaybackRates;
        // Find the current rate's slot (nearest), then step one notch and clamp to the table ends.
        var idx = 0;
        for (var i = 1; i < rates.Length; i++)
        {
            if (Math.Abs(rates[i] - state.PlaybackFps) < Math.Abs(rates[idx] - state.PlaybackFps))
            {
                idx = i;
            }
        }

        idx = Math.Clamp(idx + (faster ? 1 : -1), 0, rates.Length - 1);
        state.PlaybackFps = rates[idx];
        state.NeedsRedraw = true;
    }

    // The viewport pan drag (Begin/Update/EndPan + ViewerState.IsPanning/PanStart) moved onto the
    // renderer's DIR.Lib PanZoomController: the gesture state lives on the controller, and only the
    // resulting display transform (Zoom/PanOffset/ZoomToFit) is written back to ViewerState.

    /// <summary>
    /// Updates cursor pixel info from a screen position, converting to image coordinates.
    /// Returns true if the cursor is over the VISIBLE image.
    /// </summary>
    /// <remarks>
    /// <b>The pointer must be inside the image PANE, not merely inside the image's mathematical
    /// extent.</b> Zoomed in, that extent runs underneath the toolbar, the file list and the info
    /// panel, all of which are drawn OVER it -- so testing the extent alone reports a pixel value for
    /// a pixel that is not visible where the pointer is, and the caller, seeing the readout change,
    /// repaints the whole window for every motion event. Measured on an Adreno X1-85: 8% GPU just
    /// sliding the pointer down the file list. Zoom out far enough that the image no longer reaches
    /// the file list and the same gesture costs 0%, which is what made the cause visible -- the tell
    /// is that the waste APPEARS as you zoom in, which reads like a rendering cost rather than a
    /// hit-test bug.
    /// </remarks>
    public static bool UpdateCursorFromScreenPosition(
        AstroImageDocument? document, ViewerState state,
        float px, float py,
        float areaX, float areaY, float areaW, float areaH)
    {
        if (document?.UnstretchedImage is not { } image)
        {
            return false;
        }

        if (px < areaX || px >= areaX + areaW || py < areaY || py >= areaY + areaH)
        {
            state.CursorImagePosition = null;
            state.CursorPixelInfo = null;
            return false;
        }

        var scale = state.Zoom;
        var drawW = image.Width * scale;
        var drawH = image.Height * scale;
        var offsetX = areaX + (areaW - drawW) / 2f + state.PanOffset.X;
        var offsetY = areaY + (areaH - drawH) / 2f + state.PanOffset.Y;

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

    /// <summary>
    /// Handles a file or folder drop. Scans the folder and optionally selects the dropped file.
    /// </summary>
    public static void HandleFileDrop(ViewerState state, string path)
    {
        if (Directory.Exists(path))
        {
            ScanFolder(state, path);
            if (state.ImageFileNames.Count > 0)
            {
                SelectFile(state, 0);
            }
            state.NeedsRedraw = true;
            return;
        }

        if (File.Exists(path) && AstroImageDocument.IsSupportedExtension(Path.GetExtension(path)))
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                ScanFolder(state, dir, Path.GetFileName(path));
            }
            state.RequestedFilePath = path;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>
    /// Handles a toolbar action by dispatching to the appropriate state mutation.
    /// Returns <c>true</c> if the action was fully handled, <c>false</c> if the caller
    /// must handle it (e.g. <see cref="ToolbarAction.Open"/> or <see cref="ToolbarAction.PlateSolve"/>
    /// which require DI services).
    /// </summary>
    /// <param name="split">The viewer's before/after split control, which owns its own state; null in a
    /// host with no viewer widget, where <see cref="ToolbarAction.Compare"/> is simply inert.</param>
    /// <param name="hasBeforePixels">Whether the backend is holding pre-enhance pixels, which decides
    /// which comparison <see cref="ToolbarAction.Compare"/> turns on. Passed in rather than read from
    /// state because it is a property of the GPU backend, not of the view state.</param>
    /// <param name="hasCrop">Whether a display crop is in force, which is the next comparison
    /// <see cref="ToolbarAction.Compare"/> reaches for when no pixels were retained. Both of the
    /// viewer's press dispatchers pass it: the keyboard and the button must choose the same mode, and
    /// this is the pair that has silently disagreed before.</param>
    /// <summary>
    /// The largest denominator the zoom ladder reaches, i.e. 1:9. This is the shared vocabulary of the
    /// zoom control -- the keyboard's Ctrl+1..Ctrl+9, the dropdown's rows, and the wheel all speak it,
    /// and the dropdown's label list is BUILT from this so a change here cannot leave them disagreeing.
    /// </summary>
    public const int MaxZoomRatioDenominator = 9;

    /// <summary>A zoom counts as being ON a ladder rung within this much of it. Loose enough to survive
    /// float division, far tighter than the 15% gap to the neighbouring rung.</summary>
    private const float ZoomRatioSnapTolerance = 0.001f;

    /// <summary>
    /// Steps the zoom along the 1:1 .. 1:<see cref="MaxZoomRatioDenominator"/> ladder, where a POSITIVE
    /// step means zoom IN.
    /// </summary>
    /// <remarks>
    /// <para><b>The axis runs backwards from every other cycler here</b>, which is the whole reason this
    /// is not a plain index step: the rung is the DENOMINATOR, so zooming in means a smaller number.
    /// Up therefore subtracts.</para>
    ///
    /// <para><b>Fit is deliberately not a rung.</b> It is a mode, not a magnification: for a large image
    /// it sits below 1:9 and for a small one above 1:1, so it has no fixed place on the ladder, and
    /// <see cref="ZoomToFit"/> does not even write <see cref="ViewerState.Zoom"/> (the renderer derives
    /// the fit scale), so there is no number to step from. Up out of Fit lands on 1:1 -- the one rung
    /// that means something absolute -- and down stays put rather than guessing. Fit remains one click
    /// or right-click away.</para>
    ///
    /// <para><b>An off-rung zoom snaps toward the direction of travel, and that snap IS the notch.</b>
    /// The wheel over the IMAGE zooms by a 1.15 factor, so it readily leaves the zoom between rungs
    /// (43%); one notch up from there should land on the next rung up (1:2 = 50%), not jump two.</para>
    ///
    /// <para><b>A zoom already past 1:1 must not be clamped down onto it.</b> The image can be magnified
    /// beyond 100% by the wheel, and clamping an up-notch to the top rung would zoom OUT in answer to a
    /// zoom-IN request -- the same class of surprise the ramps clamp to avoid.</para>
    /// </remarks>
    private static void StepZoomRatio(ViewerState state, int steps)
    {
        if (steps == 0)
        {
            return;
        }

        if (state.ZoomToFit)
        {
            if (steps > 0)
            {
                ZoomTo(state, 1f);
            }
            return;
        }

        // Already magnified past the top rung: an up-notch has nowhere to go, and must not land on 1:1.
        if (steps > 0 && state.Zoom > 1f + ZoomRatioSnapTolerance)
        {
            return;
        }

        var exact = 1f / MathF.Max(state.Zoom, ZoomRatioSnapTolerance);
        var rounded = (int)MathF.Round(exact);
        int rung;
        var remaining = steps;
        if (rounded >= 1 && MathF.Abs(exact - rounded) < ZoomRatioSnapTolerance)
        {
            rung = rounded;
        }
        else
        {
            rung = steps > 0 ? (int)MathF.Floor(exact) : (int)MathF.Ceiling(exact);
            remaining -= steps > 0 ? 1 : -1;
        }

        // Up subtracts: see the remark above on the axis running backwards.
        ZoomTo(state, 1f / Math.Clamp(rung - remaining, 1, MaxZoomRatioDenominator));
    }

    /// <summary>
    /// A wheel notch over a multi-option toolbar button steps that button's options: up = forward,
    /// down = back. Returns <c>false</c> for a button the wheel does not drive, so the caller can fall
    /// through rather than swallowing the event.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not simply <see cref="HandleToolbarAction"/> with
    /// <c>reverse: !up</c>.</b> Three actions overload <c>reverse</c> to mean a DIFFERENT action
    /// rather than the same cycle backwards, so routing the wheel through the click dispatcher would
    /// hand each of them a gesture nobody asked for: a scroll down on Compare would RE-PIN the before
    /// image (silently discarding the comparison baseline), on Boost it would switch the curve MODE, and
    /// on Zoom it would toggle Fit/1:1 instead of stepping the ratio ladder. This lists only the genuine
    /// cyclers -- Zoom among them, but through its own stepper -- and calls their cycle
    /// helper directly. A button added later is opt-in: absent from this switch it keeps ignoring the
    /// wheel, which is the safe default for an action whose reverse means something else.</para>
    ///
    /// <para><b>Intensity ramps clamp; sets wrap.</b> Boost and HDR are ascending ladders, so wrapping
    /// would mean one notch past the top silently turns the effect OFF -- the exact opposite of what
    /// "scroll up" asked for, and indistinguishable from a bug. The rest (stretch link, stretch preset,
    /// channel, debayer) are unordered sets, where wrapping is natural and clamping would strand the
    /// user at an end with no way onward.</para>
    ///
    /// <para><b><paramref name="steps"/> is a signed WHOLE-notch count, and may be 0.</b> The caller
    /// accumulates the raw wheel delta, because a trackpad delivers many sub-1.0 deltas (the file-list
    /// scroller in this same viewer accumulates them for exactly that reason) and one option per event
    /// would race through a five-entry list on a single gentle swipe. A 0-step call still reports
    /// handled: the return value says "the wheel drives this button", not "something changed", which is
    /// what lets the caller reset its accumulator on the buttons it does not drive.</para>
    ///
    /// <para>An end-of-ramp notch still reports handled. It is a no-op the user asked for, and
    /// returning false there would fall through to whatever claims the event next.</para>
    /// </remarks>
    public static bool TryHandleToolbarWheel(ViewerState state, AstroImageDocument? document, ToolbarAction action, int steps)
    {
        var reverse = steps < 0;
        var count = Math.Abs(steps);
        switch (action)
        {
            // Ramps take the step count as an index offset, so the clamp does the bounding once
            // regardless of how big a delta arrived.
            case ToolbarAction.CurvesBoost:
                SetCurvesBoostIndex(state, state.CurvesBoostIndex + steps);
                return true;
            case ToolbarAction.Hdr:
                SetHdrPresetIndex(state, state.HdrPresetIndex + steps);
                return true;
            // Wrapping cyclers step one at a time: the mapping is a cycle, not an index, and
            // CycleChannelView's is not even arithmetic, so repeating the helper is the only form that
            // cannot disagree with what a click does.
            case ToolbarAction.StretchLink:
                for (var i = 0; i < count; i++) CycleStretchLink(state, reverse);
                return true;
            case ToolbarAction.StretchParams:
                for (var i = 0; i < count; i++) CycleStretchPreset(state, reverse);
                return true;
            case ToolbarAction.Debayer:
                for (var i = 0; i < count; i++) CycleDebayerAlgorithm(state, reverse);
                return true;
            case ToolbarAction.Channel:
                if (document is not null)
                {
                    var channels = document.UnstretchedImage.ChannelCount;
                    for (var i = 0; i < count; i++) CycleChannelView(state, channels, reverse);
                }
                return true;
            // Its own stepper, because the rung is a DENOMINATOR (so up subtracts) and Fit is not a
            // rung at all. See StepZoomRatio.
            case ToolbarAction.Zoom:
                StepZoomRatio(state, steps);
                state.NeedsRedraw = true;
                return true;
            default:
                return false;
        }
    }

    public static bool HandleToolbarAction(ViewerState state, AstroImageDocument? document, ToolbarAction action, bool reverse = false,
        SplitCompareController? split = null, bool hasBeforePixels = false, bool hasCrop = false)
    {
        switch (action)
        {
            case ToolbarAction.StretchToggle:
                ToggleStretch(state);
                return true;
            case ToolbarAction.StretchLink:
                CycleStretchLink(state, reverse);
                return true;
            case ToolbarAction.StretchParams:
                CycleStretchPreset(state, reverse);
                return true;
            case ToolbarAction.Channel:
                if (document is not null)
                {
                    CycleChannelView(state, document.UnstretchedImage.ChannelCount);
                }
                return true;
            case ToolbarAction.Debayer:
                CycleDebayerAlgorithm(state, reverse);
                return true;
            case ToolbarAction.CurvesBoost:
                if (reverse)
                {
                    CycleCurvesMode(state);
                }
                else
                {
                    CycleCurvesBoost(state);
                }
                return true;
            case ToolbarAction.Hdr:
                CycleHdr(state, reverse);
                return true;
            case ToolbarAction.Grid:
                state.ShowGrid = !state.ShowGrid;
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.Overlays:
                state.ShowOverlays = !state.ShowOverlays;
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.Stars:
                state.ShowStarOverlay = !state.ShowStarOverlay;
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.ColorCalibrate:
                SetColorCalibrationEnabled(state, !state.ColorCalibrationEnabled);
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.BackgroundNeutralize:
                state.BackgroundNeutralizationEnabled = !state.BackgroundNeutralizationEnabled;
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.SpccCalibrate:
                SetColorCalibrationEnabled(state, !state.ColorCalibrationEnabled);
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.Shortcuts:
                // The list is opened by the host's dropdown path; nothing to mutate here, but it must
                // report handled so it does not fall through to the DI-backed action handler.
                return true;
            case ToolbarAction.Compare:
                if (reverse)
                {
                    split?.RequestPin();
                }
                else
                {
                    split?.Toggle(hasBeforePixels, hasCrop);
                }
                state.NeedsRedraw = true;
                return true;
            case ToolbarAction.ZoomFit:
                ZoomToFit(state);
                return true;
            case ToolbarAction.ZoomActual:
                ZoomToActual(state);
                return true;
            // Left-click opens the menu, so this is the RIGHT-click path: a straight fit/actual toggle,
            // which is the one-click gesture the old pair of buttons gave and a menu cannot.
            case ToolbarAction.Zoom:
                if (state.ZoomToFit)
                {
                    ZoomToActual(state);
                }
                else
                {
                    ZoomToFit(state);
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// What one auto-crop scan found: the rectangle, which of the two tiers answered, and whether the
    /// walk refused an edge. All three are needed at the status bar, because a crop is a border that is
    /// missing and the only other evidence is the file looking different.
    /// </summary>
    /// <param name="Rect">The area to keep, in full-frame pixels.</param>
    /// <param name="FromCoverage">True when the master's own coverage plane answered (exact), false when
    /// the edge-noise walk did (an estimate).</param>
    /// <param name="Declined">True when at least one edge refused: its noise was still falling at the
    /// bound, so that edge was left alone. Only meaningful for the walk.</param>
    public readonly record struct CropScan(Rectangle Rect, bool FromCoverage, bool Declined);

    /// <summary>
    /// Finds the area of a stacked master worth showing. Runs off the render thread: reading a coverage
    /// sidecar decodes a second full-frame FITS, and the walk measures four edge profiles.
    /// </summary>
    /// <remarks>
    /// <para><b>The exact tier wins wherever it exists.</b> A TianWen drizzle master writes its
    /// accumulated per-pixel weight to <c>&lt;master&gt;.rejection.fits</c>, which states the covered
    /// area outright; everything else -- another program's composite, an older sidecar that cannot say
    /// which kind of map it is -- gets the estimate. The same split as the mount limits' mechanical
    /// tier: a fallback, never a cross-check.</para>
    /// <para>Both tiers start from <see cref="Image.LargestCoveredRectangle()"/>, and the walk runs even
    /// when that keeps the whole frame. A frame with no canvas ring can still carry the band -- a stack
    /// someone has already cropped to its zero-free rectangle is exactly that shape, and it is the file
    /// most likely to be handed to a viewer. Skipping the walk there to save a second was the first
    /// version, and it made the feature silently not apply to the case that needs it most. An ordinary
    /// photograph answers "nothing to trim" on its own: vignetting LOWERS a corner's absolute noise
    /// along with its signal, so the outermost band is never the noisiest part of the profile.</para>
    /// </remarks>
    public static CropScan ScanForCrop(Image image, string? filePath, ILogger? logger = null)
    {
        var union = image.LargestCoveredRectangle();
        if (union.Width <= 0 || union.Height <= 0)
        {
            return new CropScan(union, FromCoverage: false, Declined: false);
        }

        if (filePath is { Length: > 0 } path)
        {
            try
            {
                if (IntegrationFitsWriter.TryReadCoverageMap(path, out var coverage))
                {
                    var exact = image.LargestCoveredRectangle(coverage);
                    if (exact.Width > 0 && exact.Height > 0)
                    {
                        logger?.LogDebug("Auto-crop from the coverage plane: {W}x{H} at ({X},{Y})",
                            exact.Width, exact.Height, exact.X, exact.Y);
                        return new CropScan(exact, FromCoverage: true, Declined: false);
                    }
                }
            }
            catch (Exception ex)
            {
                // A sidecar that cannot be read is not a reason to refuse the crop: fall through to the
                // walk, which needs nothing but the pixels already in hand.
                logger?.LogDebug(ex, "Coverage sidecar beside {Path} could not be used", path);
            }
        }

        var trims = CoverageEdgeWalk.Measure(image, union);
        logger?.LogDebug(
            "Auto-crop edge walk: left {L} top {T} right {R} bottom {B} (declined: {Declined})",
            trims.Left.Depth, trims.Top.Depth, trims.Right.Depth, trims.Bottom.Depth, trims.AnyDeclined);
        return new CropScan(trims.Apply(union), FromCoverage: false, trims.AnyDeclined);
    }
}
