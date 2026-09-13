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

### The AI runner's linear/stretched auto-detect misreads a bright-sky master, on about 2.5 percent of this archive

`ChunkedNafnetRunner.NeedsStretch` is SAS Pro's heuristic: unit-scale the frame, take
`median(value - min)`, and call anything at or above `AiNafnetInputs.StretchAutoDetectMedianThreshold`
(0.125) already-stretched, skipping `ApplyInputStretch`. It is right for the shape of frame it was
designed around, where sky is a small fraction of the peak. **It is wrong for a linear master whose sky
is a large fraction of it**, and this archive has some.

Measured over all 79 retained masters of `2026-09-full` (2026-09-06): the statistic runs p5 0.0008,
p50 0.0044, p95 0.0449, so the typical master clears the threshold by nearly 3x. **Two exceed it.** The
worse one is a 120 s ASI585 stack whose raw median is 21,013 against a max of 66,060, so its sky sits at
32 percent of full scale with the stars clipped near saturation and a dynamic range of only about 3x. It
is genuinely linear; the heuristic simply cannot tell that apart from a stretched frame. The other sits
at 0.127 against the 0.125 bar, which is a knife-edge rather than a clear call.

**Consequences, and they differ by caller.** `DatasetDegradationExporter` REFUSES such a master
outright rather than exporting pairs from it, so the training path fails loudly and drops the session
(the message names the auto-detect). At INFERENCE nothing refuses: the stretch is skipped and the model
is fed the frame as-is. In this particular case the damage is smaller than it sounds, because the
unstretched median of 0.32 lands near the 0.25 the training tiles carry, so the LEVEL is roughly right
and only the nonlinear SHAPE differs; it is not the 100x level error of the H0 defect
([denoiser-training.md](plans/denoiser-training.md) fact 0). The effect has not been measured.

**Not repaired on a sample of two, and one obvious repair does not work.** The natural discriminator is
skew, since a linear astro frame's brightest pixels sit orders of magnitude above sky while a stretched
one's do not, but this master's bright end is CLIPPED, so its q99.9-to-median ratio is 1.85 and a
skew test would call it stretched too. Anything better needs more than two examples to be tuned on.

### SPCC's remaining error budget is the white-reference sub-type, and it is a few percent

`Tycho2ColorCalibration.WhiteReference` defines the spectrum that renders neutral and defaults to
`AverageSpiralGalaxy` = the SWIRE **Sb** template, matching PixInsight's and Siril's SPCC default.
It resolves a REAL spectrum by record name through `FilterCurveDatabase.TryGetSedByName`: the 25
SWIRE galaxy templates (Polletta et al.) are already embedded in `pickles_sed.gs.gz` alongside the
131 Pickles stellar ones. They are absent from the B-V index on purpose -- `PrecomputeSedBvIndex`
excludes `GALAXY_*` so a galaxy can never be matched to a *star's* colour index -- and that
exclusion is also what hid them: a white reference carried as a B-V could not reach them, so it had
to stand a B-V-matched star in for the galaxy.

Everything below was measured on one SMC OSC master (QHY294C / IMX492, 221x60s, 3837x2619), and the
ordering is the point -- it says where accuracy actually comes from:

| source of error | effect on the fit | status |
|-----------------|-------------------|--------|
| no white reference at all | the difference between a green cast and neutral | fixed |
| B-V-matched star vs the real Sb spectrum | R 0.527 -> 0.463, B 1.205 -> 1.300 | fixed |
| **spiral sub-type Sa / Sb / Sc** | **R 0.456-0.475 (4 %), B 1.256-1.333 (6 %)** | **dominant, open** |
| optical filter not in the header | **R -5.2 %, B -2.2 %** (measured with the REAL LPS-D3 curve) | fixed for this rig |
| clipped stars in the fit | R 1.7 %, B 1.6 % | fixed |
| aperture radius 4 -> 18 px | R 0.8 %, B 0.7 % -- flat | not a lever |

Two things follow. **The sub-type is the knob**, not the maths: PixInsight's exact "average spiral"
spectrum is not published, so if a PI fit on the same data disagrees by a few percent, reach for
`WhiteReference.SpiralSa` / `SpiralSc` before suspecting the integration. And **the white reference
makes SPCC much less sensitive to a missing filter curve** than it looks: before the reference
existed, naming this frame's IDAS LPS moved the fit by -17 % / +22 %; with it, the same rename moves
it -4.3 % / -2.6 %, because the reference is integrated through the *same* throughput and any
band-dependent factor common to both cancels in the division. Only the interaction between the
filter's notches and the differing spectral shapes survives. That is also why the photon-vs-energy
lambda weighting in `FilterCurve.IntegrateSedThroughput` does not bias the result -- but the
guarantee only holds while star and reference go through literally the same code, which is what
`IntegrateBandRatios` is for.

Reproducibility, as a cross-check on the whole chain: the same night's data at three stages of APP
processing -- full frame, cropped, and cropped + light-pollution-corrected + background-neutralised
-- fits R = 0.463 in all three (identical to 3 dp) and B within 1.3 %.

#### The filter curve we have is not the filter that took the data

The SMC frames above were shot through an **IDAS LPS-D3** (formerly NGS1). `filter_curves.gs.gz`
embeds **`IDAS_LPS_P3_LIGHT_POLLUTION`** and no D-series curve at all. P and D are different filters,
not spellings of one: per the vendor's own page the **D3 is a NOTCH filter**, suppressing OI 557.7 nm,
NaI 589.0 / 589.6 nm and OI 630.0 / 636.4 nm, where the P-series is a broad multi-band shaped to
preserve continuum colour. So the **-4.3 % / -2.6 % row above was measured with the P3 curve standing
in** and indicates the SCALE of an unmodelled LP filter, not the D3's actual effect. Do not quote it
as the latter.

Two ways to get this wrong, and the second is worse:

- **Writing `FILTER = 'IDAS LPS-D3'` resolves to nothing.** `TryMatchFilter` gates on
  `shared * 2 >= keyTokens.Count`, and `IDAS_LPS_P3_LIGHT_POLLUTION` tokenises to five
  (`idas lps p3 light pollution`) of which the D3 name shares two -- `p3` and `d3` are different
  tokens -- so 4 < 5 rejects it. The throughput falls back to sensor QE x CFA, which is exactly what
  the current `FILTER = 'RGB'` already produces. Rewriting the file changes no number.
- **Writing `FILTER = 'IDAS LPS-P3'` DOES resolve** (three of five tokens, passes the gate) and is the
  actively harmful option: SPCC would integrate a transmission curve the light never passed through
  and return a confidently wrong triple. Same failure shape as the phantom `CFA_R` -> `BAADER_R`
  fuzzy match that this session removed -- one token apart, entirely plausible-looking.

**That token gate is therefore load-bearing and is pinned by a test**, because loosening the matcher
(a natural-looking "be more forgiving about filter names" change) would silently start resolving D3
to P3. A near-miss must stay a miss.

#### FIXED: the D3 curve was digitised from the vendor chart, mechanically and checkably

The vendor publishes the spectrum only as a PNG plot, and a curve read off a chart BY EYE must never
be entered as if measured -- it would shape the entire colour calibration while looking
authoritative. So the extraction is a tool instead:
**`tools/digitize-filter-curve/digitize_filter_curve.py`**. It calibrates off the chart's own
gridlines (fitting a uniform grid rather than thresholding, which is what separates a gridline from a
legend border -- on this chart the gridlines run 410..1173 px and the legend's borders 637..638, so
length cannot tell them apart), selects the filter's black trace against the coloured lamp/line
traces, excludes the legend box (it holds a black line SAMPLE, ink of exactly the right colour at a
wavelength where the filter is opaque -- ~29 % at 760 nm, where the truth is 0), and takes the ink's
vertical centroid per column.

**It re-draws the result back onto the source chart** (`--overlay`), which is the only real proof, and
it validates against prose the vendor wrote rather than against the chart it read:

| vendor states blocked | extracted |
|---|---|
| OI 557.7 nm | 1.8 % |
| NaI 589.0 / 589.6 nm | 3.4 % / 2.6 % |
| OI 630.0 / 636.4 nm | 1.4 % / 10.9 % |

Peak 96.6 %, 852 samples over 349..1200 nm. Those five wavelengths took no part in building the curve
or calibrating the axes, so their landing in the notches tests the digitisation and the scaling
together. A mis-set axis range or an unconverted percent would not produce this.

**Local curves survive re-import, which is the part that needed designing.** `filter_curves.gs.gz` is
rebuilt WHOLESALE from upstream by `tools/import-sasp-data`, so a curve appended by hand would be
destroyed by the next import with no sign in the diff. Local additions therefore live as committed
CSVs in `tools/import-sasp-data/local-filters/` and are merged on every run; `--merge-only` rebuilds
from the existing file plus those CSVs with no upstream fetch. The CSVs are in CHART units (nm,
percent) so a row can be checked against the plot by eye, and the importer converts to the database
convention (**Angstrom, fraction 0-1**) once, with a guard that refuses a file already in fractions --
percent where a fraction is expected makes a filter 100x over-transmissive and every calibration from
it confidently wrong.

**Measured effect on the SMC master**, `FILTER` card patched in place to `IDAS LPS-D3`:

| | no filter term | with the D3 curve |
|---|---|---|
| SPCC fit | R 0.464, B 1.301 (585 stars) | **R 0.440, B 1.273** (563 stars) |
| sensor luma weights | 0.3550 / 0.4064 / 0.2386 | 0.2488 / 0.4562 / 0.2951 |

so **R -5.2 %, B -2.2 %** -- close to the -4.3 % / -2.6 % the P3 stand-in had predicted, which
retrospectively justifies that estimate as a scale indicator while not being the number itself. The
luma-weight shift is the independent confirmation that the curve reaches the throughput rather than
merely resolving by name: red falls and green/blue rise, which is what blocking the red OI lines and
the sodium doublet must do.

Two things the importer's own history left behind, both fixed: it resolved the repo root by counting
`".."` and was one short, so every defaulted path landed under `tools/` -- and
`Directory.CreateDirectory` then made an empty `tools/src/TianWen.Lib/Astrometry/Catalogs`, untracked
and so invisible to `git status`. The root is now found by searching upward for `src/TianWen.slnx`, a
FILE the real root has and a stray output directory cannot manufacture; a directory marker was tried
first and was defeated by that very decoy.

### FIXED: an auto-stretch that cancelled the white balance it was given

**Root cause: `Linked` replicated channel 0's STATS instead of sharing one CURVE.**
`StretchSolver.ComputeStretchUniforms` set `ch1 = ch2 = ch0` and then scaled each copy by *that
channel's own* WB multiplier, which yields three different curves whose anchors move in lockstep with
the multipliers they are meant to reveal. Channel c's curve was fitted so `median0 * wb_c` lands on
the stretch target while its data arrives as `median_c * wb_c`, so the rendered ratio came out as
`median_c / median0` -- `wb_c` divided out exactly. A white balance had no effect on a linked render.
`MasterPreviewRenderer` compounded it by rendering `Unlinked`, which absorbs a per-channel gain by
design.

Measured on the SMC master, downsampled to 960x655, before the fix:

| WB handed to the renderer | rendered mean R,G,B | p99 | p99.9 |
|---------------------------|--------------------|-----|-------|
| 1.003 / 1 / 0.999 | 28.31 28.72 27.70 | 71 74 70 | 147 147 144 |
| 0.341 / 1 / 0.850 | 28.29 28.72 27.70 | 71 74 70 | 146 147 143 |
| 0.536 / 1 / 1.186 | 28.30 28.72 27.70 | 71 74 70 | 146 147 144 |

Three very different white balances rendering to within 0.02 of a byte of each other, so SPCC's
colour never reached the display and anything judging a WB change from one of these PNGs was
measuring nothing. The evidence is deliberately an internal A/B -- same renderer, same file, three
WBs -- because no external image can settle it: a finished edit carries a hand-chosen stretch, star
separation and saturation work, so its channel statistics say nothing about what an auto-render
should produce. **Do not use a finished edit as a target for this renderer.**

**The fix, in three parts.** `Linked` now derives ONE curve from the mean of the per-channel
WB-applied medians and MADs and writes it into all three uniform slots, which is PixInsight's and
Siril's linked STF; the shader needed no change, because Linked and Unlinked always differed only in
the uniforms. `MasterPreviewRenderer` renders `Linked`. And `ViewerActions.DefaultStretchMode` is
`Linked`, so a fresh viewer shows the calibration instead of a mode that discards it. Pinned by
`StretchLinkedWhiteBalanceTests`.

**`Unlinked` still absorbs the auto calibration, and that is correct.** A per-channel
auto-normalising curve neutralises the background, which is the entire purpose of an unlinked
stretch; the MANUAL white balance survives there because only the AUTO half scales the stats (see the
`shaderWhiteBalance` split in `StretchSolver`). The two modes now differ in behaviour rather than
merely in which stats they copy, which is what makes the PixInsight names finally mean the same thing
here as they do there.

**FIXED, and found by the fix above: background neutralisation ignored the white balance.** Once the
calibration reached the display, the SMC master rendered visibly, wrongly BLUE -- and pressing NeutBg
did nothing, reporting gains of `1.00 / 1.00 / 1.00`. Both facts had one cause. The gains run BEFORE
the WB multiply (`pedestal -> bg-neut -> WB -> curve`), and `ComputeGains` honoured its
`whiteBalance` argument for `MinPivot` only -- Mean, the default, ignored it. So on a master whose
background APP had already equalised, Mean correctly answered "already neutral", and the SPCC triple
then took the post-WB background to a **2.66x blue-over-red** imbalance:

| | pre-WB bg | x WB | post-WB bg |
|---|---|---|---|
| R | 0.0019 | 0.464 | 0.000882 |
| G | 0.0020 | 1.000 | 0.002000 |
| B | 0.0018 | 1.301 | 0.002342 |

Every method now solves for a neutral POST-WB background: the method picks a pivot LEVEL over the
WB-applied backgrounds and each channel's target is that level divided back through its own
multiplier. On the numbers above this lands all three on 0.001741 exactly (B/R = 1.0000). A neutral
or absent WB reduces to the previous arithmetic bit-for-bit, so an uncalibrated image is untouched.
`AstroImageDocument` passes its calibration in, keys the per-method gain cache on **(method, WB)**
(keyed on method alone it would serve gains solved for a stale triple -- the same
stale-cached-projection shape as a palette-derived texture outliving a theme switch), and a
calibration landing after a neutralisation re-solves it. Pinned by `BackgroundNeutralizationTests`.

**The gain readout is F4, not F2, and that is load-bearing.** The gain is affine about 1.0
(`out = v*g + (1-g)`) while a sky background sits near 0.002, so the gain that fixes a 2.66x cast is
`(0.9981, 1.0003, 1.0005)` -- three `1.00`s at two decimals. The readout said "this did nothing" over
an image it had visibly just fixed, which cost real time in diagnosis.

**The remaining order note:** the industry sequence is neutralise, calibrate, then stretch linked,
and `MasterPreviewRenderer` follows it (its per-channel stats are pre-folded into post-bg-neut space
before the shared curve is derived).

**Still open: TianWen's WB triples are not directly comparable with PixInsight's digit-for-digit.**
PI applies its own normalisation, so establish both conventions before reading a disagreement as an
error in either. The white-reference sub-type remains the largest term in our own error budget (Sa /
Sb / Sc span 4 % in R and 6 % in B).

The master these were measured on (`*-lpc-cbg.fits`) is **linear**: 99.7 % of its pixels sit within
1 % of the sky floor, the median is at 0.046 of full range and only the top 0.01 % passes 0.34. APP's
"cbg" is a calibrated *background* (light-pollution correction plus background neutralisation, and it
makes the three channel backgrounds identical to 5 decimal places), not a stretch -- which is exactly
why the sky-background gray-world fallback measured (1.003, 1.000, 0.999) on it and looked like a
successful calibration.

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
`FilterCurveDatabase` ships 180 spectral curves with fuzzy name matching, and it is the right tool
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

### FIXED: a brand token alone won the fuzzy filter match, so a duo-band resolved to a dichroic

**Root cause: the coverage gate only ever asked about the KEY, and a two-token key is BRAND +
CHANNEL.** `FilterCurveDatabase.TryMatchFilter` scored a candidate on shared tokens and admitted it
when `shared * 2 >= keyTokens.Count`. For a key like `OPTOLONG_B` that is two tokens, so matching the
brand alone satisfied it, and the score penalised only the key's *extra* tokens -- never the needle's.
The needle's most discriminating word could match nothing at no cost:

| written `FILTER` card | resolved to | what that curve actually is |
|---|---|---|
| `Optolong L-eNhance` | `OPTOLONG_B` | a broadband blue LRGB dichroic, for a dual-band Ha+OIII |
| `Optolong L-eXtreme` | `OPTOLONG_B` | same |
| `Optolong L-Ultimate` | `OPTOLONG_B` | same |
| `Optolong L-Quad Enhance` | `OPTOLONG_B` | a filter the database does not carry at all |
| `IDAS` | `IDAS_NBZ` | whichever IDAS curve had fewest tokens, so a dual-band for a bare brand |
| `CFA_R` | `BAADER_R` | a mono dichroic, put into a modelled OSC throughput |

The correct entries lose because they are *longer*: `SONY_CMOS_B-UVIRCUT_/_OPT._L-ENHANCE` is seven
tokens, so `Optolong L-eNhance` covers two of seven and the half-coverage gate rejects it, leaving the
field to the brand's two-token LRGB curves. The bare-token forms (`L-eNhance` with no brand) were
already tested and already returned false; adding the brand is what flipped it, and no test wrote the
brand.

**This is the bad failure mode, not the mild one.** A missing curve makes SPCC decline, which is
visible. A wrong curve is used as if it described the glass in the light path: the `CFA_R` instance
skewed a real SPCC fit until it was found by hand, and it is recorded in
`StretchTests_NewPipeline` as one of four causes that "each moved the fit".

**A second route to the same wrong answer, found by adding a curve.** `OPTOLONG_L_QUAD_ENHANCE`
captured L-eNhance, L-eXtreme and L-Ultimate, and `OPTOLONG_L_ULTIMATE` would capture L-eNhance and
L-eXtreme -- not through a two-token key this time, but because `optolong` plus the single letter `l`
already clears half-coverage on a three- or four-token key. Two gates, because one does not cover the
other:

| gate | rejects because | catches |
|---|---|---|
| document frequency | an unmatched KEY token names exactly one curve, so it is what makes that curve specific | `L-eNhance` -> L-Quad Enhance (`quad` names one curve) |
| two-sided token difference | needle has a token the key lacks AND key has one the needle lacks, so the names diverge | `L-eNhance` -> L-Ultimate (`ultimate` names SEVEN, so frequency is silent) |

Frequency is measured over the catalogue rather than hand-listed, because the distinction is not
lexical: `idas` unmatched by `LPS-D3` must be ALLOWED (three curves, a brand), `light`/`pollution`
unmatched by `IDAS LPS P3` must be allowed (two each, a series suffix), `quad` names one.

The two-sided rule is far narrower than it sounds, because **a one-sided difference still resolves in
both directions** -- a name that says LESS (`LPS-D3` leaves `{idas}`, `Askar D1` leaves
`{colourmagic}`) and a name that says MORE, which is what a real filter-wheel slot looks like
(`Baader R CCD 31mm` leaves `{ccd, 31, mm}`). Single-character tokens deliberately COUNT: `Baader B`
against `BAADER_R` is `{b}` versus `{r}`, exactly the divergence that must be refused. And a
tokenisation artifact cannot trigger it, because `Askar Colour Magic D1` normalises to the curve's own
name and returns on the exact path first.

**Fix:** a key of two tokens or fewer must be covered in FULL. The bare-channel-letter path (a needle
of `R` or `Ha`, which shares no token with any key but ends one) is explicitly exempt, since one
token is all it ever had to offer. Pinned by `ABrandTokenAloneIsNotAFilterMatch` over all six rows
above, by `AOneSidedTokenDifferenceStillResolves` / `ATwoSidedTokenDifferenceIsRefused`, and by
`TheColourMagicDuoBandsPassTheirOwnLineAndBlockTheOther`, which pins the names beside the physics.
The "second route" above (a new curve capturing its own siblings) is pinned separately by
`AddingASpecificProductDoesNotCaptureItsSiblings` -- re-run `ReportKnownLightPollutionFilters` after
adding any curve and read every line, including ones you did not touch.

**Optolong's duo-bands genuinely are not in the database except pre-convolved with a sensor**
(`SONY_CMOS_*-UVIRCUT` / `CANON_FULL_SPECTRUM_*` x L-eNhance / L-eXtreme / L-ULTIMATE), so "no match"
is the honest answer for a bare **L-eXtreme**. Standalone light-pollution / duo-band coverage is
`IDAS_LPS_D3`, `IDAS_NBZ`, `ASKAR_COLOURMAGIC_D1` (OIII+Ha), `ASKAR_COLOURMAGIC_D2` (OIII+SII),
`OPTOLONG_L_QUAD_ENHANCE` (quad-band), `OPTOLONG_L_ULTIMATE` (dual 3 nm) and `OPTOLONG_L_ENHANCE`
(tri-line) -- **all seven digitised here** from vendor charts by `tools/digitize-filter-curve/`, the
chart-unit CSVs under `tools/import-sasp-data/local-filters/` (see the `digitize-filter` skill for the
chart families, the gates and the retraction manifest). Upstream adds only
`IDAS_LPS_P3_LIGHT_POLLUTION`, `OPTOLONG_L-PRO_LIGHT_POLLUTION` and `SVBONY_SV260`.

### L-eNhance is TRI-LINE, and Hb 486.1 is the one wavelength that identifies it

Not a labelling nicety: its blue window is **23 nm** wide (the vendor annotates it "FWHM OIII&Hb"), so
the channel that looks like OIII carries OIII **plus H-beta summed together**, and anything unmixing an
OSC frame shot through it on a strictly two-line Ha/OIII model is solving the wrong system (see
[plans/narrowband-colour.md](plans/narrowband-colour.md)). Nor is the band flat: **Hb 486.1 reads
96.4% against OIII 500.7's 85.9%**, because the band centres near 490 and 500.7 sits on its falling
shoulder.

**Hb 486.1 is also the identity check against L-Ultimate**, whose 3 nm blue band reads **0.0%** there.
Optolong have published charts under the L-Ultimate name that are actually L-eNhance, and that one
wavelength separates them. Pinned as a pair by `TheEnhanceIsTriLineAndPassesHBeta` and
`TheUltimateIsTwoNarrowBandsAndDoesNotReachHBeta`.

**This is why the ZOOMED charts matter.** At the ~1 px/nm of a full-range chart you cannot tell whether
Hb falls inside the blue band; at 9 px/nm you can. A wide chart yields a curve that looks fine and
loses the one fact that distinguishes the filter. The cost: L-eNhance has no full-range chart, so its
out-of-band is ASSERTED (zeros at 350/460/525/630/680/800) rather than measured -- each band is
bracketed by measured zeros, but UV/IR leakage is invisible to it.

SPCC declines on the two ColourMagic curves, and **the curve is not what is missing** -- the SED
library is (see the narrowband entry below). They are here for sensor-matched luma weights, for the
narrowband colour work where which line lands in which CFA channel is the whole question, and as the
pre-convolved response a duo-band OSC frame must be modelled through rather than the bare CFA.

### FIXED: `LoadAsync` built its task as the `CompareExchange` argument, so every racing caller loaded

**Root cause: an argument is evaluated before the call it is passed to.**

```csharp
var existing = Interlocked.CompareExchange(ref _loadTask, Task.Run(() => DoLoad(ct)), null);
if (existing is not null) return new ValueTask(existing);
```

`Task.Run(...)` runs whether or not the CAS wins. A loser returned the winner's task -- correctly
waiting for a complete load -- and then let its own `DoLoad` run on in the background, where
`ImmutableInterlocked.Update(..., current.AddRange(incoming), ...)` **appended** a second copy of
every curve. Two concurrent callers turned 180 filters into 360 and 16 sensor curves into 32. Not
merely a wrong count: `TryMatchFilter` enumerates `_allFilters`, so every candidate was scored twice.

And the flag led the data. `Interlocked.Exchange(ref _loaded, 1)` was reached by the CAS winner
*before* `DoLoad` had run, so `IsLoaded` answered true over an empty database -- the same shape as the
viewer's SPCC guard declining against a database nobody had loaded.

**Fix:** publish a `TaskCompletionSource` placeholder first, do the work behind it, and raise
`_loaded` only once the data is there; the two accumulating arrays now REPLACE rather than append, so
they cannot double however they are entered.

**Why it survived a green suite: the count tests only fail on the interleavings that race.** The full
suite passed at 178/16 the run before, and failed at 356/32 under a narrower `--filter` that happened
to schedule two loaders together. The invariant is *duplicate-freeness*, which holds under every
interleaving, so that is what `ConcurrentLoadsLeaveNoDuplicateCurves` asserts -- eight concurrent
loads, then distinct names. A count is one consequence of the invariant, not the invariant.

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

### An uncalibrated master flat under-corrects by its own offset fraction (FIXED 2026-08-03)

A recorded flat is `offset + signal`. `BuildFlatMaster` normalised each frame to mean=1 and
medianed them, which divides the offset in, so the master described a flatter field than the
illumination it stood for and the correction applied to the lights was scaled by
`signal / (offset + signal)`. Measured on a real ASI533MC Pro frame: bias 788 ADU under a flat at
38,912, so 2.03%, leaving about 0.41% of a 20% corner vignette uncorrected.

Two reasons it went unnoticed for so long, both worth remembering. Half a percent of residual
vignetting is genuinely invisible in a stretched picture, so no amount of looking at output would
have found it. And the archive's dark-flats made the gap look filled: 17,697 of them were scanned,
grouped and cached by every run, and never handed to a builder, so the folder listing said the
calibration existed.

It matters for the training set more than for a picture. The residual is a smooth,
position-dependent multiplicative error that is **identical in every sub of a session**, so it
survives every Noise2Noise pair intact and is exactly the kind of structure a denoiser learns as
signal rather than removes.

Fixed by subtracting a master bias from each flat before normalising. Bias rather than dark-flat
because at flat exposures they are the same measurement (a 1.09 s dark-flat medians 784 against the
bias's 788, dark current over a second on a cooled sensor being nil) and bias needs no exposure
match. See `MasterFrameBuilder.BuildFlatMaster`, pinned by `MasterFrameBuilderTests`.

### Some dark-flats are recorded as `IMAGETYP='DARK'`

On the reference archive, 2,220 dark-flat frames sit in a `DARKFLAT` folder while their header says
`DARK` (against 17,697 that say `DARKFLAT`), and in the Vela tree it is all 340 of them. The tell is
the exposure: they match the flats to the millisecond (4.46 s, 4.61 s, 1.09 s), and a
`MASTERDARKFLAT` exists at those same exposures. It is a capture-time configuration, not a bug in
anything we own.

**It is currently harmless, and only just.** `CalibrationResolver.BestDark` gates candidates to a
0.5x to 2.0x exposure window whose comment says it excludes dark-flats, and no camera in the archive
has a mislabelled dark-flat inside that window for any of its lights. The closest is ASI533MC Pro:
6.7 s mislabelled darks against 15 s lights, which is 0.45x, under the cutoff by 0.05. SV605CC has
them at exactly 10 s and 15 s but no lights below 60 s.

**Do not narrow that window** without re-checking this, and do not "fix" the labels by reclassifying
a short `DARK` as a dark-flat on exposure alone: a genuine short dark library would be
indistinguishable. Preferring an exposure-matched dark-flat as the flat pedestal is the change that
would make the labels start to matter; bias was chosen partly to avoid depending on them.

### A `PsfKernel` narrower than about 1.5 px does not blur by its label

`PsfKernel.Build` evaluates the Moffat (or Gaussian) at pixel CENTRES and renormalises the truncated
taps. That is exact enough from 2 px FWHM up (the composed width lands within three percent of the
continuous profile's), and wrong below it: a nominal 1 px beta-4 Moffat puts 65 percent of its mass in
one pixel and widens a 2.15 px core as a 0.73 px continuous kernel would (0.59 on a 1.53 px core, 0.79
on 2.81), and a nominal 0.5 px kernel is a near-delta worth 0.1 px. Area sampling does not rescue it
(4x4 gets 1 px to 0.91 to 1.07 and 0.5 px to 0.56 to 0.81), because a half-pixel profile has no
representation on the pixel grid at all. Measured 2026-09-07 by composing the sampled taps with a
continuous core (`docs/plans/deconvolver-training.md`, E1d).

**What it did.** The estimated-kernel oracle probe read its width estimates against the nominal
width and reported a 0.65 under-read at 1.1 to 1.3x blur, while quadrature "read 0.92" there only
because its own 1.3x over-read cancelled the kernel's 0.7x under-delivery; against the width applied
the composed estimate was within 0.92 to 1.07. E1b's and E1d's light-end bins were run at effective
blur ratios nearer 1.12 and 1.005 than their labels. **What it did not do:** the trainer conditions on
`Psf01Estimated`, measured on the degraded cell, so no training label carries the error.

**Where it stands.** `MoffatComposition.ComposedFwhm(core, beta, PsfKernel)` composes with the kernel
as sampled and `EffectiveKernelFwhm` inverts it; the probe prints `effK` and `estW/e`; the exporter's
training-only `Psf01FromKernel` composes with the kernel applied. Still open: the exporter DRAWS a
nominal width, so a draw under about 1.5 px realises a lighter blur than `degradations.jsonl` records
and the light end of the training distribution is lighter than intended; drawing the blur RATIO and
solving for the kernel is the fix, and needs a re-export to take effect. The general rule is the
same one denoiser-training H8 learned on a point-sampled Gaussian: the truth of an injected blur is
the kernel APPLIED, and a sub-2 px result must never be read against a label.

### SharpCap frames carry no site, so a per-sub quantity that needs one is NaN for a third of the archive

Every SharpCap capture in the archive (all 27 sessions checked, 4.0 and 4.1) writes `OBJCTRA`,
`OBJCTDEC`, `RA`, `DEC` and `DATE-OBS` and **no `SITELAT` / `SITELONG` / `SITEELEV`**; N.I.N.A. writes
all of them. `SiteContext.Airmass` answers NaN without a site, so `SessionPsf.SubAirmass` is NaN for
those 27 of 79 sessions and E2.9's per-session fit could not run on them (`docs/plans/
deconvolver-training.md`, E2.9). SharpCap 4.1 writes an `AIRMASS` card of its own (15 of the 27), 4.0
did not (the nine Vela SNR panels, Omega Cen, the 2022 Eta Car, the SII Eta Car).

**Why the card can stand in, and only where it does.** On the 52 sessions carrying both, the computed
air mass and the card agree to a median 0.001 to 0.015 (one 0.034, one 0.311 on a session whose folder
and `OBJECT` name different stars), so `tools/psf-airmass-report.py --airmass either` uses the card
where the computation has no site, labelled as such. The store itself still records the card only as
a cross-check (`SubHeaderAirmass`), never in `SubAirmass`. **Owed:** a site fallback for the dataset
build (the profile's site, or a `--site lat,lon` switch) so the computed value exists for SharpCap
sessions too; sixty minutes of measure stage once built. Check the header inventory of every capture
software in an archive before pre-registering a per-sub computed quantity.

### A night whose cooler drifts across a degree stacks as several masters, and two filters of one target share a master file name

`LightGroupKey` wraps `MasterGroupKey`, whose temperature is `CCD-TEMP` rounded to the degree, so
`tianwen stack` partitions lights by that integer: the Great Orion Nebula session of 2025-10-15
(SV605CC, 13.7 to 12.1 C over the night) came out as three masters of 49, 18 and 4 frames, each with
its own reference frame and canvas, and the L-Ultimate Orion night beside it (6 to 8 C) as three more.
Nothing downstream can put those back together, since the references differ. Found 2026-09-07 when
E2.10 needed one manifest for one night (`docs/plans/deconvolver-training.md`, E2.10a).

**Fixed as an opt-in.** `--group-temp-tolerance <C>` (`StackingOptions.LightGroupTemperatureToleranceC`,
default 0 so every existing invocation groups exactly as before) sorts a target's frames by temperature
and cuts only where consecutive readings are further apart than the tolerance, so a drift stays whole
and a different night's 8 C still separates; the cluster's key carries its rounded median temperature
for the dark match and the slug. The default is not changed because the rounding is also what pairs a
group with a dark at its temperature, and a wider default would silently widen every dark match.

**Still open: the LIGHT slug carries no filter.** `MasterGroupKey.Slug` appends the filter only for
flats, so two light groups that differ ONLY by filter (same object, exposure, temperature, gain) map to
one file name, `master_<object>_light_<exp>s_<T>C_g<gain>.fits`, and the second overwrites the first
in one run. The grouping itself is correct (the filter is in the KEY); only the name collides, and a
`--group-filter` cannot separate them either. Adding the filter to the light slug changes every light
master's file name, so it is recorded rather than done here.

### The per-sub width in the PSF store is the registration detector's, and on an OSC frame it does not read seeing

`SessionPsf.SubFwhm` (and the quality gate's `FrameMetrics.MedianFwhm`) come from the one detect site,
`FrameRegistration.DetectAsync`, which measures on the PRE-DEBAYER mosaic through the mono path, for
registration reasons that stand (a debayered plane manufactures spurious detections; the mosaic keeps
the choice filter-independent). As a WIDTH on an OSC frame that measure reads a floor: on the Great
Orion Nebula 2025-10-15 night every sub reads exactly 1.70 px there, the sharp ones and the one both
debayers put at 2.8 to 2.9 px, while the store holds 1.71 to 2.60 for the same subs, which is that
floor plus what the 2000-star retry does to a median. The debayered GREEN plane's bright-star fit
ranks the same six subs 1.7 to 2.5 px in the order the sky did. Found 2026-09-07 when a seeing split
ranked on the store put the night's softest frame in the sharp third
(`docs/plans/deconvolver-training.md`, E2.10a); E2.9's air-mass slopes were computed on the same
column and are withdrawn to inconclusive.

**Two things it is not.** It is not a registration problem: the detector's positions are fine, and
that is what it is for. And it is not a mono-camera problem: a mono frame has no mosaic and the width
is a width. **Owed:** a `SubFwhmGreen` column from `PsfProfileFit` on the debayered green plane at the
mosaic detections' positions (no second detection), filled by `--remeasure-subs`; until then, rank
OSC subs on nothing in the store, and treat a per-channel width on a debayered OSC sub as a property
of the interpolation (AHD and VNG disagree by two on red).

**Corrected 2026-09-07 evening: the 1.70 is not the mono path's floor.** The unmoved detections of
that night (residual warm pixels, half of every list) read FWHM 1.69 to 1.71 and HFD 1.70 to 1.71 on
the mono plane, the stars 2.55 to 2.86 and 3.38 to 3.75; a median over such a list is the warm pixels'
width, and that is what the store, the estimator and the gate reported for every sub. The mono path
reads a star's width, widened by the fold; it is the LIST that is wrong (two entries down). The
`SubFwhmGreen` column exists now, and its fit refuses on exactly the sessions whose lists are half warm
pixels, so the owed item is the detector guard, not another column.

### FIXED: the registration refiner averaged sensor-fixed detections into the shift, halving it under 5 px of drift (2026-09-07)

`RegistrationRefiner` closes the sub-pixel residual the quad match leaves by pairing each detection
with its nearest reference detection within 5 px and fitting a Procrustes over the pairs. A detection
fixed to the sensor (a residual warm pixel: the Orion 2025-10-15 group's dark was a -5 C one under
12 C lights, and the 2000-star retry lowers the detection threshold until it reaches them) is in both
lists at the same raw position, so under the bulk affine it pairs with its own copy at a residual of
minus the frame's drift, and a least-squares fit over stars and copies together lands between them.
With half the list warm pixels the refined shift was half the bulk one on every frame within 5 px of
the reference (frame 0036: -1.72 px for a true -3.57), the refine RMS read 0.9 to 1.9 px where it
should read 0.3, and every stack of the night sat at 2.7 px on green from subs of 1.7 to 2.5: a sum of
frames each misplaced by half its own drift, which grows with the frame count and looked like a
resampling cost. The bulk quad solution (RANSAC over the brightest quads) was right throughout.

Fixed by `UnmovedTolerancePx`: a pair whose raw positions coincide within 0.35 px while the bulk
affine moved the detection by more than 0.7 px is dropped and counted (`N unmoved dropped` in the
register log). **What remains:** a frame that drifted under 0.7 px cannot be separated by position,
keeps them, and is biased by up to half its drift; and the count is a calibration diagnostic in its own
right (hundreds a frame mean the dark did not match). The measurement, the three probes and the
pre-registered validation: `docs/plans/deconvolver-training.md`, E2.10a, "the third finding placed".
Every master stacked before the fix from a well-guided night with residual warm pixels carries this
blur, the retained dataset masters included.

### A single warm photosite passes the star detector on an OSC mosaic

On an RGGB frame `Image.FindStarsAsync` measures on a `BilinearMono` fold of the mosaic, which turns one
hot photosite into a 2 by 2 blob of a quarter of its excess; that blob has an HFD near 1 px and passes
the detector's size floor (`HFD > 0.8`, "at least 2 pixels in size"), so a residual warm pixel is a star
to every consumer of the list: the registration (fixed above, downstream of it), the quality gate's HFD
and FWHM medians and the reference pick, the PSF store's per-sub fit (`SubFwhmGreen` refused on 0 of
60, 78 and 84 subs of the three warmest SV605CC sessions, against 42 to 100 percent elsewhere), and
the plate solver's candidate list. The 2000-star retry makes it worse: on a field with 700 real stars
it lowers the threshold until warm pixels fill the list. **Owed:** a guard in the RGGB branch of
`DetectStarsAsync` on the raw mosaic (a detection whose flux sits in one photosite is not a star),
pre-registered and measured on the Orion night's moved and unmoved populations before it ships,
because it touches every consumer of the star list and the fixtures pin star counts. **Shipped the
same evening** (`Image.SinglePhotositeFractionMax`, 0.85): the real RGGB fixture loses 1.2 percent of
its detections, all of them the narrow spikes; on the Orion frames the lists halve and the unmoved
pairs fall from hundreds to a handful. **The store re-measure with the guarded detector (79 sessions,
8,507 subs, 96 minutes, the same evening) gave two of the three sessions their green fits back and
not the third:** Orion L-Quad 2025-10-15 fits 55 of 68 (green 1.82 / 2.17 / 2.46 px at p10 / p50 /
p90, the widest within-night spread in the archive at 1.36), Orion L-Ultimate 2025-10-14 64 of 77,
Tarantula L-Ultimate 2025-10-14 still 0 of 84; the archive's green fit fraction moved 73 to 74
percent (5,987 of 8,037), E2.9's slopes are unchanged (1 of 78 sessions at a 1.3x air-mass span),
and E2.10's candidate list grew from 14 to 19 with the Orion L-Quad night now its best pair (1.89
against 2.38 px, 1.26x). **What the third session shows, measured on its own subs
(`ReportWhyTheGreenFitRefusesASessionsSubs`, `C:/temp/e2/green-fit-refusals-tarantula-*.txt`):** a
population the 0.85 guard does not reach. On that night (sensor 12.7 to 9.0 C, no dark warmer than
-5 C) the fit refuses `PoorFit` at a log residual of 0.5 to 1.0 on the warm early subs and passes at
0.26 to 0.31 once the sensor has cooled, raw or calibrated alike; the stacked profile it refused is a
spike (0.04 of peak at 0.9 px against 0.21 on a passing sub) with a bump at 1.5 px, which is a hot
photosite as VNG renders it on the green plane, not a star. The detections the guard KEPT split by
their peak-photosite share into a class under 0.5 (about 1,000 to 1,200 a frame, stable through the
night: the stars) and a class between 0.5 and 0.85 (2,260 on the 12.7 C sub, 1,076 at 9.0 C: warm
pixels whose eight neighbours' noise pulls the share under the threshold, and pairs). The cooled
sister night (-10 C) carries as many of the second class and fits anyway, because there they are
faint; at 12.7 C they are an order of magnitude brighter and the profile fit stacks its 400 brightest
by PEAK over the signal floor, so they fill it. The single-photosite guard was measured on the
unmoved-versus-moved pairing, which admits only detections bright enough to pair, so this faint
class never entered the measurement. **Owed:** a guard that is noise-aware (the neighbours'
background-subtracted sum against their noise, not a fixed share), or the fit's stack ranked by flux
rather than peak, or both; pre-register with the share classes above as the readout, on this night,
its cooled sister and an Orion warm night (where the class is 15 to 53 a frame on L-Quad and 103 to
142 on L-Ultimate).

### A `--manifest` stack's output manifest re-listed the whole group, not the frames it stacked (FIXED 2026-09-13)

`tianwen stack --manifest <third>` integrates only the manifest's frames (`STACK_N` 79 and 80 on
E2.10b's thirds of a 244-frame group, 2026-09-07) but wrote an output manifest listing all 244: every
group frame the input manifest did not list as matched was recorded as a quality reject, as though
this run had considered it. It had not; the selection was the input manifest's, and the written
manifest's own contract separates "considered and rejected" from "never offered". Those frames are
no longer listed, so a downstream split reads the stacked set (pinned by
`StackingPipelineRgbBayerSyntheticTest.AManifestStackWritesTheFramesItStackedNotTheGroup`). Still
cosmetic and still true: the progress counters count the group ("register 88/244"); read `STACK_N`
or the log's "N/N matched" line for what a filtered stack contains.

### A stacked master is its subs plus the warp kernel's own blur, and bilinear costs about a pixel of FWHM in quadrature at 2 px seeing

Every frame but the reference is resampled onto the reference grid (`Image.WarpToReferenceGridAsync`),
and until R1 the kernel was bilinear and only bilinear: a triangle of unit base, whose variance at a
fractional phase is phase times one minus phase per axis, 0.25 px squared at half phase. Measured star
by star on the Orion 2025-10-15 night (`docs/plans/deconvolver-training.md`, "the third finding
placed"): a master is the MEAN of its warped frames to 0.3 percent, so the combine adds nothing, and
the frames at fractional shifts read 2.4 to 2.7 px where the frames at integer shifts (the reference,
and one whose drift happened to be near-integer) read their subs' 2.15. On a synthetic 2.15 px star a
half-pixel shift adds 1.15 px of FWHM in quadrature under bilinear and 0.00 under Lanczos-3
(`WarpInterpolationTests`). **What follows:** every master built before R1 carries it, the retained
dataset masters and the deconvolver's training targets included; a seeing-split pair cannot be built
from masters whose width does not follow their inputs; and a drizzle master has its own pixel-kernel
cost, measured 4 to 9 percent apart from a staged one before either registration fix.
`--warp-interpolation Lanczos3` is opt-in while its ringing on real frames is measured (R1); the
default flip changes every master and is the user's decision.

### FIXED (E1g-2, 2026-09-07): the star profile fit refused a SHARP master, because a Gaussian core with a faint wing is not a Moffat

**Fixed the same evening by fitting the CORE** (`PsfProfileFit.CoreFitFloor`: bins above two percent
of the peak, about two FWHM) and reporting the wing beside it (`Result.WingAt2Fwhm`, `WingAt3Fwhm`, the
profile's own value). Every refused profile returns (the R1 Lanczos master 2.32 px, the two-frame
stack 2.20, the VNG subs 1.73 to 1.90), every accepted width is unchanged to the hundredth, and the
exponents rose by one to three everywhere because the wing bins had been pulling them down. **What
remains:** above an exponent of about six the core cannot tell exponents apart (a beta-7 synthetic
reads 10.7), so a high beta means "Gaussian-cored" and the wing number carries the rest; and the
store's `MoffatBeta` column and E0's beta statistics were measured with the wing in the fit, so they
are a different quantity from a post-E1g-2 value until the masters are re-measured. The history:

`PsfProfileFit` stacks bright isolated stars after a per-star annulus background, then fits a Moffat in
LOG space, equal weight per quarter-pixel bin, over every bin above 0.2 percent of the peak out to 12
px, with the width fixed at the stacked profile's half maximum and only the exponent searched; it
refuses above a log residual of 0.5. A sharp stacked star (the R1 Lanczos master at 2.3 px, a two-frame
stack, a VNG sub at 1.8 px) is Gaussian to within 0.02 at every bin out to 2 px and then carries a
faint wing of half a percent to two percent from 3 to 5 px. No Moffat with that half-maximum width
follows both: the exponent that reaches the wing overshoots the core (0.159 where the profile has
0.106 at 2.1 px), the one that fits the core has no wing, and with two dozen equal-weight bins in log
space across three decades the search settles between them at 0.77 to 0.83 and refuses. A blurrier
master sits closer to the Moffat family and passes (the bilinear twin at 2.47 px, beta 3.9; the whole
night, 2.61, beta 4.6). **So the fit refuses sharp inputs systematically, which is the estimator step
refusing the frames the deconvolver most wants to measure**, and the betas it accepts on blurrier
masters lean the same way. It was first read as far-wing background residue (a floor relative to the
profile's outer level was built and withdrawn within the hour: the annulus already leaves the outer bins
at 0.03 to 0.07 percent, and the relative floor turned a detached halo's `PoorFit` into
`TooFewFitBins`); the fit's `Diagnostics` now carry the stacked profile, so the shape is read rather
than inferred. **The fix (E1g-2 in `docs/plans/deconvolver-training.md`)** is the log fit over the
bins above 2 percent of the peak (`PsfProfileFit.CoreFitFloor`, about 2 FWHM) with the wing reported
beside it (`Result.WingAt2Fwhm` / `WingAt3Fwhm`). On the sharp masters every refused profile returns
with the accepted widths unchanged to the hundredth; over E1e's 180 oracle rows the fit's own
refusals fell from nine entries to one, though the row count only fell 30 to 25, because behind the
shape refusal on the Eta Car 24 mm frame sits a star budget (the brightness band holds 38 / 10 / 2 / 0
stars at 1 / 2 / 3 / 4 px of injected blur), which no fit cures.

## GPU / rendering

### Dangling stack pointer via single-argument Vortice ctors

`new VkPipelineColorBlendStateCreateInfo(attachment)` stores `pAttachments = &attachment` pointing
at the constructor's stack frame, which is reclaimed on return. On strict drivers (Mesa lavapipe)
the garbage `VkBlendOp` produced fully black output; on ARM64 the stack happened to hold valid ops,
so it "worked." Always `stackalloc` the attachment array with a lifetime spanning the
`vkCreateGraphicsPipeline` call and set `pAttachments` explicitly. Recorded in memory
(`feedback_vkblend_dangling_ctor`); it bit `VkPipelineSet`, `VkFitsImagePipeline`, `VkSkyMapPipeline`.

## Desktop shell

### Explorer draws no thumbnail for a FITS on a OneDrive path, and it is not our handler

Reported after 7.1.1627: a folder of FITS under OneDrive shows the app's file-type icon while the same
files copied to a local directory show thumbnails.

**It is not a failure, and the error code is what misleads.** Asking
`IShellItemImageFactory.GetImage` with `SIIGBF_THUMBNAILONLY` answers `0x8004B205`, which reads like an
error and is `WTS_E_EXTRACTIONPENDING`: the shell has QUEUED the extraction asynchronously and is
telling the caller to ask again. Reading it as a failure produced a first, wrong report that the
thumbnail feature was dead for every FITS. Retried properly, a OneDrive-backed file stays pending
indefinitely (twelve attempts across six seconds) while the same bytes in a local directory answer on
the first attempt. The files are `PINNED | REPARSE_POINT`: on the device, still behind the cloud filter.

**Our code is exonerated by direct measurement, not by argument.** `ThumbnailRenderer.RenderAsync` over
a `FileStream` on those very OneDrive files returns correct rasters -- 256x171 in 547 ms for the 238 MB
IMX455 single frame, 256x252 in 47 ms, 256x181 in 210 ms -- so reading a cloud placeholder is not the
problem, and buffering the shell's `IStream` is the only other thing the handler does. What never
completes is the shell's own extraction queue for a cloud placeholder handed to a PACKAGED,
out-of-process handler.

**A TIFF drawing correctly in the same folder proves nothing about this**, and was the other misleading
signal: Windows renders TIFF through an in-box WIC codec loaded IN-PROCESS, while a packaged handler
may only ever run in the surrogate. Different activation paths to the same bytes.

`ThumbnailDiagnostics` (failures only, `OutputDebugString` plus a log under
`LocalApplicationData/TianWen/Logs`, redirected by MSIX into the package LocalCache) is in place to
settle the remaining question if it ever matters: a line means the handler ran and how far the stream
got, an empty log against a thumbnail-less window means it was never asked.

Unrelated but adjacent, and correct behaviour: `corr.fits` answers `0x8004B200`
(`WTS_E_FAILEDEXTRACTION`) because an astrometry.net correspondence table carries no image HDU, which
is the HDU walk declining cleanly rather than a bug.

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

## Scan provenance: every package spells it differently

Measured across the 10P archive (540 FITS) and three Astro Pixel Processor stacks, 2026-08-25.

**The first pass at this concluded APP's HOO composite "carries no provenance card at all". That was
wrong, and wrong in an instructive way: the census searched for `SWCREATE` / `IMAGETYP` / `STACK_N` /
`NUMFRAME` and APP writes none of them.** The file is in fact richly self-describing --

```
SOFTWARE= 'Astro Pixel Processor by Aries Productions'
VERSION = '2.0.0-beta29'
FRAME   = 'Other/Processed'    / frame was processed by Astro Pixel Processor
FILT-1  = 'HOO 1 composite'
```

-- in a vocabulary we simply were not reading. A search that does not cover the space says nothing
about the space.

| card | who writes it | says |
|---|---|---|
| `IMAGETYP` / `FRAMETYP` | N.I.N.A., MaxIm, most capture software | frame type |
| **`FRAME`** | **Astro Pixel Processor** | frame type, incl. `Other/Processed` for derived output |
| `SWCREATE` | SharpCap, N.I.N.A. | author |
| **`SOFTWARE`** | **Astro Pixel Processor** | author |
| `SWMODIFY` | us (and MaxIm's convention) | who modified someone else's file |
| `STACK_N` / `NUMFRAME` | us / APP + others | an integration of N frames |

All six are read now: `FRAME` falls in behind `FRAMETYP` and `IMAGETYP`, `SOFTWARE` behind
`SWCREATE`, and `FrameType.Processed` exists so "this is derived" is a positive statement rather than
the `None` that means "we could not tell".

**Two traps worth keeping.** `EXPTIME = 0` is NOT usable as "not a real frame" -- a bias is
legitimately zero-second. And `'Other/Processed'` contains a **slash**, which is also the FITS comment
separator: a reader that splits on it before extracting the quoted string truncates the value to
`Other` and the match silently fails. (Both spellings are accepted, so it does not matter which
arrives.)

**Still open: `SWMODIFY` is overloaded.** Its correct meaning is "our software modified someone else's
file", which `FitsHeaderEditor` header-tagging also is -- `dataset tag-filter` amended 525 frames of
the 10P set. The scan needs the narrower "we produced these PIXELS, do not re-ingest". They only fail
to collide because header surgery writes no `SW*` card, which is a convention rather than a guarantee:
the day tagging stamps `SWMODIFY` honestly, every frame it touched drops out of its own stack. A
dedicated "derived pixel product" card would make the guard a fact instead of an accident.
