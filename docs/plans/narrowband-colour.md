# Narrowband colour: normalization, unmixing, calibration

An Ha/OIII/SII stack has **no colour path at all** in TianWen today. `Tycho2ColorCalibration`
(SPCC) integrates a Pickles SED against QE x CFA over the whole visible band, which is the right
model for a broadband OSC frame and the wrong one for a 3 nm passband. So a narrowband master gets
whatever the channel assignment plus per-channel autostretch happen to produce, which in practice
means the familiar red-dominated HOO.

Research source: five saved videos plus their transcripts, which turned out to cover **seven
different techniques** rather than one. Investigated 2026-08-02. Only one of the seven has published
maths; the rest were read out of GPL-3.0 source, nearly all of it in a single place (see Reference
implementations below). Two attributions in the first draft of this document were wrong and are
corrected in place rather than silently: the "Perfect Narrowband Colors" video is
NarrowbandNormalization and not SPCC, and AstroColorMixer's model turned out to be public rather than
locked inside the app.

## Two use cases, one engine (read this before prioritising anything)

**The primary goal is a richer picture, not a photometric measurement.** A narrowband stack that
renders as two colours is the actual complaint; getting the line ratios provably right is not, on its
own, worth anything to that user. Where there is real signal, the job is to find it and bring it out.

**A true science mode is nonetheless in scope**, and the two are not in tension. The organising
principle for this whole plan:

> **Model as precisely as the data allows, always. The mode governs what you are permitted to ADD on
> top, not how well you model.**

Precise modelling is what *creates* aesthetic headroom rather than competing with it, because every
unit of real signal you correctly separate is a unit you no longer have to invent:

| Step | Science value | Picture value |
|---|---|---|
| Phase 0 continuum subtraction | Standard photometric practice; the line image is finally the line | **Removes stars physically rather than by inpainting**, and stops continuum being mixed into every channel |
| Phase 1 normalization | Makes channels commensurate | **This is the red-dominance fix.** Purely aesthetic motivation, achieved by a statistical method |
| Phase 3 unmixing | Recovers true line images | Cleaner line separation = more *real* colour separation for the mixer to work with |
| Three-line solve (Hb) | Correct OIII, uncontaminated | **A real, measured blue channel** instead of one synthesised from Ha |
| Phase 4 SPCC | Photometric truth | Little. Explicitly cannot produce the palettes people want (ADR-3) |

Note the last row: the *most* scientific item is the one with the least aesthetic payoff, and the
purely statistical item in row 1 is the one that fixes the complaint. Rigour is worth pursuing where
it yields signal, not as an end in itself. So the ordering is by **signal recovered per unit of
work**, which is why phase 1 leads and phase 4 is blocked at the back.

## The data model: a set of mono line images

**The pipeline's native input is N named mono planes (`Ha`, `OIII`, `SII`, `Hb`), not an RGB image.**
This is how the reference workflow is actually set up: the 2021 video's PixelMath operates on two
grayscale windows, `H` and `O`, that already exist as separate images before any mixing happens. Only
at the mix do they become RGB.

That is worth stating explicitly because the two hardware paths reach that state very differently:

| | How the lines get separated | Consequence |
|---|---|---|
| **Mono + filter wheel** | **Optically.** Each line was shot through its own filter and stacked into its own master | Already separated, perfectly, by construction. No unmixing exists or is needed |
| **OSC + dual/tri/quad-band** | **Algebraically**, from one RGB frame where R is mostly Ha and G/B are mostly OIII | Needs an unmixing step just to *reach* the state a mono imager starts from |

**So phase 3 is not a core phase, it is the OSC on-ramp.** A mono imager skips it entirely and gets a
strictly better result, because optical separation beats any algebraic estimate: there is no
crosstalk model, no sensor coefficient table, and no conditioning to worry about.

Two things fall out that were previously muddled in this document:

- **Phase 1 is about line planes, not RGB channels.** Alchemy states it as "align G and B to R"
  because its input is an OSC dual-band RGB, but the operation is really *align each weak line image
  to the reference (strongest, usually Ha) by median offset then signal-strength gain*. Stated that
  way it generalises to any number of planes and reads correctly for mono, which is the form we
  should implement.
- **SII is easy for mono and hard for OSC**, which inverts the earlier Deferred note. A mono imager
  with an SII filter simply has a fourth plane and full SHO is available immediately. A quad-band OSC
  user is trying to recover four lines from three channels, which is underdetermined. The difficulty
  was never SII; it was doing algebra on too few measurements.

Everything else composes per-plane without change: continuum subtraction (phase 0) subtracts a scaled
broadband from *each* line plane, and the phase 5 mask is built from *one* line plane, which is
exactly what the OIII range mask is.

## Status: NOT STARTED (research + decision recorded 2026-08-02)

| Phase | What | Where | Status |
|-------|------|-------|--------|
| **0** | **Continuum subtraction.** Remove the broadband starlight a narrowband filter also passes, via a photometric star-flux fit against a matched broadband frame. Prerequisite for everything below: without it the "line" images are line + continuum. | `TianWen.Lib/Imaging/`, new `ContinuumSubtractor` | NOT STARTED |
| 1 | **Robust plane normalization.** Align each weak line plane to the reference plane (usually Ha) by median offset then MAD/percentile gain, about the background. No catalog, no spectra, no new data. Works for any N. | `TianWen.Lib/Imaging/`, new `NarrowbandNormalizer` | NOT STARTED |
| 2 | **Palette mixer + named presets.** `Ha`/`OIII` to RGB as a per-channel lerp, applied globally. Presets name which effect they apply (H-beta vs hue rotation). | same | NOT STARTED |
| 3 | **Line unmixing: the OSC on-ramp only.** Recovers mono line planes from one dual/tri-band RGB frame, via DBXtract algebra + per-sensor crosstalk coefficients. **Mono imagers skip this entirely** and start at phase 1. **3a: the three-line Ha/Hb/OIII solve is the high-value variant** (exactly determined, the only source of *measured* blue). Gated on a known sensor. | same, plus a coefficient table asset | NOT STARTED |
| 4 | **SPCC narrowband mode.** Declared passbands convolved against real star spectra. Needs a Gaia DR3 spectra source. | `Astrometry/`, extends `Tycho2ColorCalibration` | NOT STARTED (blocked, see ADR-3) |
| 5 | **Masked colour adjustment.** Mask = (hue band and/or **line plane**) x luminance range, minus protection ramps (low-saturation / shadow / highlight). Then curves over Lab `L`/`C` + per-channel RGB, `C` as a hue-preserving scale of `a`,`b`. Reuses `FritschCarlsonSpline` (ADR-7). See ADR-8/11. | `Image.Masks.cs`, `MasterPreviewRenderer` | NOT STARTED (separate concern, see ADR-4/8) |
| 6 | **Narrowband star colour.** Synthesize plausible RGB stars from the line planes and recombine with the starless narrowband image. Fixes the magenta stars that narrowband colour calibration produces. | `TianWen.Lib/Imaging/`, composes with `SharpenPipeline`'s star lineage | NOT STARTED |

Phases 1 and 2 are the useful minimum and are independent of everything else. Phase 3 improves
phase 1 where the sensor is known. Phase 4 is a different feature that happens to share the word
"narrowband". Phase 5 is a display stage and is not colour calibration at all.

## The seven techniques

### A. Siril SPCC narrowband mode (built in, not a script)

[docs](https://siril.readthedocs.io/en/latest/processing/color-calibration/spcc.html)

**Attribution corrected 2026-08-02.** This section was originally headed by the "Perfect Narrowband
Colors in Siril" video. The transcript shows that video is **not about SPCC at all**: it covers
NarrowbandNormalization (technique F below). The SPCC description here is accurate, being taken from
the Siril docs, but no video backs it and nothing in the saved set demonstrates it.

Physics-based. You declare a **centre wavelength and a bandwidth per channel** (about 3 nm for
ultra-narrowband mono, up to about 35 nm for a quad-band OSC filter). Siril synthesizes a passband
from those two numbers rather than loading a measured transmission curve, then:

1. Plate-solves, queries Gaia DR3 for stars in frame.
2. Convolves each star's **`xp_sampled` spectrum** with the synthesized passband to get expected
   photon flux per channel.
3. Convolves the same passband with the white reference (default: average spiral galaxy) as the
   absolute anchor.
4. Robust linear fit of catalog-predicted vs image-measured **R/G and B/G flux ratios**.
5. Applies the resulting multiplicative coefficient per channel.

For HOO where two channels carry the same data, the docs say set both channels to the same nominal
wavelength and bandwidth.

**The documented non-goal:** Siril explicitly warns not to expect the Hubble palette from this, and
that SHO through SPCC produces "an image with a huge green cast". That is correct behaviour, not a
bug: SPCC reproduces true spectral intensities, and true SHO intensities *are* green-dominated. The
Hubble palette is an artistic remap, so a photometric calibrator should not be trying to produce it.

Limits worth recording: `xp_sampled` exists for sources brighter than about mag 17.6; the local
catalog keeps the 127 brightest per HEALpix level 8 to avoid crowding; atmospheric correction models
Rayleigh scattering only, with aerosol and molecular absorption unmodelled.

### B. VeraLux Alchemy (GPL-3.0 Python script)

[Video](https://www.youtube.com/watch?v=eaVgMztmm0Q) - [source](https://gitlab.com/free-astro/siril-scripts/-/tree/main/VeraLux)
(`VeraLux_Alchemy.py`, 990 lines, Riccardo Paterniti, v1.0.3). The "Now with Curves" in the video
title is the sibling `VeraLux_Curves.py` landing in the same suite, not a change to Alchemy.

Statistics-based, no astrophysics, **strictly linear domain in and out**. Three independent stages.

**B1. Robust normalization.** Per channel: median (background), MAD, and the 99.5th percentile.
Signal strength is `p99.5 - median`. Then align G and B to the R reference:

```
G = G - med_g + med_r                        # background offset
G = (G - med_r) * (str_r / str_g) + med_r    # gain, applied ABOUT the background
```

The second line is the load-bearing detail: applying gain about the median rather than about zero is
what stops a signal-amplitude match from dragging the black point with it. A manual boost multiplies
into the same gain. Output is clipped to [0, 1].

**B2. Quantum unmixing** (optional). DBXtract-derived algebra that solves Ha and OIII out of R/G/B
given nine per-sensor crosstalk coefficients (`r1,r2,r3,g1,g2,g3,b1,b2,b3`), tabulated for about 40
sensors including IMX533, IMX571, IMX294, IMX455 and a range of Canon bodies. Backgrounds are
removed per channel by median first, restored after:

```
cota   = min(g2 / r2, 0.12)                       # crosstalk suppression clamp
OIII_G = (g0 - cota * r0) / (g1 - g2 * r1 / r2)
OIII_B = (b0 - b2 * r0 / r2) / (b1 - b2 * r1 / r2)
OIII   = (2*g1*OIII_G + b1*OIII_B) / (2*g1 + b1) + max(bg_b, bg_g)
HA     = (r0 - r1 * (OIII - bg_gb)) / r2 + (bg_r + bg_gb)
```

with `r0/g0/b0` the background-subtracted channels. Degenerate coefficients (`|r2| < eps`, or either
denominator vanishing) fall back to the naive `Ha = R`, `OIII = (G + B) / 2`. Both outputs clipped
to [0, 1].

**B3. Palette mix.** A plain per-channel lerp between the two line images:

```
R_out = Ha * (1 - mix_r) + OIII * mix_r
G_out = Ha * (1 - mix_g) + OIII * mix_g
B_out = Ha * (1 - mix_b) + OIII * mix_b
```

Without unmixing, `Ha = R` and `OIII = (G + B) / 2` feed the same mixer.

**Its preview engine is our stretch.** Alchemy's WYSIWYG preview is a port of Siril's
`find_linked_midtones_balance`: MAD x 1.4826, shadow clipping at -2.8 sigma, target background 0.25,
then the standard MTF. That is the same autostretch `StretchSolver` already implements, so we get the
preview for free and, more usefully, it confirms our stretch matches the one the technique was tuned
against.

### C. AstroColorMixer (Siril port of a PixInsight script)

[Video](https://www.youtube.com/watch?v=oBRvQrOr8Yo) - [page](https://cosgrovescosmos.com/astro-color-mixer-web).
Patrick Cosgrove's script, ported to Siril by Cuiv.

**Model recovered 2026-08-02.** The Siril port is public GPL-3.0 source
([`processing/AstroColorMixer.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/AstroColorMixer.py),
Yannick Dutertre / Cuiv 2026, ported from Patrick Cosgrove's PixInsight original with explicit
permission, v1.1.9, 2767 lines). It is **richer than VeraLux Curves in the dimension that matters
here**, and this section supersedes the earlier note that its maths was unobtainable.

Operates on **stretched** nonlinear RGB, in **HSL** (not Lab). Three orthogonal pieces.

**1. Hue-band selection, and the band labels give the game away.** Eight fixed bands plus a custom
one, each named for what it means astronomically:

| Band | Centre | Label in the source |
|---|---|---|
| red | 0 deg | **Red / H-alpha** |
| orange | 30 | Orange / Galaxy Cores |
| yellow | 60 | Yellow / Warm Stars |
| green | 120 | Green / Cast Control |
| cyan | 180 | **Cyan / OIII** |
| blue | 240 | Blue / Reflection Nebula |
| purple | 275 | Purple / Violet Cleanup |
| magenta | 315 | Magenta / Halo Cleanup |

The mask itself is a smoothstep on circular hue distance:

```
distance = min(|h - c|, 360 - |h - c|)
inner    = width * (1 - feather)
t        = clamp((distance - inner) / (outer - inner), 0, 1)
mask     = 1 - t*t*(3 - 2t)
```

The custom band shapes a hue-arc triangle through **an MTF** (`balance = 1 - strength`; the source
comments that this reproduces PixInsight's `ColorMask`), then weights by `s^0.35` in Chrominance mode
or `l^0.45` in Lightness mode.

**2. A luminance range mask, ANDed with the band mask.** Note it carries *both* feather axes, which
settles the question raised in technique D:

```
m = smoothstep(low - feather, low, luma) * (1 - smoothstep(high, high + feather, luma))
```

value-domain feathering, plus an optional gamma boost `m^0.55`, plus an **independent spatial soften
radius**. Presets: All, Shadows `0-0.35`, Midtones `0.20-0.78`, Highlights `0.55-1.0`, Bright Stars
`0.78-1.0`. Final mask is band x range, and the UI can display the combined product.

**3. Protection ramps that subtract from the mask**, with different presets for stars-present vs
starless data: saturation floor (`satFloor` 0.03-0.05 to `satFull` 0.18-0.25), shadow floor, and
highlight roll-off (`highlightStart` 0.70 with stars, 0.85 starless). The **low-saturation protection
is the practically important one**: it stops the adjustment from saturating near-grey pixels, which
is what would otherwise turn background noise into chroma noise.

Per band you then get hue shift, saturation and luminance, with `SENSITIVITY_RANGES`
(Fine/Normal/Advanced/Strong) scaling the slider ranges rather than changing the algorithm. Curves
exist too (4096-entry LUT). Passes stack like Photoshop layers, and presets serialise to JSON.

**Where our advantage is, and it is a real one.** Those band labels are the tell: "Cyan / OIII" and
"Red / H-alpha" mean the tool is using **hue as a proxy for line identity**, because after mixing
down to RGB that is all it has. It cannot distinguish cyan-because-OIII from cyan-because-gradient or
cyan-because-noise. **We keep the line planes** (ADR-12), so our mask can be built from the actual
OIII signal (ADR-11) instead of inferred from the colour of the result. Same selection intent,
ground truth instead of inference.

### D. VeraLux Curves (GPL-3.0 Python script) - the readable reference for phase 5

`VeraLux_Curves.py`, 2152 lines, same suite and author as Alchemy, v1.0.1. Initially deferred as
"may or may not be interesting"; read on 2026-08-02 and it is **the same role as AstroColorMixer with
the maths visible**, which makes technique C's hidden model largely irrelevant to us.

Post-stretch, operates on `[0,1]` display-referred data, clipped between every stage.

**Split-domain apply order** is fixed: `RGB/K` (all channels through one LUT) -> per-channel `R`,
`G`, `B` -> `L` -> `S` -> `C`, each stage optional and each with its own independent luminance mask.

- **`L`**: convert to CIE Lab, curve `L/100`, convert back. Contrast without touching saturation.
- **`S`**: convert to HSV, curve the S channel. Blunt but predictable.
- **`C`**: the interesting one, and exactly what AstroColorMixer means by "nonlinear chroma-vector
  control". In Lab, `chroma = sqrt(a^2 + b^2)`, normalise by 128, run the curve, then scale **both**
  `a` and `b` by `c_new / chroma`. Because `a` and `b` are scaled by the same factor, the hue angle is
  exactly preserved and only the chroma magnitude moves. That is a genuinely better saturation
  control than an HSV S-curve, and it is four lines of maths.

**Masking is value-domain, not spatial, and this differs from ours.** Their feather is a sigmoid on
the luminance value:

```
lum   = mean(R,G,B)
lower = sigmoid(2.5 * (lum - lum_min) / feather)     # only when lum_min > 0
upper = sigmoid(2.5 * (lum_max - lum) / feather)     # only when lum_max < 1
mask  = min(lower, upper)
```

Our `Image.LuminanceRangeMask` feathers with a **spatial** Gaussian (`blurSigma = 3f`). These are not
the same thing: a value-domain roll-off has no spatial extent and so cannot bleed a selection across
an edge, where a Gaussian blur can and does. Both are then applied identically, as a lerp
`original * (1 - mask) + transformed * mask`, which is our `BlendThroughMask`.

**Correction (2026-08-02):** an earlier version of this section called the value-domain form "the
more correct primitive". That was too strong. PixInsight's `RangeSelection` deliberately combines a
*hard* value threshold with *heavy* spatial smoothing (see the mask-gated section below, where the
observed settings are fuzziness 0.00 and smoothness 35.5), because the spatial blur is not there to
feather an edge. It is there to turn a noisy per-pixel threshold into a smooth **structural envelope**
that follows the nebula rather than the noise. Both primitives are legitimate and they do different
jobs; we already have the spatial one.

**Their curve engine is Akima spline into a 65536-entry LUT. Do not copy that part** (see ADR-7).

### E. Continuum subtraction (PixInsight, SETI Astro, and standard observational practice)

**Not modelled anywhere in TianWen today** (zero occurrences of "continuum" in code, docs, plans or
TODO, checked 2026-08-02). It is also the step that logically precedes everything else here.

**The problem.** A 3 nm Ha filter does not pass only Ha. It passes a 3 nm slice of *everything*,
including the broadband continuum. Nebula emission is a line, so it appears only at 656.3 nm, but
stars are continuum sources and radiate across the whole band. So an "Ha" frame is really
`Ha_line + continuum`, and the continuum part is mostly stars plus any continuum-bright structure
(reflection nebulosity, galaxy disks). Every downstream number in this plan is computed on that sum.

**The fix.** Scale a matched broadband frame and subtract it:

```
Ha_pure = Ha - k * (R - median(R))
```

The `- median(R)` matters: subtracting the broadband *structure above its own background* rather than
the raw frame keeps the operation background-neutral instead of dragging a second pedestal in.

**Everything hinges on `k`**, and there are three ways to get it, in ascending order of quality:

1. **By hand.** Trial and error until stars stop standing out. Over-subtract and you punch dark holes
   where stellar contribution was high; under-subtract and residual star cores remain.
2. **From filter profiles.** Integrate the digitised narrowband and continuum transmission curves and
   take the ratio. Principled, but ignores the actual SED of the field and the real throughput.
3. **Photometrically, from the stars in the frame** (PixInsight's `PhotometricContinuumSubtraction`,
   and the standard observational method). Detect stars in both registered frames, measure flux in
   each, plot narrowband flux against broadband flux, and **the slope of the linear fit is `k`** by
   construction, since it is the ratio that forces stellar images to cancel. PCS defaults to 400
   stars and rejects anything peaking above 0.8 of full scale, because saturated stars are the fastest
   way to bend the fit. A visibly non-linear plot means too few stars, not a broken model.
4. **By minimising the flatness of the residual, with no star detection at all.** This is what
   Siril's [`ContinuumSubtraction.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/ContinuumSubtraction.py)
   does, and it is both cheaper and more robust than method 3 for our purposes:

   ```
   residual(k) = nb - (co - median(co)) * k
   objective   = AAD(residual) = mean(|x - mean(x)|)
   ```

   Sweep `k` coarsely over `linspace(-1, 5, 12)` to bracket, then finely over 40 points, then **fit
   an analytic smooth-V** `A*sqrt((k - s0)^2 + eps^2) + B` to the AAD-vs-k curve and take the vertex
   `s0` as the answer, clipped to `[0, 1]`. The fit is what gives sub-grid precision instead of being
   limited to the sweep resolution.

   The insight is neat: **correct subtraction is the flattest residual.** All the continuum structure
   cancels, so dispersion is minimised. Under-subtract and star cores survive; over-subtract and you
   punch dark holes. Either way you have *added* structure back and the AAD rises. It needs no star
   detection, no photometry and no catalog, just two registered frames and about 50 evaluations of a
   cheap global statistic.

   The same script also offers the inverse operation, blending the continuum-subtracted emission back
   into RGB with per-channel weights: `R' = R + (cs - median(cs)) * q * w_R`.

**Take method 4 first, and keep method 3 as a cross-check.** Method 4 needs nothing we do not
already have (a 1-D optimisation over a global statistic, plus `curve_fit`-equivalent for the vertex),
where method 3 needs star detection and matching across two frames. We *can* do method 3 cheaply
(`FindStarsAsync` detects and measures, the stacker's quad matcher already registers the frames, and
`Tycho2ColorCalibration` does star-flux fitting for SPCC), so it is worth having as a second opinion:
if the two disagree materially, something is wrong with the pairing or the frames. The genuinely new
work either way is associating a narrowband group with its broadband counterpart, which the stacker
does not model.

**Cost to the user:** a matched broadband frame. Mono imagers shooting Ha/OIII/SII plus RGB have it
already. An OSC dual-band user needs a separate broadband session, which is a real ask and the reason
this cannot be mandatory.

### F. NarrowbandNormalization (the SHO answer, and what that video was actually about)

[Video](https://www.youtube.com/watch?v=uLy9TA2Bo2A) -
[source](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/NarrowbandNormalization.py)
(GPL-3.0, Yannick Dutertre / Cuiv 2026, **clean-room implementation of Bill Blanshan and Mike
Cranfield's** PixInsight `NarrowbandNormalization` with Blanshan's permission; original at
cosmicphotons.com). Note Cranfield is the same author as the GHS stretch we already implement.

**The problem it solves is the one ADR-3 left open.** In SHO, Ha goes to green and Ha dominates, so
the Hubble palette comes out overwhelmingly green. The traditional fix is SCNR (green removal), and
the author's objection is exactly right: "you have removed a lot of information from your image".
NarrowbandNormalization instead *renormalizes the channels against each other* so green stops
dominating without discarding the green signal.

Applies to **stretched** data (its own instructions say so), takes a palette selector (SHO etc.) plus
per-line boosts (OIII boost, SII boost) and lightness controls.

**Scope note from the author, which maps cleanly onto our phases:** this "complements the existing
free script called VeraLux Alchemy which is more for HOO type of images with one-shot color cameras.
This one is really more complete." So Alchemy is the OSC/HOO tool and NarrowbandNormalization is the
mono/SHO tool. Our phase 1 plus phase 2 covers the same ground for both, which is the payoff of
ADR-12's N-plane model.

### G. Narrowband star colour (`NB_2_RGB`)

[Video](https://www.youtube.com/watch?v=ijFpFiQrBhQ) (Rich, Deep Space Astro) -
[source](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/NB_2_RGB.py)
(GPL-3.0, Cyril Richard from Franklin Marek / SAS code).

**The gap this fills has been flagged twice in this document without a solution.** Stars are continuum
sources, so through narrowband filters they carry no meaningful colour, and once you colour-calibrate
for a palette they come out **magenta**. It is why the published HOO recipes push Ha into green partly
just to stop stars going green, and why the RESCUE workflow removes stars before touching colour.

The fix is to synthesize star colour from the line planes rather than inventing or importing it:

```
R = 0.5*Ha + 0.5*(SII or Ha)
G = ratio*Ha + (1 - ratio)*OIII        # ratio default 0.30
B = OIII
```

then a star stretch, SCNR, and a fixed saturation boost of 1.2. Workflow: strip stars from the
palette composite and **discard them**, generate RGB stars from the narrowband planes, recombine.

### H. Spectral Extract (synthesised narrowband from OSC, by fitting the QE curves)

[Source](https://github.com/Ionfreefly01/siril-spectral-extract) (GPL-3.0-or-later, Python 3 +
PyQt6 + OpenCV + tifffile, a Siril 1.4 script). Found 2026-08-11.

**It attacks phase 3 from the other end, and the method is better founded than the one phase 3
currently names.** Where `DBXtract` hardcodes per-channel line responses for one sensor and one
filter, this *derives* the mixing coefficients per sensor by fitting the channel response curves to
the passband you asked for, as a regularised least squares:

```
minimise || T(lambda) - (a_R*QE_R(lambda) + a_G*QE_G(lambda) + a_B*QE_B(lambda)) ||^2 + alpha*||a||^2
extracted = a_R*R + a_G*G + a_B*B
```

Three properties worth having, none of which the hardcoded-table approach gives:

- **It fits the whole curve, not the transmission at one wavelength.** Our measured table above reads
  throughput at 4861 / 5007 / 6563 / 6717 Angstrom. That says how much of each line reaches each
  channel; it says nothing about what continuum comes with it. Fitting against `T(lambda)` minimises
  out-of-band leakage explicitly, which is what makes the result a *passband* rather than a ratio.
- **Negative coefficients are the continuum rejection.** Subtracting the broadband light that leaked
  into the other channels is what buys an effective passband narrower than any single channel.
- **The Tikhonov term is not decoration.** Negative coefficients amplify noise, so `alpha` is the
  knob that trades achieved bandwidth against noise gain. A phase 3 unmix without one has that
  trade-off happening anyway, just not under anyone's control.

**Do not import its headline limitation without checking whether it applies to us. It probably does
not.** The author is admirably blunt: "three broadband measurements cannot be unmixed into a 3 nm
passband", and a 12 nm Ha request comes back at about 88 nm effective. That is measured for a **bare
OSC with no narrowband filter**, where the only spectral structure available is the CFA itself, and
it is the correct answer for that problem. **Phase 3's input is a different and much better
conditioned one: an OSC behind a dual, tri or quad-band filter.** The filter has already done the
narrowing, so the solve is not "synthesise 12 nm out of a 100 nm channel" but "separate two lines
the CFA mixed, inside windows that are already narrow". Conflating the two would make phase 3 look
impossible when it is not. The converse error is just as available: the measured table shows Ha
arriving in green at about 0.19 of red on every one of these filters, so the mixing is real and a
naive channel assignment is not a substitute for the solve.

**What this costs us is small, because the inputs are already shipped.** `FilterCurveDatabase`
carries `SONY_CMOS_{R,G,B}-UVIRCUT_/_<filter>` and the Canon equivalents for L-eNhance, L-eXtreme,
Antlia ALP-T and Antlia Triband, which is sensor CFA times filter per channel: exactly the
`QE_{R,G,B}(lambda)` this fit needs, pre-convolved. `CameraColorMatrix.ComputeCamXyz(qe, cfaR, cfaG,
cfaB)` already integrates curves of that shape. The missing piece is a three-unknown regularised
least-squares solve and a target-passband generator, not a new data dependency. That is a
meaningfully cheaper phase 3 than sourcing or measuring a crosstalk table.

**The single most useful thing to take is the honesty, not the algebra: report the achieved
passband.** The tool tells the user "you asked for 12 nm, this sensor gives you 88". Nothing in this
plan currently produces that number, and phase 3 without it is a black box that silently returns a
worse answer on a filter it cannot serve. Emitting requested-versus-achieved per channel, from the
fitted residual, is what would let the phase refuse a job instead of doing it badly. It also gives
the verification sweep in section (b) something concrete to measure per filter and sensor.

**Licence changes nothing here.** GPL-3.0-or-later, so ADR-2 applies exactly as revised: lawful to
vendor under AGPL-3.0 since 2026-08-11, still reimplemented by preference rather than by
prohibition. It is Python against PyQt6, OpenCV and Siril's scripting host in any case, so there is
nothing portable to lift; what transfers is the formulation above.

**Note the convergence.** That green row is our phase 2 lerp exactly, with `mix_g = 0.70`, which is
*the same 0.7/0.3 split* the RESCUE video used for its nebula mix. Two independent sources landing on
the same coefficient for the same reason (rotate hue off the Ha/OIII extremes) is decent evidence the
number is not arbitrary.

This composes with what we have: `SharpenPipeline` already models star removal with a
stars/starless lineage (`SharpenIntermediates.StarsAndStarlessLineage`), so phase 6 is a different
*filling* for the stars plate rather than new pipeline structure.

## The HOO blue problem (why the mixer needs presets, not just sliders)

[Video](https://www.youtube.com/watch?v=2QS2Pyhf7as) ("RESCUE the BLUE! Dual Narrowband and HOO
pictures!", Cuiv, **October 2021**, so this one is an older tutorial rather than part of the recent
batch). The video's own steps could not be extracted; what follows is the problem it addresses and
the standard solution family, from the PixelMath references below plus the underlying physics.

**Naive HOO is rank-deficient, and that is the whole problem.** Assigning `R = Ha`, `G = OIII`,
`B = OIII` makes G and B *identical arrays*. Two independent signals are being painted into a
three-dimensional colour space, so every OIII region lands on exactly one hue (cyan, 180 degrees) and
no amount of stretching, saturation or curves can produce blue. It is not that blue is hard to get;
there is no degree of freedom that could produce it. Any fix must therefore introduce a third
quantity, and there are only two candidates.

**Fix 1: mix Ha into blue, standing in for H-beta.** The physically honest one. Real nebulae emit
H-beta at 486.1 nm, which genuinely is blue, and it is tied to H-alpha by the Balmer decrement: Case
B recombination at T = 10,000 K, n_e = 100 cm^-3 gives an intrinsic Ha/Hb of **2.86**, i.e.
Hb ≈ 0.35 x Ha. Published recipes:

| Blend | R | G | B | As mixer coefficients `(mix_r, mix_g, mix_b)` |
|---|---|---|---|---|
| Naive HOO | Ha | OIII | OIII | `(0, 1, 1)` |
| H-beta natural | Ha | OIII | 0.15 Ha + 0.85 OIII | `(0, 1, 0.85)` |
| Tweaked natural (Rista) | Ha | 0.20 Ha + 0.80 OIII | 0.15 Ha + 0.85 OIII | `(0, 0.80, 0.85)` |
| Blue rescue (video) | Ha | 0.30 Ha + 0.70 OIII | 0.05 Ha + 0.95 OIII | `(0, 0.70, 0.95)` |

**Confirmed against the real PixelMath from the video** (user screenshots, G and B tabs):

```
G = O*0.7  + H*0.3
B = O*0.95 + H*0.05
```

so `(mix_r, mix_g, mix_b) = (0, 0.70, 0.95)`, with R presumably plain `H` (tab not captured).

Two things that settles immediately. The blend is a **weighted sum of the two line images**, so the
Phase 2 lerp is the right primitive and no matrix machinery is needed. And both rows **sum to 1.0**,
the convention that stops the mix changing overall brightness, which a lerp enforces structurally
where an unconstrained 3x2 matrix would not.

**But the numbers refute the H-beta reading, and that is the more useful finding.** Green takes
*six times* more Ha than blue. Under the H-beta rationale below you would expect the opposite, or at
least parity, since H-beta is a blue line. Work the hues instead:

| | naive HOO | video's mix |
|---|---|---|
| Ha region | `(1, 0, 0)` pure red | `(1, 0.30, 0.05)` red-orange |
| OIII region | `(0, 1, 1)` **cyan** | `(0, 0.70, 0.95)` **blue** |

That is the actual mechanism, and it is what the title means. **Rescuing the blue is done by
starving green, not by feeding blue.** Dropping green's OIII share to 0.70 while blue keeps 0.95
rotates the OIII hue off cyan and toward real blue; the Ha backfilled into green exists to hold the
brightness and, as a bonus, warms the Ha regions from flat red to orange and stops stars going green.

So there are **two independent reasons to put Ha into a channel**, and they are easy to conflate:

1. **H-beta (physical).** Ha into *blue*, ~15%, because Hb is genuinely emitted and genuinely blue.
   Makes Ha regions slightly magenta. Justified by the Balmer decrement below.
2. **Hue rotation (aesthetic, but principled).** Ha into *green*, 20-35%, to move OIII off cyan
   toward blue. Nothing to do with H-beta; it is a colour-geometry fix for a rank-deficient palette.

Cuiv's numbers are overwhelmingly (2) with a trace of (1). Jon Rista's `(0, 0.80, 0.85)` is a
roughly even mix of both. **Presets must therefore name which effect they are applying**, or the
"H-beta" label ends up on a coefficient that has nothing to do with H-beta. Correcting this here
because the first version of this section asserted the H-beta rationale for all of it, on the
strength of one reference, before the actual coefficients were available.

**The H-beta rationale is filter-dependent, and gets it wrong in both directions.** This is the
detail that makes rationale (1) unsafe as a blanket default, and it splits the hardware in two:

| Filter | H-beta 486.1 nm | Consequence for "mix Ha into blue" |
|---|---|---|
| Mono 3 nm Ha + 3 nm OIII (e.g. Antlia) | **Blocked.** 486.1 is 14.6 nm off OIII 500.7, far outside a 3 nm passband | Hb was never measured. The blend *invents* it from Ha |
| Optolong L-eXtreme (dual 7 nm) | **Blocked deliberately**, for contrast in light pollution | Same as above |
| Optolong L-eNhance (tri-band) | **Passes.** Its 24 nm bandpass spans both OIII and Hb | Hb is already in the blue data. Adding Ha **double-counts it** |
| Quad-band (Ha/Hb/OIII/SII) | **Passes** | Same double-count |

So the same coefficient is a reasonable estimate on one filter and a straightforward error on
another.

**But framing the Hb-passing filter as "contaminated" is backwards, and this is the important
correction.** Under the picture-first goal above, an L-eNhance is not a filter with a polluted OIII
channel. It is a filter that **captured a third real emission line**. Hb at 486.1 nm is genuinely
blue and genuinely measured, so:

- **The rank-2 problem is solved physically, not cosmetically.** With Ha, Hb and OIII there are three
  independent line images, which is exactly the third degree of freedom that naive HOO lacks. Blue
  stops being something you synthesise from Ha and becomes something you **recover from signal that
  is already in the file**.
- **Three lines across three channels is exactly determined.** Ha lands in R, OIII 500.7 in G and B,
  Hb 486.1 mostly in B with some G. Three measurements, three unknowns. So the harder-looking case is
  the one with a unique solution, where the two-line dual-band case is genuinely underdetermined and
  can only ever be estimated.

That inverts the priority. A three-line solve is not a purist refinement to bolt on after the easy
work; it is **the single largest source of real colour** available to a tri-band or quad-band user,
and it delivers to the picture use case and the science use case with the same computation. It is
still sequenced after phases 1-2 because those are cheap and universal, but it should be understood
as high-value rather than as pedantry.

Caveat worth carrying: Hb is intrinsically about 1/2.86 of Ha and is often faint, so how much real
blue this recovers is object-dependent and SNR-limited. Exactly determined does not mean
well-conditioned.

Two further consequences worth stating before anyone builds on this:

- **On an Hb-passing filter the "OIII" channel is not OIII.** A 24 nm window covering both 500.7 and
  486.1 delivers OIII **plus** Hb summed into one measurement. That is a real contamination, not a
  labelling quibble, and it means phase 3's unmixing (a strictly two-line Ha/OIII model) is solving
  the wrong system for exactly the filters where a third line is present. Three lines in three
  channels is potentially exactly determined and therefore *more* tractable, but it is a different
  system with a different coefficient set.
- **Phase 4 cannot describe these filters either.** SPCC narrowband takes one centre wavelength and
  one bandwidth per channel, which cannot represent a channel that sees two lines.

**Rationale (2), hue rotation, is filter-independent**, because it is colour geometry rather than
physics: starving green to move OIII off cyan works the same whatever the filter passed. That
asymmetry is what decides the default (ADR-5).

### The full workflow, from the author's transcript

The user supplied the video transcript, which settles several things this document had inferred from
screenshots. Timestamps are from that transcript.

**The order of operations, start to finish:**

1. Background extraction (ABE + DBE) on each **linear mono** plane (02:17).
2. **`EZ_SoftStretch` on each plane separately**, Ha and OIII (03:32).
3. PixelMath mix into RGB (03:56).
4. **StarNet++ on all three images**, keeping a star mask for later (05:07).
5. `RangeSelection` on **OIII** to build `range_mask` (06:16).
6. Curves through that mask: B, then c, then G, then R, then saturation (08:10).
7. **The same again with a Ha-derived mask** and warmer curves (10:03).
8. ABE + DBE *again*, because the masked work amplified vignetting into blue corners (11:38).
9. Global curves on the whole image, RGB contrast plus c (13:21).
10. `DarkStructureEnhance`, default parameters (14:35).
11. Stars added back: PixelMath `$T + star_mask` (15:01), then `EZ_StarReduction`, then denoise.

**Four corrections and confirmations this forces:**

- **ADR-12 is confirmed in the author's own words** (03:07): "you could use here it's h alpha and
  oxygen 3 directly from a monochrome camera, but if you had an L-eXtreme it works exactly the same
  way: you separate your RGB channels and then you combine green and blue in PixelMath to create an
  oxygen 3 and you use red as h alpha". Mono is the native form and OSC is an on-ramp, exactly as
  ADR-12 states. Note his on-ramp is the naive `OIII = (G+B)/2`, not a crosstalk unmix, which is
  precisely the gap phase 3 fills.
- **The entire colour workflow runs STARLESS**, which this document had missed. Stars are removed
  before the masks are built and added back at the very end. That is load-bearing rather than
  incidental: it keeps stars out of the range mask (a star is bright in OIII and would otherwise be
  selected) and stops the colour curves from tinting star cores.
- **Each plane is stretched *before* the mix**, so the mix is on display-referred data. This differs
  from Alchemy, which is emphatically linear-domain, and the per-plane soft stretch is effectively
  doing phase 1's job: separately auto-stretching each plane is a crude way of making planes
  commensurate. Two valid orderings therefore exist, and we chose linear (see ADR-13).
- **There are two masked passes, one per line**, not one. OIII mask drives the blue; Ha mask drives
  the warm tones. The technique generalises to "for each line plane, build a mask from it and adjust
  colour through it", which extends to SHO for free.

**Two things the author says that are worth quoting.** On the 5% Ha in blue (04:24): the original
recipe "had oxygen 3 times 0.95 plus 0.05 times h alpha... **that might be psychological**". So even
the author doubts the blue term does anything, which independently supports this document's finding
that the *green* term is what rotates the hue. And on the ethic (18:52), which is ADR-9 arrived at
from the other direction: "without betraying the spirit of the data... because we're using only masks
and range masks we're using the original data to pull that blue back... we're not selecting a
specific area that we like to be blue, we're actually using the original data".

Credit, per the author, belongs to an unnamed Cloudy Nights post he could not relocate.

### The mix is gated by a signal-presence mask, not applied globally

The 2021 video keeps the PixelMath mix global, then builds a **range mask from the OIII line image
itself** and uses it to gate a **`CurvesTransformation` applied afterwards** (user screenshots; the
narration is "will decide how much of the O3 will become a blue kind of...", and the target image's
status bar reads `Modified - Masked`). The full chain is:

1. **Global** PixelMath mix, `G = 0.7 O + 0.3 H`, `B = 0.95 O + 0.05 H`, producing the RGB composite.
2. `RangeSelection` on the **O** channel, producing `range_mask`.
3. That mask applied to the composite.
4. **Masked per-channel RGB curves.** In the observed frame the red curve is pulled well below the
   diagonal while green stays near it, so red is suppressed *only inside the mask*. That is the
   actual rescue: the OIII regions go blue while the Ha regions keep their colour.

Observed `RangeSelection` settings, source image `O`:

| Parameter | Value | What it does |
|---|---|---|
| Lower limit | 0.24 | Threshold: only pixels above 24% in the OIII image participate |
| Upper limit | 1.00 | No upper bound |
| Fuzziness | 0.00 | **Hard** threshold, no value-domain ramp |
| Smoothness | 35.5 | **Heavy spatial blur** of the resulting mask |
| Lightness | on | Mask from lightness rather than per-channel |

Hard threshold plus heavy blur is a deliberate pairing, not a contradiction. The threshold answers
"is there OIII here", which per-pixel is a noisy binary question; the large smoothing turns that into
a smooth envelope tracking the **structure** of the nebula. The output (visible in the screenshot) is
a soft grey map of where the OIII actually lives.

**Why this matters more than the coefficients do.** A global mix tints the entire frame, including
regions with no OIII at all, where "rescuing the blue" just means colouring noise and Ha-only
structure. Gating on the line image means the enhancement is applied **only where there is real
signal to enhance**, which is the plan's governing principle (ADR-9) expressed as a mask rather than
as a model. It also makes the strong coefficients safe: you can push a much more aggressive blue
inside the mask than you would ever dare apply globally.

**Where this lands in our phases.** The mix itself stays global (phase 2, three constants). The
gating belongs to the **colour-adjustment stage, phase 5**, which is already specced as masked curves
over `L`/`S`/`C` plus per-channel RGB from technique D. The single change phase 5 needs is that its
mask may be sourced from a **line image** rather than from composite luminance, which our
`LuminanceRangeMask` cannot currently do.

That is a smaller change than treating the mix as a field would have been, and it composes better:
one global palette decision, then one spatially-gated colour adjustment, rather than coefficients
that vary per pixel for reasons the user cannot see.

**We can automate the one manual step.** The 0.24 threshold is hand-tuned per image, which is why
this reads as a fiddly manual process. It is really asking "where is this pixel above the noise?",
which is a question we already answer: `Image.Background()`'s iteratively sigma-clipped noise
estimate is exactly the quantity `FindStarsAsync` builds its detection level from (`3.5 x noise`).
Deriving the threshold from the OIII image's own statistics turns the manual step into a default,
with the slider kept as an override. (Precision note: `Background()`'s noise term is a clipped SD,
not MAD; the codebase computes MAD separately in `GetPedestralMedianAndMADScaledToUnit`.)

**And phase 0 makes the mask honest.** On a continuum-contaminated OIII frame, a brightness threshold
selects "anything bright", which includes stars and reflection nebulosity. After continuum
subtraction the OIII image is pure line emission, so the same threshold becomes a genuine "is there
OIII here" test. The phases compound.

**And yes, the modern scripts subsume this entirely.** Alchemy's `mix_r/mix_g/mix_b` is exactly this
PixelMath with a live preview instead of three dialog round-trips; the 2021 video is the manual form
of the same arithmetic. Nothing here needs implementing twice.

**Why practice says 5-20% when the physics says 35%** (this is inference, not sourced, but it is
consistent and worth recording): 2.86 is the *unattenuated* ratio. Dust extinction reddens, so the
*observed* Ha/Hb runs higher, typically 4 to 6 in a dusty emission region, which puts the observed
Hb/Ha at 0.17 to 0.25. The empirical 15-20% is the Balmer decrement plus typical galactic extinction.
That is a satisfying result, because it means the preset is not a taste knob: it is an estimate of a
real line ratio, and a user imaging a low-extinction target has a principled reason to push it up.

The second reason those recipes put Ha into *green* as well is unrelated to physics: it stops stars
going green, since a star is broadband and lands in the OIII channel with nothing to balance it.

**Fix 2: keep the blue channel's own OIII measurement.** On an OSC dual-band, OIII at 500.7 nm lands
in both the G and B photosites, at different QE. So `OIII_B` really is an independent measurement,
just a noisier one, and using it directly gives blue a real degree of freedom. **Note the tension:**
Alchemy's unmixing (technique B2 above) deliberately *destroys* this by collapsing both into one
weighted OIII estimate, `(2*g1*OIII_G + b1*OIII_B) / (2*g1 + b1)`, because its goal is one clean
line image. Phases 2 and 3 therefore pull in opposite directions on blue, and that is a real design
decision to make rather than an accident to discover later.

**Consequence for the plan:** the Phase 2 mixer as specified already expresses fix 1 exactly (the
table above is just three settings of the existing lerp), so this costs no new mechanism. What it
changes is that shipping bare sliders would be a mistake. The coefficients encode a line ratio, so
they ship as **named presets with the reasoning attached**, defaulting to a natural blend rather
than to naive HOO.

References: [Jon Rista, HOO with
PixelMath](https://jonrista.com/the-astrophotographers-guide/pixinsights/narrow-band-combinations-with-pixelmath-hoo/)
(source of the blend percentages); Cannistra bicolor technique and Light Vortex Astronomy's bicolour
tutorial are the other standard references, both currently unreachable over TLS (expired certificate
and a handshake failure respectively).

## Validating this against the `D:\Astro-Pics` archive

The dataset builder's archive sweep (`tianwen dataset build`, P0 shipped, not yet run on the real
archive) is the natural vehicle for both *using* and *verifying* everything above. Recorded here
because the two workstreams need each other and neither plan said so.

**The blocker this exposed is now cleared (2026-08-02).** `SessionDiscovery.GroupSessions` keyed
sessions on `(SessionDir, Instrument, Target)` with **no filter**, so a mono Ha+OIII night collapsed
into one session: the star-count gate saw a bimodal population and rejected the OIII frames, and
`SessionRegistrar` stacked both filters into one meaningless master. The key now carries the filter.

**And it disproved an assumption this section made**, which matters more than the fix. Keying on the
canonical `Filter.Name` (what `MasterGroupKey` compares on) would not have worked:
`Filter.FromName`'s patterns are **anchored**, so real header text like `"Ha 3nm"`, `"OIII 3nm"` or
`"Antlia ALP-T"` matches nothing and canonicalises to a single `Filter.Unknown`. The session key
therefore falls back to the raw header text; see [known-limitations.md](../known-limitations.md).

The same hole runs straight through the bandpass claim below. `Filter.Unknown` carries
`Bandpass.None`, and `FILTCLAS` (the coarse-classification card that would rescue the parse) is a
**TianWen-written convention**, not something N.I.N.A. emits, so on a N.I.N.A.-captured archive the
bandpass is derived from `FILTER` through that same anchored parse. **On `D:\Astro-Pics` a 3 nm Ha
frame most likely reports `Bandpass.None`, not `Bandpass.Ha`.** Auditing the distinct `FILTER` values
in the archive is the first thing the sweep should print.

### The resolver is mostly already shipped, and it covers the OSC case first

An earlier draft of this section called for a new widened name parser. That was written without
checking, and it is wrong for the case that matters most here: **`FilterCurveDatabase` already ships
spectral transmission curves**, embedded as `filter_curves.gs.gz`, with `TryMatchFilter` doing
token-overlap matching from a user filter string onto them. A curve beats a name map outright,
because the bandpass is not looked up, it is **measured**: interpolate throughput at 4861 / 5007 /
6563 / 6717 Å and the answer falls out, multi-line filters included.

Coverage is the inverse of what this plan assumed. Of 176 curves:

- **The OSC dual-band case is covered, pre-convolved with the CFA.** Entries exist as
  `SONY_CMOS_{R,G,B}-UVIRCUT_/_<filter>` and `CANON_FULL_SPECTRUM_{R,G,B}_/_<filter>` for
  **L-eNhance, L-eXtreme, Antlia ALP-T and Antlia Triband**. That is sensor CFA times filter, per
  channel, which is exactly the quantity phase 3 unmixing needs.
- **Mono narrowband is not covered at all.** There is not one standalone Ha / OIII / SII / Hb curve
  in the file; the Astronomik / Astrodon / Chroma / ZWO entries are LRGB sets. So the widened name
  parse is still needed, but only for the mono path, and it is the smaller half.

**Measured from the shipped curves** (Sony CMOS UV/IR-cut CFA, transmission at each line):

| Filter | passbands (>30%) | Hb 4861 | OIII 5007 | Ha 6563 | SII 6717 |
|---|---|---|---|---|---|
| L-eNhance | R 6500-6620, G/B 4820-5100 | **64-67%** | 44-87% | 74% (R) | 0% |
| L-eXtreme | R 6520-6600, G/B 4960-5040 | **0%** | 38-75% | 71% (R) | 0% |
| Antlia ALP-T | R 6540-6600, G/B 4980-5040 | **0%** | 33-65% | 75% (R) | 0% |
| Antlia Triband | R 6500-6820, G 4920-5220, B 4260-4480 + 4940-5140 | **~0.4%** | 44-88% | 79% (R) | **74% (R)** |

Three things fall out, two of which correct claims made above.

1. **ADR-5's H-beta question is answerable today, per filter, from our own data.** L-eNhance passes
   Hb at 64-67% because its blue-green band is one wide 4820-5100 Å window covering Hb *and* OIII;
   L-eXtreme and ALP-T cut at 4960-4980 and block it outright. That is the user's "an L-eNhance does,
   an Antlia 3nm probably doesn't", now measured rather than assumed.
2. **"Tri-band" does not mean `Ha | Hb | OIII`, and the claim below that it does is wrong.** Antlia
   Triband's three bands are a blue 4260-4480 window (which contains Hγ, *not* Hb), OIII, and a wide
   6500-6820 red window. So it blocks Hb while a filter marketed as narrower passes it, and its red
   band is broad enough to pass SII at 74% (not a designed SII channel: the band is continuous from
   Ha through SII, so those two lines are **not separable** on this filter). A marketing category
   name predicts nothing. Only the curve knows.
3. **This may retire ADR-2's crosstalk-table question for OSC.** DBXtract hardcodes per-channel line
   responses for one sensor and filter; the table above *is* that quantity, derived from data we
   already ship, for four real filters on two CFA families. Worth checking our numbers against the
   script before relying on it, and one discrepancy is already visible: the Ha green-over-red ratio
   is ~0.19 for every one of these filters, where DBXtract clamps its analogous term to 0.12.

Note what this does **not** change: the session key. Grouping asks "are these two frames the same
filter", which raw header text answers correctly, cheaply and synchronously. Resolving to a curve
would make grouping depend on an async resource load, and worse, would let two genuinely different
filters that fuzzy-match one curve entry collapse into a single session, which is the exact merge
this all exists to prevent. Identity and interpretation are different jobs.

**What we already have that this plan assumed we would need to build.** `MasterGroupKey` carries
`FilterName` *and* `FilterBandpass`, and `Bandpass` is a bit-flags enum whose members are exactly
`Red|Green|Blue` and **`Ha`, `Hb`, `OIII`, `SII`**. So:

- **Narrowband-vs-broadband dispatch needs no new metadata**, but the bits have to get populated
  first. Test the bandpass bits *after* resolving through `FilterCurveDatabase` (OSC dual-band) or a
  widened name parse (mono); a descriptive `FILTER` string currently yields `Bandpass.None` on its
  own. The data model is right and only the recogniser was missing, which is a much better position
  than the reverse.
- **ADR-5's filter dependence is expressible in the model we have**, but do not populate it from the
  filter's marketing category. `Ha | OIII` for a dual-band is right; `Ha | Hb | OIII` for a
  "tri-band" is **not** (see the measured table above: Antlia Triband blocks Hb and passes SII).
  Derive the bits from the curve. Note what an unresolved filter costs: `None` is neither "passes
  Hb" nor "blocks Hb", so the preset gate must treat unresolved as unknown and fall back to hue
  rotation, the blind-safe default ADR-5 already names, rather than guessing.
- **Phase 0's frame pairing is nearly free.** `LightGroupKey(MasterGroupKey, ObjectName)` is almost
  the pairing key already: same `ObjectName`, one narrowband bandpass and one broadband, same train.

### (a) Use

- **Phase 0 needs a matched broadband frame**, and the archive scan is precisely the thing that can
  find one. Pair on object plus optical train, differing in bandpass.
- The scan can report **what narrowband data actually exists**: which targets, filters, sensors, and
  crucially which targets have both narrowband and broadband coverage. That inventory is the
  precondition for every item below, and nothing currently produces it.

### (b) Verify

- **Measure our own crosstalk coefficients instead of sourcing DBXtract's table.** This is the one
  worth doing, and since 2026-08-11 the reason is purely technical: taking DBXtract's table is lawful
  under AGPL-3.0, so this is no longer about permission. **Fit them from our own data** wherever the
  archive has the same target through a dual-band OSC and through mono narrowband, or dual-band data
  with a known sensor. Measured coefficients ground phase 3 in *this* rig rather than a published
  average over somebody else's filters and sensor, which is the actual benefit; retiring the licence
  question was only ever a side effect and is now moot.
- **Does the AAD scale solver converge on real pairs?** Cheap to check: run it across every
  narrowband/broadband pair the scan finds and look at the distribution of `k` and the residual
  curvature. A well-behaved solver should give a tight `k` per filter/sensor combination.
- **Does phase 1 normalization actually fix red-dominance?** Measure channel medians and the
  post-mix hue distribution before and after, across many targets rather than the one example every
  tutorial uses.
- **Cross-check phase 0 method 3 against method 4** on the same pairs. Material disagreement means
  the pairing or the frames are wrong, which is exactly what an archive sweep should surface.

Note the shape: these are *measurements over a corpus*, which is what the dataset builder already is.
`DatasetPsfNoiseReport` is the precedent, a report emitted from the same scan.

## Reference implementations (all GPL-3.0, algorithms only per ADR-2)

Nearly every phase of this plan has a readable reference in one place, the official Siril script
repository, [`free-astro/siril-scripts`](https://gitlab.com/free-astro/siril-scripts). Recorded
because it was discovered late and would have saved earlier guessing:

| Phase | Script | What to take |
|---|---|---|
| 0 | `processing/ContinuumSubtraction.py` | The AAD-minimisation scale solver + smooth-V vertex fit |
| 1-2 | `VeraLux/VeraLux_Alchemy.py` | Robust plane normalization; palette lerp (OSC/HOO) |
| 1-2 | `processing/NarrowbandNormalization.py` | The mono/SHO counterpart; green-dominance fix without SCNR |
| 2 | `processing/PalettePicker.py`, `Narrowband_Palette_Picker.py` | Palette presets |
| 3 | **`processing/DBXtract.py`** | The crosstalk coefficients **at their actual source**, which is what ADR-2 requires rather than lifting them from Alchemy |
| 3 | `processing/Hubble_Palette_from_Dual-Band_OSC.py` | The OSC on-ramp |
| 5 | `processing/AstroColorMixer.py` | Hue-band masks, range masks, protection ramps |
| 5 | `VeraLux/VeraLux_Curves.py` | Lab `L`/`C` curve domains |
| 6 | `processing/NB_2_RGB.py` | Narrowband star colour |

### A published OSC->passband synthesiser, and what it does NOT solve

[`Ionfreefly01/siril-spectral-extract`](https://github.com/Ionfreefly01/siril-spectral-extract)
(Python/PyQt6, GPL-3.0-or-later, so ADR-2 applies: algorithm only). It synthesises a requested
passband as a **weighted sum of the three CFA response curves**, regularised, then applies those
coefficients to the pixels:

```
minimise  || T(lambda) - (aR*QE_R + aG*QE_G + aB*QE_B) ||^2 + alpha*||a||^2
extracted = aR*R + aG*G + aB*B
```

The passband `T` is declared as centre + FWHM + a **shape order** (1 = Gaussian, 3-5 = flat-top with
steep shoulders, mimicking a real interference filter). Coefficients may be **negative**, which is
what lets the synthesised response be narrower than any single channel, and the result is normalised
to unity at the centre. Continuum rejection is a separate **Lagrange-multiplier constraint** -- the
coefficients must not respond to broadband light -- solved exactly and then blended in by a slider
amount, which is a cleaner formulation than subtracting a scaled continuum after the fact and is
worth taking for phase 0 alongside `ContinuumSubtraction.py`.

**Its own README states the limit plainly: "three broadband measurements cannot be unmixed into a
3 nm passband."** So this is the best CFA-basis APPROXIMATION to a requested response, not recovery
of a narrowband signal, and it must not be described as extraction. That makes it a phase 0 / phase 3
reference and the concrete form of the ADR-12 on-ramp -- **not** a phase 4 one.

**It does not unblock ADR-3, and the distinction is easy to lose.** Both PixInsight's narrowband SPCC
and this tool "fit curves", but over different things: SPCC fits over **per-star Gaia DR3
`xp_sampled` spectra**, which is the data ADR-3 is blocked on, while this fits over **sensor response
curves**, which say nothing about any star. Nothing here supplies a spectrum, so phase 4 stays exactly
where it was.

**We are better placed to run this fit than the reference is.** Its four built-in presets are, in its
own words, "representative shapes, not measured data for any specific camera", with measured QE
loadable from CSV as an optional extra -- and measured curves are the one input we have in quantity:
`FilterCurveDatabase` carries 180, including real sensor QE (IMX533/571/455/585/183/462), the Sony and
Canon CFA families, and the pre-convolved sensor x CFA x filter sets.

**One trap that follows directly.** For a frame shot THROUGH a duo-band filter, the `QE_R/G/B` in that
fit must be the **pre-convolved sensor x CFA x filter** response, never the bare CFA -- the filter is
in the light path and dominates the shape. Getting this wrong is not hypothetical: the fuzzy matcher
used to resolve `CFA_R` to `BAADER_R`, putting a mono dichroic into a modelled OSC throughput and
skewing a real SPCC fit (see [known-limitations.md](../known-limitations.md)). The duo-band curves
this needs for the ColourMagic filters are now in the database
(`ASKAR_COLOURMAGIC_D1`/`D2`), digitised from the vendor charts.

## Pros and cons

(Historical note: this table compares the three techniques known when ADR-1 was decided, and it is
what that decision was argued from. Techniques D-G arrived later and are decided in their own
sections; they are deliberately not retrofitted into the table, because the decision it justified
has not changed.)


| | A. SPCC narrowband | B. VeraLux Alchemy | C. AstroColorMixer |
|---|---|---|---|
| **Basis** | Photometric: real star spectra x declared passband | Robust statistics on the image itself | Manual chroma editing |
| **Domain** | Linear, pre-stretch | Linear, pre-stretch | Post-stretch (display) |
| **Answers "what colour is it really"** | Yes, that is the whole point | No, it makes channels commensurate | No |
| **New data dependency** | **Gaia DR3 `xp_sampled` spectra** (large, and we have none) | None | None |
| **New user input** | Centre wavelength + bandwidth per channel | Nothing required; optional sensor pick + mix sliders | Masks and curves, per image |
| **Reuses what we have** | `Tycho2ColorCalibration` shape only; the SED source has to change | Median/MAD/percentile, MTF, `StretchSolver`: nearly all of it | `MaskedBoost`, `LuminanceRangeMask`, `BlendThroughMask` |
| **Works for SHO** | **No, by design** (green cast is the correct answer) | Yes, it is palette-agnostic | Yes |
| **Works with no catalog / no plate solve** | No | Yes | Yes |
| **Deterministic across sessions** | Yes, tied to catalog photometry | Per-image, so two nights of the same target can normalize differently | No, it is hand work |
| **Effort for us** | High (spectra source, storage, query path) | **Low** (about a day for B1+B3) | Medium (hue axis on an existing stage) |
| **Fixes the red-dominated HOO complaint** | Indirectly | **Directly, this is exactly what it is for** | Only by hand |
| **Licence** | Docs only, no code taken | **GPL-3.0**; reimplement by preference, not by prohibition (ADR-2, revised for AGPL-3.0) | Unknown, not inspected |

The row that decides it is "new data dependency" crossed with "fixes the actual complaint".

## Decision record

### ADR-1: Ship robust normalization first, as its own feature

**Decision.** Implement Alchemy-style normalization plus the palette mixer (phases 1 and 2) before
any spectral work, and treat it as the answer to "narrowband colour" for now.

**Still true after phase 0 was added (2026-08-02):** continuum subtraction precedes these in the
*pipeline*, but it is **conditional** (it needs a matched broadband frame the user may not have),
whereas phases 1 and 2 are **universal** and apply to any narrowband stack. So this remains the first
thing to build: it is the first step that always runs.

**Why.** It needs no catalog, no plate solve, no new data asset and no user input, it is built almost
entirely from primitives we already have (`GetPedestralMedianAndMADScaledToUnit`, the percentile
path, the MTF), and it targets the specific failure the user actually sees. A calibrator that needs a
multi-gigabyte spectral catalog to fix a red cast is the wrong first move.

**Consequence.** The result is *not* photometrically calibrated and we must not describe it as such
in the UI or the FITS history. It makes channels commensurate. That is a different claim, and
conflating the two is how a "calibrated" label ends up on an image nobody calibrated.

### ADR-2: Reimplement from the algorithm, do not vendor the code

**Decision.** Write our own implementation from the maths recorded above. Do not copy source, and do
not lift the coefficient table out of `VeraLux_Alchemy.py`.

**Why.** **Revised 2026-08-11 and the decision is unchanged, but the reason is no longer licensing.**
The original reason was that copying GPL-3.0 source into an LGPL-2.1 library is a violation. TianWen
is now **AGPL-3.0-or-later**, and AGPL-3.0 section 13 expressly permits combining a covered work with
GPL-3.0 material and conveying the result, so vendoring VeraLux would be *lawful*. It is still not
wanted:

- **Vendored GPL-3.0 parts stay GPL-3.0**, as section 13 provides, so the file would carry different
  terms from the rest of the tree and every future edit needs that kept straight. A self-contained
  algebraic transform is not worth that bookkeeping.
- **Python is not the target.** These are numpy scripts operating on whole-image arrays; TianWen needs
  span-based single-pass C# that composes with the existing stretch pipeline. A port is most of the
  work either way, and a mechanical translation of GPL source is a derivative work without being any
  less effort.
- The **coefficient table is a separate question** and the licence change does not settle it. The
  crosstalk coefficients originate from **DBXtract**, so if we want them in phase 3 we source them
  from there and record the provenance.

**Consequence, revised 2026-08-11: phase 3 is no longer licence-gated and may be re-ordered.** It sat
behind phases 1 and 2 because the coefficient table had to be sourced without a licence problem.
`DBXtract.py` and `VeraLux_Alchemy.py` are both GPL-3.0 Siril scripts, so under AGPL-3.0 taking the
table from either is lawful and that gate is gone. What remains is **provenance hygiene, not
permission**: prefer DBXtract because it is where the numbers originate, and record which file and
revision they came from, so a future reader can tell a sourced constant from a guessed one. Better
still, fit them from our own data (see "Verify" above), which makes them measurements rather than
anyone's table.

Vendoring is likewise now a fallback rather than a prohibition, available if reimplementing some
specific step proves disproportionate. It carries a real cost: a vendored part stays GPL-3.0 while the
rest of the tree is AGPL-3.0, and every future edit has to keep that straight.

### ADR-3: SPCC narrowband is blocked on a spectra source, and is scoped to exclude SHO

**Decision.** Do not extend `Tycho2ColorCalibration` to narrowband by swapping in a narrow passband
over the existing Pickles SEDs. Phase 4 stays blocked until we have a real per-star spectral source.

**Why.** This corrects the first version of this item, which assumed the existing spectral machinery
was most of the work. It is not. A Pickles template is a **spectral type average**, so it does not
know whether a given star shows Ha in absorption or emission at that exact wavelength. Broadband
integration averages that error away across hundreds of nanometres; a 3 nm window is made of nothing
else. Feeding a narrow passband to type-averaged SEDs would produce a confidently wrong calibration,
which is worse than none.

**Also decided:** when phase 4 does happen, SHO is explicitly out of scope, following Siril's own
guidance. The earlier guess that SII/Ha overlap needed a better-conditioned 3x3 was wrong about the
problem. Nothing is ill-conditioned; the palette is simply an artistic mapping and a photometric
calibrator has no business producing it.

**Consequence.** Narrowband SPCC is a Gaia DR3 project, not a colour project, and should be planned
alongside the existing Gaia items in [inbox.md](../todo/inbox.md) (Stellarium `.dat` loader,
Gaia SP download) rather than on its own.

**What SHO gets instead (added 2026-08-02).** This ADR ruled SHO out of the *photometric* path but
left it with no answer at all, which was a gap. The answer is technique F,
NarrowbandNormalization: renormalize the channels against each other so Ha stops swamping green,
rather than deleting green with SCNR. That is a normalization, not a calibration, and it belongs to
phases 1-2 where it costs us nothing extra.

### ADR-4: Chroma editing stays a render stage and never touches a linear master

**Decision.** Phase 5 extends `Image.MaskedBoost` where it already lives, in the display render path,
and the hue axis inherits the same rule.

**Why.** This is the existing invariant from the masked-boost work, and it applies here for the same
reason: a luminance range mask degenerates to about zero everywhere on linear data, because the
background sits near 0, star cores are rolled off, and nebulosity is a few percent of peak. It is a
render stage because it can only be a render stage.

**Consequence.** Phases 1 to 3 (linear, pre-stretch) and phase 5 (post-stretch, display) can never be
merged into one "narrowband colour" step, however much the UI might want to present them together.
The linear FITS and EXR masters and the `--split-plates` TIFFs stay untouched by phase 5.

### ADR-5: Presets must name which effect they apply, and the blind default is hue rotation

**Decision.** Phase 2 ships **named presets**, not bare sliders, and never defaults to naive HOO.
The default is the **hue-rotation** preset (Ha into green), *not* the H-beta blend, and each preset
records which of the two effects it applies. The H-beta presets are gated on knowing the filter
blocks H-beta, and are not offered blind.

**Why.** Naive HOO is rank-deficient (G and B identical), so it can only ever produce cyan, and
defaulting to it would ship a known-degenerate result as if it were the neutral choice. The
alternative coefficients are not a taste setting either: they estimate the H-beta line, tied to
H-alpha by the Balmer decrement. A number with a physical meaning should not be presented as an
unlabelled slider, because the user cannot then reason about when to change it (low-extinction
target: push toward the intrinsic 0.35; dusty target: leave it).

**Amended 2026-08-02 (user).** The first version of this ADR defaulted to the H-beta blend, which is
wrong on any filter that actually passes H-beta: an L-eNhance or a quad-band already has Hb in the
blue data, so adding Ha on top double-counts it, while an Antlia 3 nm or an L-eXtreme blocks Hb
entirely and the blend is a synthetic estimate of a line that was never measured. Hue rotation has
no such dependency, so it is the only safe blind default.

**Consequence.** The mixer needs no new mechanism, only presets over the lerp Phase 2 already has.
The UI owes a short explanation per preset, not just a name. And an H-beta preset needs the **filter
set**, which is rig configuration rather than frame data: it belongs in the profile beside the other
static equipment facts (the filter wheel's installed filters), and is resolvable from the FITS
`FILTER` cards the stacker already groups on. Absent that knowledge, offer hue rotation and say why
the physical blend is unavailable rather than guessing.

### ADR-6: Phases 2 and 3 conflict over blue, and Phase 3 must not be applied blindly

**Decision.** Treat OSC dual-band unmixing (phase 3) and the natural blend (phase 2) as alternative
sources of blue, and make the interaction explicit when phase 3 lands. Do not chain them by default.

**Why.** They disagree about the same channel. Phase 2 gets blue by adding Ha (synthesising H-beta).
Phase 3's weighted OIII estimate deliberately merges `OIII_G` and `OIII_B` into one line image,
discarding the very independence that gave the blue photosites their own say. Run both naively and
blue is first flattened and then re-synthesised from a different signal, which is not obviously
wrong but is certainly not what either technique intends.

**Consequence.** Phase 3 lands with a decision on whether unmixing suppresses the phase 2 blue blend,
whether the blend is re-derived from the unmixed `Ha`, or whether the two are exposed as a choice.
Recording it now so that phase 3 does not silently regress phase 2's blue.

### ADR-7: Keep Fritsch-Carlson. Do NOT "upgrade" the curve engine to Akima

**Decision.** Phase 5 reuses `FritschCarlsonSpline` unchanged. The reference implementation's Akima
spline is not an improvement for us and must not be adopted on the strength of its docstring.

**Why.** VeraLux picks Akima to avoid the ringing that a natural cubic spline produces near control
points, and says so prominently. But Akima only *reduces* oscillation; it does not forbid it.
`FritschCarlsonSpline` is a monotone cubic Hermite interpolant and **guarantees** the interpolant
preserves the monotonicity of its control points, so with monotone knots it cannot overshoot at all.
For a tone curve that is the stronger property and the one you actually want: a non-monotone tone
curve inverts local contrast somewhere in the range, which is a defect in every case, not a style.
We solved this problem already and solved it harder.

**Consequence.** Recorded because the reference makes a loud, plausible case for Akima and a future
reader comparing the two docstrings could easily conclude we are behind. We are not.

**One real difference, and it is deliberate:** their LUT has 65536 entries, ours has 33
(`ComputeKnots33`). That is a GPU constraint, not an oversight. The 33 knots pack into 9 std140 vec4
slots so the CPU path and the GLSL `applyCurveLUT` can share one layout, which is the CPU/GPU stretch
mirror rule. If 33 knots ever proves too coarse, the fix is to widen *both* paths together, never to
let the CPU LUT grow on its own.

### ADR-8: Phase 5 takes selection from AstroColorMixer and adjustment from VeraLux Curves

**Superseded and rewritten 2026-08-02.** The original decision was to specify phase 5 from
`VeraLux_Curves.py` alone and skip characterising AstroColorMixer, on the grounds that ACM's model
lived only inside the running app. **That was wrong on the facts:** the Siril port is public GPL-3.0
source, and reading it showed ACM is the *richer* reference for masking. Both are now used.

**Decision.** Split the reference by concern.

- **Selection comes from AstroColorMixer:** hue-band masks (smoothstep on circular hue distance), a
  luminance range mask carrying **both** a value-domain feather and an independent spatial soften
  radius, protection ramps (low-saturation, shadow, highlight) subtracted from the result, and the
  band x range product as the final mask.
- **Adjustment comes from VeraLux Curves:** work in **Lab**, not ACM's HSL, so chroma is a
  hue-preserving scale of `a` and `b` (`c_new/chroma` applied to both) rather than an HSL saturation
  push. Lab `C` is the better-behaved axis and is four lines of arithmetic.
- **Selection additionally comes from us:** masks may be sourced from a **line plane** (ADR-11),
  which neither reference can do because both operate after the mix.

**Why split rather than pick one.** They are strong in different dimensions and the union is
coherent: ACM has almost nothing on colour-space handling (HSL saturation is the blunt option
VeraLux explicitly improves on), and VeraLux has almost nothing on selection (luminance ranges only,
no hue bands, no protection). Taking the better half of each costs no extra machinery.

**The finding worth carrying beyond this ADR.** ACM's band labels are "Red / H-alpha" and
"Cyan / OIII", which reveals that it is using **hue as a proxy for line identity** because after the
mix that is all it has. It cannot tell cyan-because-OIII from cyan-because-gradient. We keep the line
planes, so we select on ground truth rather than inference. That is the single clearest place where
our architecture is better than the references rather than merely equivalent.

**Consequence.** Phase 5 is: mask (hue band and/or line plane, x luminance range, minus protection)
then curves over Lab `L`/`C` plus per-channel RGB. Licence position is unchanged, ADR-2 applies to
both references. The "install ACM to read the appendix" item is dropped from Deferred as obsolete.

### ADR-9: Model as precisely as the data allows; the mode gates additions, not accuracy

**Decision.** There is one modelling engine, run at full precision in every mode. A "science mode"
does not make the maths better and a "picture mode" does not make it sloppier. What the mode selects
is whether *synthetic* content is permitted on top: invented lines (the H-beta blend on a filter that
blocked H-beta), aesthetic hue remaps, and palette presets that are not physical.

**Why.** The two goals looked opposed and are not. Every unit of signal correctly separated is a unit
that does not have to be invented, so accuracy is the thing that *buys* aesthetic freedom. The
clearest case is the Hb-passing filter: read as contamination it is a defect to work around, read as
a third measured line it is the best available source of real blue, and the same three-line solve
serves both readings. Conversely the most rigorous item in the plan, SPCC narrowband, has almost no
aesthetic payoff and is explicitly unable to produce the palettes people want, which is why rigour is
not the ordering principle on its own.

**Consequence.** Prioritise by **signal recovered per unit of work**, not by scientific purity. That
is why phase 1 (statistics, no physics, fixes the actual complaint) leads and phase 4 (maximum
physics, minimal payoff) is at the back. It also means the mode flag is a late, thin decision about
what to add, not a fork in the pipeline; and anything synthetic must be **recorded as synthetic** in
the output provenance, so a science-mode consumer can reject it rather than having to trust a label.

### ADR-10: Continuum subtraction is phase 0, and it is not a star-removal method

**Decision.** Continuum subtraction runs **before** normalization, unmixing and mixing, on linear
registered frames, and is offered as an optional preprocessing step gated on a matched broadband
frame being present. It is deliberately **not** wired into the `IStarRemover` role.

**Why phase 0.** Everything else in this plan computes on the line images. If those images are
`line + continuum`, then phase 1's median/MAD/p99.5 statistics are measuring a broadband pedestal and
a field of stars alongside the line signal, and phase 2's palette mix blends continuum into every
output channel. Subtracting afterwards cannot undo that; the numbers were already fitted to the wrong
quantity. It is also the purest instance of ADR-9: it is precise physical modelling whose direct
product is *more real signal correctly separated*, which is exactly what buys colour headroom.

**Why not a star remover.** It does remove stars, and it removes them *physically* rather than by
inpainting, which is strictly more honest than `StarXTerminator` or the SAS star remover where the
data supports it. But the two are not interchangeable and conflating them would be a mistake:
continuum subtraction requires a matched broadband frame, only removes the *continuum* component (an
emission-line star, or a star with strong Ha, leaves residual), and its by-product is a corrected
science frame rather than a starless plate. `IStarRemover` must keep working with no broadband frame
at all. Treat continuum subtraction as calibration that happens to remove stars, not as a star
remover that happens to calibrate.

**Consequence.** It needs frame pairing that the stacker does not currently model: a narrowband
`MasterGroupKey` has to be associated with its broadband counterpart from the same target. That
pairing is the actual new work, since the fit itself is largely assembled from parts we own
(`FindStarsAsync`, the quad-match registration, and the flux-fitting shape `Tycho2ColorCalibration`
already uses). Absent a broadband frame the step is skipped, and everything downstream behaves
exactly as it does today.

### ADR-11: A mask may be sourced from a line image, and gates the colour-adjustment stage

**Decision.** Masks in this plan may be built from a **single line image** (typically OIII), not only
from composite luminance, and that mask gates **phase 5's colour adjustment**. The palette mix in
phase 2 stays global. Derive the mask threshold automatically from the line image's own statistics
rather than exposing a raw slider as the primary control.

**Corrected 2026-08-02.** The first version of this ADR had the mask gating the *mix*, inferred from
a screenshot of the mask being built. A later screenshot showed what it actually gates: a
`CurvesTransformation` applied after a global mix, with the target reading `Modified - Masked` and
the red curve pulled below the diagonal inside the mask. So the shape is one global palette decision
followed by one spatially-gated colour adjustment, not per-pixel mix coefficients.

**Why gated at all.** A frame-wide colour push also hits regions with no OIII, where it is colouring
noise and Ha-only structure. Gating puts the adjustment **only where there is signal to adjust**,
which is ADR-9 expressed as a mask instead of as a model, and it is what makes an aggressive push
safe: inside a mask you can suppress red far harder than you would dare frame-wide.

**Why a line image and not luminance.** Composite luminance answers "is this pixel bright", which
includes bright Ha with no OIII at all. The OIII channel answers "is there OIII here", which is the
question being asked. This is the one real capability gap: `Image.LuminanceRangeMask` derives from
composite luminance and has no line-image source.

**Why automatic.** The reference hand-tunes the threshold (0.24 observed), which is most of why the
workflow reads as fiddly. It is asking "is this above the noise", which `Image.Background()`'s
iteratively sigma-clipped noise estimate already answers; it is the same quantity `FindStarsAsync`
derives its detection level from. The manual step becomes a default with an override: an improvement
on the reference, not a port of it.

**Consequence.** Phase 5 gains a mask-source parameter; phase 2 is unchanged. Keep the mask primitive
**spatial-blur capable**: the reference deliberately pairs a hard threshold (fuzziness 0.00) with
heavy smoothing (35.5) to get a structural envelope rather than a noisy per-pixel selection. And the
phases compound: on a continuum-contaminated frame a brightness threshold selects anything bright,
stars included, whereas after phase 0 it is a genuine "is there OIII here" test.

### ADR-12: The pipeline operates on mono line planes; OSC extraction is an on-ramp

**Decision.** Model the input as a set of **named mono line planes** (`Ha`, `OIII`, `SII`, `Hb`) of
arbitrary size N. Every phase is defined over that set. The OSC dual/tri-band case is a *preprocessing
step* (phase 3) that produces the set; it is not the native representation.

**Why.** There are two ways to arrive at separated lines and they are not equivalent. A mono imager
separates them **optically**, one filter per line, which is exact and has no model. An OSC dual-band
user separates them **algebraically** from three channels, which is an estimate with a crosstalk
model, a sensor coefficient table, and conditioning to worry about. Writing the pipeline against the
RGB form (as Alchemy does, because that is its input) bakes the harder, lossier case into the core and
makes the better-equipped user route through machinery they do not need.

**Consequence.**

- **Mono imagers skip phase 3 entirely** and start at phase 1, with a strictly better result than any
  unmixing can produce.
- **Phase 1 is restated over planes:** align each weak plane to the reference plane by median offset
  then signal-strength gain. Alchemy's "align G and B to R" is that operation with N fixed at 3 and
  the planes wearing RGB names.
- **SII inverts.** The earlier Deferred note called quad-band SII "a larger linear system"; the
  difficulty was never SII, it was doing algebra on too few measurements. A mono imager with an SII
  filter just has a fourth plane and full SHO is immediately available. Only *OSC* quad-band is hard,
  and it is hard because four unknowns from three channels is underdetermined.
- Phase 0 and phase 5 need no change: continuum subtraction runs per plane, and the phase 5 mask is
  built from one plane, which is exactly what the OIII range mask is.

### ADR-13: Mix in the linear domain, not after a per-plane stretch

**Decision.** Phases 1 and 2 operate on **linear** planes, per Alchemy's model, even though the
reference workflow stretches each plane first and mixes afterwards.

**Why.** Mixing linear signals preserves the physical ratio between the lines, which is what makes a
palette coefficient mean something and what lets phase 0 and phase 3 feed it sensibly. Once each
plane has been through its own independent auto-stretch, the ratio between them has been rescaled by
two different non-linear functions, so `0.7 O + 0.3 H` is no longer mixing 70/30 of anything
physical.

**What the reference gains that we lose, and how we get it back.** Per-plane stretching does make the
planes commensurate, which is genuinely the problem phase 1 solves; it is just a blunt way to do it,
because the stretch is chosen for display rather than for matching. Phase 1's median-offset plus
signal-strength gain does the same job in the linear domain and deliberately, so we keep the benefit
without the coupling.

**Consequence.** Our order is: phase 0, phase 1 normalization, phase 2 mix, *then* stretch, then
phase 5's masked colour work on the stretched result, and phase 6's star recombination last (stars
were removed before the colour work per the starless invariant, and the synthesized RGB star plate
goes back in at the very end, exactly where the reference workflow re-adds its stars). The reference's order is stretch, mix, mask,
curves. Do not port its ordering along with its coefficients; the coefficients were tuned against
stretched planes, so treat the published numbers as a starting point rather than as calibrated
values.

## Invariants

- **Linear in, linear out** for phases 1 to 3. The output is stretch-ready and must not be
  pre-stretched, mirroring how the technique was designed and how our own masters flow.
- **Gain is applied about the background, never about zero.** `(G - med_r) * gain + med_r`. Getting
  this wrong silently moves the black point every time the gain is not 1.0.
- **The masked colour work runs on starless planes.** Stars are removed before masks are built and
  recombined afterwards. Otherwise a star, which is bright in every line, gets selected by every
  signal-presence mask and has colour curves applied to its core. We already have the star
  remove/recombine structure in `SharpenPipeline` (`SharpenIntermediates.StarsAndStarlessLineage`).
- **Continuum subtraction happens first or not at all.** Never after normalization or mixing: those
  steps fit their coefficients to whatever is in the frame, so a late subtraction leaves numbers
  derived from `line + continuum` in place. See ADR-10.
- **Normalization is not calibration.** See ADR-1. Do not write an SPCC-style provenance card for it.
- **Degenerate coefficients fall back, they do not throw.** The unmixing path has three separate
  guards (`|r2| < eps`, and each denominator) and every one drops to `Ha = R, OIII = (G+B)/2`. An
  unknown or mistyped sensor must degrade to the naive mapping, not fail the stack.
- **A sensor profile is a property of the rig, not of a frame.** If phase 3 lands, the sensor pick
  and any declared passbands belong in the profile next to the other OTA/camera facts, resolved the
  way `OTAData` sensor specs already are. They are static, so `feedback_no_varying_values_in_profile`
  does not bar them.

## Deferred

- ~~AstroColorMixer's actual model~~ and ~~`VeraLux_Curves.py` (not read)~~: **both closed 2026-08-02.**
  Curves was read and turned out to be the same role with visible maths, so it supersedes the
  AstroColorMixer investigation entirely (ADR-8). Both are written up above.
- **A value-domain feather option on `Image.LuminanceRangeMask`.** Our feather is a spatial Gaussian;
  technique D's is a sigmoid on the luminance value, which cannot bleed a selection across an edge.
  Worth adding as an option (not a replacement: spatial feathering is still right for some masks),
  but it is an independent improvement to an existing primitive rather than part of this plan.
- **OSC quad-band unmixing only.** Four lines from three channels is underdetermined and needs a
  constraint or a fourth measurement. Genuinely deferred, and note the scope: this is an *OSC*
  limitation, not an SII limitation. A **mono** imager with an SII filter simply has a fourth plane
  and full SHO works today with no unmixing at all (ADR-12).
  (**The three-line Hb case is NOT deferred** and moved into phase 3a: it is exactly determined and
  is the largest available source of real blue. See the HOO section and ADR-9.)
