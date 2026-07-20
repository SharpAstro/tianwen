# Render batching + shaped-text caching (web AND desktop)

**Status: SHIPPED (2026-07-20) -- the draw-call batching. The shaped-text cache is deferred (see
below).** Trigger: enabling the sky-atlas object overlay ([O]) and zooming out collapses the frame
rate **in the browser**. Investigation showed the same two architectural inefficiencies exist in the
desktop Vulkan backend -- native margins just hide them (for now).

**Key realization while implementing:** the polyline draw-call batching needs **no new DIR.Lib API**.
`DrawPolyline` / `DrawPolylineDashed` already exist as `virtual` on the base `Renderer<TSurface>` (their
doc comments literally say *"GPU renderers should override with a batched rotated-quad implementation"*),
and neither GPU backend had overridden them. So P1 (a DIR.Lib `WritePolylineQuads` helper) was dropped
as unnecessary, and the release chain collapsed to just the two GPU-backend bumps + a tianwen repin --
**no DIR.Lib 6.15, no Console.Lib rebuild**. The `ShapedRunCache` half of the original P1 is separately
deferred: it is *caching*, not *batching*, and for the default `AdvanceShaper` (one rune per glyph, no
GSUB/GPOS) the win is marginal because per-glyph metrics stay renderer-side regardless; the dominant
"labels + zoom-out" cost was unambiguously the ~10k unbatched ellipse draws, which the batching fixes.
Revisit `ShapedRunCache` only if profiling shows shaping is still hot (it becomes worthwhile if
`ShapingTextShaper` -- HarfBuzz-style, RTL/ligatures -- ever becomes the default).

## Problem, with evidence

Two independent costs, both scaling with visible object count, both per frame:

1. **Unbatched line segments.** `DIR.Lib.Renderer.DrawPolyline` is a base-class loop of
   `DrawLine` per segment, and its own doc comment says *"GPU renderers should override with a
   batched rotated-quad implementation"* -- **neither GPU backend ever did**:
   - `WebGl.Renderer`: every `DrawLine` = one `Opcode.Draw` -> one `gl.drawArrays` (+ attrib
     rebind) in the JS replay. The web overlay traces every extended-object ellipse as 33
     `DrawLine` calls (`SkyMapTab.ObjectOverlay.cs DrawOverlayEllipse`): at wide FOV
     (mag <= 8 whole-sphere gather, ~200-400 ellipses) that is **~10k gl draws/frame** --
     ~5-10x past a browser's comfortable budget. This is the reported stall.
   - `SdlVulkan.Renderer`: every `DrawLine` = `DrawTriangles` = **one `vkCmdPushConstants` +
     `vkCmdBindVertexBuffers` + `vkCmdDraw`**. ~10-50x cheaper per call than web (no browser
     boundary), so no user-visible stall today -- but chart-invalidation frames, the selected
     sky path, and any future polyline-heavy view pay thousands of avoidable command
     recordings. Same bug, wider margin.
   - Counter-proof that the GPU is not the limit: the same web canvas renders the full ~2.5M-star
     Tycho-2 field in ONE instanced draw at full frame rate.

2. **Per-frame text shaping.** Both backends' `DrawText`/`MeasureText` run `TextShaper.Shape` +
   per-glyph atlas lookups on EVERY call, no shaped-run reuse:
   - Web: overlay label pass = `MeasureText` per considered item (placement) + `DrawText` per
     label per frame, each a full shape -- under WASM this is the second-largest overlay cost.
   - Desktop: `VkSkyMapTab` caches label *placement* (PlaceLabels runs only on cache miss) but
     still re-shapes every label line every frame, **plus an extra `MeasureText` per line inside
     the draw loop** (`VkSkyMapTab.cs` ~552), plus every other UI label app-wide
     (toolbar/panels/status) -- shaping is the standing per-frame text tax on all backends.

Secondary web-only finding (same family): the browser planner uses the base (uncached)
`RenderChart`, so `AltitudeChartRenderer` re-renders every frame with per-sample `DrawLine`
curves and a **1-px-wide `FillRect` per pixel column** for pinned-window fills -- hundreds of
draws per pinned target per frame. Desktop is immune (chart cached as a GPU texture).
`FillRectangles` (batched) is ALREADY overridden on WebGl -- the fill just doesn't use it.

## Why the DESKTOP Debug build crawls (the multiplier stack)

The reported "Debug build is horribly slow, unknown why" is these draw counts times three
Debug-only multipliers:

1. **`VK_LAYER_KHRONOS_validation` was ALWAYS ON in Debug** -- `VulkanValidation.Enabled =
   CompiledDebug || SDLVK_VALIDATION=1`, with **no opt-out**. The layer CPU-validates every
   single `vkCmd*` call, so its cost is proportional to exactly the call count this plan
   reduces: thousands of per-segment/per-label draw+push-constants+rebind triples per frame,
   each validated. Release is fast partly because validation vanishes.
   **FIXED (2026-07-20, SdlVulkan.Renderer 6.26):** flipped to opt-IN --
   `CompiledDebug && SDLVK_VALIDATION=1` -- a plain Debug run is fast by default, validation is
   a deliberate diagnostic (the `run-gui` skill documents the env var), and a Release/AOT build
   can never enable it. Batching (P2/P3) remains the structural fix for the validated-call
   count when validation IS on.
2. **Debug JIT**: no optimizations for our assemblies -- the per-frame math (shaping,
   projection, per-candidate trig) runs several times slower.
3. **DebugInspector side-costs**: `LayoutInspection.Enabled` captures the arranged layout tree
   per widget per frame (alloc + retention) once the inspector is attached.

## Fix plan (as shipped)

| Phase | Repo | Work | Release | Status |
|-------|------|------|---------|--------|
| P1 | DIR.Lib | `WritePolylineQuads` helper -- **dropped**: the base `DrawPolyline`/`DrawPolylineDashed` are already `virtual`, so each backend just overrides them; no shared vertex helper is needed (WebGL expands strokes in its VS, Vulkan uses position-only Flat quads -- different vertex formats, no shared CPU expansion). `ShapedRunCache` -- **deferred** (see the status note above). | (none) | N/A |
| P2 | SdlVulkan.Renderer | Override `DrawPolyline`/`DrawPolylineDashed`: expand every segment to its rotated quad into a reused scratch accumulator, record the whole run as ONE Flat-pipeline draw via the existing `DrawTriangles` path (one `vkCmdDraw`). | 6.27 | DONE |
| P3 | WebGl.Renderer | Same overrides -> concatenate every segment's 6 stroke vertices into one vertex run + one `Opcode.Draw` (no shader change; the Stroke VS already carries per-vertex P0/P1). `DrawLine` shares the new `WriteStrokeSegment` helper. | 1.10 | DONE |
| P4 | tianwen | (a) `DrawOverlayEllipse` -> build the point ring and call `Renderer.DrawPolyline` once, adaptive segment count `clamp(screenRadiusPx/2, 8, 32)`; (d) planner chart: pinned-window column fill + moon-dot dashes -> `FillRectangles` batches, altitude curves -> `DrawPolyline`. | repin | DONE |
| P4 (deferred) | tianwen | (b) cache `IsAboveHorizon` per candidate; (c) reuse Pass-1 projections for the label pass. Both are CPU micro-opts (~tens of us/frame) dwarfed by the draw-call win, and carry refactor risk in the overlay pass structure -- deferred until profiling shows they matter. | -- | deferred |

`DrawOverlayEllipse` calls `Renderer.DrawPolyline` **directly** (not via a `PixelWidgetBase` forwarder):
adding a forwarder would have forced a DIR.Lib release, and the batched override lives on the GPU
renderers anyway (the CPU `RgbaImageRenderer` keeps the per-segment base loop, so the offline overlay
render test stays valid).

Release chain (much smaller than first planned -- both libs depend only on the already-published
DIR.Lib 6.14): SdlVulkan.Renderer 6.27 + WebGl.Renderer 1.10 (in parallel) -> tianwen repin
(`Directory.Packages.props` SdlVulkan 6.25.* -> 6.27.*; `TianWen.UI.Web.csproj` WebGl 1.9.* -> 1.10.*).
Patch segment stays CI-reserved.

## Verification

- DIR.Lib: unit tests pinning `WritePolylineQuads` vertex output + `ShapedRunCache` hit/invalidation
  semantics; existing `LayoutEngineTests`/renderer tests stay green (byte-identical raster for the
  CPU `RgbaImageRenderer`, which keeps the base per-segment path).
- WebGl.Renderer: a command-buffer test hook asserting Draw-command COUNT for a 33-point polyline
  == 1 (was 32) -- the regression guard for the actual failure mode.
- tianwen: `SkyMapObjectOverlayRenderTests` (offline RgbaImage render) stays pixel-stable within
  tolerance (adaptive segments change geometry slightly); add a wide-FOV overlay render test
  asserting the candidate/label counts stay within the documented caps.
- Manual: deployed atlas at FOV 120-180 deg with [O]+[D] on -- pan/zoom stays smooth; desktop
  `frame_stats` via the sdl-ui-inspector before/after for the shaping-cache win.

## Non-goals

- WebGPU pivot: NOT justified by this -- the cost is command count + CPU shaping (identical under
  WebGPU). If/when a WebGPU backend lands (compute use-cases), it goes in the SAME repo/package as
  a second JS replayer behind the unchanged C# command stream, with WebGL2 fallback; repo renames
  to `Web.Renderer` at that point (GitHub redirects; new NuGet ID, deprecate `WebGl.Renderer`).
- Merging consecutive same-color flat draws renderer-wide (a general draw-merging layer): bigger
  refactor, revisit only if the above is insufficient.
- TUI: event-driven redraw + CPU rasterization dwarfs shaping; base per-segment path is fine there.
