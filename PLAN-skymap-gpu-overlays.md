# Plan: Sky Map GPU Overlay Migration

Move remaining CPU-drawn sky map overlays to the GPU pipeline for consistent
rendering and reduced CPU/GPU round-trips.

## Phase 1: Mosaic panels + sensor FOV -> LinePipeline [DONE]

Currently `DrawFovQuadrilateral` computes unit-vector corners on CPU, converts
back to RA/Dec, projects via `SkyMapProjection`, then draws 4x `DrawLine` per
panel through `PixelWidgetBase`. Each line goes through the CPU pixel path
(Bresenham or scanline quad).

**Target**: upload panel corners as line-list vertex buffers (J2000 unit vectors,
same format as constellation figures/boundaries) and let the vertex shader
project them via the UBO view matrix.

### Steps

- [ ] Add a `_mosaicLineFloats` list to `VkSkyMapTab` (like `_horizonFloats`)
- [ ] In `RenderMosaicPanels`, compute the 4 unit-vector corners per panel
      (reuse the tangent-plane offset math from `DrawFovQuadrilateral`) and
      append 8 line segments (4 edges x 2 vertices) to the float list
- [ ] Do the same for the sensor FOV rectangle (mount overlay)
- [ ] Write to the ring buffer via `WriteToRingBuffer` and draw with
      `DrawLineBuffer` + a distinct push-constant color (red, like now)
- [ ] Remove the CPU `DrawFovQuadrilateral` method and the `DrawLine` calls
- [ ] The vertex shader handles stereographic projection automatically -
      panels tilt correctly with the grid at all declinations, including poles

**Benefit**: eliminates ~20 CPU `DrawLine` calls per frame (4 per panel x
~5 panels + sensor FOV), replaces with a single GPU draw call. Projection
is free (vertex shader). No pole singularity concern since unit vectors
never touch RA/Dec coordinates.

## Phase 2: Planet dots -> already GPU

`FillCircle` delegates to `VkRenderer.FillEllipse` (EllipsePipeline GPU shader).
Already GPU-rendered. The CPU cost is just VSOP87a position computation + 9 method calls.

### Steps

- [ ] Add planet positions to a small per-frame instance buffer (same format
      as stars: vec3 pos + float mag + float bv, ~10 entries)
- [ ] Reuse the `StarPipeline` instanced quad path or create a small
      dedicated pipeline with larger minimum radius (planets should always
      be visible dots, not magnitude-scaled)
- [ ] Remove CPU `FillCircle` calls from `DrawPlanetLabels` in `SkyMapTab`
- [ ] Keep planet label text as-is until Phase 3

**Benefit**: planets rendered in the same pass as stars, consistent visual
style, zero CPU draw calls.

## Phase 3: Sky map text labels -> batch glyph rendering

Currently all sky map text goes through `PixelWidgetBase.DrawText` ->
`VkRenderer.DrawText` -> GPU glyph atlas. This is already GPU-rendered
(each glyph is a textured quad via the SDF font atlas pipeline), but each
`DrawText` call does per-label MeasureText + per-glyph draw calls.

The optimization is batching: accumulate all label glyph quads into a single
vertex buffer upload and draw them in one instanced call. This reduces ~100
individual draw calls to 1. Requires VkRenderer-level changes (glyph batching
API) which is a separate concern from the sky map.

### Steps

- [ ] Factor out the projection + visibility check from `DrawConstellationNames`,
      `DrawPlanetLabels`, `DrawGridLabels` into a shared `ComputeLabelPositions`
      that returns a list of `(float screenX, float screenY, string text, color)`
- [ ] Call `VkRenderer.DrawGlyphAtBaseline` directly for each label character
      instead of going through `PixelWidgetBase.DrawText` -> `Renderer.DrawText`
      -> MeasureText + layout. This skips the text layout engine and renders
      glyphs at known screen positions
- [ ] Batch glyph draws: accumulate all label glyphs into a single vertex
      buffer upload (each glyph = 1 textured quad), draw in one instanced call
- [ ] Label collision avoidance (`OverlayEngine.PlaceLabels`) stays on CPU -
      it runs once per frame and produces screen positions, which is fine
- [ ] Grid labels stay at viewport edges (screen-space), no projection needed

**Benefit**: eliminates per-label `MeasureText` + `DrawText` overhead. All
glyph quads submitted in one GPU batch. The font atlas is already a GPU
texture (uploaded once at startup). Constellation names, planet labels, and
grid labels rendered in ~1 draw call total instead of ~100+ individual
DrawText calls.

## Phase 4: Kill the CPU RA/Dec grid scan (meridian lag fix)

**Problem.** Overlay markers already render GPU-side (`VkOverlayShapes`), but
`OverlayEngine.ComputeSkyMapOverlays` scans the RA/Dec grid cell-by-cell on
the CPU every frame to decide *what* to render. When `poleInView` or
`FOV >= 90 deg` (exactly when the meridian is visible — it goes pole-to-pole),
the scan goes full all-sky: 360 RA * 181 Dec = ~65,000 cells, ~5 ms CPU
per frame, noticeable as "lag when the meridian is in view."

**Fix.** Replace the grid scan with a direct enumeration of
`ICelestialObjectDB.AllObjectIndices` (~5-10k deep-sky objects), project each
via the existing `SkyMapProjection`, cull by screen bounds and FOV-aware mag
cutoff. ~5k CPU ops per frame is a flat cost independent of view, bounded,
and much cheaper than the cell scan.

### Steps

- [ ] In `ComputeSkyMapOverlays`, replace the `for (dec ...) for (ra ...)`
      nested loop over grid cells with a single `foreach (idx in
      db.AllObjectIndices)` pass over the catalogue directly.
- [ ] Keep the existing per-candidate filters (mag cutoff, extended-object
      type gate, screen-bounds cull) exactly as they are. Only the outer
      iteration changes.
- [ ] Delete the `poleInView` branch that widened to all-sky — no longer
      relevant since the new pass iterates objects directly, not cells.
- [ ] Delete the RA wrap-around handling — objects have absolute coordinates,
      projection decides visibility.
- [ ] Keep `seen` HashSet just as a dedup guard for cross-index objects.

**Benefit.** Frame cost becomes `O(N_objects)` instead of `O(N_cells *
avg_objects_per_cell)`. Today at wide FOV: 65,000 cell lookups * 10 ns + 5k
object operations; after: just the 5k object operations. Expected: 5 ms
-> <1 ms per frame at wide FOV. Meridian-in-view lag gone.

**Risk.** Extended low. The grid scan was an indexing optimization for narrow
FOV; at 5-10k total objects, the overhead of iterating all of them beats the
grid lookup at any FOV wider than ~30 deg (break-even, measured worth
confirming with a benchmark before / after). Narrow-FOV case might lose a
few microseconds; acceptable.

## Phase 5: Cache meridian line geometry

**Problem.** `BuildMeridianLine` runs in `VkSkyMapTab.Render` every frame,
rebuilding 200 verts from `site.LST`. LST drifts at ~1 second wall-clock per
second; the meridian moves <0.0042 deg of RA per frame at 60 FPS — far below
sub-pixel on screen. Still, we pay ~200 trig calls + a ring-buffer write each
frame for zero visual change.

### Steps

- [ ] Cache `_meridianFloats` on `VkSkyMapTab`; only rebuild when
      `|lst - _lastMeridianLst| > lstThreshold` (threshold = one pixel of
      sub-pixel RA motion at current FOV, ~0.01 h / 36 arcsec).
- [ ] Same pattern for `_horizonFloats` (also rebuilt per frame from site).
- [ ] Don't write to ring buffer if cached; retain the previous ring-buffer
      offset for the draw call.

**Benefit.** Saves ~1 ms per frame when idle (no pan/zoom, no LST drift that
matters). Compounds nicely with Phase 4 — interactive + idle both become
free on the CPU side.

## Not planned (stays CPU)

- **Info strip**: fixed screen-space overlay, only redrawn on state change
- **Crosshair**: 2x `FillRect`, trivial
- **Reticle**: already GPU via `VkOverlayShapes.DrawReticle`
- **DSO overlay ellipses**: already GPU via `VkOverlayShapes.DrawEllipse`
- **Label collision layout** (`OverlayEngine.PlaceLabels`): runs once per
  frame on the ~80 top-priority candidates, trivial cost.
