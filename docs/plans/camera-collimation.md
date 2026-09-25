# Camera Collimation: Measured Live, from the Camera That Images

**Status: NOT STARTED.** Written 2026-09-25, raised by the user ("real-time collimation, camera based") alongside
[focus-tolerance.md](focus-tolerance.md) and the speckle-imaging revision of [video-guiding.md](video-guiding.md).

**Sources** (saved under `OneDrive\Dokumente\Astro-Info`):
- the MetaGuide 2024 manual (v6.1.7): chapters 9 and 12 on collimation, 17 on seeing, 27 on collimating at the
  diffraction limit;
- GoldAstro's collimation pages (`GoldAstro\collimation_*.md`).

Nothing in TianWen measures collimation today. The closest things are:
- per-star `Ellipticity` on `ImagedStar`, whose comment says it is useful for spotting collimation issues;
- the offline centre-to-corner FWHM and ellipticity profile per optical train in the dataset bake
  (`DatasetPsfNoiseReport`).

There is no mask, tilt or collimation tool anywhere in `src/`.

## Why a camera, and why video

**The errors that matter are sub-pixel.** GoldAstro's ray traces, with 6 µm pixels:

| Telescope | Error | Effect on the on-axis spot |
|---|---|---|
| 200 mm f/6 Newtonian | 1/8 turn of an M4x0.7 collimation screw: 88 µm at the mirror edge | grows to over half a pixel |
| 200 mm f/10 SCT | 1/72 turn on the secondary: 10 µm | grows to nearly a pixel |
| 200 mm f/8 RC | 1/36 turn on the secondary: 19 µm | grows to nearly a pixel |
| 100 mm triplet | one element tilted by 4 µm | grows to nearly a pixel |

In each case the Airy FWHM is 0.6 to 1.0 pixels. Seeing hides this in any single short frame. Averaging many frames
after aligning each one (speckle shift-and-add) is what makes it visible.

**MetaGuide's collimation is exactly that.** It runs video at 10-20 frames per second or more and aligns each frame
on its peak: the "Peak" StarStacking mode. The manual says the coma dot is disabled in Centroid mode, and that the
peak-aligned stack has a smaller FWHM, which is "essential to resolving the Airy pattern". It stacks the aligned
frames and draws a red "coma dot", offset in the direction of the flare. The user turns screws until the dot sits
on the star, while guiding keeps the star centred.

The manual never gives the dot's formula. Its behaviour, in the flare's direction and disabled in Centroid mode, is
what an offset between the stack's centre of light and its peak would do.

**Visual methods fall short, and some of them mislead:**
- **Cheshire and laser tools** are good to a millimetre or two.
- **Donut concentricity is too faint to see.** In GoldAstro's example, the obstruction's shadow sits 1 pixel off
  centre in a 100 pixel donut. That 1 % error is invisible, yet it matters at focus.
- **The donut can mislead outright.** On a Newtonian, a secondary that is off-centre but correctly angled shifts
  the shadow while adding no aberration: a false alarm. The same offset can also hide a tilted primary: a false
  all-clear.
- **MetaGuide agrees:** complex optics may show their best stars while the defocused star looks misaligned.
- **So:** use the donut for the rough stage, and do the final stage in focus.

## Method

1. **The aligned star stack.** This is shared with video guiding and specified in [video-guiding.md](video-guiding.md):
   - an ROI stream around a bright star;
   - a sub-pixel peak position for each frame;
   - a rolling shift-and-add of the frames;
   - optionally, a 2x drizzle onto a finer grid using those sub-pixel shifts. The atmosphere dithers the frames
     for free.
2. **Measure the stack:**
   - Its radial profile, and its FWHM against the theoretical Airy FWHM (1.02 λN, with the obstruction's effect).
     This says how far the optics are from diffraction-limited.
   - The azimuthal harmonics of the stacked star around its peak. m = 1 is coma: the coma dot, with a direction
     and a magnitude. m = 2 is astigmatism, and m = 3 is trefoil (pinched optics). That gives one number per
     aberration, each with a direction, updated several times a second.
   - Rough stage, defocused: fit the donut's outer edge and the obstruction's shadow, and report the vector between
     their centres.
3. **Keep the star centred while the screws turn.** Use the built-in guider (MetaGuide's "collimation with automatic
   re-centering"), or `PlanetaryRecenterController`'s ROI jog.
4. **Calibrate the screws, the way the guider calibrates its axes.**
   - Turn each screw a known fraction of a turn, and record how the coma vector changes.
   - A least-squares fit gives a 2 x N matrix.
   - Inverting it recommends a move, such as "screw 2, 1/8 turn clockwise".
   - Neither MetaGuide nor GoldFocus calibrates the screws. MetaGuide shows the direction, and GoldFocus reports the
     error in pixels.
5. **The field, from any camera and no video.** Use the PSF per region of an ordinary sub, or of the AutoFocus rungs
   that `SaveIntermediates` already keeps:
   - Coma and ellipticity vectors across the field point away from the collimation centre. Locate it by least
     squares.
   - Fit a V-curve per region from the AutoFocus rungs, and take each region's best focus. Together they give the
     sensor's tilt plane and field curvature, in steps.
   - Judge them against the focus tolerance ([focus-tolerance.md](focus-tolerance.md)): "every corner is within
     7 %", or "top left is two zones out: tilt".
   - This is the answer for the small refractors, where "collimation" really means tilt and spacing.
6. **Research, later: wavefront curvature sensing (Roddier).** Take stacks inside and outside focus at ±Δ by moving
   the focuser, which TianWen already controls, and solve for Zernike coefficients. That turns the whole question
   into numbers in waves.

## Hardware notes

- **Sampling the Airy pattern** needs a pixel of at most `λN/2`. At 650 nm that means `N ≥ 3.1 · pixel` (pixel in µm):

  | Pixel | Minimum focal ratio |
  |---|---|
  | 2.4 µm (the QHY178M driven on 2026-09-16) | f/7.4 |
  | 2.9 µm (IMX585) | f/8.9 |
  | 3.76 µm | f/11.6 |

  MetaGuide is more lenient: it says 3.7 µm pixels show the ring "as low as f/7 or so" with an IR-pass filter. An
  f/10 SCT qualifies. An f/4 Newtonian needs a Barlow, the 2x drizzle, or the field method in step 5.
- **Capture settings, from MetaGuide:** 10-20 frames per second or more, linear (gamma 1), the peak at about two
  thirds of full scale and never saturated, a red or IR filter, and a star high in the sky.
- **Frame rate is the Phase D question again.** Every native camera is single-frame in TianWen today (see
  [video-guiding.md](video-guiding.md)). Until native video lands, the stack runs on the planetary controller's
  short-exposure loop, at whatever rate that reaches.

## Phasing

| Phase | Scope | Needs |
|---|---|---|
| C0 | Field analysis (step 5): collimation centre and tilt plane from a sub and from the AutoFocus rungs | nothing new; offline-capable |
| C1 | Aligned stack + radial profile + harmonics (steps 1-2) on the short-exposure loop | the shared stack from video-guiding |
| C2 | Live collimation mode: guider re-centring (3) + screw calibration (4) | C1, a UI mode |
| C3 | Native-video rates | Phase D ([planetary-native-video.md](planetary-native-video.md)) |
| C4 | Curvature wavefront sensing (6) | research |

C0 comes first because it needs no new capture path, and it answers the question for every rig, refractors
included.
