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

### A dataset session is one target through one FILTER, and the canonical filter name cannot say which

**Fixed 2026-08-02**, before it was ever observed: the build had only been exercised on the broadband
reference archive, and this would have surfaced on the first narrowband-bearing run of
`D:\Astro-Pics`. Recorded because the obvious fix is the wrong one, and because the same reasoning
will apply to whatever gets added to the session key next.

`SessionDiscovery.GroupSessions` keyed sessions on `(SessionDir, Instrument, Target)` with no filter.
The code argued against itself: the comment immediately above the key says a dated LIGHT folder
"routinely holds several pointings distinguished only by OBJECT, and mixing them would both break
registration and poison the session-relative star-count gate". Exactly the right reasoning, applied
to Target and not to Filter.

On a **mono narrowband archive** the consequences were concrete, because Ha and OIII of one target on
one night land in one folder under one OBJECT:

1. **The star-count gate sees a bimodal population.** `SessionFrameAnalyzer.ApplyGate` is MAD-based
   and session-relative, which is right, but OIII detects far fewer stars than Ha through equivalent
   filters. The OIII frames sit in the left tail and are rejected as `StarCountTooLow` for being a
   different filter rather than for being bad. `maxRejectFraction` caps the damage at 50%, which is
   still half a night of good data.
2. **`SessionRegistrar` integrates one master per session**, so Ha and OIII frames are stacked
   together. The result is not a line master and not anything else either. For an N2N dataset that is
   a corrupted training target, produced silently.
3. **Flats are filter-specific**, and `CalibrationResolver` picks the flat from `Lights[0]`, so a
   filter-mixed session calibrates everything against whichever filter happened to sort first.

**The trap: keying on the canonical filter name does not fix it.** The natural move is to copy what
`MasterGroupKey` compares on, which is `Filter.Name` plus `Bandpass`. That fails on real data, because
`Filter.FromName`'s patterns are **anchored** (`^\s*...\s*$`): `"Ha"` parses, but `"Ha 3nm"`,
`"OIII 3nm"` and `"Antlia ALP-T"` match nothing and all canonicalise to the single value
`Filter.Unknown`. A key built from the canonical name alone therefore merges Ha and OIII right back
together for precisely the archives the split exists to separate, while looking correct in any test
whose fixture spells its filters `"Ha"` and `"OIII"`.

The session key is `(SessionDir, Instrument, Target, FilterOf(frame))`, where `FilterOf` is the
canonical name when the header parsed and the **trimmed raw header text** when it did not.
`Bandpass` is deliberately absent: it is a function of the canonical name for every recognised
filter and `None` for every unrecognised one, so it partitions nothing the name does not.

**Identity is not interpretation, and the key deliberately stays on the identity side.**
`FilterCurveDatabase` ships 176 spectral curves with fuzzy name matching, and it is the right tool
for asking *what lines does this filter pass* (measure the throughput at 4861 / 5007 / 6563 / 6717 Å;
see [plans/narrowband-colour.md](plans/narrowband-colour.md)). It is the wrong tool for asking
*are these two frames the same filter*. Resolving the session key through it would make a pure,
synchronous grouping function depend on an async embedded-resource load, and fuzzy matching would let
two genuinely different filters that both land on one curve entry collapse into a single session,
which is the merge this whole entry is about.

Two properties worth keeping if this key changes again. **Over-splitting is the safe direction**: an
over-split session still registers to a valid master and any remainder below `MinSubsPerSession` is
dropped through a reported counter, whereas a merge corrupts a master silently. And **the id only
grows when the new field is present**, because `test-sessions.txt` is a stable per-id hash, so an id
that does not move cannot change train/test sets; every broadband session built before filters
entered the key keeps its exact id and its exact assignment.

**A frame with no `FILTER` card at all needs a declaration, not a better parser.** N.I.N.A. does not
model a hand-fitted filter, which is how a dual-band usually goes onto an OSC, so those frames carry
nothing to key on. `.tianwen-meta.json` (`FrameMetaSidecar`) declares it per directory, cascading
like `.gitignore`, applied at the frame source so lights and their flats learn it together. Format
and the reasoning behind fill-only semantics:
[plans/ai-denoise-deconv.md](plans/ai-denoise-deconv.md).

See [docs/plans/narrowband-colour.md](plans/narrowband-colour.md) for what the archive sweep is
otherwise wanted for.

### A narrowband stack has no colour path, and naive HOO is uniformly cyan by construction

Two separate things, both easily mistaken for a broken colour pipeline.

**SPCC is broadband-only.** `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` integrates
a Pickles SED against QE x CFA across the whole visible band. That is the right model for an OSC
broadband frame and the wrong one for a 3 nm passband, so an Ha/OIII/SII master gets no calibration
at all: the palette is whatever channel assignment plus per-channel autostretch produce. Do not
"fix" this by pointing a narrow passband at the existing SEDs: a Pickles template is a spectral
*type average* and cannot know whether a given star shows Ha in absorption or emission over 3 nm, so
it would return a confidently wrong answer rather than no answer.

**Naive HOO is rank-deficient.** `R = Ha`, `G = OIII`, `B = OIII` makes G and B the *same array*.
Two independent signals in a three-dimensional colour space means every OIII region lands on exactly
one hue (cyan), and no stretch, saturation, WB or curve can produce blue from it. If an HOO master
renders uniformly teal, the renderer is working correctly and the palette is the problem. The fix is
to introduce a third quantity, normally ~15% Ha mixed into blue standing for H-beta (the Balmer
decrement ties Hb to Ha; intrinsic ratio 2.86, with dust extinction accounting for the gap between
that 0.35 and the 15-20% used in practice).

Both are planned, with the algorithms and thirteen ADRs, in
[docs/plans/narrowband-colour.md](plans/narrowband-colour.md).

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
