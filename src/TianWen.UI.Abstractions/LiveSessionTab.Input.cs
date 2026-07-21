using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Input handling: mouse (viewer pan/zoom, clickable regions) and keyboard routing for all modes.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        /// <inheritdoc/>
        public override bool HandleInput(InputEvent evt)
        {
            if (State is not { } state)
            {
                return false;
            }

            // Planetary mode: the planetary view does its own position-aware hit dispatch (toolbar / sliders /
            // Start-Stop), so forward raw MOUSE events straight to it. Keys are NOT forwarded so global
            // shortcuts (Esc, mode switching) stay free.
            if (state.Mode == LiveSessionMode.Planetary && PlanetaryView is { } planetaryView
                && evt is InputEvent.MouseDown or InputEvent.MouseMove or InputEvent.MouseUp or InputEvent.Scroll)
            {
                return planetaryView.HandleInput(evt);
            }

            switch (evt)
            {
                // A session prompt is modal-ish: Enter = Continue, Escape = Cancel. Handled first so it
                // wins over abort-confirm / mode shortcuts while open. Mouse clicks reach the [Continue] /
                // [Cancel] buttons via their registered clickable regions.
                case InputEvent.KeyDown(InputKey.Enter, _) when state.PendingPrompt is not null:
                    PostSignal(new RespondSessionPromptSignal(true));
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.PendingPrompt is not null:
                    PostSignal(new RespondSessionPromptSignal(false));
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.ShowAbortConfirm:
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Enter, _) when state.ShowAbortConfirm:
                    PostSignal(new ConfirmAbortSessionSignal());
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.IsRunning:
                    state.ShowAbortConfirm = true;
                    state.NeedsRedraw = true;
                    return true;

                // Polar-align fake-mount jog: arrow keys nudge simulated (az, alt)
                // misalignment by 1' (5' with Shift). Az on Left/Right, Alt on
                // Up/Down. Only active in PolarAlign mode so the keys are free
                // for other purposes elsewhere. The signal is a no-op when the
                // connected mount isn't a FakeSkywatcherMountDriver, so this
                // is safe to leave wired up unconditionally.
                case InputEvent.KeyDown(InputKey.Left, var ml) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (ml & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(-step, 0));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Right, var mr) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (mr & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(+step, 0));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Up, var mu) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (mu & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(0, +step));
                    return true;
                }
                case InputEvent.KeyDown(InputKey.Down, var md) when state.Mode == LiveSessionMode.PolarAlign:
                {
                    var step = (md & InputModifier.Shift) != 0 ? 5.0 : 1.0;
                    PostSignal(new NudgeFakeMountMisalignmentSignal(0, -step));
                    return true;
                }

                case InputEvent.Scroll(var scrollY, var mx, var my, _) when PreviewView is not null && !_previewState.ZoomToFit:
                {
                    // Cursor-anchored zoom via the shared controller (was a copy of the viewer's
                    // formula): seed the transform from the preview state, zoom, write back.
                    var vs = _previewState;
                    _previewPanZoom.Zoom = vs.Zoom;
                    _previewPanZoom.PanOffset = new Vector2(vs.PanOffset.X, vs.PanOffset.Y);
                    if (_previewPanZoom.ZoomAtCursor(scrollY, mx, my, _viewerImageRect))
                    {
                        vs.Zoom = _previewPanZoom.Zoom;
                        vs.PanOffset = (_previewPanZoom.PanOffset.X, _previewPanZoom.PanOffset.Y);
                        state.NeedsRedraw = true;
                    }
                    return true;
                }

                case InputEvent.Scroll(_, _, _, _):
                    // Exposure-log tail-follow scroll, viewport-gated by the controller (scrolling
                    // elsewhere on the tab falls through instead of moving the log).
                    if (_logScroll.HandleInput(evt))
                    {
                        state.NeedsRedraw = true;
                        return true;
                    }
                    return false;

                case InputEvent.MouseDown(var mx, var my, _, _, _):
                    // Exposure-log body drag-to-scroll, tried first (the controller viewport-gates the
                    // press) so a press on the log never grabs the preview pan while zoomed.
                    if (_logScroll.HandleInput(evt))
                    {
                        state.NeedsRedraw = true;
                        return true;
                    }
                    // Preview viewer mouse drag for panning
                    if (PreviewView is not null && !_previewState.ZoomToFit)
                    {
                        _previewPanZoom.PanOffset = new Vector2(_previewState.PanOffset.X, _previewState.PanOffset.Y);
                        _previewPanZoom.BeginPan(mx, my);
                        return true;
                    }
                    return false;

                case InputEvent.MouseMove(var mx, var my):
                    if (_logScroll.HandleInput(evt)) // false when its gesture is idle
                    {
                        state.NeedsRedraw = true;
                        return true;
                    }
                    if (_previewPanZoom.UpdatePan(mx, my))
                    {
                        _previewState.PanOffset = (_previewPanZoom.PanOffset.X, _previewPanZoom.PanOffset.Y);
                        state.NeedsRedraw = true;
                        return true;
                    }
                    return false;

                case InputEvent.MouseUp(_, _, _):
                    if (_logScroll.HandleInput(evt))
                    {
                        _logScroll.TakeAtomTap(); // log rows have no tap action -- discard
                        state.NeedsRedraw = true;
                        return true;
                    }
                    if (_previewPanZoom.IsPanning)
                    {
                        _previewPanZoom.EndPan();
                        return true;
                    }
                    return false;

                // Preview viewer keyboard shortcuts
                case InputEvent.KeyDown(InputKey.F, _) when PreviewView is not null:
                    _previewState.ZoomToFit = true;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.R, _) when PreviewView is not null:
                    _previewState.ZoomToFit = false;
                    _previewState.Zoom = 1f;
                    _previewState.PanOffset = (0, 0);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.T, _) when PreviewView is not null:
                    CyclePreviewStretch(_previewState);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.B, _) when PreviewView is not null:
                    ViewerActions.CycleCurvesBoost(_previewState);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.S, _) when PreviewView is not null:
                    ViewerActions.CycleStretchPreset(_previewState);
                    state.NeedsRedraw = true;
                    return true;

                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Top strip: phase pill + activity + progress + clock
        // -----------------------------------------------------------------------
    }
}
