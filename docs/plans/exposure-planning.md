# Exposure planning: sub length, gain, a short set for bright cores, and how long a target needs

**Status: NOT STARTED** (2026-10-20). Written from the owner's questions on moving the Sharpstar 61 EDPH III with its 0.75x
reducer (270 mm) from the SVBONY SV605CC to the ZWO ASI585MC Pro: is 12 bits enough, should subs get shorter, is gain
252 too high, how many frames stop paying. Every answer below was MEASURED on the owner's own frames, and each is what
an automatic suggestion would have to compute. Milestone: `exposure-planning`.

What a frame plan holds today: per target, a list of `FilterExposure` (filter, sub length, count) plus the observation's
gain and offset (`ScheduledObservation`), all typed by hand in the session tab (`SessionConfigGroups`, `SessionContent`).
Nothing proposes a value.

## Measured on the SH61 with both cameras (2026-10-20)

**The camera, by photon transfer** (pairs of flats and of dark flats at one exposure, per Bayer colour, the frame's
centre half: `var(F1 - F2) / 2` is the shot noise plus the read noise, `var(D1 - D2) / 2` the read noise alone):

| | SV605CC (IMX533), gain 120 | ASI585MC Pro (IMX585), gain 252 |
|---|---|---|
| ADC | 14-bit | 12-bit |
| Electrons per ADC step | **0.823** (flats 2025-10-18) | **0.488** (flats 2025-05-25 and 2025-08-09, every colour, pairs within 0.003) |
| What the header says (`EGAIN`) | none | 0.621 (N.I.N.A., the ZWO SDK's `ElecPerADU`); 0.5619 (SharpCap) |
| Read noise, dark-flat pairs / bias pairs | 1.45 / 0.86 e- | 0.96 / 0.51 e- |
| Headroom above bias | about 13,000 e- | about 1,950 e- |

- **The SDK's own figure is 27 % high for the 585 at gain 252** (and right for an IMX533: N.I.N.A. wrote 0.79 for an
  ASI533MC Pro at gain 121). So a suggestion takes a MEASURED gain, from the profile's own flats, and the SDK's only as a
  fallback. TianWen's ZWO driver also reads `ElecPerADU` once at connect, never after a gain change (#738).
- **Gain 252 is where the 585 switches to high conversion gain.** At 0.49 e- a step it is past unity (by extrapolation
  about gain 190 to 200). Its cost is the headroom, never the noise.
- **12 bits costs no noise.** Rounding adds 0.29 of a step; the sky in one of the 585's 60 s broadband subs is 13 to 36
  steps (Helix and Rim, 2025).

**The sky, through the L-Quad Enhance** (Sculptor, 120 s subs, both nights, the 20th percentile of 64 px block medians
less the bias, converted at 0.823 e-/step):

| | Red (Ha, SII) | Green | Blue |
|---|---|---|---|
| SV605CC, measured | 1.1 to 1.3 e-/s | 3.1 to 3.7 e-/s | 3.5 to 4.2 e-/s |
| ASI585, estimated (0.595 the pixel area; the IMX585's response at the lines) | about 1.2 e-/s | about 1.9 e-/s | about 2.1 e-/s |

The sensor curves in `sensor_qe.gs.gz` (`FilterCurveDatabase`) read, relative to each die's own OIII response, Ha and SII
about 1.8 times as strong on the IMX585 as on the IMX533 (IMX533: Hb 0.91, OIII 0.91, Ha 0.58, SII 0.54; IMX585, normalised
to its peak: 0.84, 0.86, 0.99, 0.99). The two curves are not on one absolute scale.

**So a sub is sky-limited within seconds**: read noise adds under 5 % once the sky gives 10 times its square, 3 to 9 e-,
which the faintest channel collects in 2 to 8 s. At 60 s read noise costs a stack under 1 % against one long exposure,
at 30 s about 1 %. **What sets the sub length on the 585 is the HEADROOM**: at 120 s the sky takes 150 to 250 e- of
the 1,950, and bright star cores clip at stars 3 to 4 times fainter than on the SV605CC. The owner's 585 sessions used
60 s, which halves that and doubles the frames (Bayer drizzle wants 60 or more; rejection and the cost of one bad sub
improve with more).

## How long a target needs, measured

Signal-to-noise of NGC 253's outer disk in the owner's three Sculptor masters (`C:/temp/e2/sculptor-snr.py`: the disk at
0.12 and 0.16 deg along PA 52 on both sides, against the high-pass noise of blank sky 0.55 deg off the minor axis):

| Master | Subs (120 s) | Outer-disk SNR per pixel, G | Against October | Square root of the frames predicts |
|---|---|---|---|---|
| October | 90 (3.0 h) | 11.2 | 1.00 | 1.00 |
| September | 156 (5.2 h) | 14.1 | 1.27 | 1.32 |
| Both nights | 233 (7.8 h) | 17.6 | 1.57 | 1.61 |

No floor yet: the SNR grows as the square root of the frames within about 3 %, so nothing systematic (a gradient, a flat,
walking noise) limits it at 7.8 h. What falls is each hour's gain: 15 % at 3 h, 6 % at 7.8 h, 3 % at 15 h. "Enough" is a
target's own: the disk is measured; the faint halo would still gain. A goal in hours is [multi-night-progress](multi-night-progress.md)
P1; a goal in SNR is what E1 can turn into hours.

**A mosaic multiplies it.** The catalogue's LMC (ESO 056-115) is a 10.8 x 9.2 deg ellipse. `MosaicGenerator` at 270 mm
(the bounding box plus a 10 % margin) gives:

| | 20 % overlap | 10 % overlap |
|---|---|---|
| ASI585, landscape (2.36 x 1.33 deg) | 72 panels (12 x 6) | 50 (10 x 5) |
| ASI585, portrait | 70 (7 x 10) | 54 (6 x 9) |
| SV605CC (2.40 deg square) | 42 (7 x 6) | 30 (6 x 5) |

One panel's worth, measured: the Tarantula on 2026-10-17 (SV605CC, L-Quad Enhance, 120 s) ran from 01:27 to 05:15 local,
108 subs (3.6 h) before the owner set aside 12 more at dawn; the quality gate kept 104 (3.5 h). Its subs read FWHM 2.2 to
2.7 px (6.2 to 7.7 arcsec), eccentricity 0.38 to 0.42 and 1,270 to 1,615 stars each. The gate's four were the first
cloud (0011) and the last three before dawn, whose HFD rose to 3.2 px as the focuser's probe fell from 14.2 to 10.5 C
over the run (a refocus on temperature, #579, would have kept them). At that depth per panel, the whole catalogue
ellipse is about 108 h (30 panels) to 151 h (42) on the SV605CC and 180 to 260 h (50 to 72) on the ASI585, that is 30 to
72 nights of Saturday's 3.6 h; at Sculptor's two-night depth (7.8 h a panel), 2.2 times that. The 585 may need less time
per panel in Ha and SII (its response there), which only a 585 frame of the same field can say.

## E1: an Auto button that suggests a frame plan from what is in the frame

Tracked by #1694. The owner's idea (2026-10-20): the session configuration offers **Auto**, which reads what the
target's field holds and suggests the frame plan's values; the user keeps editing them, and Auto never overwrites a value
the user set.

What it needs, all of which exists or was measured above:
- **The camera at its gain**: electrons per step, read noise and headroom, measured from the profile's own flats and dark
  flats (photon transfer, as above), the SDK's values only as a fallback.
- **The sky rate per filter**: from the rig's own earlier subs with that filter (the ledger of multi-night-progress P0
  reads the headers; a rate needs one light's pixels and its bias), else a site and moon estimate.
- **The brightest thing in the field worth keeping**: the catalogue's stars (Tycho-2 magnitudes) and the object's surface
  brightness over the planned FOV, at the camera's plate scale and the rig's measured star size.
- **A goal**: SNR or hours, per [multi-night-progress](multi-night-progress.md) P1.

What it suggests: the sub length between the sky-limited floor (10 times the read noise's square) and the headroom
ceiling for the chosen sources; the gain (high conversion gain where the camera has it); the count for the goal; and
whether a short set is needed (E2).

## E2: a short set for bright cores

Tracked by #1695. The Tarantula, M42 and M8 have cores that clip in any sub long enough for their faint nebula, and
the 585's 1,950 e- of headroom makes that sooner. The frame plan gets a second, short exposure set per target (or per
filter), suggested by E1 where the long sub clips the target's own core, and captured as its own group: `tianwen stack`
already keys a light group on the exposure, so the short subs stack into their own master
(`master_<target>_light_10s_...`). Merging the two masters is [stacking](stacking.md) § "L. HDR composition".
