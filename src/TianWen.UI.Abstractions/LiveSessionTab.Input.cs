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

            // Planetary mode: the router has already dispatched a press on any of the view's controls (the view is
            // one of this tab's Children), so what reaches here is what no region claimed: a pan, the PiP drag, a
            // wheel zoom. Forward those MOUSE events to the view. Keys reach it only at the end of the switch below,
            // after this tab's own and never the window's, so global shortcuts (Esc, mode switching) stay free.
            if (state.Mode == LiveSessionMode.Planetary && PlanetaryView is { } planetaryView
                && evt is InputEvent.MouseDown or InputEvent.MouseMove or InputEvent.MouseUp or InputEvent.Scroll)
            {
                return planetaryView.Widget.HandleInput(evt);
            }

            switch (evt)
            {
                // The quit's question is the app's and above everything, a run's prompt included: Enter takes the
                // default, its letter the other, Escape stays.
                case InputEvent.KeyDown(InputKey.Enter, _) when state.QuitDialog is { } quit:
                    PostSignal(new AnswerQuitSignal(quit.Default));
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.QuitDialog is not null:
                    PostSignal(new AnswerQuitSignal(null));
                    return true;

                case InputEvent.KeyDown(var key, _) when state.QuitDialog is { } quit && key == quit.OtherKey:
                    PostSignal(new AnswerQuitSignal(quit.Other));
                    return true;

                // A request for control of this computer's rig, under the quit's question: Enter declines (the default,
                // so an Enter meant for something else hands the rig to nobody), A allows, Escape answers it later.
                case InputEvent.KeyDown(InputKey.Enter, _) when state.QuitDialog is null && state.ControlRequest is { } request:
                    PostSignal(new AnswerControlRequestSignal(request.Id, Allow: false));
                    return true;

                case InputEvent.KeyDown(ControlRequestQuestion.AllowKey, _) when state.QuitDialog is null && state.ControlRequest is { } request:
                    PostSignal(new AnswerControlRequestSignal(request.Id, Allow: true));
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.QuitDialog is null && state.ControlRequest is not null:
                    PostSignal(new DismissControlRequestSignal());
                    return true;

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

                // The wheel over the preview is the viewer's (its zoom at the pointer, as in tianwen-fits); anywhere
                // else on the tab it scrolls the exposure log, below.
                case InputEvent.Scroll(_, var wx, var wy, _) when PreviewOnScreen() is { } wheelViewer && _viewerImageRect.Contains(wx, wy):
                    return ToViewer(state, wheelViewer, evt);

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
                    // A press on the preview that no region claimed (the router has already run its toolbar's
                    // buttons) is the viewer's: a pan, a tap that selects an object. The tab used to pan it with
                    // a copy of the viewer's own pan and zoom.
                    if (PreviewOnScreen() is { } pressViewer && _viewerImageRect.Contains(mx, my))
                    {
                        return ToViewer(state, pressViewer, evt);
                    }
                    return false;

                case InputEvent.MouseMove:
                    if (_logScroll.HandleInput(evt)) // false when its gesture is idle
                    {
                        state.NeedsRedraw = true;
                        return true;
                    }
                    // Every move goes to the viewer, inside its rect or not: a pan it began follows the pointer
                    // anywhere, and its readout clears once the pointer leaves the picture.
                    return PreviewOnScreen() is { } moveViewer && ToViewer(state, moveViewer, evt);

                case InputEvent.MouseUp(_, _, _):
                    if (_logScroll.HandleInput(evt))
                    {
                        _logScroll.TakeAtomTap(); // log rows have no tap action -- discard
                        state.NeedsRedraw = true;
                        return true;
                    }
                    return PreviewOnScreen() is { } releaseViewer && ToViewer(state, releaseViewer, evt);

                // The viewer's keys, for the viewer ON SCREEN: the planetary view in Planetary mode, else the
                // preview. They are the keys its tooltips name, the same as tianwen-fits' (the user's call,
                // 2026-10-01; the preview's own T, S and B went with the toolbar it drew), and the viewer acts on
                // one only where its host offers that button (ToolbarOffer). They used to act on the preview's
                // state whatever the mode, so in Planetary mode F and R zoomed a viewer nobody could see (found in
                // the ZWO live check, 2026-09-28). The WINDOW's keys stay out of it: through the viewer, Escape
                // would quit, Tab cycle a field, Space and the arrows step a file list, F11 go full screen.
                case InputEvent.KeyDown(var key, _) when !IsWindowKey(key) && OnScreenViewer() is { } keyViewer:
                    return ToViewer(state, keyViewer, evt);

                default:
                    return false;
            }
        }

        /// <summary>
        /// The viewer on screen: the one this tab painted last frame, the planetary view in Planetary mode, else the
        /// preview where it had room. Read off what was PAINTED, never off the mode alone, so a mode that shows no
        /// viewer (the flats) hands its keys to none.
        /// </summary>
        private PixelWidgetBase<TSurface>? OnScreenViewer() => _children.Count > 0 ? _children[0] : null;

        /// <summary>The preview viewer, when it is the one on screen.</summary>
        private ImageRendererBase<TSurface>? PreviewOnScreen()
            => PreviewView is { } preview && ReferenceEquals(OnScreenViewer(), preview) ? preview : null;

        private static bool IsWindowKey(InputKey key) => key is InputKey.Escape or InputKey.Tab or InputKey.Space
            or InputKey.Up or InputKey.Down or InputKey.Left or InputKey.Right or InputKey.Home or InputKey.End
            or InputKey.Enter or InputKey.F11;

        /// <summary>Hands <paramref name="evt"/> to <paramref name="viewer"/>, and draws a frame when it took it.</summary>
        private static bool ToViewer(LiveSessionState state, PixelWidgetBase<TSurface> viewer, InputEvent evt)
        {
            if (!viewer.HandleInput(evt))
            {
                return false;
            }
            state.NeedsRedraw = true;
            return true;
        }

        // -----------------------------------------------------------------------
        // Top strip: phase pill + activity + progress + clock
        // -----------------------------------------------------------------------
    }
}
