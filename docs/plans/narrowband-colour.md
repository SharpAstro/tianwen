# Narrowband colour: normalization, unmixing, calibration

An Ha/OIII/SII stack has **no colour path at all** in TianWen today. `Tycho2ColorCalibration`
(SPCC) integrates a Pickles SED against QE x CFA over the whole visible band, which is the right
model for a broadband OSC frame and the wrong one for a 3 nm passband. So a narrowband master gets
whatever the channel assignment plus per-channel autostretch happen to produce, which in practice
means the familiar red-dominated HOO.

Research source: four Cuiv videos the user saved, which turned out to be four *different*
techniques rather than one (three recent, plus one 2021 tutorial). Investigated 2026-08-02; the
algorithms below are recorded here because only one of the four has published maths. Two were read
out of GPL source, and the fourth documents its model only inside the running app.

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
| 5 | **Masked colour adjustment.** Curves over `L`/`S`/`C` + per-channel RGB, gated by a mask that may be sourced from a **line image** (ADR-11, threshold auto-derived from `Background()` + MAD). `C` is a hue-preserving scale of Lab `a`,`b`. Reuses `FritschCarlsonSpline` (ADR-7). | `Image.Masks.cs`, `MasterPreviewRenderer` | NOT STARTED (separate concern, see ADR-4/8) |

Phases 1 and 2 are the useful minimum and are independent of everything else. Phase 3 improves
phase 1 where the sensor is known. Phase 4 is a different feature that happens to share the word
"narrowband". Phase 5 is a display stage and is not colour calibration at all.

## The four techniques

### A. Siril SPCC narrowband mode (built in, not a script)

[Video](https://www.youtube.com/watch?v=uLy9TA2Bo2A) - [docs](https://siril.readthedocs.io/en/latest/processing/color-calibration/spcc.html)

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

Nonlinear RGB chroma-vector control: hue, saturation and luminance adjustment gated by range masks,
composed in multi-pass layers, applied to **stretched** data. The published page has no maths (the
technical appendix ships inside the app), so the model is not recorded here and would need the app
installed to characterise.

This is a finishing tool, not a calibrator, and it is the one we most nearly have already:
`Image.MaskedBoost` composing `LuminanceRangeMask` -> `Saturate`/`ContrastBoost` ->
`BlendThroughMask` is the same shape. What is missing is the hue axis and the layering.

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

**We already have most of method 3.** `FindStarsAsync` detects and measures, the frames are already
registered by the stacker's quad matcher, and `Tycho2ColorCalibration` already does star-flux fitting
for SPCC. What is missing is the pairing of a narrowband group with its broadband counterpart and the
robust fit itself. That makes the *good* version the cheap one for us, which is unusual and worth
exploiting.

**Cost to the user:** a matched broadband frame. Mono imagers shooting Ha/OIII/SII plus RGB have it
already. An OSC dual-band user needs a separate broadband session, which is a real ask and the reason
this cannot be mandatory.

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
which is a question we already answer: `Image.Background()` plus a MAD-scaled sigma gives a
signal-detection threshold directly, the same machinery `FindStarsAsync` uses. Deriving the threshold
from the OIII image's own statistics turns the manual step into a default, with the slider kept as an
override.

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

## Pros and cons

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
| **Licence** | Docs only, no code taken | **GPL-3.0**, so reimplement, never copy | Unknown, not inspected |

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

**Decision.** VeraLux is GPL-3.0-or-later; TianWen is LGPL-2.1. Write our own implementation from the
maths recorded above. Do not copy source, and do not lift the coefficient table out of
`VeraLux_Alchemy.py`.

**Why.** Copying GPL-3.0 source into an LGPL-2.1 library is a licence violation, and a coefficient
table copied verbatim carries the compilation with it. The crosstalk coefficients originate from
**DBXtract**, so if we want them in phase 3 we source them from there and record the provenance.

**Consequence.** Phase 3 is gated on sourcing the table cleanly, which is why it sits behind phases 1
and 2 rather than shipping with them.

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

### ADR-8: Phase 5's reference is VeraLux Curves, not AstroColorMixer

**Decision.** Specify the hue/chroma work from `VeraLux_Curves.py` and drop the plan to characterise
AstroColorMixer by installing it.

**Why.** They fill the same role, but AstroColorMixer's model is only documented inside the running
app, while Curves is readable source implementing the same idea in four lines of Lab arithmetic. A
readable reference beats an opaque one even when the opaque one is more polished. The licence
position is also already settled for this codebase by ADR-2, which applies unchanged here.

**Consequence.** Technique C stays in this document as context for what the category is, but nothing
depends on it any more, and the "install it to read the appendix" item is dropped from Deferred.
Phase 5 gains a concrete shape: value-domain luminance masks, then `L`/`S`/`C` domains in that order,
with `C` implemented as a hue-preserving scale of `a` and `b` in Lab.

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
workflow reads as fiddly. It is asking "is this above the noise", which `Image.Background()` plus a
MAD-scaled sigma already answers, the same machinery behind `FindStarsAsync`. The manual step becomes
a default with an override: an improvement on the reference, not a port of it.

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

## Invariants

- **Linear in, linear out** for phases 1 to 3. The output is stretch-ready and must not be
  pre-stretched, mirroring how the technique was designed and how our own masters flow.
- **Gain is applied about the background, never about zero.** `(G - med_r) * gain + med_r`. Getting
  this wrong silently moves the black point every time the gain is not 1.0.
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
