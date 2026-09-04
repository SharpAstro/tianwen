using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        // Viewport pan + cursor-anchored wheel zoom (DIR.Lib): the controller owns the gesture; the
        // display transform stays on ViewerState (render/persistence/toolbar contracts untouched), so
        // each gesture seeds the controller from state and writes the result back. Defaults match the
        // viewer's historical behaviour (floor 0.01, step 1.15, no upper clamp).
        private readonly PanZoomController _panZoom = new PanZoomController();

        // Which toolbar button the pointer was last over, so a move that changes it can ask for the
        // repaint that hover chrome needs. Derived, render-thread only -- not view state.
        private ToolbarAction? _lastHoveredToolbarButton;

        /// <summary>
        /// Begin a viewport pan drag. Public for hosts with bespoke press dispatch (the standalone
        /// viewer's <c>Program.cs</c>); the embedded <see cref="HandleInput"/> path calls it internally.
        /// Move/release continue through <see cref="HandleInput"/> in both hosts.
        /// </summary>
        public void BeginViewportPan(float x, float y)
        {
            if (_state is not { } state)
            {
                return;
            }
            _panZoom.PanOffset = new Vector2(state.PanOffset.X, state.PanOffset.Y);
            _panZoom.BeginPan(x, y);
        }

        // Called only through HandleInput, which forces full damage for anything that asks for a
        // frame without saying what changed. See ImageRendererBase.Damage.cs.
        private bool HandleViewerInput(InputEvent evt)
        {
            // The split divider's drag. Its PRESS armed it from the region it painted, so only motion
            // and release are routed -- and only here, which both hosts already forward to. A control
            // that owns its state costs one line, not a branch in each of three handlers.
            if (Split.HandleInput(evt))
            {
                if (_state is { } dragState)
                {
                    dragState.NeedsRedraw = true;
                }

                // Only the strip the divider crossed differs: both halves draw the same quad into
                // complementary clips, so a pixel left of both positions or right of both is unchanged.
                if (Split.LastDragSweep is { } sweep)
                {
                    RequestDamage(sweep);
                }

                return true;
            }

            return evt switch
            {
                InputEvent.KeyDown(var key, var modifiers) => HandleViewerKey(key, modifiers),
                InputEvent.MouseDown(var px, var py, _, _, _) => HandleViewerMouseDown(px, py, evt),
                InputEvent.MouseMove(var px, var py) => HandleViewerMouseMove(px, py, evt),
                InputEvent.MouseUp(_, _, _) => HandleViewerMouseUp(evt),
                InputEvent.Scroll(var delta, var mx, var my, _) => HandleViewerScroll(delta, mx, my),
                _ => false
            };
        }

        private bool HandleViewerKey(InputKey key, InputModifier modifiers)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // An open overlay gets first crack at the keyboard, so Escape/Enter/Arrows reach it before
            // falling through to global shortcuts (Escape would otherwise quit via RequestExitSignal).
            // Asked of whatever CLAIMED the keyboard by painting, not of one named dropdown: this viewer is
            // its own host when it runs standalone (tianwen-fits), so a second overlay added here would
            // otherwise need its own line, which is the omission this whole mechanism removes.
            if (Ui.KeyboardClaimant?.HandleKeyDown(key) == true)
            {
                state.NeedsRedraw = true;
                return true;
            }

            var ctrl = (modifiers & InputModifier.Ctrl) != 0;
            var shift = (modifiers & InputModifier.Shift) != 0;

            // SER transport keys take priority while a sequence is loaded -- they deliberately claim
            // Space / arrows / Home / End / Up / Down (Up/Down would otherwise step the file list) for
            // playback. Seeks route through state.RequestedFrame so decode stays off the render thread.
            if (state.IsSequence && !ctrl && HandleTransportKey(key, state))
            {
                state.NeedsRedraw = true;
                return true;
            }

            if (ctrl)
            {
                switch (key)
                {
                    case InputKey.Plus:
                        ViewerActions.ZoomIn(state);
                        return true;
                    case InputKey.Minus:
                        ViewerActions.ZoomOut(state);
                        return true;
                    case InputKey.D0:
                        ViewerActions.ZoomToFit(state);
                        return true;
                    case InputKey.D1:
                        ViewerActions.ZoomToActual(state);
                        return true;
                    case >= InputKey.D2 and <= InputKey.D9:
                        ViewerActions.ZoomTo(state, 1f / (key - InputKey.D0));
                        return true;
                }
            }

            switch (key)
            {
                case InputKey.Escape:
                    PostSignal(new RequestExitSignal());
                    return true;
                case InputKey.F11:
                    PostSignal(new ToggleFullscreenSignal());
                    return true;
                case InputKey.T:
                    ViewerActions.ToggleStretch(state);
                    return true;
                case InputKey.S:
                    state.ShowStarOverlay = !state.ShowStarOverlay;
                    return true;
                case InputKey.C:
                    if (_document is not null)
                    {
                        ViewerActions.CycleChannelView(state, _document.UnstretchedImage.ChannelCount);
                    }
                    return true;
                case InputKey.D:
                    ViewerActions.CycleDebayerAlgorithm(state);
                    return true;
                case InputKey.I:
                    state.ShowInfoPanel = !state.ShowInfoPanel;
                    return true;
                case InputKey.K:
                    // Toggle the live rolling-window stack vs the raw frame (sequence-only). The controller
                    // keeps showing the raw frame until the first master is built.
                    if (state.IsSequence)
                    {
                        state.ShowStacked = !state.ShowStacked;
                        state.WaveletDirty = true; // push the current sharpen state when (re)entering stacked
                        state.NeedsTextureUpdate = true;
                        state.NeedsRedraw = true;
                    }
                    return true;
                case InputKey.L:
                    state.ShowFileList = !state.ShowFileList;
                    return true;
                case InputKey.Plus:
                    ViewerActions.CycleStretchPreset(state);
                    return true;
                case InputKey.Minus:
                    ViewerActions.CycleStretchPreset(state, reverse: true);
                    return true;
                case InputKey.B:
                    if (shift)
                    {
                        ViewerActions.CycleCurvesMode(state);
                    }
                    else
                    {
                        ViewerActions.CycleCurvesBoost(state);
                    }
                    return true;
                case InputKey.A:
                    // A/B compare. Shift re-pins the current settings without leaving the split.
                    if (shift)
                    {
                        Split.RequestPin();
                    }
                    else
                    {
                        Split.Toggle(HasBeforeImageTextures);
                    }
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.G:
                    state.ShowGrid = !state.ShowGrid;
                    return true;
                case InputKey.O:
                    state.ShowOverlays = !state.ShowOverlays;
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.H:
                    // Shift holds or releases the display mapping the blink is measured against. It
                    // moved off Shift+Space because on a transport Shift means the OTHER DIRECTION
                    // everywhere else, and H is where "hold" reads.
                    if (shift)
                    {
                        state.CarryDisplayAcrossFrames = !state.CarryDisplayAcrossFrames;
                        if (!state.CarryDisplayAcrossFrames)
                        {
                            // Blinking through per-frame auto-stretches is a flicker, not a comparison.
                            state.IsBlinking = false;
                        }
                        state.NeedsRedraw = true;
                    }
                    else
                    {
                        ViewerActions.CycleHdr(state);
                    }
                    return true;
                case InputKey.V:
                    if (shift)
                    {
                        state.HistogramLogScale = !state.HistogramLogScale;
                    }
                    else
                    {
                        state.ShowHistogram = !state.ShowHistogram;
                    }
                    return true;
                case InputKey.P:
                    PostSignal(new PlateSolveSignal());
                    return true;
                case InputKey.E:
                    // AI enhance, only where a SharpenPipeline is wired (the button is hidden otherwise).
                    if (EnhanceAvailable)
                    {
                        PostSignal(new EnhanceImageSignal());
                    }
                    return true;
                case InputKey.F:
                    ViewerActions.ZoomToFit(state);
                    return true;
                case InputKey.N:
                    TryToggleBackgroundNeutralization(state);
                    return true;
                case InputKey.W:
                    // The same gesture as the SPCC toolbar button: toggle the calibration, and start one
                    // if the document has none yet. It used to only START, which on a calibrated document
                    // was a no-op, so W could neither switch the calibration off nor back on.
                    ViewerActions.SetColorCalibrationEnabled(state, !state.ColorCalibrationEnabled);
                    TryStartColorCalibration(state);
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.R:
                    ViewerActions.ZoomToActual(state);
                    return true;
                case InputKey.Z:
                    // Opens the MENU rather than cycling a zoom, because the menu is the only way to
                    // reach 1:N without already knowing which Ctrl+digit each ratio is -- and a
                    // keyboard user could not open it at all before. F and R keep their direct fit and
                    // 1:1, so the fast paths are untouched; this is the discoverable one.
                    //
                    // Returns the open result rather than a bare true: with no document the Zoom button
                    // is not painted, so there are no bounds to anchor a menu to, and claiming the key
                    // there would swallow it for nothing.
                    return OpenToolbarDropdown(state, ToolbarAction.Zoom);
                case InputKey.Space:
                    // The SER transport claims Space while a sequence is loaded (handled above), so this
                    // is the still-image case: the same play/pause gesture, pointed at the file list.
                    // Ctrl returns to the frame the run is HELD to -- the reference a blink is measured
                    // against, and the one thing a blink comparator needs that stepping cannot give.
                    if (ctrl)
                    {
                        SnapToDisplayAnchor(state);
                    }
                    else if (state.ImageFileNames.Count >= 2)
                    {
                        // Shift is the other direction, as on any transport. Pressing a direction while
                        // already running THAT way pauses; pressing the other one reverses rather than
                        // stopping, so a comparison never needs two presses to turn around.
                        var step = shift ? -1 : 1;
                        state.IsBlinking = !(state.IsBlinking && state.BlinkStep == step);
                        state.BlinkStep = step;
                    }
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.Up:
                    if (state.SelectedFileIndex > 0)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex - 1);
                    }
                    return true;
                case InputKey.Down:
                    if (state.SelectedFileIndex < state.ImageFileNames.Count - 1)
                    {
                        ViewerActions.SelectFile(state, state.SelectedFileIndex + 1);
                    }
                    return true;
                default:
                    return false;
            }
        }

        // SER transport keys (sequence-only): play/pause, step, jump to ends, speed. Step/Home/End pause
        // and request a frame; the SequencePlayer decodes it off the render thread next tick.
        private bool HandleTransportKey(InputKey key, ViewerState state)
        {
            switch (key)
            {
                case InputKey.Space:
                case InputKey.Tab:
                    state.IsPlaying = !state.IsPlaying;
                    return true;
                case InputKey.Left:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Max(0, state.FrameIndex - 1);
                    return true;
                case InputKey.Right:
                    state.IsPlaying = false;
                    state.RequestedFrame = Math.Min(state.FrameCount - 1, state.FrameIndex + 1);
                    return true;
                case InputKey.Home:
                    state.IsPlaying = false;
                    state.RequestedFrame = 0;
                    return true;
                case InputKey.End:
                    state.IsPlaying = false;
                    state.RequestedFrame = state.FrameCount - 1;
                    return true;
                case InputKey.Up:
                    ViewerActions.CyclePlaybackSpeed(state, faster: true);
                    return true;
                case InputKey.Down:
                    ViewerActions.CyclePlaybackSpeed(state, faster: false);
                    return true;
                default:
                    return false;
            }
        }

        private void TryStartColorCalibration(ViewerState state)
        {
            if (_document?.Stars is { Count: >= 5 }
                && _document.ColorCalibration is null
                && (_document.UnstretchedImage.ChannelCount >= 3
                    || _document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB)
                && _document.TryBeginColorCalibration())
            {
                state.StatusMessage = "Calibrating color...";
                state.NeedsRedraw = true;

                // Capture-by-local so the work always clears the in-flight flag on the document it
                // started for, even if the user has navigated away in the meantime.
                var docForTask = _document;

                // Guarded, and tracked when the host supplied a tracker. This used to be a DISCARDED
                // Task.Run with a try/finally and no catch, which failed three ways at once: a throw
                // became an unobserved task exception so nothing logged it, the status line kept
                // saying "Calibrating color..." with no way out, and shutdown abandoned the work
                // instead of draining it. RunGuardedAsync is exposed static for exactly the
                // no-tracker case, so the error routing is identical on both paths.
                var logger = Logger ?? NullLogger.Instance;
                if (Tracker is { } tracker)
                {
                    tracker.RunGuarded(
                        ct => CalibrateColorAsync(docForTask, state, ct),
                        AppToken, logger, "Colour calibration",
                        onError: ex => state.StatusMessage = $"Calibration failed: {StatusText.FromException(ex)}",
                        onFinally: () => EndColorCalibration(docForTask, state));
                }
                else
                {
                    _ = BackgroundTaskTracker.RunGuardedAsync(
                        ct => CalibrateColorAsync(docForTask, state, ct),
                        AppToken, logger, "Colour calibration",
                        onError: ex => state.StatusMessage = $"Calibration failed: {StatusText.FromException(ex)}",
                        onFinally: () => EndColorCalibration(docForTask, state));
                }
            }
        }

        private static void EndColorCalibration(AstroImageDocument document, ViewerState state)
        {
            document.EndColorCalibration();
            state.NeedsRedraw = true;
        }

        private async Task CalibrateColorAsync(AstroImageDocument document, ViewerState state, CancellationToken cancellationToken)
        {
            // Force the catalog rather than only using an already-warm one. SPCC MATCHES STARS against
            // it, so an unforced lazy meant the better method was handed nothing and the run silently
            // became a sky-background estimate -- the cost this was avoiding (a one-off Tycho-2
            // decode, ~500 ms) is one the user has just explicitly asked for by pressing the button,
            // and a plate-solved frame has usually paid it already (CatalogPlateSolver self-inits).
            var db = CelestialObjectDB is { } lazy
                ? await lazy.WithCancellation(cancellationToken)
                : null!;

            // SPCC first, sky-background as the fallback.
            var (matched, diag) = await document.ComputeSpccColorCalibrationAsync(db);
            if (matched <= 0)
            {
                // Log WHY SPCC declined before the fallback overwrites its diagnostic. Without this
                // the log showed only "Sky background R=1.003 ..." -- a successful-looking line that
                // says nothing about the better method having been tried and refused, and on an
                // already-background-neutralised master the fallback returns ~neutral, so the whole
                // thing reads like a calibration that worked. That is precisely the trail that has to
                // exist when someone asks why SPCC "did nothing".
                Logger?.LogInformation("SPCC declined, falling back to sky background: {Reason}", diag);
                (matched, diag) = await document.ComputeColorCalibrationAsync(db);
            }

            if (document.ColorCalibration is { } wb)
            {
                ViewerActions.SetColorCalibrationEnabled(state, true);

                // Re-solve background neutralisation against the calibration that just landed. The
                // gains are chosen so the background is neutral AFTER the WB multiply, so they are a
                // function of the WB -- and a user who neutralised BEFORE calibrating would otherwise
                // keep gains solved for no calibration at all, which is exactly the cast this whole
                // coupling exists to prevent. Cheap: the per-(method, WB) cache makes it a lookup
                // once seen, and the background scan itself is already done.
                if (state.BackgroundNeutralizationEnabled)
                {
                    // A calibration just landed and is being enabled, so its triple is the one to solve
                    // the neutralisation against.
                    document.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod, applyColorCalibration: true);
                }

                // The manual triple is dropped, because the pipeline MULTIPLIES the two
                // (StretchSolver.ComposeWhiteBalance is auto x manual) and what just landed is an
                // absolute answer measured from star photometry. A relative guess on top of it -- a
                // hand-dragged slider, or the gray-world "Auto" button, which writes this same manual
                // slot -- is a second correction applied to an already-corrected image. Pressing Auto
                // and then SPCC used to do exactly that, silently, with the sliders still reading
                // 1.00 throughout.
                //
                // The reset itself now lives in ViewerActions.SetColorCalibrationEnabled above, which
                // also REMEMBERS the triple so switching the calibration off restores it.

                // NO automatic stretch-mode change. Calibrating colour used to silently flip
                // Unlinked to Linked, which is irritating in the obvious way -- the user picked a
                // display mode and a colour operation moved it -- and it was papering over the real
                // defect rather than fixing it: Unlinked auto-normalises each channel independently
                // and so cancels any per-channel gain, which is why a WB looked like it did nothing
                // there. Linked is not actually a cure either (StretchSolver still folds the WB into
                // each channel's stats before deriving its shadow/midtone), so the flip traded one
                // wrong render for another while also taking away the user's choice.
                //
                // The fix belongs in the stretch, not here: ONE common curve with the WB shifting the
                // channels relative to it -- which is what PixInsight's linked STF after SPCC does,
                // and why a PI linked stretch looks like what this viewer calls Unlinked.

                // To the logger, not Console.Error. Stderr is only a channel for whoever thought to
                // redirect it, so these lines were absent from the app log where anyone looking for
                // them would go.
                Logger?.LogDebug("Colour calibration: {Diagnostics}", diag);
                state.StatusMessage = matched > 0
                    ? $"WB ({matched}★): R={wb.Item1:F3} G=1.000 B={wb.Item3:F3}"
                    : null;
            }
            else
            {
                Logger?.LogWarning("Colour calibration found no solution: {Diagnostics}", diag);
                state.StatusMessage = $"Calibration failed: {diag}";
            }
        }

        private void TryToggleBackgroundNeutralization(ViewerState state)
        {
            if (state.BackgroundNeutralizationEnabled)
            {
                _document!.BackgroundNeutralization = null;
                state.BackgroundNeutralizationEnabled = false;
                state.NeedsRedraw = true;
                return;
            }

            var gains = _document?.ComputeBackgroundNeutralization(state.BackgroundNeutralizationMethod, state.ColorCalibrationEnabled);
            if (gains is { } g)
            {
                state.BackgroundNeutralizationEnabled = true;
                state.NeedsRedraw = true;
                Logger?.LogDebug("Background neutralisation {Method}: R={R:F3} G={G:F3} B={B:F3}",
                    state.BackgroundNeutralizationMethod, g.R, g.G, g.B);
            }
        }

        // -----------------------------------------------------------------------
        // Mouse handling
        // -----------------------------------------------------------------------

        /// <summary>
        /// Handles mouse down: hit-tests toolbar/file list, then starts panning.
        /// Returns <c>true</c> if the event was consumed by hit-test, <c>false</c>
        /// if panning was started (caller may need to handle toolbar actions via
        /// <see cref="ViewerActions.HandleToolbarAction"/>).
        /// </summary>
        private bool HandleViewerMouseDown(float px, float py, InputEvent evt)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // Unified hit test: OnClick handlers fire for self-contained actions (e.g. HistogramLog).
            // A control that arms its OWN drag from the region it painted (the split divider) has
            // already done so by the time this returns -- which is why there is no branch for it below,
            // in either of the viewer's two press dispatchers.
            var hit = HitTestAndDispatch(px, py);

            if (hit is HitResult.ButtonHit { Action: var action } && Enum.TryParse<ToolbarAction>(action, out var toolbarAction))
            {
                ViewerActions.HandleToolbarAction(state, _document, toolbarAction,
                    split: Split, hasBeforePixels: HasBeforeImageTextures);
                if (toolbarAction is ToolbarAction.ColorCalibrate or ToolbarAction.SpccCalibrate)
                {
                    TryStartColorCalibration(state);
                }
                else if (toolbarAction is ToolbarAction.BackgroundNeutralize)
                {
                    TryToggleBackgroundNeutralization(state);
                }
                return true;
            }

            if (hit is ResizeHandleHit { Id: "FileList" })
            {
                state.IsResizingFileList = true;
                state.NeedsRedraw = true;
                return true;
            }

            if (hit is TransportScrubHit)
            {
                BeginScrubAt(px);
                return true;
            }

            if (hit is WhiteBalanceSliderHit { Channel: var wbChannel })
            {
                BeginWhiteBalanceDragAt(wbChannel, px);
                return true;
            }

            if (hit is WaveletSliderHit { Band: var wlBand })
            {
                BeginWaveletDragAt(wlBand, px);
                return true;
            }

            // A file-list row is registered as a region but is NOT claimed here: it carries no OnClick,
            // and the press has to continue to the scroll controller below or drag-to-scroll dies and
            // nothing ever selects (the tap is taken on RELEASE). Excluded by TYPE rather than by
            // rebuilding the pane's geometry here -- the whole reason the row registers a region is so
            // that this file does not own a second copy of where the rows are.
            if (hit is not null && hit is not HitResult.ListItemHit { ListId: FileListId })
            {
                return true; // OnClick already handled it (e.g. HistogramLog, PlayPause)
            }

            // Unclaimed press over the file list falls through to the scroll controller (viewport-gated):
            // arms drag-to-scroll / grabs the thumb. Select fires on the tap RELEASE (TakeAtomTap in
            // HandleViewerMouseUp), so a touch drag scrolls instead of selecting the row under the finger.
            if (_fileListScroll.HandleInput(evt))
            {
                state.NeedsRedraw = true;
                return true;
            }

            // An unclaimed RIGHT press on the image is the context menu, and it is handled here rather
            // than at the top of this method so every existing right-click keeps its meaning: over a
            // toolbar button it still reverse-cycles that button, and over any other declared region it
            // still reaches whatever claimed it. Only a press that would otherwise have started a pan
            // opens a menu.
            if (evt is InputEvent.MouseDown { Button: MouseButton.Right }
                && TryOpenImageContextMenu(state, px, py))
            {
                return true;
            }

            // No hit: start panning, but ONLY when the press is inside the image viewport. Otherwise a press
            // in the side panels / toolbar gaps / letterbox would grab the image and pan it (e.g. clicking the
            // planetary control panel must not drag the stream). Confines the drag to its viewport.
            var imgArea = _layout.ImageArea;
            var inViewport = px >= imgArea.X && px < imgArea.X + imgArea.Width
                          && py >= imgArea.Y && py < imgArea.Y + imgArea.Height;
            if (inViewport)
            {
                BeginViewportPan(px, py);
            }
            return false;
        }

        /// <summary>Last hovered file-list row (-1 = the header, int.MinValue = not over the pane).</summary>
        private int _lastHoveredFileListRow = int.MinValue;

        private bool HandleViewerMouseMove(float px, float py, InputEvent evt)
        {
            if (_state is not { } state)
            {
                return false;
            }

            state.MouseScreenPosition = (px, py);

            // An OPEN menu tracks the pointer, so every motion event is a repaint while one is up.
            // Unconditional rather than keyed on the hovered row: the row is resolved during paint (the
            // widget owns the geometry it drew), so there is nothing here to compare against, and a
            // menu is up for a moment while the pointer travels a short distance. Without this the
            // highlight only moved when something unrelated forced a frame, which is precisely how the
            // context menu came out with no hover state at all.
            if (state.ToolbarDropdown.IsOpen)
            {
                state.NeedsRedraw = true;
            }

            // Hover-driven toolbar chrome -- the button highlight AND its tooltip -- changes with the
            // pointer, but a move over the TOOLBAR changes no image pixel, so this method used to
            // return false and no frame was painted. The highlight was quietly stale the whole time;
            // the tooltip made it obvious, appearing only when some unrelated event forced a repaint.
            // Gated on the toolbar band because the hit test measures every label, and a move over the
            // image must stay free.
            var tb = _layout.Toolbar;
            var overToolbar = tb.Height > 0f && py >= tb.Y && py < tb.Bottom;
            var hoveredButton = overToolbar ? HitTestToolbar(px, py) : null;
            if (hoveredButton != _lastHoveredToolbarButton)
            {
                _lastHoveredToolbarButton = hoveredButton;
                state.NeedsRedraw = true;
            }

            // The same reasoning as the toolbar above, for the file list: the row highlight, a row's
            // hover tooltip and the header's full-path tooltip are ALL hover-driven, and a move over
            // the pane changes no image pixel -- so without this the pane repainted only when some
            // unrelated event forced a frame, which is exactly why the tooltip looked like it needed a
            // click to appear.
            //
            // Keyed on the hovered row (the header is index -1) so this costs one repaint per row
            // crossed rather than one per mouse-move. Read from the REGIONS the pane registered last
            // frame, not from re-derived geometry -- HitTest does not dispatch, and the point of the
            // rows being regions is that nothing else has to know where they are.
            var fileListHover = HitTest(px, py) is HitResult.ListItemHit { ListId: FileListId, Index: var hoverRow }
                ? hoverRow
                : int.MinValue;
            if (fileListHover != _lastHoveredFileListRow)
            {
                _lastHoveredFileListRow = fileListHover;
                state.NeedsRedraw = true;
            }

            // File-list drag-to-scroll / thumb drag in progress (returns false when its gesture is idle,
            // so ordinary moves fall through to the branches below).
            if (_fileListScroll.HandleInput(evt))
            {
                state.NeedsRedraw = true;
                return true;
            }

            // Transport scrub drag: continuously seek to the dragged frame (decoded off the render thread).
            if (state.IsScrubbing)
            {
                ScrubAt(px);
                return true;
            }

            // White-balance slider drag: continuously re-derive the WB multiplier from cursor-X.
            if (state.WhiteBalanceDragChannel >= 0)
            {
                UpdateWhiteBalanceDrag(px);
                return true;
            }

            // Wavelet-layer slider drag: continuously re-derive the per-layer gain from cursor-X.
            if (state.WaveletDragBand >= 0)
            {
                UpdateWaveletDrag(px);
                return true;
            }

            // File-list resize drag: width tracks the cursor's X position in
            // DPI-independent units. Clamped by FileListWidthBase's setter.
            if (state.IsResizingFileList)
            {
                state.FileListWidthBase = px / DpiScale;
                state.NeedsRedraw = true;
                return true;
            }

            // Panning always needs a redraw (image position changes)
            if (_panZoom.UpdatePan(px, py))
            {
                state.PanOffset = (_panZoom.PanOffset.X, _panZoom.PanOffset.Y);
                return true;
            }

            // Only redraw when cursor moves to a different image pixel
            var prevPos = state.CursorImagePosition;
            // Image-area pane rect (origin + size) from the single layout pass.
            var area = _layout.ImageArea;
            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, area.X, area.Y, area.Width, area.Height);
            if (state.CursorImagePosition == prevPos)
            {
                return false;
            }

            // The most frequent redraw in the whole app, and the cheapest to bound: a readout is
            // shown in exactly two places, so nothing else needs repainting. The info panel is not
            // optional here even though it looks secondary -- it lists the per-channel pixel values,
            // so omitting it would leave them frozen at whatever the pointer last touched while the
            // status bar beside them kept updating.
            RequestDamage(_layout.StatusBar);
            if (state.ShowInfoPanel)
            {
                RequestDamage(_layout.InfoPanel);
            }

            return true;
        }

        private bool HandleViewerMouseUp(InputEvent evt)
        {
            if (_state is { } state)
            {
                // File-list gesture release: a tap selects the row (the Planner/Equipment tap-on-release
                // model); a drag release just ends the scroll. Consumed releases skip the branches below
                // (no pan/scrub was active, the press went to the controller).
                if (_fileListScroll.HandleInput(evt))
                {
                    if (_fileListScroll.TakeAtomTap() is { } tappedRow && tappedRow < state.ImageFileNames.Count)
                    {
                        ViewerActions.SelectFile(state, tappedRow);
                    }
                    state.NeedsRedraw = true;
                    return true;
                }

                if (state.IsScrubbing)
                {
                    state.IsScrubbing = false;
                    state.NeedsRedraw = true;
                }
                if (state.WhiteBalanceDragChannel >= 0)
                {
                    state.WhiteBalanceDragChannel = -1;
                    state.NeedsRedraw = true;
                }
                if (state.WaveletDragBand >= 0)
                {
                    state.WaveletDragBand = -1;
                    state.NeedsRedraw = true;
                }
                if (state.IsResizingFileList)
                {
                    state.IsResizingFileList = false;
                    state.NeedsRedraw = true;
                }
                _panZoom.EndPan();
                return true;
            }
            return false;
        }

        /// <summary>Which toolbar button <see cref="_toolbarWheelAccumulator"/> belongs to; null when the
        /// wheel is not over a wheel-driven button. Moving between buttons resets the accumulation.</summary>
        private ToolbarAction? _toolbarWheelAction;

        /// <summary>Unconsumed wheel delta over that button, carried between events so a trackpad's
        /// sub-1.0 deltas add up to a notch instead of each counting as one.</summary>
        private float _toolbarWheelAccumulator;

        private bool HandleViewerScroll(float scrollY, float mouseX, float mouseY)
        {
            if (_state is not { } state)
            {
                return false;
            }

            // A wheel notch over a multi-option toolbar button steps that button's options (up = forward),
            // which is the only gesture the toolbar gives the wheel -- so this is checked before the panes
            // below even though the toolbar overlaps neither of them.
            //
            // HitTest, NOT HitTestAndDispatch: dispatching fires the region's OnClick, so scrolling over a
            // button would also PRESS it. And going through the region system rather than the toolbar's own
            // rects is what makes an open dropdown win -- it registers a full-viewport backdrop, so that
            // answers the hit and the wheel cannot reach a button drawn underneath it.
            if (HitTest(mouseX, mouseY) is HitResult.ButtonHit { Action: var buttonAction }
                && Enum.TryParse<ToolbarAction>(buttonAction, out var wheelAction))
            {
                // Accumulate, and step only per WHOLE notch. A trackpad delivers many sub-1.0 deltas --
                // the file-list controller below accumulates them for the same reason -- and stepping
                // one option per event would run through a five-entry preset list on one gentle swipe.
                // Moving to a different button starts a fresh accumulation so a leftover fraction from
                // the neighbour cannot make the first notch land early.
                if (_toolbarWheelAction != wheelAction)
                {
                    _toolbarWheelAction = wheelAction;
                    _toolbarWheelAccumulator = 0f;
                }
                _toolbarWheelAccumulator += scrollY;

                // Truncation toward zero, so the remainder keeps its sign and a slow swipe accumulates
                // instead of being repeatedly discarded.
                var steps = (int)_toolbarWheelAccumulator;
                if (ViewerActions.TryHandleToolbarWheel(state, _document, wheelAction, steps))
                {
                    _toolbarWheelAccumulator -= steps;
                    state.NeedsRedraw = true;
                    return true;
                }

                // Not a wheel-driven button: drop the accumulation rather than leaving a fraction to
                // surprise the next button that is.
                _toolbarWheelAction = null;
                _toolbarWheelAccumulator = 0f;
            }

            // Scroll file list when hovering over it (pane rect from the single arranged layout). The wheel
            // goes through the controller, whose fractional offset accumulates sub-1.0 trackpad deltas
            // instead of truncating them to zero, and whose bound is Count-visible (not the old Count-1).
            var fileListPane = _layout.FileList;
            if (state.ShowFileList && fileListPane.Contains(mouseX, mouseY))
            {
                _fileListScroll.HandleInput(new InputEvent.Scroll(scrollY, mouseX, mouseY));
                state.NeedsRedraw = true;
                return true;
            }

            // Zoom: inside the image viewport (image-area pane rect from the single layout pass).
            // RectF32.Contains is half-open on both axes, which is the same test this spelled out.
            var area = _layout.ImageArea;

            if (area.Contains(mouseX, mouseY))
            {
                // Cursor-anchored zoom via the shared controller: seed the display transform from state,
                // run the zoom, write the result back. A clamped no-op (already at the floor) changes
                // nothing, including ZoomToFit, which only clears when the zoom actually moves.
                _panZoom.Zoom = state.Zoom;
                _panZoom.PanOffset = new Vector2(state.PanOffset.X, state.PanOffset.Y);
                if (_panZoom.ZoomAtCursor(scrollY, mouseX, mouseY, area))
                {
                    state.Zoom = _panZoom.Zoom;
                    state.PanOffset = (_panZoom.PanOffset.X, _panZoom.PanOffset.Y);
                    state.ZoomToFit = false;
                }
                return true;
            }

            return false;
        }
    }
}
