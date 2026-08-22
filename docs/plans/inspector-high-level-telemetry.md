# The inspector protocol is low-level: add the high-level story

**Status: PLANNED (raised by the user 2026-08-22).** Three notes, one theme:

- *"I was thinking if the AppShell could also participate in the MCP debugger somehow (like
  Console.Lib has its own debugger, I believe based on the same protocol), where we could get general
  process info like RAM."*
- *"but we can also just factor all the env details into the SDL debugger"* -- the decision, and the
  cheaper shape.
- *"just saying noticed the protocol is very low level, doesn't say much what is going on high level,
  like how many resident frames in the cache etc"* -- the actual gap.

## What is wrong today

The inspector answers **what the frame looks like** in exquisite detail (every arranged node, every
clickable region, the resolved pen of every terminal cell) and answers **almost nothing about what
the process is doing**. Ask it why the app feels heavy and it can tell you the rect of a button.

Concretely, none of these is reachable, and every one of them has been wanted during a real
investigation this month:

- **Process cost**: working set, managed heap, GC counts by generation, thread count, uptime. The
  viewer-memory-footprint work measured a 2.5 GB document by hand precisely because nothing reports
  it, and [[feedback_working_set_cannot_measure_this]] exists because working set alone is too noisy
  to conclude from -- so the useful answer is *both* numbers plus allocation counters, not one.
- **What is resident**: how many frames are in the SER / preview cache, how many textures the
  renderer holds and their total bytes, the staging buffer's high-water mark (which
  `viewer-memory-footprint.md` M1 exists to trim and which is invisible while it happens), how many
  documents are open, whether the float planes were dropped for an 8-bit document (D1').
- **What the frame loop is doing**: frames painted versus frames skipped, how many were PARTIAL and
  the damage area as a fraction of the surface. The TUI already logs exactly this shape once a second
  (`TUI paint: N frames, M cells (K opaque)`) and it is the reason a repaint regression there is
  findable; the GPU side has `[rdiag] frame.slow` and nothing cumulative.
- **Environment**: GPU name/type/driver, Vulkan API version, validation-layer availability, DPI
  scale, the resolved font faces, the app version. Most of this is already computed at startup and
  logged once -- event 501 names the GPU -- but a running process cannot be *asked*.

## The decision: the SDL debugger, not a separate AppShell participant

The user's first instinct was to have SharpAstro.AppShell join the inspector transport the way
Console.Lib does. The second note retracts it in favour of folding the environment details into the
SDL debugger, and that is the right call for a reason worth writing down:

**AppShell has no process to report on.** `DebugInspectorCore` (DIR.Lib.Diagnostics) is a transport
plus a command loop; a participant is a *host* that owns a window, a render thread and a lifetime.
Console.Lib qualifies because it owns the terminal; SdlVulkan.Renderer qualifies because it owns the
swapchain. AppShell owns neither -- it is shell plumbing (`InstanceGate`, activation), and its facts
(which folder this instance claims, whether it is the empty primary) are worth exposing but they are
**one section of a host's report, not a second endpoint to discover and connect to**. Two endpoints
per process would also mean two discovery answers for one pid, which the sidecar's
`list_instances` has no way to present.

So: **one command per host, `env` / `stats`, served by the existing inspector**, with the generic half
living where the generic knowledge is.

## Shape

- **`DIR.Lib.Diagnostics` owns the process half.** Working set, GC/heap, threads, uptime, framework
  version -- nothing in that list is SDL-specific or terminal-specific, so both hosts get it free and
  the TUI's `appState` gains the same section. This is the same argument that put `CursorKind` and
  `IActivatableWindow` where they are: name the meaning centrally, let each host add what only it
  knows.
- **SdlVulkan.Renderer adds the GPU + frame-loop half**: device/driver/API, validation availability,
  swapchain image count and format, frames painted / skipped / partial, damage area as a fraction,
  texture count and bytes, staging-buffer high water.
- **The app adds its domain half** through a callback, exactly like `AppState` today: resident cached
  frames, open documents, the document's own footprint, and for TianWen the session/rig facts already
  reported.
- **Hand-written JSON via `Utf8JsonWriter`**, never a reflective overload -- the AOT rule the TUI's
  `appState` already follows.

## Two things to get right

1. **A stats read must not perturb what it measures.** No forced `GC.Collect` behind an inspector
   command (that would make every reading a post-collection one and hide exactly the growth being
   hunted), and no allocation-heavy serialisation on the render thread -- the inspector runs its
   commands there, which is what makes `render_liveness` meaningful and also what makes a chatty
   command a frame stall.
2. **Counters are cumulative-with-interval, not instantaneous.** The TUI paint line got this right by
   accident and it is the reason it is usable: totals diffed across a one-second interval. A
   per-frame instantaneous read of "was this frame partial" answers about one frame and tells you
   nothing about the steady state, which is the question being asked.

## Related

- [../architecture/unattended-ui-driving.md](../architecture/unattended-ui-driving.md) -- the six
  surfaces the inspector has today, and the rules for reading them.
- [damage-based-repaint.md](damage-based-repaint.md) -- where the frame-loop counters come from, and
  which would have made the residue in P15 of
  [viewer-prerelease-fixes.md](viewer-prerelease-fixes.md) diagnosable from the app instead of by
  eye.
- [viewer-memory-footprint.md](viewer-memory-footprint.md) -- the measurements this would have
  reported directly.
