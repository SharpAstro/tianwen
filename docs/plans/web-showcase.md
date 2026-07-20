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
- **Local servers.** Local dev = `dotnet run` on :5099 (always interpreted - `RunAOTCompilation`
  has NO effect on `dotnet run`); the AOT publish is CI's artifact (P5). A second local AOT
  instance on :5100 is legitimate for realistic perf comparison, but ONLY brought up together
  with a fresh publish and torn down afterwards - the trap is STALENESS, not the port: a
  left-running stale copy cost a full debugging round ("the fix doesn't work" = testing
  yesterday's publish).
- Tab title is text-only ("TianWen") in both `<PageTitle>` and the index.html fallback: the
  favicon IS the telescope emoji (inline SVG data URI), so a title emoji renders twice.

## WebGlCanvas 1.1 assessment (2026-07-17, do NOT migrate yet)

WebGl.Renderer 1.1 ships `<WebGlCanvas>` - the reusable hi-dpi canvas host that folds in the
metrics/resize/dpr pattern this repo pioneered (see `webglcanvas-1.1-findings.md`, uncommitted).
Chess.Web migrated; **TianWen.UI.Web must NOT migrate onto 1.1**: the component only binds
`@onclick` (fires on RELEASE - cannot start a drag) + `@onkeydown`, with no pointer-move/up, no
wheel, no text input, and no attribute splatting - the planner would lose the divider drag, hover
follower, and list scroll. Migration is gated on a WebGl.Renderer 1.2 that adds true
`pointerdown`/`pointermove`/`pointerup`/`wheel` callbacks (backing-space mapped like
`OnPointerDown`) + built-in `setPointerCapture` on press (subsumes `tianwenDragCapture`) — planned
in `WebGl.Renderer/docs/plans/webglcanvas-1.2-input.md`; then
Planner.razor's `tianwenCanvasMetrics`/`tianwenWatchResize`/`tianwenDragCapture` helpers and the
resize plumbing all delete in favour of `OnReady`/`OnResized`. Also in 1.1: DIR.Lib 6.11's
read-only `SdfGlyphDiskCache` mode (the single-threaded-WASM-friendly variant).

## P2+P3 findings (2026-07-17, sky map SHIPPED)

- **P2 shipped as WebGl.Renderer 1.3**: RegisterPipeline (custom GLSL ES 3.00, split
  vertex/instance layouts, topology, additive blend, per-pipeline std140 UBO binding points),
  persistent buffers, DrawBuffer/DrawInstanced opcodes, attributeless (gl_VertexID) pipelines.
  Attribute-state hygiene matters: divisors are context-global per location (explicit reset per
  draw), stale enabled arrays disabled, dynamic VBO re-bound after custom draws.
- **P3 shipped**: `WebGlSkyMapPipeline` + `WebSkyMapTab` (TianWen.UI.Web/SkyMap) over the new
  shared `SkyMapGpuGeometry` + `SkyMapUbo` (TianWen.UI.Abstractions) - the Vulkan pipeline now
  delegates to the same builders (one geometry path). Star field = the HR catalog (~8.6k stars,
  enumeration only); `TryGetHipStarLite` gained the HR/HD cross-ref fallback (Lightweight has no
  Tycho-2, so the O(1) HIP path misses for every figure star). Horizon ground fill ported
  (attributeless full-screen inverse-projection pass). Validated interpreted AND AOT.
- **Port gotchas (browser GPU)**: GLSL ES defaults `int` to highp in VS but mediump in FS - a
  uniform block used in BOTH stages fails to LINK on an int member unless `precision highp int;`
  is declared (the fill pass was the first two-stage block). GL NDC Y is up (negate vs Vulkan in
  the final mapping; gl_VertexIndex -> gl_VertexID). Input ordering is load-bearing: hit-test
  clickable regions BEFORE HandleInput (the sky map consumes every press for drag tracking).
- **Local AOT publishes are isolated** (`obj-aot`/`bin-aot` via a project-scoped
  Directory.Build.props block + symmetric DefaultItemExcludes): a publish sharing the dev obj/bin
  regenerates fingerprinted assets under the running dev server (404s), and a GLOBAL
  -p:BaseIntermediateOutputPath poisons sibling root-csproj globs (CS0579).
- **Deferred from the sky map v1**: `RenderObjectOverlay` port (DSO/star markers + click-select
  via OverlayEngine), schedule/mount/fixed-point marker overlays, Milky Way texture, F3 search
  index wiring, comet markers (repo loads; markers draw via base labels only).

## Deep links + text-input/search round (2026-07-17, third session)

- **Deep-linkable views**: `/` or `/planner`, and `/sky-atlas` (aliases `skymap`, `sky`). All
  routes map to the same `Planner` component, so the router updates the route param in place
  (no re-init/recompute); `OnParametersSet` syncs the view, the chips navigate via
  `NavigationManager`, and browser back/forward works for free. Pages' SPA 404 fallback serves
  deep loads under the `/tianwen/` subpath.
- **Branding**: titlebar `🔭 天文` with pinyin tooltip (`tiānwén`); chips carry the GUI tab
  emoji (`📅 Planner`, `🌌 Sky Atlas`, from the `VkGuiRenderer` registry).
- **Text input + search are host-shared code now** (the second instance of the
  GuiEventHandlerBase-invisible-to-web rule; the planner search box was inert in the browser):
  - `TextInputInteraction` (Abstractions): the per-keystroke machinery moved verbatim out of
    `GuiEventHandlerBase` (suggestion/result arrow nav, Tab cycling, OnKeyOverride, clipboard,
    commit/cancel dispatch). Desktop delegates with its signal-based context; the web page
    calls it with a field-based one. Focus BOOKKEEPING stays host-owned by design (SDL needs
    StartTextInput; the browser doesn't).
  - `PlannerSearchInteraction` (Abstractions): the search-input callback wiring moved out of
    `AppSignalHandler.Planner`, parameterized on transform creation (desktop: active profile;
    web: lat/lon fields).
  - Sky-atlas F3 search wired on the web bus to the shared `SkyMapSearchActions`
    (open/filter/commit/close).
  - Web keydown nuance: the browser's keydown carries the printable character itself, so the
    page inserts it via `HandleText` after `HandleKey`; the SDL host gets a separate TextInput
    event instead. (This canvas keydown path is now the FALLBACK - while the CanvasTextOverlay
    below holds focus, editing is native and the canvas never sees the keys.)

## CanvasTextOverlay round (2026-07-17, fourth session)

- **Native text entry over canvas widgets** (WebGl.Renderer 1.4 `CanvasTextOverlay`): a REAL
  focusable `<input>` floated over the active canvas text widget - the standard canvas-UI
  companion (Figma / Docs / xterm.js all do this) - so the web app gets IME composition, native
  clipboard (Ctrl+V works), autocorrect, and the mobile soft keyboard (the driver: no mobile
  app yet, so the web build IS the mobile story). v1 is a visible, theme-styled input covering
  the widget (robust for every input source), not the invisible-mirror caret-fidelity variant.
- **Host wiring** (`Planner.razor` + `.canvas-text-input` in app.css; `.canvas-host` is now
  `position:relative`): `ActivateTextInput` shows the overlay over the widget's clickable-region
  rect (a `GetRegisteredRegions()` scan, backing px -> CSS px via dpr; the accessor is not on
  `IPixelWidget`, so the host types the active tab as `PixelWidgetBase<WebGlContext>`). While
  editing, the browser input owns the TEXT: `OnInput` mirrors value+caret into `TextInputState`
  and fires `OnTextChanged` (autocomplete); ArrowUp/Down/Enter/Escape/Tab are intercepted
  JS-side (preventDefault, never during IME composition) and routed into the shared
  `TextInputInteraction.HandleKey`, so suggestion nav / commit / cancel are the same code path
  as desktop; a canvas-side rewrite (suggestion Enter-commit) pushes back via `SetValueAsync`;
  `SyncOverlayRect` re-anchors after every RenderFrame (regions re-register per paint).
- **Blur deactivation is deferred + epoch-guarded**: a blur caused by clicking ANOTHER canvas
  input arrives around the same time as the re-activation - deactivating immediately would kill
  the new focus. Only a blur that stays unanswered for ~100 ms (tap on toolbar/browser chrome)
  deactivates.
- **Known caveat**: iOS Safari may need a second tap for the soft keyboard on first activation
  (focus() runs on an async continuation of the pointer gesture; Chrome/Android honour the
  transient-activation window). Hardware-verify on a phone against the live site.
- **Autocomplete cache must stay off the first-paint path**: `BuildAutoCompleteList` walks every
  catalog designation (x2 canonical forms) + sorts - measured 7.4 s INTERPRETED on :5099 (fine
  under AOT). It builds in the background task after first paint (search commit works without
  it) and rebuilds with comet designations once comets load.
- **Local dev comets**: `wwwroot/comets-sbdb.json` is CI-baked and gitignored, so :5099 404s it
  by default (comet-less dev session). Bake it locally with the same curl as pages.yml when
  comets are needed in dev; the dev server serves the source wwwroot directly, no rebuild.

## Selectable text + fullscreen round (2026-07-20, fifth session)

- **Details enrichment first** (all surfaces, not web-specific): `PlannerDetails.GetLines` now
  appends the full catalog record for the selected target - object type + constellation (friendly
  names), V mag, surface brightness (mag/arcsec^2), B-V, and size via
  `ICelestialObjectDB.TryGetShape` (Stellarium-parity for the IC 1297 style panel).
  `SkyMapInfoPanelData` carries `SurfaceBrightness` into the atlas info panel + F3 search line.
  Pinned by `PlannerDetailsTests`.
- **Selectable DOM text over the canvas** (DIR.Lib 6.12 + WebGl.Renderer 1.6): the browser renders
  the planner details panel's text as REAL selectable DOM spans instead of rastered glyphs. Seam:
  `PixelWidgetBase.DrawSelectableText` registers a `SelectableTextRegion` per frame (zero-copy
  `CollectionsMarshal.AsSpan` view over the frame list, mirroring the clickable tracker's
  lifecycle - and kept OUT of it so text never shadows click hit-testing);
  `Renderer.HostRendersSelectableText = true` (set once in `Planner.razor`) makes the widget skip
  the glyph raster so the DOM copy is the only text on screen. `CanvasTextLayer` (WebGl.Renderer)
  is the retained span layer: pure Blazor, no JS interop; pointer-events:none container with
  pointer-events:auto runs so canvas drags pass through everywhere except over text; Near/Center/Far
  -> flex mapping; invariant-culture inline styles. Host sync (`SyncSelectableTextLayer`)
  double-buffers two run lists and only calls StateHasChanged when the runs actually changed, so
  pointer-move repaints cost no Blazor re-render. An `@font-face` for DejaVu Sans points at the
  SAME ttf the glyph atlas fetches (browser reuses the HTTP-cached bytes) so DOM text matches
  canvas text metrics. Desktop GUI/TUI stay byte-identical (flag default-off: DrawSelectableText
  rasters exactly like DrawText). Deliberately NOT used for high-churn scene labels (sky-map
  star/constellation names) - stable info/detail panels only.
- **Fullscreen (the F11 finding)**: plain F11 is browser-reserved and unobservable by the page (no
  event, no API) - its viewport resize is already handled by the ResizeObserver like any window
  resize, which is why F11 "works" but can't be intercepted or improved. For a real edge-to-edge
  mode the toolbar gains a fullscreen button -> `WebGlCanvas.ToggleFullscreenAsync()` ->
  Fullscreen API on the canvas's PARENT (`.canvas-host`), so DOM overlays (text layer, text input)
  stay anchored and the UA's :fullscreen object-fit letterboxing of a bare canvas never applies;
  an attach()-installed `fullscreenchange` listener re-measures immediately (no stale frame while
  the ResizeObserver waits for its next rAF tick).
- **Console follow-up idea** (from the same seam, deferred): a terminal host can walk the same
  `SelectableTextRegions` to register native drag-select / double-click word-select + OSC-52
  auto-yank in Console.Lib.

## Portrait reflow round (2026-07-20, sixth session)

The iPhone-portrait finding (chart squashed beside the fixed 330-unit list) root-caused NOT to
Blazor/web but to `PlannerTab`'s landscape-only top-level layout - the same squash reproduces in a
portrait-resized desktop SDL window. Fixed once in the shared widget, so GUI + web + any host get it:

- **DIR.Lib 6.14 layout primitives** (built for this, generic to every consumer - tianwen, chess,
  PTV): `Sizing.Star(weight, min, max)` clamps with iterative-freeze redistribution (a min-clamped
  Star overflows visibly instead of starving to zero - the negative-width bug class chess hit;
  a max-clamped Star's surplus flows to its siblings), `.CollapseBelow(u)` (a Stack drops a child
  arranged under its threshold and redistributes - the declarative form of chess's manual
  "history strip only if >= min height" gate), and `Node.Wrap` / `Builder.WrapH/WrapV` flow
  containers (toolbar/chip-row wrapping; Auto-height reflows as the container narrows).
- **`PlannerTab.BuildFrameLayout`**: the top-level frame migrated from `PixelLayout` cursor docks to
  a `Layout.Builder` tree branched on the content rect's aspect (immediate mode: the tree rebuilds
  every frame, so the "media query" is a C# `if`). Landscape keeps the shipped geometry (left list -
  now capped at 42% width so tiny windows can't starve the chart - bottom-right details, chart
  fill). Portrait stacks: full-width chart at natural aspect (`min(0.72 x W, 0.45 x H)`), a compact
  details strip under it (max-clamped star, `CollapseBelow(48)` when squeezed), target list fills
  the rest (min-clamped). Region rects come off the arranged tree via keyed `Fill` leaves - and the
  frame tree is now visible to the DEBUG inspector's `describe_layout`.
- **`PlannerDetails.GetLines(maxLines:)` line budget**: portrait's compact strip sheds lines
  least-important-first (size, photometry, alias, type/constellation, rating, imaging, coords - the
  name never drops) instead of cramming all 8 into unreadable rows. Content selection, not truncation.
- **Tests** (`PlannerTabLayoutTests`, the chess `PixelGameDisplayLayoutTests` pattern): arranged-rect
  assertions at 1600x1000 / 600x400 / 390x844 (iPhone 12 Pro logical) / 780x1688@2x / 200x260
  (collapse case), plus an offline `RgbaImageRenderer` pixel render on both orientations asserting
  <2% of a magenta sentinel prefill survives (catches the zero-width-region-paints-nothing class);
  PNGs dumped beside the test binary for eyeballing. `PlannerDetailsTests` pins the shed order.
- **Not yet**: list fling/momentum scrolling on touch (named cost, shared-code addable).

## Sky-atlas interactions round (2026-07-20, seventh session)

The atlas shipped with stars + lines + F3 search but no object overlay and no way to select/pin from
the map (the click-select + pin/view signals had no subscriber on web, and `RenderObjectOverlay` was
the no-op base). All three now work on web, built by SHARING the desktop logic, not copying it:

- **Object overlay ([O] catalog markers + [D] dark nebulae + pinned landmarks)**: new shared
  `SkyMapTab.RenderObjectOverlayPrimitive` (base partial `SkyMapTab.ObjectOverlay.cs`) draws the
  overlay with the surface-agnostic `DrawLine`/`DrawCircle`/`DrawText` primitives over the SAME shared
  `OverlayEngine` gather/project/place-labels the desktop GPU path uses — only the rasterisation
  differs (CPU primitives vs the Vulkan instanced-ellipse pipeline, which WebGL has no analogue for; a
  hand-maintained mirror exactly like `TryDrawShapeMarker` mirrors the GPU selection ellipse). Ellipses
  are traced via `OverlayEngine.ComputeEllipseScreenAxes` (true sky PA, from the candidate — the
  projected `OverlayItem` drops PA), stars as crosses, labels via `PlaceLabelsBestEffort`. Candidate
  gather is cached on a quantized-centre/FOV/layer/pins key (synchronous — single-threaded WASM has no
  background thread, so unlike VkSkyMapTab's async gather it walks inline, but only on a meaningful view
  change; panning within a cell just re-projects). `WebSkyMapTab` overrides `RenderObjectOverlay` to
  call it; TUI + GUI unchanged (base virtual stays no-op, VkSkyMapTab keeps its GPU override).
- **Click + Ctrl-click select**: `SkyMapClickSelectSignal` (posted by `TryEmitClickSelect` on map
  mouse-up + by planet/comet label clicks, carrying the Ctrl modifier the web already plumbs through
  `RememberMouseDown`) now has a web subscriber. The desktop handler's projection/pinned-set boilerplate
  was EXTRACTED to the shared `SkyMapSearchActions.SelectAtScreenPoint` (derives ppr/centre from
  `SkyMapState.LastContentRect` + `CurrentViewMatrix`, preferPointSource from the modifier) — both
  `AppSignalHandler` (desktop, rewired, verified unchanged) and `Planner.razor` call the one path. Ctrl
  picks a star under an enclosing DSO ellipse. Populates `State.Search.InfoPanel`.
- **Pin from atlas + view-in-planner**: `Planner.razor`'s new `WireSkyMapInteractions` subscribes
  `SkyMapPinObjectSignal` (`PlannerActions.TogglePinFromExternal` + re-posted `SavePlannerSessionSignal`,
  drained in the same pump), `ViewInPlannerSignal` (`CommitSuggestion` + select + a DEFERRED
  `InvokeAsync(SwitchView)` — SwitchView navigates -> re-enters the pump otherwise), and
  `SkyMapSlewToObjectSignal` (no browser mount -> a status note, not a dead button). The info panel with
  its Pin / View in Planner / Goto buttons is drawn by the shared `DrawInfoPanel`, so once a click
  populates `InfoPanel` the buttons are already live — the only gap was the missing subscribers.
- **Tests**: `SkyMapSearchActionsTests.SelectAtScreenPoint_DerivesViewportAndCtrlFromState` pins the
  extracted helper (nebula vs Ctrl-star pick via `LastContentRect`); `SkyMapObjectOverlayRenderTests`
  offline-renders the primitive overlay over the Sagittarius Milky Way on the CPU `RgbaImageRenderer`
  and diffs overlay-off vs overlay-on (the diff IS the overlay footprint), PNG dumped for eyeballing.
- **Fourth GuiEventHandlerBase-invisible-to-web incident** (after divider drag, TextInputInteraction,
  planner search): the sky-map signal subscribers lived only in the desktop-only `AppSignalHandler`.
  Resolved by the same rule — extract the pure body to a shared helper, re-wire on web.
- **Still deferred on web**: mount reticle, schedule-target markers, NCP/SCP/Zenith fixed-point
  clickable markers, Milky Way texture (all `RenderMountOverlay`/`RenderFixedPointMarkers`/etc. no-op
  overrides — no device layer / cosmetic); the sky-flat sky-map group-frame rendering.

## Related research plans

- [web-tycho2.md](web-tycho2.md) — the concrete implementation plan for full Tycho-2 in the atlas
  (the "data source swap" the deferred item below promises), phased P1 lazy-fetch+serial-decode →
  P2 wasm-threads parallel decode → P3 IndexedDB cache → P4 tiling.
- [web-multithreading.md](web-multithreading.md) — real browser parallelism (wasm-threads / Web
  Workers / second-runtime), the GitHub-Pages COOP/COEP wall, and the "do we still need Blazor"
  question. Verdict: AOT already fixed the freeze; build none now (except the tyc2 parallel-decode
  consumer in web-tycho2).
- [web-webgpu.md](web-webgpu.md) — a WebGPU render backend + shader reuse (GLSL→WGSL) + GPU compute
  to parallelize the sweep without the SharedArrayBuffer wall. Verdict: defer until interactive
  re-scoring (or a heavier GPU workload) justifies it.

## Deferred

- Full Tycho-2 catalog in the browser. The render pipeline is **ready** — the instanced
  `DrawInstanced` path renders 2.5M stars fine (WebGL2; the desktop Vulkan proves the scale), so
  this is a data + payload change, not a code change. But it's a **data-delivery problem, not a
  compute/GPU one** (evaluated 2026-07-18 in [web-multithreading.md](web-multithreading.md) +
  [web-webgpu.md](web-webgpu.md)): the ~30 MB payload is the real blocker (untouched by threads or
  WebGPU); the lzip decompress is parallel across members (Lzip.Lib `LzipDecoder` `Parallel.For`) but
  only speeds up under **wasm-threads** + a **multi-member bake** (`LzipOptions.MemberSize`) — a
  ready-made wasm-threads consumer, zero new code (GPU compute is still the wrong tool: LZMA is
  sequential-within-member). Payload levers: lazy fetch (on atlas open / zoom past HR density) /
  decoded IndexedDB snapshot / spatial tiling.
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
