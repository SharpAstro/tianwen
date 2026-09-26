# Focus Tolerance: the New Critical Focus Zone, Measured by the V-Curve We Already Fit

**Status: PARTIAL; F0 (a bug, #820) SHIPPED, F1 onward NOT STARTED and tracked by #822.** Written 2026-09-25, raised by the user from GoldAstro's
[New Critical Focus Zone](https://goldastro.com/goldfocus/ncfz.php) and the technical pages it links: the
traditional CFZ, the focus and collimation calculator, focus techniques, resolution, signal and noise. They are
saved with Markdown extracts under `OneDrive\Dokumente\Astro-Info\GoldAstro`. The site answers 406 to a
non-browser agent, so fetch it with a browser User-Agent.

Companion plans:
- [camera-collimation.md](camera-collimation.md): the same tolerance judges sensor tilt.
- [video-guiding.md](video-guiding.md): builds the aligned star stack that a video autofocus would reuse.

Related issues: #579 (temperature-driven refocus, with the coefficients measured from the archive) and #585
(discrete autofocus triggers).

## The theory, and a check of it

GoldAstro sets out to replace the traditional critical focus zone, `CFZ = 4.88 λ N²`. That is the diameter of the
Airy disk's first dark ring (`2.44 λ N`), doubled for in and out, and scaled to focuser travel by `N`. In its place
comes a tolerance the imager chooses:

    NCFZ [µm] = 0.00225 · θ · √τ · A · N²

- `θ` is the total seeing: the star FWHM in arcsec **as measured through the imaging train**. It therefore
  includes pixel sampling, so an undersampled rig measures a larger `θ` and is easier to focus.
- `τ` is the tolerated growth of that FWHM, in percent. The formula is valid from 0 to about 20.
- `A` is the aperture in mm, and `N` the effective focal ratio.

GoldAstro states an accuracy of ±4 %, for θ from 0.4 to 6 arcsec. Their example is 106 mm at f/5, 3.0 arcsec seeing
and a 15 % tolerance, which gives 69 µm. From best focus the focuser may sit ±35 µm off and add at most 0.45 arcsec.
When only the seeing is known, their rule of thumb for `θ` (at 2 arcsec and worse) is
`θ ≈ 0.2 · θ_diffraction + θ_seeing`.

Their case against the traditional CFZ is two errors:
- It defocuses a point, where the telescope actually defocuses an Airy disk. One traditional CFZ of defocus
  already makes the Airy FWHM 2.2 times larger.
- It uses the first dark ring (`2.44 λN`), where the measurable size is the FWHM (`1.02 λN`).

Their scale of tolerances:

| Tolerance | Who can hold it |
|---|---|
| 15-20 % | visible by eye |
| 10-15 % | what most imagers want |
| 3-5 % | a good focuser |
| 1-2 % | a top focuser |

**The law checks out from first principles.** A focuser displacement Δ blurs a star into a disk of diameter `Δ/N`
on the sensor, which is `Δ / (A N²)` radians on the sky. Combine that disk with a Gaussian seeing core in
quadrature, and solve for the displacement that grows the core by τ. The same `θ · √τ · A · N²` falls out, with one
of two constants:
- 0.00194 if half-flux diameters add in quadrature;
- 0.00233 if second moments do.

GoldAstro's 0.00225, from an exact treatment of a defocused Airy disk in seeing (Nijboer-Zernike), sits between the
two. The √τ comes from `(1+t)² − 1 ≈ 2t`.

## Our V-curve fit already is this model

`Hyperbola.CalculateValueAtPosition` fits `HFD(x) = a · cosh(asinh((p − x)/b))`. That is exactly
`a · √(1 + ((x − p)/b)²)`, which is `HFD² = a² + (s · (x − p))²` with `s = a / b`: a seeing core and a geometric blur
that grows linearly with focuser travel, added in quadrature. This is the model above, so the zone comes straight
out of the fit, **in focuser steps**:

    HFD(x) ≤ (1 + t) · a   ⟺   |x − p| ≤ b · √((1 + t)² − 1) ≈ b · √(2t)

At the default `FocusDriftThreshold` of 1.07 (t = 0.07), the half-width is `0.381 · b`.
- Every AutoFocus already measures `b`, so knowing the zone needs nothing new.
- It needs no step size either. A focuser never knows its own step size: #579 found `StepSize` is always a
  user-entered value. So work in steps, and keep microns for display.

The fit separates the two causes, just as GoldAstro's formula does in microns:
- `a` (px) is tonight's seeing and sampling.
- `s = a / b` (px per step) is the hardware: the focal ratio, the pixel size, and how far one step moves.
- So `b = a / s` shrinks on a night of good seeing, and the zone shrinks with it.

A cross-check against #579's measurements on the ASI533MC Pro / EAF rig:
- 5.37 to 5.86 steps per °C and a 1.1 to 1.4 °C drift tolerance at about 7 % make a half-width of 6 to 8 steps.
- `b` should therefore come out at about 16 to 21 steps there. The next AutoFocus on that rig can confirm it.

GoldAstro criticises minimum-FWHM and minimum-HFD focusing because "it measures noise, not signal": near focus, the
defocus term is a small fraction of the star's size. That is right about the bottom of the V and does not apply to
a fit, whose `p` is set by the arms, where HFD changes fastest. What it does change is where to sample: spend
exposures on the arms, and distrust a run whose arms never climbed.

## Phases

### F0: drift detection is off after every AutoFocus (a bug, found while mapping this)

**SHIPPED** (#820). The session keeps one drift baseline per ACQUISITION SETTING rather than one per telescope:
`AcquisitionSetting` (filter slot, exposure, gain, exactly what `IsComparableTo` compares, which is unchanged) keys
the stored baselines and the collections in progress, per telescope and per observation (`Session.Focus.cs`). A
frame is compared with the baseline of its own setting; a setting with none yet collects its own from its first
`BaselineHfdFrameCount` frames, without disturbing the others (`AccumulateBaselineSample`). The AutoFocus
verification frame simply sits under its own 2 s key, which no science frame reaches. A filter ladder keeps a
baseline per slot side by side, so each slot is compared with where focus was when that slot was first measured,
however often the ladder changes slot. A drift refocus and a target change still clear the history window, and
now clear every setting's baseline for the telescope with it, since after the focuser moves none of them
describes the focus position. `BaselineByObservation` keeps the most recent baseline per telescope for the
scout's star-count comparison, which does not depend on the setting.

Pinned by three session tests that count refocuses through `Session.DriftRefocusCount`:
- `SessionImagingTests.GivenStartOfNightAutoFocusWhenFocusDriftsAtALongerExposureThenRefocusFiresAndFiresAgain`:
  the first target, then a second drift after the drift refocus. Zero refocuses on the old loop.
- `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenFocusDriftsOnTheNextTargetThenRefocusFires`:
  `AlwaysRefocusOnNewTarget`. Zero refocuses on the old loop.
- `SessionImagingTests.GivenFilterLadderAlternatingEveryFrameWhenFocusDriftsSlowlyThenRefocusFires`: two slots
  alternating every frame while focus creeps 8 steps per frame. With one baseline per telescope that is replaced
  whenever the frame is not comparable, the refocus filed its 2 s baseline and every later collection was
  restarted by the other slot: one refocus, then HFD from 2.65 to 11.4 with none. Per setting: 14 refocuses,
  worst HFD 2.97.

What follows is the finding as written, kept for the reasoning.

`AutoFocusAsync` stores its verification frame as the observation's drift baseline, taken at the AutoFocus exposure
of 2 s (`Session.Focus.cs`: `autoFocusExposure`, then `FrameMetrics.FromStarList(verifyStars, autoFocusExposure, ...)`).
The imaging loop then skips every frame that is not `IsComparableTo` the baseline, meaning the same exposure, gain
and filter slot, with a bare `continue` (`Session.Imaging.cs`). It never re-baselines, because the stored baseline
`IsValid`.

So unless the science exposure is 2 s, the 7 % trigger never fires:
- On the first target. The start-of-night AutoFocus runs before the loop, which files its baseline under
  observation index 0.
- For the rest of any target after a drift refocus.
- On every target, when `AlwaysRefocusOnNewTarget` is set.

`IsComparableTo` is right to refuse the comparison. A 2 s star has less image motion integrated into it than a
300 s one, so the two HFDs differ even at the same focus. The fix is to treat a baseline that is not comparable as
absent, and to collect a comparable one from the first `BaselineHfdFrameCount` science frames. Observations after
the first already take that path.

`GivenFocusDriftWhenHFDExceedsThresholdThenAutoRefocusTriggered` never sees the bug. It starts the loop with no
AutoFocus before it, and after its refocus it only asserts that a baseline exists. The test to write runs an
AutoFocus, images at 30 s, drifts the fake focuser, and must see the refocus happen.

### F1: every AutoFocus reports its zone, and refuses a V it cannot trust

Record these with each run, in the focus-run record (`AppendFocusRunRecord`), which the remote mirror already
carries:
- `a`, `b`, `s` and `p`;
- the half-width `b · √((1+t)² − 1)` for the configured tolerance.

All of them are in steps, and also in µm only where a step size is configured.

Today, converged means only that at least three rungs had enough stars and a fit ran. Add the checks that are
missing:
- `p` must lie inside the sweep. Today it is clamped to `[0, MaxStep]` and nothing else.
- Both arms must climb to about `2a` or more, or `b` is not constrained.
- The fit error must be used, not just logged.
- Above ten rungs, `MetricSampleMap` drops the two OUTERMOST positions as outliers. Those are the most informative
  samples on the arms. Reject outliers by their residual instead.

### F2: size the sweep from the V, not from 200 steps and 9 rungs

`AutoFocusRange` (200) and `AutoFocusStepCount` (9) are constants today. Instead:
1. Persist `s` per OTA as a learned sidecar, the way backlash is (`Profiles/BacklashHistory/`), and the way #579
   wants the thermal coefficient kept.
2. Predict tonight's `b = a_tonight / s`, taking `a_tonight` from the median HFD of the last frames.
3. Sweep about ±3b to ±4b. That is where HFD reaches three to four times its minimum and the arms dominate the fit.

Keep today's constants for a rig with no history.

### F3: the tolerance is quantized, and it is a share of an error budget

`FocusDriftThreshold` already is a tolerance: 1.07 means 7 %. As a fixed number, though, it is both too fine and
too optimistic (user, 2026-09-25).

**Too fine.** The zone grows only as √τ.
- 7, 8 and 9 % differ by about 14 % in width. On the #579 rig (b ≈ 18 steps, inferred above), the half-widths are
  6.9, 7.3 and 7.8 steps: less than one step apart, and inside both the backlash residual and the AutoFocus's own
  precision.
- In HFD terms, on a 2.45 px star, they are 0.17, 0.20 and 0.22 px. That is below the frame-to-frame scatter.
- The focuser quantizes the tolerance. Sitting n steps from best focus costs `t(n) = √(1 + (n/b)²) − 1`. With
  b = 18, 6, 7 and 8 steps cost 5.4, 7.3 and 9.4 %.
- A tolerance means something only in whole steps.

**Too optimistic.** The 7 % was calibrated on a single rig (#579: the ASI533MC Pro at 130 mm, about 6 arcsec per
pixel). There the pixel and the small aperture dominate the star, so seeing changes are diluted. On a well-sampled
rig, the star IS seeing and guiding, and those move by more than 7 % for reasons focus cannot fix:
- Seeing grows as airmass^0.6. A target sinking from 60° to 40° altitude grows by about 20 % from seeing alone.
- A windy spell or a periodic-error peak widens every star in a sub.
- Tilt and field curvature shift the field median with whichever stars happen to be detected.

A trend over 30 frames reads a slow change in seeing exactly as it reads a slow focus drift. So it refocuses into
seeing that refocusing cannot improve. On a rising target, the reverse happens: improving seeing hides a real drift,
which is the confound #579 found.

**So the trigger is derived, not set:**
1. Take the baseline from science frames (F0). The tolerance is then a share of the whole long-exposure star,
   guiding included.
2. Take out what focus cannot change before taking the ratio:
   - Fit the HFD trend against airmass^0.6 and temperature (#579's physical driver), not against time alone.
   - Remove guiding in quadrature, using each sub's own `GUIDERMS`. This needs the synthetic guide samples gone
     first ([video-guiding.md](video-guiding.md), section 7).
3. Fire only when the growth attributable to focus exceeds the tolerance by two standard errors of that fit.
4. Never fire below the resolvable floor. Convert each of these to a tolerance through `t(n)`, and take the largest:
   - one step;
   - the backlash residual;
   - the AutoFocus's own σ_p, from the fit's residuals (F1).
5. Show what the rig can hold tonight beside what was asked, for example "asked 7 %, resolvable 11 % tonight".
   GoldAstro's 10-15 % is a more honest default than 7 % for most rigs, and a derived floor makes the default matter
   less.
6. A candidate discriminator, to validate on saved AutoFocus rungs: FWHM over HFD. Both are already measured per
   star.
   - Our HFD is twice the flux-weighted mean radius (`Image.StarDetection.cs`).
   - On that definition, a pure defocus disk has FWHM/HFD = 1.5, and a Gaussian 0.94. A Moffat's wings pull it
     lower.
   - Growing seeing leaves the ratio where the night's profile puts it. Defocus flattens the core and raises it.

The √τ law also sets the price of tightening the tolerance. Halving it shrinks the zone by only √2, so refocusing
becomes about 1.4 times as frequent, not twice.

### F4: judge the focuser in units of `b`

- **Step quantization.** GoldAstro's worst case puts best focus midway between two steps. The growth is then
  `1 / (8 b²)`: 1.4 % at b = 3 steps, and 0.13 % at b = 10. Below about 3 steps, the focuser cannot hold a tight
  tolerance.
- **Backlash residual.** A residual of B steps costs `(B / b)² / 2`. That says whether `BacklashEstimator`'s
  residual matters on this rig at all.
- **Temperature (#579).** The allowed change is `ΔT = b · √((1+t)² − 1) / k`, for k steps per °C. #579's fixed
  1.0-1.5 °C is this number at the seeing it was measured in. Derived from tonight's `b` instead, the trigger
  tightens by itself on a good night. With k = 5.65 and b = 18, it is 1.2 °C at 7 %.
- **Filter offsets** smaller than the half-width are noise.

### F5: sanity-check the profile's optics (microns, display only)

A geometric defocus of Δ µm spreads a star over an annulus of outer diameter `Δ/N` µm on the sensor. Here ε is the
central obstruction's diameter ratio; with no obstruction it is a disk.

Our HFD is twice the flux-weighted mean radius (F3), not the true half-flux diameter. On that definition, the
annulus measures `HFD = (2/3) · (Δ/N) · (1 + ε + ε²)/(1 + ε)` µm, which is `2Δ/(3N)` with no obstruction.

So with a configured `IFocuserDriver.StepSize`, the predicted slope is
`s = (2/3) · StepSize · (1 + ε + ε²) / ((1 + ε) · N · pixel)` px per step. A measured `s` far from that names a
wrong focal length, aperture or step size in the profile.
- `OTAData` has the focal length, aperture and pixel size. It has no obstruction.
- On an SCT focused by its primary, a step moves the focal plane by a large multiple of the mirror's own travel.
  That is one more reason to keep microns out of every decision.

### F6: video autofocus (needs native video; see video-guiding.md)

At each rung, capture a second or two of short frames around the brightest stars, and shift-and-add them per star.
- The aligned stack has the image motion removed, so `a` drops, the V narrows, and `p` is better determined.
- Best focus stays the same, because image motion does not move it.
- The frame-to-frame spread is the rung's own error bar.

GoldFocus does the same to its mask pattern: "Track and Stack", in Astronomy Technology Today's review, saved with
the pages above.

A video autofocus focuses the camera that looks through the focuser being moved. That is the imaging camera, or an
OAG camera with a fixed offset.

### F7 (optional): a Bahtinov mask analyser

A Bahtinov mask read by software gives the offset of the middle spike between the two crossing ones. It is
seeing-robust to first order and needs no V-curve.

GoldFocus claims ten times Bahtinov's signal-to-noise for its own mask, but that claim is the vendor's, and its mask
and software are proprietary. This comes last because the V-curve needs no mask at all, and a mask has to be fitted
and removed by hand, which an unattended night cannot do.

## What bites

- **Work in steps.** The zone comes from the fit. Microns are for display and for the F5 sanity check.
- **A 2 s AutoFocus star and a long-exposure star are not the same measurement** (F0).
- **The zone moves with the seeing.** A constant threshold, in steps or in °C, is right on one night only.
- **A tolerance is quantized by the focuser and blurred by the night.** 7, 8 and 9 % can be the same step (F3).
  Never tune it finer than one step's `t(n)`, and never below what the night's scatter lets the trend resolve.
