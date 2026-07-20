# Render batching + shaped-text caching (web AND desktop)

**Status: PLANNED (2026-07-20).** Trigger: enabling the sky-atlas object overlay ([O]) and zooming
out collapses the frame rate **in the browser**. Investigation showed the same two architectural
inefficiencies exist in the desktop Vulkan backend -- native margins just hide them (for now).

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

1. **`VK_LAYER_KHRONOS_validation` is ALWAYS ON in Debug** -- `VulkanValidation.Enabled =
   CompiledDebug || SDLVK_VALIDATION=1` (`VulkanValidation.cs`), with **no opt-out**. The layer
   CPU-validates every single `vkCmd*` call, so its cost is proportional to exactly the call
   count this plan reduces: thousands of per-segment/per-label draw+push-constants+rebind
   triples per frame, each validated. Release is fast partly because validation vanishes.
2. **Debug JIT**: no optimizations for our assemblies -- the per-frame math (shaping,
   projection, per-candidate trig) runs several times slower.
3. **DebugInspector side-costs**: `LayoutInspection.Enabled` captures the arranged layout tree
   per widget per frame (alloc + retention) once the inspector is attached.

Action item folded into P2 (SdlVulkan.Renderer 6.26): an explicit env opt-OUT --
`SDLVK_VALIDATION=0` disables the layer even in a Debug build -- so a dev can A/B the
validation cost in seconds and run a fast Debug session when not chasing GPU bugs. (Batching
itself shrinks the validated call count, which is the structural fix.)

## Fix plan (shared machinery in DIR.Lib -- one path, all backends benefit)

| Phase | Repo | Work | Release |
|-------|------|------|---------|
| P1 | DIR.Lib | (a) `Renderer.WritePolylineQuads` helper: polyline -> rotated-quad vertex span (the base math, extracted so overrides don't re-derive it); (b) `ShapedRunCache`: LRU (~512) keyed (fontFamily, fontSize, text) caching the `TextShaper.Shape` output + line metrics (ascent/descent/visual width) so `MeasureText` becomes a lookup and `DrawText` skips shaping. Cache stores SHAPING results only, never resolved atlas UVs (atlas pages can grow/evict); glyph resolution stays per frame (cheap dictionary hits). Invalidate on `TextShaper` swap. Span-keyed lookup via `Dictionary.GetAlternateLookup<ReadOnlySpan<char>>`. | 6.15 |
| P2 | SdlVulkan.Renderer | Override `DrawPolyline`/`DrawPolylineDashed`: all segment quads -> ONE `DrawTriangles` call (one vkCmdDraw). Adopt `ShapedRunCache` in `DrawText`/`MeasureText`. Add `SDLVK_VALIDATION=0` opt-out (explicit 0/false disables the validation layer even in Debug -- today Debug has no off switch, see the Debug-multiplier section). | 6.26 |
| P3 | WebGl.Renderer | Same overrides -> one stroke-pipeline `Opcode.Draw` for the whole polyline. Adopt `ShapedRunCache`. | 1.10 |
| P4 | tianwen | (a) `DrawOverlayEllipse` -> build the point ring and call `DrawPolyline` once, with adaptive segment count `clamp(screenRadiusPx/2, 8, 32)`; (b) cache `IsAboveHorizon` per candidate at gather time + viewing-time bucket (it is recomputed per candidate AND per label per frame on both backends); (c) reuse Pass-1 projections for the label pass instead of `ProjectSkyMapCandidatesInto` re-projecting; (d) web planner chart: pinned-window column fill -> one `FillRectangles` batch; curve strokes -> `DrawPolyline`. | repin |

Release chain per the standing rule: DIR.Lib 6.15 -> (Console.Lib rebuild-bump, SdlVulkan.Renderer
6.26, WebGl.Renderer 1.10 in parallel) -> tianwen repin. Patch segment stays CI-reserved.

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
