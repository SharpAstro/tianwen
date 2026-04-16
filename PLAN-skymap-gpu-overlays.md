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

## Not planned (stays CPU)

- **Info strip**: fixed screen-space overlay, only redrawn on state change
- **Crosshair**: 2x `FillRect`, trivial
- **Reticle**: already GPU via `VkOverlayShapes.DrawReticle`
- **DSO overlay ellipses**: already GPU via `VkOverlayShapes.DrawEllipse`
