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
