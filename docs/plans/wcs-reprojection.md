# WCS-Driven Image Reprojection (undistort + mosaic stitching)

**Status: NOT STARTED.** Raised 2026-08-29, out of the [astropy-parity](astropy-parity.md) audit's
wide-angle-lens/large-mosaic discussion. `WCS.PixelToSky`/`SkyToPixel` (+ SIP) already exist as a
validated pixel<->sky mapping, built for astrometry and cataloging, but nothing in the pipeline
uses them *generatively*, to resample pixels. Two capabilities share this one plan because they
share the exact same underlying functions but differ in scope and in which resampling direction
(pull vs push) each needs.

## P0: single-frame undistort (de-SIP a frame)

Goal: given one solved frame (linear CD + SIP), produce an image whose own WCS becomes purely
linear (SIP zeroed) -- straight lines stay straight, with no dependency on any other frame.

- Needs only the pixel<->pixel SIP mapping; no sky/trig round-trip required (the sky center and CD
  matrix stay identical, only the distortion component is removed).
- Resampling must be **pull/inverse-warp**: for every output (linearized) pixel, apply the inverse
  SIP polynomial to find the corresponding input (distorted) pixel, then interpolate (bilinear at
  minimum; Lanczos/bicubic as an option) there. Never push/forward-splat a single, complete,
  continuously-defined source -- splatting only earns its keep when combining multiple *incomplete*
  sources (see the drizzle comparison below); on one complete frame it only risks unfilled output
  pixels, for no benefit.
- Reuses `SipPolynomial.Apply` exactly as-is; the new code is the resampling loop + interpolation
  kernel, not the math.

## P1: multi-frame reprojection onto a shared WCS (mosaic stitching)

Goal: combine N frames with *different* pointings (the true wide-angle/mosaic case, not
same-target stacking) onto one output canvas.

- For every canvas pixel: `PixelToSky` on the output WCS -> `SkyToPixel` on each input frame's own
  WCS to find where to sample -> interpolate -> blend/coadd (weighted by, e.g., inverse-variance or
  a feathered edge weight).
- This is the Montage/SWarp/`reproject`-style approach. Unlike this repo's existing stacking
  registration (frame-to-frame star-quad matching, deliberately independent of any WCS -- see
  CLAUDE.md's comet-integration notes: "registration ... never needed to know where the sky was"),
  this explicitly *needs* every input frame's own solved WCS, so it depends on P0's plate-solve
  quality and, for genuinely wide fields, on the WCS-projection-expansion gap
  ([astropy-parity.md](astropy-parity.md) ranked gap 3).
- **Open question, settle before implementing:** does TianWen want to own general-purpose
  flux-conserving reprojection, or is a simpler feathered blend adequate for the wide-angle/panorama
  use case (as opposed to precision photometric mosaicking)? Area-weighted, drizzle-grade flux
  conservation is a materially bigger undertaking than a feathered blend.

## Relationship to drizzle -- a different, deliberate choice of direction

`DrizzleStrategy.cs`/`DrizzleKernel.cs` (`Imaging/Stacking/`) is, confirmed by its own code, a
**push/forward-splat** algorithm: each input pixel is dropped as a `pixfrac`-sized footprint onto
the output grid, and a coverage map ("Coverage map doubles as the rejection map: per-channel
weight", `DrizzleStrategy.cs:271`) is accumulated alongside the flux and normalized (divided) at
the end. This is the *opposite* of the "always pull, never push" rule stated for P0/P1 above, and
deliberately so: drizzle's job is fundamentally different from resampling one complete image. It
combines many individually undersampled, sub-pixel-dithered frames, wants exact area/flux
conservation across that combination, and its "holes" (regions of partial or zero coverage) are the
honest, correct answer when the dither pattern does not provide enough coverage at the chosen
`pixfrac`/output scale -- not a defect to engineer away. That is precisely why the caller only picks
drizzle when `frameCount >= DrizzleOptions.MinFrameCount` (CLAUDE.md, "Deep-Sky Stacking" section),
falling back to AHD + sigma-clip otherwise: below that count, coverage cannot be trusted, so the
pipeline switches to the ordinary registration-and-resample (pull-based, one continuous source per
frame) path instead.

P0/P1 above resample a *single* already-complete source (an undistorted frame, or a finished master
being placed onto a mosaic canvas) rather than combining many incomplete ones, so pull is correct
and push would only introduce artificial holes for no benefit.

## Why this is not common in consumer astrophotography software

It is not that no software does this. SWarp (Astromatic), Montage, `reproject` (Astropy-affiliated)
and STScI's own AstroDrizzle all use exactly this WCS-driven pull-warp/reprojection mechanism, and
drizzle's own footprint mapping (above) is itself built on top of a WCS/distortion model in
professional pipelines. It is specifically **consumer single-target stacking tools** (PixInsight's
core stacking process, Siril, DeepSkyStacker, N.I.N.A.'s own stacking) that skip it, because for
same-pointing multi-sub combination: (a) direct star-quad/feature registration between frames is
cheaper than plate-solving every sub, (b) it works on subs that would not independently solve well
(few stars, low SNR) as long as they share features with a reference frame, and (c) at typical
narrow-field FOV, TAN+SIP and a plain affine/polynomial registration converge to the same answer
anyway, so there is no accuracy reason to pay for the heavier path. The WCS-reprojection route only
earns its keep once frames have genuinely different pointings (mosaics) or come from different
sessions/optics entirely -- exactly the wide-angle/mosaic case this plan targets -- and consumer
users typically handle that today by reaching for a *separate* dedicated tool (a panorama stitcher,
or a manual multi-step PixInsight mosaic script) rather than their main stacking engine. TianWen
having one pipeline that can do both -- quad-match for same-pointing stacking (already shipped) and
WCS-reprojection for cross-pointing mosaic assembly (this plan) -- would be unusual among consumer
tools.

## Dependencies / sequencing

- P0 depends on nothing new; it is a resampling loop over existing `SipPolynomial` math.
- P1 depends on P0's interpolation kernel, on solved-WCS quality for every tile (not just a
  reference frame), and, for wide fields, on expanding `WCS` beyond TAN
  ([astropy-parity.md](astropy-parity.md) ranked gap 3).
- Neither changes the existing same-target stacking path (`DrizzleStrategy`/AHD+sigma-clip); this
  is additive, for the mosaic/wide-angle case only.
