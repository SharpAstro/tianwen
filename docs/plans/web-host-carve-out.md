# The web host has no host layer, so a Razor page is one

**Status: PLANNED.** No code written. This is the design for giving the browser build the same shape
the desktop and terminal builds already have, so that `Pages/Planner.razor` stops being the third
implementation of things TianWen only wants one of.

## The measurement

`src/TianWen.UI.Web/Pages/Planner.razor` is **2,121 lines — 65% of the entire web project** (3,256
lines across every `.cs` and `.razor` in it). That is not a page. It is an application host that
happens to be written in Razor.

What is inside it, by reference count:

| what | hits | belongs to |
|---|---:|---|
| key handling / shortcuts | 86 | `GuiEventHandlerBase` (shared) |
| text input + focus | 53 | `TextInputInteraction` / `TextInputFocus` (DIR.Lib) |
| render / redraw scheduling | 52 | a web host layer |
| JS interop (`InvokeVoidAsync`, `[JSInvokable]`) | 23 | a web host layer |
| composition + hit dispatch | 13 | `CompositeWidget` (DIR.Lib, now exists) |
| pointer / wheel / pinch | 14 | a web host layer |

## Why this happened, and it is not the page's fault

Compare what each surface's renderer package actually ships:

| | rendering | event loop | app shell | window / view | input mapping | inspector |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| `SdlVulkan.Renderer` | `VkRenderer` | `SdlEventLoop` | `SdlVulkanApp` | `SdlVulkanWindow` + `SdlWindowView` | `SdlInputMapping` | `DebugInspector` |
| `Console.Lib` | `CellLayout` | its own loop | `TuiSubCommand` shape | `ITerminalViewport` | byte table | `ConsoleDebugInspector` |
| `WebGl.Renderer` | `WebGlRenderer` | **—** | **—** | `WebGlCanvas` (canvas only) | **—** | **—** |

`WebGl.Renderer` provides the renderer and the canvas plumbing and stops there. Every one of the four
missing columns has to exist for an app to run, so `Planner.razor` supplies them — inline, once, for
one app. The desktop host's `Program.cs` is small precisely because `SdlVulkanApp` and `SdlEventLoop`
are somebody else's problem.

**So the fix is not "move code into Abstractions".** Most of what is stuck in the page is
*browser-specific* — rAF scheduling, `[JSInvokable]` callbacks, pointer/touch bridging, the text
overlay. That belongs in `WebGl.Renderer`, beside the canvas it already owns. Only the
*surface-neutral* remainder belongs in `TianWen.UI.Abstractions`, and much of it is already there and
simply not being called.

## The evidence that it is diverging, not merely duplicated

The page reuses exactly nine shared types (`PlannerActions` ×8, `TextInputInteraction` ×7,
`TextInputState` ×4, `TextInputFocus` ×3, `SkyMapTab`, `SkyMapState`, `SearchInteraction`,
`PlannerTab`, `PlannerState`) and **zero** references to `GuiEventHandlerBase`, `IGuiChrome` or
`GuiAppState`. `IGuiChrome` is implemented by exactly one class in the repo — `VkGuiRenderer`.

That is the whole problem in one line: the shared input router exists, the desktop uses it, and the
web host hand-rolls its own. Every rule `GuiEventHandlerBase` owns — the link-hit route, the
click-outside-to-blur rule, double-click select-all, the self-dispatching-widget carve-out — is either
re-implemented in the page, or absent from it, and nothing says which.

This family has a track record on hand-maintained mirrors, and it is the argument for doing this at
all rather than a hypothetical:

- The sky-map overlay cache key existed twice and **both** copies carried the same quantization bug.
- The GUI chrome stated its child list **five** times and had drifted into three orderings plus an
  omission (fixed by `CompositeWidget`; see [`tab-strip-unification.md`](tab-strip-unification.md)).
- `SdlVulkan.Renderer` silently turned an override into a hide when DIR.Lib grew a method of the same
  shape; `WebGl.Renderer` hit the same class one release later.

## What already landed that makes this tractable

`CompositeWidget<TSurface>` (DIR.Lib 8.3) is the missing seam. A host declares the widgets it paints,
in paint order, and inherits hit-testing, dispatch, cursor, Tab cycling and region enumeration. The
web host currently hand-rolls all five across ~6 sites in the page; after this it declares children.

## Phasing

Each phase leaves the deployed site working, and none requires the next.

| # | Phase | Where | Notes |
|---|---|---|---|
| W1 | `WebGlApp` + a frame loop | `WebGl.Renderer` | Owns rAF scheduling and the coalescing rule (see below). The page keeps calling `RenderFrame`; it just stops owning *when*. |
| W2 | `WebGlInputMapping` + pointer/wheel/pinch/key bridging | `WebGl.Renderer` | The `[JSInvokable]` surface and the touch bridge move behind it, emitting DIR.Lib `InputEvent`s. |
| W3 | Web chrome becomes a `CompositeWidget` | `TianWen.UI.Web` | Declares children; deletes the hand-rolled dispatch/region walks. |
| W4 | Adopt `GuiEventHandlerBase` | `TianWen.UI.Web` | The 86 key references collapse to the shared router. **The largest correctness win and the one to measure.** |
| W5 | `IGuiChrome` gets a second implementer | `TianWen.UI.Abstractions` | Whatever the web host cannot satisfy is the interface's problem, not the host's — expect it to shrink. |
| W6 | A web `DebugInspector` | `WebGl.Renderer` | Optional. Would let the Playwright suite drive by region label instead of DOM probing. |

W1–W2 are the carve-out proper. W3–W5 are the payoff. W6 is separable and may never happen.

## Three things to get right

**The coalescing rule must survive the move.** The browser build has no render loop: every input
handler ends in a synchronous full repaint, which is correct for a one-shot event and waste for a
continuous gesture — measured at **1096 of 1535 move-driven repaints (71%) superseded inside their own
16.67 ms window**. `RequestRenderCoalesced` is the fix and its details are load-bearing (the pending
flag lives on the .NET side; clear it *before* painting and on the schedule-failure path, or the canvas
freezes). W1 moves that mechanism into the host layer; it must not become "the host paints when it
feels like it".

**A trackpad pinch is `ctrl`+`wheel`, a different path from the touchscreen pinch.** Blazor's
`@onwheel` versus the canvas touch bridge. Anything done to one is not automatically done to the other,
which is exactly how the densest gesture the app sees got missed the first time.

**Do not let W4 quietly change behaviour.** The page's key handling and `GuiEventHandlerBase` will
disagree somewhere, and the disagreements are the *point* — each one is either a web bug or a rule the
desktop has that the web lacks. They should be enumerated and decided, not merged and hoped over. The
Playwright suite is the check, and it asserts DOM, not pixels.

## What this does not fix

`WebGlSkyMapPipeline.cs` (884 lines) is genuinely browser-specific rendering and stays where it is.
The page will still exist and still be a page; the target is that it reads like `TianWen.UI.Gui`'s
`Program.cs` — wiring, not implementation.

## Related

- [`tab-strip-unification.md`](tab-strip-unification.md) — where `CompositeWidget` came from, and the
  same "one description, N surfaces" argument one level down.
- [`controls-upstreaming.md`](controls-upstreaming.md) — the U-plan that moved sliders, text inputs and
  the key router into DIR.Lib. This is that plan's unfinished third surface.
- [`web-showcase.md`](web-showcase.md) — what the browser build is for.
