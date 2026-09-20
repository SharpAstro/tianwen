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

### 2a-ter. On the ASI585 the discriminator is R/G, not B/G, and "66rb" is not a confound

Two corrections from curating the ASI585, both measured 2026-09-16.

**"66rb" is the SharpCap white balance, and it is NOT a confound.** SharpCap writes a
`*.CameraSettings.txt` beside every capture (the FITS headers carry no white-balance card, which is
what made this look unanswerable from the headers alone), and it reads `White Bal (R)=66`,
`White Bal (B)=66`. The folder name simply records that. But the setting is **the same on the
L-eNhance sessions**, so it cannot explain any difference between them:

| session | WB_R | WB_B | Brightness |
|---|---|---|---|
| `SMC 120s LEnh` (L-eNhance) | 66 | 66 | 7 |
| `Eta Car 24mm LeHance` (L-eNhance) | 66 | 66 | 7 |
| `Tarantula ZS61 ... 66rb` | 66 | 66 | 7 |
| `SMC ZS61 ... 66rb` | 66 | 66 | 7 |
| `Vela SNR 60s 6deg` | 64 | 64 | 3 |
| `2025-03-20` | 65 | 65 | 13 |

Every session sits within two units on a 0-100 scale, which cannot produce the 30 percent ratio
difference these sessions show. **Always read the `CameraSettings.txt` before calling a setting a
confound**; it also records gain, binning, colour space, cooler state and capture area.

**On this body R/G is the discriminating axis and B/G is the flat one, which is the reverse of
section 2's rule.** Flat sets, each minus its own dark-flat:

| ASI585 flat set | R/G | B/G |
|---|---|---|
| `SMC 120s LEnh` **(L-eNhance)** | **0.1669** | 0.8615 |
| `Tarantula ZS61 66rb` | **0.7653** | 0.8433 |
| `2025-03-20` | **0.7479** | 0.8134 |
| `Vela SNR 60s 6deg` | **0.7377** | 0.8010 |

B/G spans 0.80 to 0.86 across all four and separates nothing; R/G separates 4.5-fold. So section 2's
"B/G is the discriminator, R/G is useless" is a property of the ASI533/SV605 measurements it was
derived from, **not a general rule**, and the axis to trust has to be chosen per sensor by looking at
which one actually splits the known population.

**The other three are one population and it looks BROADBAND**, agreeing to 0.03 in R/G and 0.04 in
B/G, with all three channels within about 25 percent of each other. That is what an unfiltered (or
UV/IR-cut only) train through a broadband flat panel looks like, and the opposite of L-eNhance's
narrow Ha and OIII windows cutting red to 0.17. **Still not named**: the three need the owner to say
what was in the ZS61 train in 2024-10 and 2025, and whether it was anything at all.
`SMC ZS61 66rb` carries no flats, so it attaches only by its sky ratio (B/G 0.691, nearest
`Tarantula ZS61` at 0.661) and by sharing a rig name.

### 2a-bis. Two ASI533 flat sets match NEITHER known population (measured 2026-09-16)

Measured while curating, by the section 2a method (flat minus its own dark-flat, so no bias is
needed and the panel's brightness cancels out of the ratio). Both are unfiled ASI533 sessions that
carry their own flats, and both land between the two populations this plan established:

| ASI533 flat set | R/G | B/G |
|---|---|---|
| `Optolong-L-Ultimate-3nm` 2025-05-03 (known) | 0.1569 | **0.5670** |
| `ASI533mc -10deg 240s` (UNKNOWN) | 0.1999 | **0.7184** |
| `Rosette Dec 24` (UNKNOWN) | 0.5934 | **0.8515** |
| `Optolong-L-Quad-Enhance` 2026-04-22 (known) | 0.4405 | **1.0024** |
| `IDAS-LPS-D3` 2024-06-06 Rim Nebula (named by its capture folder, group N) | 0.2281 | **0.9528** |
| `IDAS-LPS-D3` 2023-09-15 LMC, Samyang 135 mm (by population, group Q) | 0.2254 | **0.9620** |

**The LPS D3 row (added 2026-09-20) is an ANCHOR, not a verdict from the pixels.** It is named by its capture folder (`LPS`) alone, the owner's LPS being the D3 that named group D; the header has no hint and the dark folder beside it is not evidence (its frames are named for a July L-Ultimate session, and a dark is filter-blind). The pixels EXCLUDE the two ASI533 filters with known bands (3 nm: sky 0.56, flat 0.567; L-Quad: sky 1.01 to 1.08, flat 1.002; this session: sky 0.76, flat 0.953) and name nothing. It is the first IDAS LPS D3 measurement on the ASI533, so the next unfiled ASI533 session with a flat near 0.95 has a band to be tested against.

**The LMC row is the anchor's first use.** No tag names it (its folder says `RGB`, a processing mode),
and it agrees with the anchor on BOTH axes within 2 percent: flat B/G 0.962 against 0.953 and sky B/G
0.73 to 0.76 against 0.757 to 0.779, where L-Quad sits at 1.00 (flat) and above 1.0 (sky) and the 3 nm at
0.567 / 0.56. Filed as `IDAS-LPS-D3` on that basis; the population now has two members and a flat-side
width of 0.009, which is the number the next ASI533 session gets tested against.

**The PlayerOne Uranus-C is an IMX585 like the ASI585, and its 2023 nights land where the ASI585's
BlueCut population does, not proven across bodies.** Its own flat (SMC, 2023-07-29) reads R/G 0.806,
B/G 0.592 and its bias-corrected sky R/G 0.63, B/G 0.50 at 74 to 108 ADU/s (gain 200), against the
ASI585 BlueCut population's flat B/G 0.49 to 0.50 and sky B/G 0.53 to 0.55; the same body's
`M8 M20 OIII HA` night reads 3 ADU/s at gain 220, a dual-band by rate, unnamed (`Unidentified-HaOIII`).
Group Q files the three broadband nights under the BlueCut provisional slug by sensor-family similarity
and says so; a Uranus-C flat through a known filter would settle it.

**They do not match each other either.** 0.8515 against 0.7184 is 0.13 apart where section 2a's
within-population agreement is 0.021 or better, so this is not one new population but potentially
two, or one plus a confound. Their R/G differs threefold, which section 2a already warns is the
uninformative axis.

### 2a-quater. Half of that table was in the wrong UNITS: undo the white balance first (2026-09-20)

**The confound above was the in-camera white balance, and this plan already had the mechanism two
sections down without applying it here.** The pedestal table in "Reference values on this archive"
records that the ASI533 at offset 13 is *not grey* (R 649, G 516, B 649) because ZWO white balance
was on in that era. That is a **digital gain on the raw stream**: green is never scaled, and red and
blue arrive multiplied by `WB/50`. So a raw flat R/G is a filter TIMES a camera setting, and the
table above compares sets that do not share the setting. `Optolong-L-Ultimate-3nm` 2025-05-03 is a
**gain 252, offset 20** set, which is grey; `ASI533mc -10deg 240s` and both LPS D3 rows are
**offset 13**, which is 63/63. The 3 nm row was being read against sets 1.26x brighter in blue.

Measured over five ASI533 campaigns, the pedestal recovers the setting to four figures, so no folder
name is needed: `WB_R = 50 * bias_R / bias_G`. r56 gives 576/516, WR73 gives 749/516, 78r gives
802/516, WB63 gives 649/516; the ASI585 at 65r gives 1120/864.

**Every flat set carries its own darkflat at the same exposure, gain and offset, which carries the
same scaling, so each set reduces itself with nothing external:**

```
gain_R   = darkflat_R / darkflat_G            (green is the unit)
true R/G = (flat_R - darkflat_R) / gain_R / (flat_G - darkflat_G)
```

| ASI533 flat set | raw R/G | raw B/G | WB | **true R/G** | **true B/G** |
|---|---|---|---|---|---|
| `Optolong-L-Ultimate-3nm` 2025-05-03 | 0.1794 | 0.5781 | 50/50 | 0.1576 | **0.5669** |
| `Optolong-L-Ultimate-3nm` 2025-05-21 | 0.1835 | 0.5797 | 50/50 | 0.1586 | **0.5668** |
| `ASI533mc -10deg 240s` (was UNKNOWN) | 0.2175 | 0.7262 | 63/63 | 0.1590 | **0.5703** |
| `ASI55mc Cal Jul Optolong Ultra` 2024-07-06 | 0.2238 | 0.7285 | 63/63 | 0.1696 | **0.5749** |
| `IDAS-LPS-D3` 2024-06-06 Rim Nebula | 0.2381 | 0.9563 | 63/63 | 0.1813 | **0.7579** |
| `IDAS-LPS-D3` 2023-09-15 LMC | 0.2388 | 0.9659 | 63/63 | 0.1791 | **0.7648** |
| `Vela SNR 2024` cal 2024-02-10 | 0.2279 | 0.9615 | 63/63 | 0.1734 | **0.7622** |
| `Orion Dec 24` 2024-12-08 (UNKNOWN) | 0.6168 | 0.8628 | 63/63 | 0.4842 | **0.6822** |
| `Rosette Dec 24` 2024-12-30 (UNKNOWN) | 0.6009 | 0.8561 | 63/63 | 0.4718 | **0.6770** |
| `Optolong-L-Quad-Enhance` 2026-04-22 | 0.4528 | 1.0027 | 50/50 | 0.4413 | **1.0027** |
| `Optolong-L-eNhance` 2023-06-17 (ASI533, `LEH`) | 0.1756 | 0.8895 | 78/63 | 0.0992 | **0.7026** |

**`ASI533mc -10deg 240s` is the Optolong L-Ultimate 3 nm, and its folder said so all along.** In true
units its B/G is 0.5703 against the 3 nm band's 0.5588 +/- 0.0053, and within 0.6 percent of the two
clean May 2025 sets. Its capture folder reads `L-Ultra`; the parking was caused entirely by comparing
0.7262 against 0.567. Its two sessions (Vela SNR P2a 2025-01-18, eta Car OIII+Ha 2025-01-31) had been
heading for a provisional `Unidentified-HaOIII` slug on the strength of that gap, and group R files
them as 3 nm instead. Their de-white-balanced sky B/G is 0.565 to 0.580 against the 3 nm reference
sky 0.5639 +/- 0.0099, at the reference's own 1.7 to 2.2 ADU/s, so sky and flat now agree; the sky
rate had *always* read 3 nm-class and was the tell that went unexplained.

**The two LPS D3 rows were never affected**, because both are offset 13 and shared the setting; the
anchor and its first use compare cleanly, and in true units the population is 0.1802 +/- 0.0011 /
0.7614 +/- 0.0034, tighter than the raw figures suggested. The 2024-02-10 Vela campaign flat joins
it at 0.1734 / 0.7622, which is 0.1 percent off on the discriminating axis.

**`Rosette Dec 24` survives the correction as a genuine unknown, and gains a partner.** At
0.4718 / 0.6770 it still matches no anchored band, but `Orion Dec 24` three weeks earlier reads
0.4842 / 0.6822, agreeing to 2.6 and 0.8 percent, so it is a population of two rather than a
singleton. Both sessions run 55 to 95 ADU/s, which is broadband, so R/G is evidence here (see below)
and the pair is one unidentified broadband filter. Group R files all three sessions that use them
(Orion, Rosette, Seagull) as `Unidentified-Broadband`. The L-eNhance guess the old text floated is
now excluded on this body's own measurement rather than on a cross-sensor caution: the ASI533
L-eNhance reads B/G 0.7026 and 55 to 95 ADU/s is not a dual-band's throughput.

### 2a-quinquies. For a LINE-SELECTIVE filter only B/G is usable; R/G belongs to the flat PANEL

Section 2a says R/G is the uninformative axis and section 2a-ter says that is a property of the
ASI533/SV605 measurements rather than a law. Here is the mechanism, measured over ten L-Ultimate
flat sets (seven ASI533, three SV605CC):

| filter | sets | true R/G | true B/G |
|---|---|---|---|
| `Optolong-L-Ultimate-3nm` ASI533 | 7 | 0.158 to 0.546 (**3.5x**) | 0.5526 to 0.5669 (**+/-1%**) |
| `Optolong-L-Ultimate-3nm` SV605CC | 3 | 0.281 to 0.518 | 0.5183 to 0.5387 |
| `IDAS-LPS-D3` ASI533 (broadband) | 3 | 0.1734 to 0.1813 (**+/-2%**) | 0.7579 to 0.7648 |
| `Optolong-L-Quad-Enhance` SV605CC (broadband) | 3 | 0.4117 to 0.4233 | 1.0243 to 1.0322 |

**A 3 nm window at 656 nm samples whatever far-red tail the flat panel happens to emit**, which
varies with the panel, its brightness setting and the exposure, and is a property of the light
source rather than of the filter. B/G is stable because the OIII window at 500.7 nm sits where an
LED panel actually produces light. A broadband filter integrates the panel's whole spectrum, so its
R/G is stable too and remains usable: LPS D3 holds to +/-2 percent over three sessions spanning
nine months.

**So identify a line-selective filter on B/G alone, and say so in the verdict.** Quoting a 3 nm
set's R/G as corroboration is quoting the flat panel. This is also why the L-eNhance row above cites
B/G 0.7026 and not its R/G of 0.0992: against the 3 nm's 0.5588 the blue excess is the Hb line at
486.1 nm that the L-eNhance passes and the L-Ultimate does not, which is a real physical separation,
while the red difference is two different panels.

**Neither unknown is nameable from the pixels alone and `Unidentified-Broadband` stays parked.**
`Rosette Dec 24`'s folder says `Rosette RGB 120s`, and `RGB` is a processing mode rather than a
filter by section 1's own rule, so there is no textual hint to corroborate. Naming it needs the
owner, a reference frame on this body, or a flat set shot through a filter already identified.

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
  merely a coarser rounding of the same underlying signal scale.

  **ANSWERED 2026-09-16, and the bias it needed was already in the archive.** The paragraph above
  used to end "which is not yet known, because no bias or dark exists anywhere in the archive for
  this rig to check the absolute floor per channel. Until that is shot, do not assume either
  direction." That premise was wrong: ASI585 BIAS sets exist at **three** different offsets, and all
  three agree.

  | bias set | R | G | B | G/R | min step R/G/B |
  |---|---|---|---|---|---|
  | `Vela SNR 60s 6deg` (o3) | 285.0 | 224.0 | 285.0 | 0.786 | 1 / 16 / 1 |
  | `SMC 120s LEnh ASI585 252g` (o7) | 619.5 | 472.0 | 619.5 | 0.762 | 1 / 16 / 1 |
  | `2025-03-20` (o13) | 1120.0 | 864.0 | 1120.0 | 0.771 | 2 / 16 / 2 |

  **Green is NOT 16x gained: the ratio is 0.77, and it holds across every offset.** Red and blue are
  identical to the ADU at each one, green sits consistently at about 0.77 of them, and the same 0.77
  appears in the SLOPE against offset (83.5 ADU per offset unit on red and blue, 64 on green). So the
  multiple-of-16 spacing is quantisation, exactly as the "coarser rounding" reading proposed, and the
  scale is shared rather than 16x apart.

  **What this changes:** do not divide green by 16, and do subtract the per-channel bias before any
  cross-channel ratio, because the floors genuinely differ (R and B about 1.3x green). With that
  subtraction the ASI585's B/G is usable, which is what the ADU-scale doubt was blocking.
  The three parked ASI585 + ZS61 sessions can be measured on this basis.

  **Measured the same day, and the ASI585 splits into TWO populations, only one of which is
  nameable.** Every session below is the same body in the same SharpCap build, bias-subtracted per
  channel, compared against this archive's own ASI585 reference frame
  (`frame_00002_LeEnhance.fits`, R/G 0.4288, B/G 0.8707) rather than against the IMX533 bands, which
  do not apply to an IMX585:

  | session | focal | offset | R/G | B/G | vs the reference |
  |---|---|---|---|---|---|
  | `SMC 120s LEnh ASI585 252g` | 368.8 | 7 | 0.5299 | 0.8491 | 0.975x |
  | `Eta Car 24mm LeHance 60s -10d` | 368.8 | 7 | 0.4265 | 0.8701 | **0.999x** |
  | `Tarantula Neb ZS61 ... 66rb` | 289 | 7 | 0.8510 | 0.6609 | 0.759x |
  | `SMC ZS61 ... 66rb` | 289 | 7 | 0.8382 | 0.6909 | 0.794x |
  | `Vela SNR 60s 6deg` | - | 3 | 0.7935 | 0.6785 | 0.779x |
  | `2025-03-20` | - | 13 | 0.8295 | 0.7153 | 0.822x |

  **The first two are L-eNhance and can be filed**: both folder names say so (`LEnh`, `LeHance`),
  both sit at 368.8 mm, and they match the reference to 2.5 percent and 0.1 percent.

  **The other four are NOT nameable from this, and the reason is a confound rather than a weak
  signal.** Their R/G is about twice the reference's while their B/G is a third lower, which is a
  large difference, but two of them carry **`66rb` in the folder name**, which reads as a camera
  white-balance setting, and **SharpCap writes no white-balance card at all** (the headers carry
  `GAIN` and nothing else of the kind). A per-channel gain applied in the camera and a different
  passband are indistinguishable in these ratios, so the four stay parked until the owner says what
  `66rb` was. They are also a different scope (289 mm, the ZS61), so optics differ too.
  `_provenance/reference-frames/reference-frames.csv` now carries an `adu_scale` verdict per frame,
  computed per CFA channel rather than on the whole frame at once so a per-channel mismatch like
  this one is named rather than averaged away; 13 of the 14 reference frames come out a clean x4
  with every channel agreeing, and this ASI585 frame is the one exception, reported as
  `PER-CHANNEL MISMATCH (B=native, G=x16, R=native)`.
