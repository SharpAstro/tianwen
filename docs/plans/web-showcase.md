# Web Showcase + In-Browser Planner & Atlas

Status: IN PROGRESS (2026-07-17). P0 + P1 functionally proven end-to-end in the browser on
branch `feat/web-showcase` (planner renders, geolocation, full-viewport hi-dpi canvas,
keyboard/mouse via the real `HandleInput` path). Perf + deploy work in flight - see
"P0/P1 findings" below.

Goal: a static GitHub Pages site for TianWen with (1) a **showcase landing page** (no WASM,
fast, real screenshots) and (2) a **live in-browser app** exposing the **Planner** and the
**Sky Map ("atlas")**, rendered through the new `WebGl.Renderer` (`Renderer<WebGlContext>`, the
Blazor-WebAssembly / WebGL2 sibling of `SdlVulkan.Renderer`). Mirrors the shipped
`sebgod/chess` -> `Chess.Web` pattern.

Deploy target: `https://SharpAstro.github.io/tianwen/`.

## Why this is feasible

TianWen's GUI tabs are already generic over the render surface: every tab is
`class XxxTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>` in
`TianWen.UI.Abstractions`, with a single Vulkan concretion (`Vk*Tab : XxxTab<VulkanContext>`)
in `TianWen.UI.Gui`. `WebGl.Renderer` implements the full `Renderer<TSurface>` abstract/virtual
surface (rects, ellipses, lines/polylines + dashed, MTSDF text, clip, scrim). So a browser build
instantiates the same generic tabs with `TSurface = WebGlContext` instead of `VulkanContext`.

Catalog data is embedded in `TianWen.Lib` (loaded via `Assembly.GetManifestResourceStream`), which
works unchanged under Blazor WASM - it only adds to the download payload (see Tycho-2 gating below).

## Portability findings (per render-architecture audit)

| Surface | Verdict | Detail |
|---|---|---|
| **Planner** | Portable, low risk | `PlannerTab<TSurface>.Render`, target list, details panel are pure Layout-DSL / `Renderer<TSurface>` draws. `AltitudeChartRenderer` is a generic `static` class already proven against two surfaces (TUI `RgbaImageRenderer` + GUI `VkRenderer`). The only Vulkan bit, `VkPlannerTab.RenderChart` (caches the chart as a GPU texture via `VkRenderer.DrawTexture`), is a pure perf optimization - a `PlannerTab<WebGlContext>` that does **not** override it draws correct pixels through WebGl.Renderer's existing pipelines. |
| **Sky Map (atlas)** | Needs new code | Base `SkyMapTab<TSurface>.RenderSkyMap` draws **only a sky-colour background fill**. All stars/grid/constellation-lines/DSO-markers/Milky-Way are issued straight to Vulkan in `VkSkyMapTab` via `VkSkyMapPipeline` (instanced buffers, raw `vkCmd*`). Labels (constellation names, grid/planet/comet labels, mount reticle, info strip) ARE portable (drawn in the base `Render` via `DrawText`/`DrawLine`). The dead `SkyMapRenderer` (CPU projector, never called) is a reusable source of projection math. There is no second `TSurface` concretion today, so the seam is untested for the sky map. |
| Equipment / Session / LiveSession / Notifications | Portable | Empty `Vk*Tab` subclasses; all logic in the generic base. (Out of scope for v1 - listed for completeness. LiveSession's embedded preview is a `VkImageRenderer` / `VkFitsImagePipeline`, GPU-locked, not ported.) |
| FITS viewer / Planetary / Guider preview | Not ported | `VkImageRenderer` / `VkFitsImagePipeline` (image quad + stretch/curves/HDR shader) - image-raster GPU pipeline with no WebGl.Renderer analog. Out of scope. |

## The atlas decision (owner, 2026-07-17)

Chosen: **proper GPU pipeline, no Tycho-2 stars for now.** Build the real instanced GPU sky
pipeline for WebGL (so adding the full Tycho-2 catalog later is a data + payload change, not a
re-architecture), but source the star geometry from the **HR bright-star catalog (~9k stars,
494 KB embed)** for v1 rather than the 30 MB / ~2.5M-star `tyc2.bin.lz` firehose.

Rejected alternatives: CPU immediate-mode bright-star draw (would not carry over to Tycho-2 scale);
full Tycho-2 parity now (30 MB WASM payload + far larger up-front effort).

### What "proper pipeline" requires

`WebGl.Renderer` today has a fixed 8-int32-slot command stream, four compiled pipelines
{Flat, Ellipse, Stroke, Sdf}, `drawArrays(TRIANGLES)` only, and uniforms limited to
`uProj`/`uColor`/`uExtra` (`Opcode.cs`, `WebGlPipelines.cs`, `webgl-renderer.js`). `VkSkyMapPipeline`
needs, at minimum: per-instance star attributes (J2000 unit-vector + magnitude + B-V), a
custom UBO (112 bytes: view matrix + viewport, GPU stereographic projection in the vertex shader),
instanced draw, point-sprite/quad expansion, and a texture beyond the SDF glyph atlas (Milky Way).

None of that is exposed to consumers today. So P2 adds a **general custom-pipeline extensibility
seam to `WebGl.Renderer`** (a reusable feature, not TianWen-specific - any consumer wanting custom
GPU effects benefits):

- `RegisterPipeline(vs, fs, vertexLayout, instanceLayout?, topology, blend)` -> handle, extending
  the compiled-pipeline table past the fixed 4 (the shim's `compilePipelines` already takes arrays).
- Persistent GPU buffers: upload instance/vertex data **once** and reference it across frames
  (analogous to the atlas texture living outside the per-frame vertex stream), so a pan/zoom only
  re-uploads the UBO, not the star buffer.
- `DrawInstanced` opcode -> `gl.drawArraysInstanced`.
- Custom-uniform / UBO upload for the active custom pipeline.
- A general texture-upload path (reuse the `CreatePage`/`UploadTexSubImage` machinery) for the
  Milky Way underlay (Milky Way itself is optional for v1).

`WebGlSkyMapPipeline` (the shaders, geometry build, UBO layout) then lives in TianWen, on top of
that seam - mirroring `VkSkyMapPipeline` in `TianWen.UI.Shared` over `SdlVulkan.Renderer`'s
`VulkanContext`. Layering stays correct: general primitives in the renderer, the sky-specific
pipeline in TianWen.

**Cross-repo consequence:** P2 is a `WebGl.Renderer` sibling change. Local dev uses ProjectReference
(the `UseLocalSiblings` switch); tianwen CI consumes it as a NuGet PackageReference, so P3 cannot be
pushed until a `WebGl.Renderer` minor is published to nuget.org (the standard release-lib dance,
never a local nupkg feed).

## Infra adapters (both modest)

1. **Browser `IExternal`** - the desktop `External` bundles `System.IO` atomic-JSON writes,
   serial-port enumeration/open, and a TCP guider socket, none of which exist in the WASM sandbox.
   A `BrowserExternal` provides in-memory + `localStorage` (or IndexedDB) for the JSON persistence
   the Planner touches (`PlannerPersistence`), and **no-ops / throws** on serial + TCP members
   (Planner + Atlas never call them). Site lat/lon via manual entry (no profile-folder enumeration)
   for v1.
2. **Lightweight build gate** - `tyc2.bin.lz` (30 MB) is an `EmbeddedResource` in `TianWen.Lib` and
   would otherwise ship inside the WASM download. A `Lightweight` MSBuild property (default `false`;
   full builds keep everything) gates the `EmbeddedResource Include`; no code change is needed
   because `ReadTycho2Bulk` already no-ops when the manifest entry is absent. The gate MUST be passed
   as a GLOBAL publish property - `dotnet publish ... -p:Lightweight=true` - so it flows across the
   ProjectReference into `TianWen.Lib`'s own build; a property set only on the consuming Blazor
   project (or the target RID) does NOT reach the referenced build. Result: the deploy payload drops
   from ~45 MB to ~16 MB brotli. The bright-star atlas uses the HR catalog (naked-eye sky); the DSO
   planner never needs tyc2. Note (see the atlas section): HR resolves the bright HIP stars by
   cross-identity, but the deeper HIP/Tycho field is only available WITH tyc2 - there is no small
   HIP-only middle tier in the repo. Further `Lightweight` candidates (dead without tyc2): the
   `hip_to_tyc`/`hd_to_tyc` cross-ref maps (~1.2 MB); `hd_hip_cross` must STAY (bright HIP<->HR identity).

## Minimal DI graph for the browser

`ITimeProvider` + `BrowserExternal` (as `IExternal`) + `AddAstrometry()` (`ICelestialObjectDB`
from embedded resources, `ICometRepository` via a JPL SBDB `fetch`, CORS permitting) + optionally
`AddOpenMeteo()`/`AddOpenWeatherMap()` (HTTP weather band on the planner chart). Everything else in
the desktop `Program.cs` (ZWO/QHY/Canon/ASCOM/Alpaca/Meade/OnStep/iOptron/Skywatcher/Gemini/PHD2/
BuiltInGuider/Devices/SessionFactory/FitsViewer/PlanetaryCaptureController) is native-SDK / serial /
COM / session machinery - inherently non-browser and irrelevant to Planner + Atlas.

## Project shape (mirrors Chess.Web)

New `TianWen.UI.Web` (`Microsoft.NET.Sdk.BlazorWebAssembly`, net10.0), **outside** `TianWen.slnx`
and outside CPM (`ManagePackageVersionsCentrally=false`, versions inline), `InvariantGlobalization`,
`TrimMode=partial`, `TrimmerRootAssembly` for TianWen.Lib / TianWen.UI.Abstractions / DIR.Lib /
WebGl.Renderer. Sibling `WebGl.Renderer` ProjectReference under `UseLocalSiblings`, else
`PackageReference` (floating minor). Fonts + a pre-baked `.sdfg` glyph atlas staged into the WASM
in-memory FS at startup (chess's `LoadFontsAsync`/`LoadSdfCacheAsync` + `BakeSdfAtlas` tool pattern).
Render-on-demand (no RAF spin loop). Canvas mouse/scroll/key routed into the tab hit-test /
clickable-region system.

## Code reuse (chess vs dotcc)

`sebgod/dotcc` -> `DotCC.Web` is **not** renderer-reusable: it is a plain Blazor WASM app with
DOM/HTML UI (MainLayout + Pages, CodeMirror + wabt), no `WebGl.Renderer` or canvas rendering. It
shares only the generic Blazor-on-Pages shell conventions with chess (opt out of CPM,
`TrimMode=partial`, `TrimmerRootAssembly`, `index.html` + base-href) - the pattern diverges by app.

`sebgod/chess` -> `Chess.Web` is the real template and the only current `WebGl.Renderer` consumer.
Reusable pieces, all small: `Play.razor`'s `LoadFontsAsync` + `LoadSdfCacheAsync` (stage `.ttf`/`.sdfg`
into the WASM in-memory FS), `WebGlRenderer.CreateAsync` + `PrimeFonts`, the render-on-demand loop
(~150 lines); `tools/BakeSdfAtlas` (already generic - out-dir + font paths as args); `pages.yml`.

Decision: **copy the chess recipe for P0/P1** (fast, only two consumers so rule-of-three is not met,
no shared lib yet). The pure-boilerplate bits - the WASM font/SDF-staging helper and a `<WebGlCanvas>`
host component that every WebGl.Renderer Blazor consumer needs - are the clean factoring, and belong
**in `WebGl.Renderer`** (already an `Sdk.Razor` static-web-assets package). Fold them into the P2
custom-pipeline release (near-zero marginal cost, one source of truth); chess adopting them later is
optional. `BakeSdfAtlas` similarly could move into the renderer package at that point.

## Showcase landing page

Static `index.html` + CSS at the site root (no WASM download): hero, one-line pitch, a feature
grid (device management across ASCOM/Alpaca/native SDKs; the Planner; the Sky Map / atlas; deep-sky
stacking + AI enhance; planetary lucky-imaging; the session automation), and a "Launch the live app"
button into the Blazor app. Real screenshots captured from the running **Debug** GUI via the
`sdl-ui-inspector` MCP: `run-gui`, drive to each state with `post_signal`/`describe`, `screenshot`,
and commit the PNGs under `wwwroot/img/`. Captured during the build, not in CI.

## Phasing

| Phase | Scope | Repo | Risk | Ships |
|---|---|---|---|---|
| **P0** | Web host skeleton: `TianWen.UI.Web`, `BrowserExternal`, minimal DI, `WebGlRenderer.CreateAsync` + render-on-demand loop, font/SDF staging, Tycho-2 embed gated off, canvas input plumbing. Placeholder tab renders. | tianwen | Low | - |
| **P1** | **Planner** tab: `PlannerTab<WebGlContext>` (no chart-texture override), `InitDBAsync`, comets, `ObservationScheduler`, manual site lat/lon, localStorage pins, optional weather. | tianwen | Low | Interactive planner |
| **P2** | **WebGl.Renderer extensibility**: general custom-pipeline registration + persistent instance/vertex buffers + `DrawInstanced` + custom UBO/uniform upload + general texture path. Minor NuGet release. | WebGl.Renderer | Med-High | Renderer feature |
| **P3** | **Atlas**: `WebGlSkyMapPipeline` (port `VkSkyMapPipeline` shaders/geometry) + `WebSkyMapTab<WebGlContext>` (8 render hooks), star source = HR bright stars, grid + constellation line-lists, DSO overlay ellipses, labels via base. Milky Way optional. | tianwen | Med | Interactive atlas |
| **P4** | **Showcase landing page** + inspector screenshots. Independent of P2/P3. | tianwen | Low | Landing page |
| **P5** | **Deploy**: `pages.yml` (ubuntu, wasm-tools workload, bake SDF atlas, publish `-p:UseLocalSiblings=false`, rewrite `<base href>` to `/tianwen/`, `.nojekyll` + `404.html` SPA fallback, upload + deploy pages). | tianwen | Low | Live site |

Incremental value: **P0 -> P1 -> P4 -> P5** ships a live site (planner + showcase) before the atlas
lands; **P2 -> P3** then adds the atlas. P2 gates P3 across the NuGet release boundary.

## P0/P1 findings (2026-07-17, measured in-browser)

- **It works end-to-end.** `PlannerTab<WebGlContext>` renders + interacts with ZERO changes to the
  tab/chart code. Host shell is ~350 lines of Blazor glue (`Planner.razor` + `BrowserExternal`).
- **Init-order gotchas fixed in the shell** (each was a real crash/hang):
  - `Transform.DateTime` must be seeded from the time provider before `CalculateNightWindow`
    reads `transform.DateTimeOffset` (unset JD = year -4712 -> `Arg_OleAutDateInvalid` in
    `DateTime.FromOADate`). Desktop's `RefreshDateTimeFromTimeProvider` is Lib-internal.
  - Blazor's `firstRender` fires during the first `await` in `OnInitializedAsync` (font fetch), so
    an `if (firstRender && ready)` render guard NEVER fires - paint explicitly after init.
  - Geolocation permission prompts outlive any fixed timeout: check the Permissions API state;
    granted -> fetch before first compute, prompt -> compute with default site and recompute in
    the background when the grant lands. A 📍 re-locate toolbar button reuses the same path.
  - The single browser thread means `Task.Run` work QUEUES on the UI thread: the tyc2 "background"
    bulk decode wedged the page (motivating `Lightweight`), and status lines need an explicit
    yield (`StateHasChanged` + `Task.Delay`) to paint before a synchronous compute block.
- **Lightweight fallout in `CelestialObjectDB`**: the hd-hip-cross snapshot hash guard reads
  tyc2 as an input - with tyc2 stripped, verification is impossible, so the snapshot applies
  UNVERIFIED in Lightweight builds (full builds still verify; the staleness invariant is a
  dev/CI-time concern). Call-site fallback likewise skips the tyc2-dependent live compute.
- **Perf: WASM AOT is the deploy recipe, measured A/B in-browser (2026-07-17).** Interpreted
  (Release + jiterpreter + Lightweight): catalog init **13.6 s**, tonight's-best sweep +
  profiles **24.9 s** - unshippable. AOT (`RunAOTCompilation=true`, same Lightweight publish):
  init **554 ms (24x)**, sweep **591 ms (42x)** - native-desktop-class (desktop init is ~500 ms).
  Payload cost: 16 -> 21 MB brotli (the AOT native code rides `dotnet.native.wasm`). Verdict
  closes the alternatives: no decoded-DB IndexedDB snapshot, no sweep web-tuning, no
  workers/WASI/React needed. Deploy = `dotnet publish -c Release -p:Lightweight=true
  -p:RunAOTCompilation=true`.
  Local win-arm64 caveat: the mono AOT cross-compiler fail-fasts (sgen assertion, 0xC0000409) on
  the P/Invoke-dense vendor SDKs + the WasmDedup `aot-instances.dll`; worked around via
  `_AOT_InternalForceInterpretAssemblies` (ZWO/QHY/LibUsb - browser dead weight anyway, zero perf
  impact) + `-p:WasmDedup=false` locally. CI (ubuntu-x64) should first try WITH dedup (smaller
  output) and only add the flag if the linux toolchain also crashes. Follow-up: find why the
  trimmer keeps the vendor SDKs in the web bundle at all (payload + AOT both win when they go).
- **Publish-time fingerprinting**: `index.html` MUST carry the `OverrideHtmlAssetPlaceholders`
  markers - `<link rel="preload" id="webassembly" />`, an empty `<script type="importmap">`, and
  `_framework/blazor.webassembly#[.{fingerprint}].js` - or the published page 404s (only
  fingerprinted asset names exist in `_framework`). The dev server tolerates plain names, so this
  only bites on publish. Mirrors Chess.Web.
- **JPL SBDB has no CORS headers** - browser-side comet fetch is impossible, permanently. The
  repository degrades gracefully (DSO-only). Fix = **bake comets in CI**: the Pages workflow curls
  the same SBDB query on the runner, ships `comets.json` as a static asset, and the app seeds it
  into `CometRepository`'s cache path in MEMFS (the repo's own TTL/stale logic then applies;
  weekly redeploys keep it fresh). Zero Lib changes.
- **Caching layers**: browser HTTP cache already covers the payload (fingerprinted assets); the
  site + planner-session persistence SHIPPED via localStorage (see the interaction round below);
  the comets cache stays MEMFS pending the CI bake; a decoded-DB snapshot (generalize the
  hd-hip-cross snapshot pattern to the whole DB, IndexedDB-stored) stays deferred unless AOT'd
  init is still too slow.

## P1 interaction + persistence round (2026-07-17, second session)

- **Handoff-divider drag is host-shared code now.** The slider state machine (SliderHit grab,
  click-to-place, XToTime move, release) lived only in `GuiEventHandlerBase` - the SDL host's
  event router, which the web host does not use - so divider clicks silently no-op'd in the
  browser (`Planner.razor` discarded the `HitTestAndDispatch` result). Extracted to
  `PlannerSliderInteraction` (Abstractions); the desktop handler delegates (semantics preserved,
  incl. the active-tab gate on click-to-place) and `Planner.razor` feeds it the hit result / the
  moves / the release. **Rule for future tab ports: interaction logic in `GuiEventHandlerBase`
  is INVISIBLE to the web host** - anything a ported tab needs must live in a shared
  Abstractions helper, so audit `GuiEventHandlerBase` for tab-specific blocks when porting.
- **Pointer capture** (`tianwenDragCapture` in index.html): `setPointerCapture` on pointerdown
  retargets the compatibility mousemove/mouseup back to the canvas, so drags keep tracking after
  the pointer leaves the canvas (the SDL mouse-capture analogue). Without it a swipe-style
  divider drag drops mid-gesture and can wedge in the dragging state.
- **localStorage persistence shipped** (was the P1-polish open item):
  - Last-used site (`tianwen.site`): loaded as the FIRST init step (kills the default-site flash
    on F5), saved after every compute. Geolocation / manual entry refine and overwrite it.
  - Planner session (`tianwen.planner`): pins + handoff sliders + settings. Reuses the desktop
    DTO + restore logic - `PlannerPersistence` split so `TryRestoreFromDto` (site invalidation,
    target matching, slider-window checks) is public with `SerializeToJson`/`TryRestoreFromJson`
    wrappers; the file-store `TryLoadAsync` delegates to it. The save trigger is the SAME wiring
    as desktop: `PlannerState.Bus` gets a `SignalBus`, the `IsDirty` setter auto-posts
    `SavePlannerSessionSignal`, the razor host subscribes (new public
    `PlannerState.MarkSessionSaved()` clears the assembly-internal flag) and the bus is pumped in
    `RenderFrame` - every event ends in one; the web host has no frame loop. Restore runs after
    EVERY compute, deliberately: the first compute may run before geolocation lands (saved pins
    get site-invalidated at >1 deg drift) and the post-geolocation recompute then restores them.
- **One local server.** The dual dev(:5099)/AOT-static(:5100) setup existed only for the A/B
  benchmark; a stale second copy cost a full debugging round ("the fix doesn't work" = testing
  yesterday's publish). Local dev = `dotnet run` on :5099 (always interpreted -
  `RunAOTCompilation` has NO effect on `dotnet run`); the AOT publish is CI's artifact (P5).
  Don't resurrect the second local server.
- Tab title is text-only ("TianWen") in both `<PageTitle>` and the index.html fallback: the
  favicon IS the telescope emoji (inline SVG data URI), so a title emoji renders twice.

## Deferred

- Full Tycho-2 catalog in the browser (the 30 MB payload; drops into the P2/P3 pipeline as a data
  source swap once shipped).
- Milky Way texture underlay (needs the general texture path from P2; cosmetic).
- FITS viewer / Planetary / Guider preview (image-raster GPU pipeline, no WebGl.Renderer analog).
- Equipment / Session / LiveSession tabs in the browser (portable but device-control-oriented,
  no value without a device layer).
- IndexedDB-backed profile storage (v1 uses manual site entry + localStorage pins).

## Open items

- Confirm CORS on the JPL SBDB comet endpoint from a github.io origin (else comets degrade to
  "no repository" - the planner/atlas both handle a null `ICometRepository`).
- WASM payload budget after Tycho-2 gating (DSO catalogs + HR + simbad_merge + hd_hip_cross +
  pickles_sed ~ single-digit MB compressed; verify the published `_framework` size).
- Register this plan in `docs/plans/summary.md`.
