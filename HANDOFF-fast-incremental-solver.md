# Handoff: Fast incremental plate-solver for polar-align refining

## Goal

SharpCap refines polar alignment at **~30 Hz** with sub-pixel error
gauges. Our current loop sits at **~1 Hz**, and even with the recent
70% center-crop, plate solving is still the wall. Target: **<50 ms**
per refining iteration on a 60 MP frame, ideally **<20 ms**.

## Why a full plate solve every frame is overkill

During refining the mount is essentially stationary; the only motion is
the user's slow knob adjustments (a few arcsec/sec). Frame N+1 is
within a small Euclidean delta of frame N on the sensor. Yet we run the
full pipeline each iteration:

1. Background scan + star detection on the full image (`StarFinder`)
2. Pattern matching the detected asterism against the catalog
   (`CatalogPlateSolver` quad/triangle matching)
3. Affine fit
4. Validation (project catalog stars, measure residuals)

Step 2 is most of the cost on a near-pole field with sparse stars and
a tight search radius. We discard the matched star list at the end of
each call -- and rebuild it from scratch in the next call. SharpCap's
trick is to **carry forward the matched-star list across frames**.

## Proposed approach: incremental ("differential") solver

A separate solver path used only by `PolarAlignmentSession.RefineAsync`
(and possibly the live preview WCS during refining). API sketch:

```csharp
internal sealed class IncrementalSolver
{
    // Seed from a successful blind solve. Captures the matched
    // star list (image-pixel positions + catalog J2000 RA/Dec)
    // and the current WCS.
    public void Seed(Image img, PlateSolveResult blindSolve);

    // Refresh from the next frame. Returns null if too many anchor
    // stars were lost (-> fall back to a full blind solve).
    public PlateSolveResult? Refine(Image img, CancellationToken ct);
}
```

Internally, `Refine` does:

1. **Predict** each anchor star's new pixel position by projecting its
   J2000 RA/Dec through the previous WCS. (Or: simply use last frame's
   pixel position as the prior; movement is sub-pixel between frames.)
2. **Centroid in a small ROI** (e.g. 11x11 px) around each prediction.
   If the brightest pixel within the ROI is below SNR threshold, drop
   that anchor.
3. **Refit the affine** from surviving (image_px, J2000) pairs using
   weighted least squares -- same math as `CatalogPlateSolver` but
   without the matching step.
4. **Validate** by RMS residual; if it spikes (knob nudge too large,
   star drifted out of ROI), return null and let the caller fall back
   to a full solve.

Anchors are cheap to maintain: 20-50 stars, <2 KB.

## Why this is fast

| Step | Full solve | Incremental |
|------|-----------|-------------|
| Star detect (full image) | 100-300 ms | 0 ms |
| Catalog query / pattern match | 200-500 ms | 0 ms |
| ROI centroid (50 stars x 121 px) | -- | <5 ms |
| Affine least-squares (50 pts) | <1 ms | <1 ms |
| **Total** | **~700 ms** | **~10 ms** |

The ROI work is embarrassingly parallel (each anchor is independent);
`Parallel.For` over 50 anchors lands well under 5 ms on any modern CPU.

## Where to wire it in

- `PolarAlignmentSession.RefineAsync` (TianWen.Lib/Sequencing/PolarAlignment/PolarAlignmentSession.cs):
  - After Phase A's frame-2 succeeds, call `IncrementalSolver.Seed(...)`
    with that result.
  - In the refine loop, call `IncrementalSolver.Refine(...)` instead of
    `_source.CaptureAndSolveAsync(...)`. On null return, fall through
    to a full blind solve (re-seed afterward).
- The existing crop fallback (`currentCrop = 1.0` after 2 consecutive
  failures) becomes the "lost the star list, need to re-blind-solve"
  branch.
- `MainCameraCaptureSource.CaptureAndSolveAsync` should expose a way
  to request "capture only, skip the solver" so the incremental solver
  can run on the captured `Image` without re-doing capture work. Add a
  separate `ICaptureSource.CaptureAsync` returning the raw image and
  let the orchestrator choose its solver.

## Edge cases to handle

1. **User dramatically nudges a knob** -- anchors drift outside their
   ROIs. Detected via the residual spike; fall back to blind solve and
   re-seed. Worst-case latency = one normal solve (~700 ms), still
   acceptable as a recovery path.
2. **A passing satellite or seeing spike** moves a single anchor's
   centroid. The weighted-least-squares step should down-weight outliers
   (Huber loss or RANSAC). 5-sigma rejection is enough for polar align
   accuracy.
3. **Anchor stars saturate or drop below SNR** as exposure changes.
   Refresh the seed list every N successful refines (e.g. every 30).
4. **Mount actually slewed** (cancel + restart polar align): re-seed
   from a fresh blind solve. Already handled by the orchestrator's
   re-entry path.
5. **Image rotation** between frames is essentially zero during
   refining (no derotator motion, no field rotation when paused). Only
   translation + tiny scale change from refraction. Affine fit captures
   all of this.

## Tracking what already exists

- `StarFinder` (TianWen.Lib/Astrometry/StarFinder.cs) -- the full-image
  star detector. Has a `BitMatrix` star mask that could be reused as
  the anchor list source.
- `BoundingBoxes` / star centroid helpers somewhere in
  TianWen.Lib/Astrometry -- check for an already-extracted ROI centroid
  routine before writing a new one.
- `CatalogPlateSolver.AttachCDMatrix` does the affine -> WCS conversion;
  the incremental solver can reuse it.
- `WCS.SkyToPixel` for predicting anchor positions from previous WCS.

## Test plan

1. Unit-test `IncrementalSolver.Refine` against synthetic frames
   with known shifts (1 px, 5 px, 50 px, 200 px, full-FOV scramble).
2. Verify residual escalation triggers the fallback at the right
   threshold.
3. Property test: seed + 100 random sub-pixel shifts -> recovered WCS
   matches ground-truth within 0.1 px RMS.
4. Bench against `CatalogPlateSolver` on a representative polar-align
   FITS: incremental should be at least **20x faster**.
5. End-to-end via `PolarAlignmentSessionTests`: refining loop reaches
   IsSettled within the same number of ticks as before, just faster.

## Out of scope (for the first cut)

- Sub-pixel star centroiding via Gaussian fit. Plain weighted
  centroid is fine for arcmin-level polar alignment accuracy.
- Exposing the incremental solver as a generic `IPlateSolver`. It only
  works when there's a recent successful seed; live preview / FITS
  viewer / first probe should keep using the blind solver.
- Distortion (PV/SIP) terms. The polar-align FOV is small enough that
  pure affine is exact to sub-pixel.
