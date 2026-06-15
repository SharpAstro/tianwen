# Known Limitations and Root Causes

Why certain limitations and subtle bugs exist or existed -- the *reasons*, not the task list
(open work lives in `../TODO.md` + `todo/`). The point is to not re-learn these the hard way,
and to not "fix" things that are physics rather than bugs. Distilled from since-deleted handoff
notes plus this codebase's recurring failure modes.

## Astrometry / polar alignment

### Near-pole plate-solve is noise-limited by geometry, not by a bug

At very high `|Dec|` (e.g. Dec = -89.97 deg) the live polar-align gauge reads ~1' peak-to-peak
of jitter, and RA appears to swing wildly (3h <-> 21h <-> 1h between consecutive solves). This is
**geometric, not a solver defect**:

- The J2000 unit vector at the pole has `Z ~ -1` and `X, Y` in the ~5e-4 range.
- Small CD-matrix centroid uncertainty from the catalog matcher (~0.5 px ~ 7" over ~120 inliers)
  propagates to ~5e-4 of unit-vector noise = ~1.7' of axis noise.
- RA is geometrically singular at the pole, so its *coordinate* value is unstable even when the
  underlying unit vector is steady. The live tracker sees the unit-vector noise; the RA readout is
  a red herring.

**What to do:** do not chase this with tighter matching tolerances. Median the recovered **axis
vector** (renormalised) over a short window -- that beats the noise down ~sqrt(N) without EWMA lag.
Median the axis (the quantity we care about), NOT the WCS center (medianing the RA-singular center
is what a reverted prototype got wrong). Tracked in `todo/sequencing.md` (polar align).

### Sidereal-frame transport: two timestamps are two J2000 frames

Two pointing vectors captured even seconds apart do **not** share a J2000 frame -- a
topocentric-fixed axis has a J2000 representation that rotates at the sidereal rate. Treating
`v1` (at T1) and `v2` (at T1+~16 s) as co-framed gave Phase A axis recovery a **4-5' sidereal
bias**. Fix: stamp the capture UTC and sidereal-back-rotate the later vector into the earlier
vector's frame before any geometric solve; anchor a single `_referenceUtc` so downstream
normalisation stays in that frame.

This is the same class of error as the fake-mount guide-render bug below: **sidereal is a frame
transform, never an additive offset.**

### Quad-match tolerance mixes units

`StarReferenceTable` quad invariants mix an **absolute-pixel** `Dist1` with **normalised** ratios
`Dist2..Dist6`. A single fixed `quadTolerance` therefore fails when absolute scale drifts (pole
rotation + user knob motion + catalog-driven seed centroids push `Dist1` past the gate) -- quads
that should match get rejected, and the fast path silently falls back to a full solve every tick.
Fix: sweep the tolerance (`FindOffsetAndRotationWithRetryAsync`) and accept the first affine that
passes `Matrix3x2Helper.Decompose` validation (mirror/scale/skew rejection); the Decompose check is
the real correctness gate, not the star count.

### Differential solvers accumulate; always align to a frozen seed

The first `IncrementalSolver` composed each frame's affine onto the *previous frame's output* WCS.
Errors compounded over ~30 frames into a ~5-10' systematic axis bias. The fix was to quad-match
every frame independently against the **frozen seed** reference, so per-frame plate noise is the
precision floor with no accumulation. General rule for any incremental/differential estimator:
re-anchor to an immutable reference, never to your own last output.

## Imaging / stretch pipeline

### CPU/GPU stretch mirror drifts silently

The stretch math runs twice (GLSL shader + CPU mirror for TUI/tests). They diverge silently unless
every stage is mirrored. Concrete bugs this produced: bisection direction inverted in
`ConvergeStretchFactor`; WB applied before shadow on GPU but shadows derived from pre-WB stats
(WB-reduced channels clamped to zero); LUT divisor `lut.Length-1` (CPU) vs hardcoded `32` (GLSL);
`stretchMode` enum mapped wrong so Unlinked hit the Luma path on GPU only. See the
"Stretch Pipeline: CPU/GPU Mirror" section in `../CLAUDE.md` for the contract that prevents this.

### Normalisation invalidates derived floors

`ScaleFloatValuesToUnitInPlace` sets `MaxValue = 1`. A MAD floor written as `invMax * 0.5f` then
collapses to `0.5` -- half the dynamic range -- pinning every masked MAD and driving shadows ~28x
too high. Lesson: floors/thresholds derived from `MaxValue` (or any pre-normalisation scale) break
the moment the image is rescaled. Use a fixed bin-width floor (`0.5/65535`) that is correct
regardless of normalisation state. (See also the `Image` mutability notes in `../CLAUDE.md` --
`ScaleFloatValuesToUnitInPlace` mutates in place and leaves the original `MaxValue` inconsistent.)

## GPU / rendering

### Dangling stack pointer via single-argument Vortice ctors

`new VkPipelineColorBlendStateCreateInfo(attachment)` stores `pAttachments = &attachment` pointing
at the constructor's stack frame, which is reclaimed on return. On strict drivers (Mesa lavapipe)
the garbage `VkBlendOp` produced fully black output; on ARM64 the stack happened to hold valid ops,
so it "worked." Always `stackalloc` the attachment array with a lifetime spanning the
`vkCreateGraphicsPipeline` call and set `pAttachments` explicitly. Recorded in memory
(`feedback_vkblend_dangling_ctor`); it bit `VkPipelineSet`, `VkFitsImagePipeline`, `VkSkyMapPipeline`.

## Dependency injection

### `Microsoft.Extensions.Logging` never resolves a non-generic `ILogger`

DI registers `ILogger<T>` (open generic) and `ILoggerFactory`, never `ILogger`. A ctor
`(Foo, ILogger? logger = null)` therefore silently gets `logger = null`, and every
`_logger?.LogDebug(...)` goes dark -- which is exactly how the `CatalogPlateSolver`-fails-on-drizzle
bug hid for weeks (no diagnostics fired). Use `ILogger<TSelf>` for direct resolution, or a factory
lambda when a non-generic `ILogger` ctor parameter must be preserved. Full writeup in the
"Plate Solving" section of `../CLAUDE.md`.

## Fake device simulation

### Sidereal baked into the fake mount's reported RA breaks the guide-loop render

`FakeMountDriver.GetRightAscensionAsync` returns `_ra + _accumulatedRaHours` where
`_accumulatedRaHours` includes the full sidereal advance. The hand-rolled `GuideLoopTests`
renderer drives the star from `(reportedRa - initialRa)`, so the simulated guide star races across
the frame at ~20 px per 2 s exposure -- past the 16 px tracker ROI -- and is lost after ~2 frames.
The neural-vs-P comparison consequently records only ~2 error samples over 360 frames and proves
nothing. A real tracking mount holds sky-RA roughly constant (sidereal is tracked out), so sidereal
must never be an additive term on reported RA. The coherent fix (believed/true seam, disturbances
as composable terms, sensor vs pointing stages) is designed in
[`architecture/fake-disturbance-model.md`](architecture/fake-disturbance-model.md).
