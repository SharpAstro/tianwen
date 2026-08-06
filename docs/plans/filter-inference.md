# Filter Inference from Raw CFA Pixels

**Status: method validated on 58 sessions / 8,507 frames across TWO camera bodies, tooling NOT
committed.** The measurement exists only as scratch scripts plus a provenance folder on `D:`, so a
fresh checkout can reproduce the *tagging* and none of the *inference*. This plan is the path from
that state to committed, re-runnable tooling. Companion to
[astro-archive-survey.md](astro-archive-survey.md) (what is in the archive) and
[ai-denoise-deconv.md](ai-denoise-deconv.md) (why the archive needs to be labelled at all: a
training pool that mixes 3 nm dual-band with broadband is not one population).

**The second body is the interesting part.** Group C (10 sessions / 1,346 frames, SVBONY SV605CC +
SH61 EDPH 270 mm) was measured with the same reference bands derived on the ASI533, and the two
sessions whose folders independently say `Ha-OIII` both landed in the 3 nm band. So the bands are a
property of the **sensor family**, not of the body they were calibrated on, which is what makes
this method worth committing rather than re-deriving per camera.

## 1. The problem

**No FITS header in the archive names a filter.** Neither SharpCap nor N.I.N.A. wrote a `FILTER`
card for a filter screwed into the train, because nothing electronic knew it was there. Folder names
carry a hint on roughly a third of paths, and the hints are actively misleading in two ways the
owner had to point out:

- **`RGB` and `LUM` are processing modes, not filters** on an OSC sensor. A dual-band stack processed
  as RGB in PixInsight lands in a folder called `RGB`. Treating those tokens as filter evidence put
  4,533 frames of false evidence into the first pass.
- **Mono frames in an OSC archive are extracted, not captured.** 130 sessions / 5,942 frames have no
  `BAYERPAT` on a camera that has one, including two PIPP Moon runs of 3,059 and 2,000 frames. They
  are narrowband-extraction *outputs*, so their apparent "filter" is a channel name.

So the filter has to come from the pixels.

## 2. The mechanism, and what it can actually resolve

A filter's passband layout is imprinted on the **raw, undebayered** frame, because the Bayer matrix
samples it at three different spectral positions. The raw histogram is therefore multimodal with one
mode per CFA colour, and the *relative* mode positions encode the passband. Two measurements fall
out:

| measurement | what it is | what it separates |
|---|---|---|
| **sky rate** | background above bias, in e-/px/s, divided by airmass | passband *width*, to within a factor of about 2 |
| **background B/G** | blue over green background ratio | passband *layout*, which is the discriminating one |

**B/G is the discriminator; R/G is useless** (the populations overlap). Physically: a 3 nm Ha/OIII
dual-band drops OIII 500.7 mostly into G and leaves B nearly empty, while a quad-band's Hb window at
486 nm opens B up. Measured on this archive:

- **3 nm population: B/G = 0.5639 +/- 0.0099** over 44 sessions
- **L-Quad Enhance: 1.009 to 1.082**, which puts the nearest quad-band **43 standard deviations**
  off the 3 nm mean

**Honest resolution limits.** 3 nm versus broadband is a factor of 40 in sky rate, trivial. 3 nm
versus L-eNhance is about 6x, doable. **Two different dual-bands are not separable** by this method,
and neither are two quad-bands. Absolute sky rate alone is confounded by sky brightness, moon,
airmass and f-ratio, which is why the layout ratio carries the verdict and the rate is corroboration.

**The strongest single piece of evidence that the metric tracks the filter and not the night:** a
session under a 93% moon at +33 degrees altitude produced *less* background than a moonless night on
the same rig. Only a narrow passband does that.

### 2a. A FLAT set measures the passband far better than the sky does

Found while measuring group C, and it is the strongest tool here for one specific question. **A flat
panel is a fixed, bright, uniform source**, so the channel ratios of a flat set carry the filter's
passband with **none** of the sky's confounds: no airmass, no moon, no nebula in the field, no
gradient, and signal levels 4 orders above the sky background. Measured on the SV605CC, per-channel
above the measured bias:

| flat set | R/G | B/G | folder said |
|---|---|---|---|
| 2025-08-22 (Helix) | 0.496 | **0.518** | nothing |
| 2025-10-15 (`Ha-OIII Cal`) | 0.518 | **0.520** | Ha-OIII |
| 2025-11-03 | 0.281 | **0.539** | nothing |
| 2025-10-18 (`RGB Cal`) | 0.420 | **1.023** | RGB |
| 2026-02-19 | 0.415 | **1.037** | nothing |

Two populations, 0.48 apart, with every pair inside a population agreeing to <= 0.021. That is how
the three **untagged** flat sets were attributed: each matched a tagged set to within 0.02.

**What this can and cannot do.** It cannot *name* a filter, because the panel's own spectrum is
unknown, so the absolute ratio is not comparable to a sky ratio or to another rig's panel. What it
does, far better than the sky measurement, is answer **"were these two sessions shot through the
same filter?"**
So the workflow is: name the filter once per population from a session that has independent evidence,
then attach every other session by its flat set. Note that R/G is again the useless axis (0.281 to
0.518 *within* one population), consistent with section 2's finding on the sky ratio.

**A flat set is also the only evidence available for a session shot warm.** Half of group C ran with
the cooler not holding, at +4 to +12 C, and a warm sky measurement is a worse measurement; the flats
are unaffected because they are short exposures where dark current cannot accumulate.

### 2b. The owner's hand-labelled reference frames are the naming evidence

`C:/temp/tests/examples/` holds four frames the owner set aside with the filter appended to the
filename. Three name a population this method can only otherwise call "band A" or "band B", so they
are the input section 2a's workflow needs, and they should be treated as reference data rather than
scratch:

| frame | rig | label | status |
|---|---|---|---|
| `2025-10-18_21-20-45__3.90_30.00s_0006_L-QuadEnhance` | SV605CC + SH61 EDPH | L-Quad Enhance | names group C's quad population |
| `2026-02-21_01-24-25__-5.00_60.00s_0010_HaOIII-3nm` | ASI533 + Samyang | 3 nm | names group A/B |
| `2026-02-20_22-57-32__-5.10_60.00s_0003_LPS` | QHY294 + SWQ8, gain 1600 | LPS | names the group D session before it is measured |
| `frame_00002_LeEnhance` | ASI585MC Pro, gain 252 off 7 | L-eNhance | names an ASI585 population |

**The first row is worth reading twice.** That frame is **pixel-identical** (sha256 over the
undebayered payload) to
`2025/2025-10/C2025_R2_SWAN/2025-10-18/RGB/2025-10-18_21-20-45__3.90_30.00s_0006.fits`, so a frame
the owner labelled L-Quad Enhance by hand lives in a folder called **`RGB`**. That is section 1's
processing-mode rule demonstrated on a single file, and it independently confirms the verdict the
pixels gave for all 6 quad-band sessions in group C.

**The set is now 14 frames** (`D:/Astro-Organized/_provenance/reference-frames/`, with a
`reference-frames.csv` recording per frame how its label was established, and an `adu_scale`
verdict). It was extended from the owner's 4 by adding one frame per session identified in group C
plus the ASI533's only L-Quad session, on the rule **one frame per identified session, from the
middle of the run, sourced untagged so `FILTER` stays absent**. Frames were chosen to span the
confounds that could break the method rather than to accumulate examples: the set deliberately
includes a session shot at +12 C with the cooler not holding, one under a 79% moon at +23 degrees
altitude, one at airmass 2.68, and both extremes of each measured population.

**Self-test result: 12 of the 12 testable frames classify to their known label from a SINGLE frame
each**, which is a harder test than the method faces (it takes the median of three). The 2 untestable
ones are the singleton anchors, which have no reference band to be tested against yet. Margins to the
nearest band edge run 0.089 to 0.127, so nothing is a marginal call. Two things this run makes
visible that a per-session median hides:

- **R/G is useless, again, and now on labelled data.** The 3 nm frames span 0.276 to 0.545 and the
  quad-band frames 0.368 to 0.415: fully overlapping. Third independent confirmation of section 2.
- **There is a small body-dependent offset, and it is not noise.** The SV605CC's two populations sit
  *further apart* than the ASI533's (3 nm 0.529 to 0.545 against 0.567; quad 1.073 to 1.101 against
  1.010), so this body discriminates marginally better. Nowhere near enough to matter at the current
  band widths, but it means **the bands must not be narrowed toward per-body precision without
  per-body calibration**.

## 3. Bias must be measured, never fitted

This is the load-bearing correction and the reason the first pass produced four wrong verdicts,
including flagging as "NOT 3 nm" a session sitting in a folder literally named
`Vela SNR P2a 240s L-Ultra -10d`.

The first pass fitted bias per frame from a photon-transfer relation. For one camera setting whose
true gain is 0.197 e-/ADU it returned gains from **0.119 to 0.344**, and on one frame a bias of
**-7177 ADU**. Replacing it with bias measured from the archive's own bias frames, keyed on
`(gain, offset)`, cut the within-population scatter **7.4x** (B/G spread 0.274 to 0.042, sd 0.073 to
0.010) and moved the L-Quad separation from "12x the largest internal gap" to 43 sd.

It also forced me to retract my own explanation of the data. I had reported B/G tracking sky rate at
r = 0.913 and read it as "B/G needs signal to be meaningful". That correlation was an artifact of
both quantities sharing the same bias error. With measured bias, r = +0.14. **A correlation between
two quantities derived from the same bad intermediate is not a finding.**

Reference values on this archive:

| body | gain | offset | R | G | B | n | note |
|---|---|---|---|---|---|---|---|
| ASI533MC Pro | 121 | 20 | 796 | 796 | 796 | 200 | grey |
| ASI533MC Pro | 252 | 20 | 780 | 780 | 780 | 100 | grey |
| ASI533MC Pro | 212 | 20 | 788 | 790 | 788 | 144 | grey |
| ASI533MC Pro | 121 | 13 | 649 | **516** | 649 | 100 | **not grey**: ZWO white balance was on in that era |
| SV605CC | 120 | 20 | 804 | 804 | 804 | 300 | grey; identical at -9.8 C and -4.3 C |

That fourth row is why the pipeline undoes white balance using the **bias frame's own channel ratios**
rather than assuming a grey pedestal. The folder names from that era record `78r 63b`, which
corroborates it independently.

Gain model, validated: `g = 0.7949 * 10^(-gain/200)` e-/ADU for the ASI533 under N.I.N.A.'s times-four
recording scale. It reproduces an independently fitted gain-252 value to **0.9%**.

**Measure the gain from FLAT PAIRS, and the model becomes a cross-check rather than an input.** For
the SV605CC at gain 120, consecutive flat pairs (signal above bias over half the variance of their
difference, per CFA plane, central region only so vignetting cannot inflate the variance) give
**0.2059 e-/ADU**, with a spread of 0.6% across three independent flat sets and all three channels.
The ASI533 model predicts 0.1997 for gain 120, so it transfers to **3.1%**. Close enough to trust
as a sanity check, not close enough to prefer over a measurement that costs two frames. Note this is
the *same physical relation* the broken per-frame fit in this section tried to exploit; what makes it
work is a pair of frames at **matched, uniform, high illumination** instead of one frame's spatial
variance across a structured sky.

## 4. Negative results, recorded so they are not retried

Four things I expected to work and which do not. Each is cheap to re-attempt and a waste of time.

1. **Star colour locus.** Prediction: narrowband tightens the stellar colour distribution. Measured
   the *opposite* (spread 0.194 for broadband LPS rising to 0.423 for 3 nm). Cause: SNR confound.
   Narrowband frames have noisier stars, and noise widens a colour locus faster than a passband
   narrows it.
2. **Channel lock** (are two channels correlated across stars). Fails even SNR-matched, because at
   matched *luminance* SNR a narrowband frame has flux in one channel and pure noise in the others,
   so there is nothing to correlate.
3. **Normalising sky rate for optics made it worse.** L-Quad appeared to pass *less* sky than
   broadband LPS after normalisation, because that particular frame sat at airmass 1.6, sun -19.4
   degrees, on the galactic plane. Per-frame geometry beats per-rig normalisation.
4. **Per-frame bias fitting**, per section 3.

## 5. What is already committed

The **write** end is done, tested, and was used for all 4,724 cards written in the reorganisation:

- **`tianwen dataset tag-filter`** (`src/TianWen.Cli/DatasetSubCommand.cs`): dry run by default,
  `--frame-type` defaulting to Light + Flat + DarkFlat, `--overwrite-existing` off so filling a blank
  is separated from overruling a value, and `--hard-links` defaulting to `Refuse` so a de-duplicated
  archive cannot be edited through one of its names by accident.
- **`FitsHeaderEditor`** (`src/TianWen.Lib/Imaging/Calibration/`): the write is never in place. It
  builds a temp file, verifies the pixel payload against the original, and only then `File.Replace`s
  with a backup.

The **inference** end does not exist in the repo at all.

## 6. Phasing

| Phase | Deliverable | Notes |
|---|---|---|
| **F1** | `dataset bias-library` | Scan an archive for `IMAGETYP=BIAS`, group on `(camera, gain, offset, temperature)`, emit per-channel medians + n to a JSON library. Per-channel, never grey-averaged, so the white-balance era is representable. This is the artifact everything else keys on. |
| **F2** | frame-scale detection | GCD of pixel values distinguishes N.I.N.A.'s times-four 14-to-16-bit scaling from an unscaled writer. Currently an assumption carried in a comment; SharpCap sessions were set aside wholesale because of it. Must be per-frame and reported, never inferred from `SWCREATE` alone. |
| **F3** | `dataset measure-filter` | Per session: sample frames from the middle of the run, resolve bias from the F1 library, compute sky rate in e-/px/s / airmass and background B/G, emit a row per session. Read-only, writes no headers. Should also measure **flat sets** (section 2a): same code path, a far cleaner signal, and the only one available for a session shot warm. |
| **F4** | band derivation + classification | Cluster the F3 rows, or match them against a committed reference band table, and emit a proposed `FILTER` per session with the basis and the deviation. Feeds `tag-filter` as input, and must stay a *proposal* that a human locks in. Two populations that separate by tens of sd (group C: 60) still want the human step, because the method names a *band*, and which filter occupies that band is the owner's knowledge. |
| **F5** | `archive organize` | The reorganise-into-a-new-root tool: copy with verification, never write to the source, dedup hard-linked frames to one copy, file calibration by what it *is*. Proven twice now: 5,750 files / 96.95 GiB and 2,565 files / 43.25 GiB, 0 failures. Must **group each calibration folder on `(date, gain, offset, exposure)` reading every header, and refuse an exposure subset it was not told to expect** (see section 7); that check is what caught 67 dark-flats inside a folder named `DARK`. |

**F1 through F3 are the reusable core**; F4 is where judgement lives and should stay assisted rather
than automatic. F5 is independently useful and does not depend on F1 to F4.

**Ordering note from group C:** the gain measurement (section 3) and the flat-set measurement
(section 2a) both come from FLAT PAIRS, so F1 should emit a flat library alongside the bias library
and F3 should read both. Doing that makes a new body's constants (bias, gain, and its flat
signatures) a single pass over its calibration frames rather than three.

## 7. Invariants for whoever builds this

- **Never write to the source archive.** The reorganisation model (copy to a new root, verify both
  sides, leave the original untouched) is not a safety dance to be optimised away. There is one copy
  of this data.
- **A session is one filter.** Every measurement here samples frames from the middle of a run and
  attributes the verdict to the whole session. That assumption is the owner's and it held on all 48
  sessions, but it is an assumption, so a per-session spread that exceeds the population sd is a
  signal to stop, not to average.
- **A folder name states what a frame is, never what it applies to.** Calibration-to-light
  association is many-to-many (one flat set serves several nights; one night draws on sets shot weeks
  apart), so it belongs in a map file. Filing flats under the *session* date left 10 of 18 session
  dates with no flats folder at all.
- **Calibration folders must key on temperature**, not just gain and offset. Without it a bias folder
  silently merged two sets taken seven months and 5 degrees C apart, and daylight dark-flats at +22 C
  hid inside a folder named `DARK`.
- **A calibration folder holds whatever it holds, so GROUP IT before naming it, and refuse a subset
  you did not expect.** This has now happened twice, in two different rigs, and the second time the
  folder was not even anomalous-looking: `2026-02-19/DARK` on the SV605CC holds 60 frames at 60 s, 60
  at 120 s, and **67 at 7.2443 s, which is exactly the exposure of the `FLAT` set beside it**, so a
  third of that folder is dark-flats. Grouping on `(date, gain, offset, exposure)` and declaring the
  exposures a folder is *allowed* to contain turns this into a refusal instead of a mislabelled
  destination. Sampling cannot find it: reading the middle frame called the whole set 120 s and
  reading an early frame called it 60 s, and both are wrong. **Read every header.**
- **Do not report a dark mismatch without the sensor's dark current.** The same 5 C mismatch is
  disqualifying at +10 C and irrelevant at -5 C, where an IMX533 accumulates well under one electron
  in 60 s and the dark exists for hot pixels rather than thermal signal. Group C's real gap is not
  the five sessions whose nearest dark is a few degrees off; it is the five shot at **+4 to +12 C**
  with the cooler not holding and no dark within 9 C.
- **A whole-file hash cannot verify a tagged frame.** Tagging rewrites the header, so the file is
  *supposed* to differ. Only a pixel-payload comparison (read with `do_not_scale_image_data` so
  `BZERO`/`BSCALE` cannot mask a difference) proves the science data survived.
- **An exposure that is an exact multiple of the rig's sub length is a stacked integration.** Three of
  the six group C sessions excluded from measurement were `EXPTIME` 7680 s, 3060 s and 1080 s against
  a 120 s sub, which makes them N.I.N.A. and PixInsight integrations rather than frames. The
  committed provenance skip (`STACK_N`, TianWen `SWCREATE`) only catches *our own* outputs, so it
  cannot see these.

## 8. Known gaps

- **Group C is DONE** (2026-08-05): all 16 SV605CC + SH61 EDPH sessions accounted for: 10 measured,
  organized and tagged (1,346 lights: 575 L-Ultimate 3 nm, 771 L-Quad Enhance), 6 correctly excluded
  (3 stacked integrations, 2 runs under 20 frames, 1 pair of loose bias frames). The bands transferred
  from the ASI533 with no adjustment; the body's own bias (804 grey, 300 frames) and gain (0.2059
  e-/ADU from flat pairs) were measured first. Basis per session in
  `D:/Astro-Organized/_provenance/group-c-locked.csv`.
- **Groups D and beyond are unmeasured.** The 18,354 frames with `TELESCOP='?'`, the Newtonian's
  history (which appears exactly once, as `SWQ8`), and the next coherent blocks after group C, in the
  order they are worth doing:
  1. **2 ASI1600MM Pro mono sessions** (598 frames, 2025-02). Mono, so one filter per session, and
     both carry a path tag (`Ha`, `Luminance`) with one already holding a `FILTER` card. Cheapest.
  2. **1 QHY294 + SWQ8 600 mm session** (193 frames, 2026-02-20). Already named `LPS` by a reference
     frame (section 2b), and the identification itself is done: raw (bias-uncorrected) B/G is tight
     across all three targets sharing the night (Centaurus A 0.788, Running Chicken Nebula 0.796,
     Omega Cen Cluster 0.824), nowhere near the roughly 2x swing a real filter change would produce
     against this archive's reference bands, so one filter (LPS) ran the whole night and no
     narrowband was swapped in for the emission-nebula target. **Confirmed to be three targets
     sharing one `LIGHT` folder** (same gain/offset/temp, one continuous night), which breaks the
     one-session-one-target assumption and must split into three sessions on organizing.
     **Parked** on the owner's call: no bias or dark was ever shot for this rig (gain 1600 is
     otherwise unrepresented anywhere in the archive), so it cannot get a bias-corrected verdict of
     the rigor group C got. The one FLAT set present (46 frames, same settings as the lights) gave an
     ambiguous raw ratio that overlaps the 3nm range uncorrected, which is exactly why it needs the
     bias rather than being read as-is. Pick back up if a bias set for this rig ever gets shot;
     until then the LPS/three-way-split facts above stand but nothing gets organized.
  3. **3 ASI585MC Pro + WO ZS61 sessions** (637 frames, 2025, no filter evidence in any path).
     **Parked** on the owner's call: this body's ADU scale is unresolved (see below), so its sky rate
     cannot be trusted. Its flats would still work (section 2a) if it is picked up later.
- **Askar D1/D2 and IDAS D3 have zero textual presence anywhere in the archive.** If they were used,
  nothing but pixels will say so, and section 2's limits mean a dual-band-versus-dual-band call is
  not available.
- **HIP 80609 (Rho Oph), 2026-04-21, L-Quad Enhance, has no usable dark.** Nearest match is 335 days
  and 4.9 C off at the wrong exposure. Needs a matching dark or dark scaling.
- **The ASI585's ADU scale is not one number, and treating it as one was the mistake.** The
  whole-frame 63%-on-a-4-ADU-grid / 53%-on-a-16-ADU-grid figure on the L-eNhance reference frame
  (SharpCap 4.1) looked like neither a clean shift nor native data, and was read that way in an
  earlier version of this section. **Per channel it is completely clean**: measured directly on
  pixel values (not just the modulo statistic), R and B have adjacent-integer granularity
  (min gap 1 between distinct values in a 200x200 patch, i.e. native/unscaled), while **both green
  Bayer sites have a hard minimum gap of exactly 16** between any two distinct values, independently
  confirmed at both physical G positions. The 63%/53% whole-frame figures are exactly the arithmetic
  mean of native (25%, 6.25%) on two channels and a clean x16 shift (100%, 100%) on the other two,
  an artifact of averaging across channels rather than a genuinely ambiguous scale. **The owner
  confirms SharpCap writes the ASI533/ASI585/ASI294MC as 14-bit unscaled**, which this now measures
  as true for R and B; G's reduced precision is the part that needed pixel-level evidence.
  **This does not make an ASI585 frame unusable.** Calibration frames shot on the same rig and
  software carry the identical per-channel quantization, so channel-wise bias/dark subtraction still
  cancels correctly. It only bites the filter-inference ratios (B/G, R/G) specifically, and only if
  green's multiple-of-16 values represent a genuine 16x gain difference from red/blue rather than
  merely a coarser rounding of the same underlying signal scale, **which is not yet known** because
  no bias or dark exists anywhere in the archive for this rig to check the absolute floor per
  channel. Until that is shot, do not assume either direction: don't divide green by 16, and don't
  use it unscaled, in a cross-channel ratio.
  `_provenance/reference-frames/reference-frames.csv` now carries an `adu_scale` verdict per frame,
  computed per CFA channel rather than on the whole frame at once so a per-channel mismatch like
  this one is named rather than averaged away; 13 of the 14 reference frames come out a clean x4
  with every channel agreeing, and this ASI585 frame is the one exception, reported as
  `PER-CHANNEL MISMATCH (B=native, G=x16, R=native)`.
