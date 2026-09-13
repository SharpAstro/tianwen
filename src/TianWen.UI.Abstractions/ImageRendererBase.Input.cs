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
        // MaxZoom, because the controller's own default is float.PositiveInfinity: the wheel and a
        // pinch go through ZoomByFactor, which clamps to this and to nothing else.
        private readonly PanZoomController _panZoom = new PanZoomController
        {
            MaxZoom = ViewerActions.MaxZoom,
        };

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
            // A touchscreen synthesizes mouse events from the FIRST finger, so a two-finger pinch is
            // also a press-and-drag as far as this path can tell. Arming a pan from one would drag the
            // image with that finger while the same gesture zooms it. Refused here rather than in each
            // caller because both hosts arm the pan themselves (the standalone viewer's Program.cs has
            // its own press dispatch), so this is the one place both go through.
            if (_isPinching || _state is not { } state)
            {
                return;
            }
            _panZoom.PanOffset = new Vector2(state.PanOffset.X, state.PanOffset.Y);
            _panZoom.BeginPan(x, y);

            // Where the press landed, so the release can tell a TAP from a drag. Recorded here because
            // this is the one place both hosts arm the pan -- the standalone viewer has its own press
            // dispatch (Program.cs) and would otherwise never record it, so click-to-select would be
            // dead in the only host that has a context ladder to select from.
            _pressToSelect = (x, y);
        }

        /// <summary>
        /// Where an unclaimed press on the picture landed, or null when no such press is outstanding.
        /// </summary>
        /// <remarks>
        /// <b>A selection fires on the tap RELEASE, not the press</b>, because a press on the picture
        /// is also the start of a pan: selecting there would fire on every drag, and a drag that ends
        /// somewhere else would leave whatever happened to be under the finger selected. Same
        /// tap-on-release model the file list uses (<c>TakeAtomTap</c>) and for the same reason.
        /// </remarks>
        private (float X, float Y)? _pressToSelect;

        /// <summary>
        /// How far a press may travel and still count as a tap rather than a drag, in screen pixels.
        /// </summary>
        /// <remarks>
        /// Not zero: a mouse moves a pixel or two under a real finger, and a touchscreen more, so an
        /// exact-equality test would make selection work on a trackpad and fail on a touchscreen. Not
        /// large either -- past a few pixels the gesture has visibly panned the picture, and the user
        /// is not asking about whatever is under the pointer at the end of that.
        /// </remarks>
        private const float TapSlopPx = 4f;

        /// <summary>
        /// True from the first <see cref="InputEvent.Pinch"/> of a touchscreen gesture until its
        /// <see cref="InputEvent.PinchEnd"/>. Derived, render-thread only -- not view state: it exists
        /// solely to keep the first finger's synthesized mouse drag from panning mid-pinch.
        /// </summary>
        private bool _isPinching;

        /// <summary>
        /// Two-finger pinch zoom over the image, anchored at the finger midpoint.
        /// </summary>
        /// <remarks>
        /// <para><b>Only a TOUCHSCREEN pinch is acted on.</b> A Windows precision touchpad fires the
        /// FingerMotion events this arrives as <em>and</em> a mouse wheel for one pinch gesture, and the
        /// wheel is what this viewer has always zoomed on -- so acting on both would zoom twice per
        /// gesture. <see cref="SkyMapTab"/> resolves the same dual-fire the other way round (pinch wins,
        /// the wheel is suppressed for a grace window) because its wheel zoom anchors at the view centre
        /// and a touchpad pinch wants the cursor; here the wheel path is already cursor-anchored, which
        /// is the same answer this would give.</para>
        /// <para><b>The scale is per-EVENT, not cumulative since the gesture began</b> -- the renderer
        /// re-bases its reference distance after each dispatch -- so it multiplies straight onto the
        /// current zoom.</para>
        /// <para>The pan the first finger drove before the second one landed is deliberately left
        /// applied: it is travel the user made, and undoing it would teleport the view whenever a
        /// deliberate one-finger pan turns into a pinch without lifting.</para>
        /// </remarks>
        private bool HandleViewerPinch(float scale, float centerX, float centerY, PinchSource source)
        {
            if (source != PinchSource.Touchscreen || _state is not { } state)
            {
                return false;
            }

            _isPinching = true;
            _panZoom.EndPan();

            // Anchored on the image pane, like the wheel: a midpoint over the file list or the toolbar
            // is not a gesture on the image, and the anchor arithmetic is expressed against this rect.
            var area = _layout.ImageArea;
            if (!float.IsFinite(scale) || scale <= 0f || !area.Contains(centerX, centerY))
            {
                return false;
            }

            // Same seed-run-write-back as the wheel: the controller owns the gesture, the display
            // transform stays on ViewerState. A clamped no-op (already at the floor) changes nothing,
            // ZoomToFit included -- it only clears when the zoom actually moves.
            _panZoom.Zoom = state.Zoom;
            _panZoom.PanOffset = new Vector2(state.PanOffset.X, state.PanOffset.Y);
            if (!_panZoom.ZoomByFactor(scale, centerX, centerY, area))
            {
                return false;
            }

            state.Zoom = _panZoom.Zoom;
            state.PanOffset = (_panZoom.PanOffset.X, _panZoom.PanOffset.Y);
            state.ZoomToFit = false;
            return true;
        }

        /// <summary>
        /// End of a pinch. The pan stays disarmed until the next press, so the finger still on the glass
        /// when its partner lifts cannot resume a drag from an anchor the zoom has since moved under.
        /// </summary>
        private bool HandleViewerPinchEnd()
        {
            _isPinching = false;
            return false; // nothing moved, so nothing to repaint
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
                InputEvent.KeyDown k => HandleViewerKey(k.Key, k.Modifiers, k.Repeat),
                InputEvent.KeyUp u => HandleViewerKeyUp(u.Key),
                InputEvent.MouseDown(var px, var py, _, _, _) => HandleViewerMouseDown(px, py, evt),
                InputEvent.MouseMove(var px, var py) => HandleViewerMouseMove(px, py, evt),
                InputEvent.MouseUp(_, _, _) => HandleViewerMouseUp(evt),
                InputEvent.Scroll(var delta, var mx, var my, _) => HandleViewerScroll(delta, mx, my),
                InputEvent.Pinch p => HandleViewerPinch(p.Scale, p.X, p.Y, p.Source),
                InputEvent.PinchEnd => HandleViewerPinchEnd(),
                _ => false
            };
        }

        private bool HandleViewerKey(InputKey key, InputModifier modifiers, bool repeat)
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

            // An auto-repeat is the same press arriving again at the OS repeat rate, so only a STEP may
            // act on one. Every other key here either toggles something or is a one-shot, and a toggle
            // driven by auto-repeat flips several times a second: held Space started and stopped the blink
            // continuously, which is how this was reported. Stated once rather than per case, because the
            // list of keys that genuinely want repeating is the short half. Placed after the claimant
            // check so an overlay that owns the keyboard keeps its own arrow repeats.
            if (repeat && !RepeatsAsAStep(key))
            {
                // A repeat is the only evidence the platform gives that a key is being HELD rather than
                // tapped, and holding Space suspends a blink for as long as it is down. Everything else
                // reaching here wants the repeat dropped, which is what P23 fixed.
                if (key is InputKey.Space && _blinkStoppedByThisPress)
                {
                    state.BlinkResumeOnRelease = true;
                }

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
                    // A SELECTION is dismissed before Escape means "quit". Not a second copy of the
                    // claimant rule above -- a selection is not a painted overlay and owns no
                    // keyboard, so nothing else can answer for it -- but it IS the same principle:
                    // Escape retires the most recent thing it can, and only quits when there is
                    // nothing left to retire. Without this the only way to clear a selection is a
                    // click on empty sky, which is not available when the frame is full of objects.
                    if (state.SelectedObject is not null)
                    {
                        state.SelectedObject = null;
                        state.StatusMessage = null;
                        state.NeedsRedraw = true;
                        return true;
                    }

                    // Reached only when nothing on screen claimed the keyboard: an open dropdown eats
                    // Escape at the KeyboardClaimant check above, which is the mechanism's whole point
                    // and is pinned by ViewerEscapeTests. A special case here would be a second copy of
                    // that rule, which is exactly what the comment on that check says it removes.
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
                    // Shift is the crop, because C on its own has cycled the channel view since before
                    // there was one. Posted rather than done here for the reason P and E are: the scan
                    // is tens of milliseconds and belongs off this thread.
                    if (shift)
                    {
                        PostSignal(new AutoCropSignal());
                    }
                    else if (_document is not null)
                    {
                        ViewerActions.CycleChannelView(state, _document.UnstretchedImage.ChannelCount);
                    }
                    return true;
                case InputKey.D:
                    // Through the BUTTON's predicate, not a second copy of it. The button has always
                    // been disabled for a frame with no CFA; the key was not, so on a mono frame D
                    // cycled a demosaic that has nothing to act on, and the status line changing said
                    // the picture had changed when it had not. (Auto already resolves mono to
                    // BilinearMono, so every option there was the same option.)
                    if (IsToolbarButtonEnabled(ToolbarAction.Debayer, _document))
                    {
                        ViewerActions.CycleDebayerAlgorithm(state);
                    }
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
                // F1 is where every application puts help, and this one had it behind a "?" button
                // alone. Through OpenToolbarDropdown so the key and the button are ONE path: it
                // anchors under the button, starts the AI capability probe and opens the root page.
                // It answers false when the button was never painted (a chromeless embedded host),
                // which is the right no-op rather than a panel anchored at nothing.
                case InputKey.F1:
                    return OpenToolbarDropdown(state, ToolbarAction.Shortcuts);
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
                        Split.Toggle(HasBeforeImageTextures, _cropActive);
                    }
                    state.NeedsRedraw = true;
                    return true;
                case InputKey.G:
                    ViewerActions.ToggleGrid(state);
                    return true;
                case InputKey.O:
                    // Shift steps back down the ladder, the direction every other cycler here gives
                    // Shift. The ladder wraps either way, so neither end is a dead end.
                    ViewerActions.CycleOverlayLevel(state, reverse: shift);
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
                    // Opens the white-balance popover, which is where the calibration now lives beside
                    // the sliders it populates. It used to toggle the calibration directly, from a time
                    // when that was a toolbar button and the only thing W could usefully reach; now the
                    // popover holds the toggle, the provenance line and the three sliders, and a key
                    // that opened only one of them would be the odd way in. Same shape as Z, which
                    // opens the zoom menu rather than cycling a zoom.
                    OpenToolbarDropdown(state, ToolbarAction.WhiteBalance);
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
                        var wasBlinking = state.IsBlinking;
                        state.IsBlinking = !(state.IsBlinking && state.BlinkStep == step);
                        state.BlinkStep = step;

                        // Only a press that STOPPED a running blink can be promoted to a hold by the
                        // repeat that may follow; one that started a blink has nothing to restore.
                        // Cleared unconditionally, so a release lost to a focus change cannot be
                        // collected by the next press.
                        _blinkStoppedByThisPress = wasBlinking && !state.IsBlinking;
                        state.BlinkResumeOnRelease = false;
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
        /// <summary>
        /// Keys whose action is a STEP, so repeating the key repeats the action: walking the file list,
        /// stepping or seeking a sequence, and zooming. Holding one of these is a request for more of it,
        /// which is what auto-repeat is for.
        /// </summary>
        /// <summary>
        /// Whether the press currently down was the one that stopped a running blink, and so is a
        /// candidate for promotion to a hold. Render-thread only, and deliberately NOT on
        /// <see cref="ViewerState"/>: it is dead the moment the key comes up, so nothing outside the
        /// press has any business reading it.
        /// </summary>
        private bool _blinkStoppedByThisPress;

        /// <summary>
        /// A key coming back up. Only Space means anything here: released after being HELD, it resumes
        /// the blink that the press suspended.
        /// </summary>
        /// <remarks>
        /// Returns false for anything else rather than claiming the event, so a host stays free to route
        /// releases elsewhere. Reported 2026-09-07: "holding down space when we are blinking should pause
        /// it". Suppressing the repeat (P23) made a hold ONE stop instead of a stream of them, which is
        /// the half that needed no new event; this is the other half, and it needed
        /// <c>InputEvent.KeyUp</c> to exist at all (DIR.Lib 8.14).
        /// </remarks>
        private bool HandleViewerKeyUp(InputKey key)
        {
            if (key is not InputKey.Space || _state is not { } state)
            {
                return false;
            }

            _blinkStoppedByThisPress = false;
            if (!state.BlinkResumeOnRelease)
            {
                // A tap, so the stop it performed stands. This is the path that keeps Space a toggle.
                return false;
            }

            state.BlinkResumeOnRelease = false;
            state.IsBlinking = true;
            state.NeedsRedraw = true;
            return true;
        }

        private static bool RepeatsAsAStep(InputKey key)
            => key is InputKey.Up or InputKey.Down or InputKey.Left or InputKey.Right
                or InputKey.PageUp or InputKey.PageDown or InputKey.Plus or InputKey.Minus;

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
                RunGuarded(
                    ct => CalibrateColorAsync(docForTask, state, ct),
                    "Colour calibration",
                    onError: ex => state.StatusMessage = $"Calibration failed: {StatusText.FromException(ex)}",
                    onFinally: () => EndColorCalibration(docForTask, state));
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
                : null;

            // SPCC first, sky-background as the fallback. With no catalogue SPCC has nothing to match
            // against, so it is not ATTEMPTED rather than handed a null through a parameter that does
            // not accept one -- which is what the null-forgiving `!` here used to do. The fallback
            // needs no catalogue and still runs.
            var (matched, diag) = db is not null
                ? await document.ComputeSpccColorCalibrationAsync(db)
                : (0, "No star catalogue is loaded");
            if (matched <= 0)
            {
                // Log WHY SPCC declined before the fallback overwrites its diagnostic. Without this
                // the log showed only "Sky background R=1.003 ..." -- a successful-looking line that
                // says nothing about the better method having been tried and refused, and on an
                // already-background-neutralised master the fallback returns ~neutral, so the whole
                // thing reads like a calibration that worked. That is precisely the trail that has to
                // exist when someone asks why SPCC "did nothing".
                Logger?.LogInformation("SPCC declined, falling back to sky background: {Reason}", diag);
                (matched, diag) = await document.ComputeColorCalibrationAsync();
            }

            if (document.ColorCalibration is { } wb)
            {
                // THE CALIBRATION BELONGS TO THE SET, not to the frame that happened to be on screen
                // when it was asked for. A fit written only to this document is invisible to every
                // other frame of the run, because ColorCalibration reads the ANCHOR first -- so
                // calibrating one sub and blinking to the next showed the next one uncalibrated, which
                // is the opposite of what a blink is for. Pushed onto the anchor, every comparable
                // frame reads the same triple through Basis, and one fit holds the whole set.
                //
                // Comparability is already the anchor's own rule (DisplayCarry: geometry, planes,
                // depth, CFA, filter and OBJECT), so this cannot leak across targets: a different
                // object has no anchor in common to push to.
                if (document.DisplayAnchor is { } anchor)
                {
                    anchor.InheritColorCalibration(wb, document.ColorCalibrationSummary,
                        document.IsNarrowbandColorCalibration);
                }

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
                if (_document is { } loaded)
                {
                    loaded.BackgroundNeutralization = null;
                }
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
                // The white-balance button opens a popover and cycles nothing, so it takes the
                // dropdown route here as well as in the standalone host's dispatcher (which consults
                // OpenToolbarDropdown for every button). One path for the one button on this bar whose
                // press has no cycle to fall through to.
                if (toolbarAction is ToolbarAction.WhiteBalance && OpenToolbarDropdown(state, toolbarAction))
                {
                    return true;
                }

                ViewerActions.HandleToolbarAction(state, _document, toolbarAction,
                    split: Split, hasBeforePixels: HasBeforeImageTextures, hasCrop: HasDisplayCrop);
                if (toolbarAction is ToolbarAction.ColorCalibrate)
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
            // Through HandleFileListScroll, which stands the controller down while the list is hidden --
            // its extent is stale then, and it would eat the press as a scroll gesture.
            if (HandleFileListScroll(state, evt))
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

            // The sky's palette gets every move, before anything else looks at it: it holds its own
            // fade open from the pointer, and while its grip is dragging the move is ITS move -- a
            // palette dragged across the picture must not also pan the picture. It answers false the
            // moment the grip is not held, so the ordinary path below is unaffected.
            if (PaintedSkyBackdrop is { } sky && sky.HandleInput(evt))
            {
                state.NeedsRedraw = true;
                return true;
            }

            // Set by any branch below that repaints something OTHER than the pixel readout. Every one of
            // them asks for a frame and FALLS THROUGH to the narrowing at the end of this method, which
            // declares the readout's two rects and nothing else -- so without this the narrow region
            // silently replaces the repaint they asked for. Tracked rather than inferred from
            // NeedsRedraw, which an earlier event in the same frame may already have set.
            var hoverRepaint = false;

            // An OPEN menu tracks the pointer, so every motion event is a repaint while one is up.
            // Unconditional rather than keyed on the hovered row: the row is resolved during paint (the
            // widget owns the geometry it drew), so there is nothing here to compare against, and a
            // menu is up for a moment while the pointer travels a short distance. Without this the
            // highlight only moved when something unrelated forced a frame, which is precisely how the
            // context menu came out with no hover state at all.
            if (state.ToolbarDropdown.IsOpen)
            {
                state.NeedsRedraw = true;
                hoverRepaint = true;
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
                hoverRepaint = true;
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
                hoverRepaint = true;
            }

            // File-list drag-to-scroll / thumb drag in progress (returns false when its gesture is idle,
            // so ordinary moves fall through to the branches below). Gated with the press above: a
            // collapsed list must not steer a drag that began on the picture either.
            if (HandleFileListScroll(state, evt))
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
            // The pane and the placement, both from the single layout pass.
            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, CurrentViewportLayout(state));
            if (state.CursorImagePosition == prevPos)
            {
                return false;
            }

            // The most frequent redraw in the whole app, and the cheapest to bound: a readout is
            // shown in exactly two places, so nothing else needs repainting. The info panel is not
            // optional here even though it looks secondary -- it lists the per-channel pixel values,
            // so omitting it would leave them frozen at whatever the pointer last touched while the
            // status bar beside them kept updating.
            //
            // ONLY when the readout is the one thing this move changed. The hover branches above ask
            // for a repaint of chrome that is neither of these two rects -- a toolbar tooltip, a
            // file-list row, an open menu -- and then fall through to here, so narrowing over the top
            // of one drops it: the tooltip's own pixels are left behind, and because damage is tracked
            // per swapchain image they are left behind in SOME images and not others, which reads as a
            // flicker rather than as a stale tooltip. The wrapper in ImageRendererBase.Damage.cs cannot
            // catch this; it guards a non-declaring event in a DIFFERENT dispatch, and both changes
            // arrive inside this one handler. They coincide exactly when the pointer crosses out of the
            // image pane, so the cost is one full frame per crossing and none at all while it travels
            // across the image -- which is the case the whole mechanism was measured for.
            if (!hoverRepaint)
            {
                RequestDamage(_layout.StatusBar);
                if (state.ShowInfoPanel)
                {
                    RequestDamage(_layout.InfoPanel);
                }
            }

            return true;
        }

        private bool HandleViewerMouseUp(InputEvent evt)
        {
            // Taken and cleared FIRST, so an armed tap cannot outlive its own release. Every path
            // below either uses it or drops it: a press whose release is claimed by something else
            // (the palette grip, just below) would otherwise leave it set for a LATER release
            // somewhere else to act on, and this is the one release path both hosts share.
            var press = _pressToSelect;
            _pressToSelect = null;

            // Ends a palette grip drag, and goes no further when it does -- the same reason the map's
            // own release path stops there: a release consumed by the panel must not also read as a
            // click on what is behind it.
            if (PaintedSkyBackdrop is { } sky && sky.HandleInput(evt))
            {
                return true;
            }

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

                // A LEFT tap on the picture that never became a drag asks what is there. Gated on
                // _pressToSelect, which is set ONLY by an unclaimed press inside the image viewport --
                // so a release ending a slider drag, a transport scrub or a file-list gesture cannot
                // reach this, without any of them being named here.
                if (evt is InputEvent.MouseUp { Button: MouseButton.Left } up
                    && press is { } from
                    && MathF.Abs(up.X - from.X) <= TapSlopPx
                    && MathF.Abs(up.Y - from.Y) <= TapSlopPx
                    && TrySelectObjectAt(state, up.X, up.Y))
                {
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
