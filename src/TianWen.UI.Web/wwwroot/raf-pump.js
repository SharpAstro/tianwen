// Coalesces the app's canvas repaints onto the browser's frame clock.
//
// The web host has no render loop: every input event ends in a synchronous full repaint
// (Planner.RenderFrame). For one-shot events (a click, a key, a chip switch) that is exactly right --
// painting immediately is the lowest latency there is. For a CONTINUOUS gesture it is not: a drag or a
// trackpad pinch delivers several events between two vsyncs, and each one paints a frame that the next
// one overwrites before the compositor ever sees it. Measured in a browser trace of a touch session:
// 1096 of 1535 move-driven repaints (71%) were superseded within their own 16.67 ms window, 275 windows
// carrying four repaints each.
//
// So the continuous-gesture handlers mark the frame dirty instead, and this schedules ONE repaint per
// animation frame. requestAnimationFrame is the right clock rather than a timer: it fires once per
// compositor frame, is throttled with the tab, and is skipped entirely on a hidden tab -- so a
// background tab holding a latched gesture stops burning CPU for free.
//
// The pending flag lives on the .NET side (Planner._rafPending) rather than here, so a gesture that is
// already dirty costs NOTHING at all -- no interop crossing, no call into this module. That makes the
// steady-state cost of a gesture exactly two crossings per PAINTED frame (this schedule, plus the
// callback below), regardless of how many events arrive.
//
// This lives in the app, not in WebGl.Renderer, because WHEN to paint is the app's policy: the canvas
// component owns input and the GL surface, but it has no view on whether a given repaint is worth a
// frame. Worth upstreaming if a second consumer wants the same pump.

/**
 * Requests a single repaint on the next animation frame.
 * @param {object} ref DotNetObjectReference to the component exposing RenderFrameFromRaf.
 */
export function schedule(ref) {
  requestAnimationFrame(() => {
    // Fire-and-forget: the callback clears the component's pending flag, so a throw here (teardown
    // race - the component disposed between the schedule and the frame) must not reject unhandled.
    // Swallowing also cannot wedge the pump, because the flag is cleared on the .NET side FIRST.
    ref.invokeMethodAsync("RenderFrameFromRaf").catch(() => { /* component gone */ });
  });
}
