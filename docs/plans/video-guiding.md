# Video (MetaGuide-style) Guiding

**Status: NOT STARTED.** Raised 2026-08-29 by the user, referencing Frank FreeStar8n's
[MetaGuide](https://smallstarspot.com/metaguide/) (the "Antonio" surname attached to this handle
elsewhere is unconfirmed -- stick to "Frank FreeStar8n," the name actually used on smallstarspot.com
and AstroBin). Not an Astropy-parity item, but **correction:
this IS partly a NINA-parity item** -- NINA has native MetaGuide support (see below), which
TianWen has neither half of. Cross-linked from [nina-parity.md](nina-parity.md).

## Two distinct things this could mean -- both worth tracking, do not conflate

1. **Adopt the technique internally**: build TianWen's own video-rate guider mode using the same
   hot-spot/lucky-selection/rolling-average approach. This is the bulk of this document.
2. **Support MetaGuide as an external guider**, the way `OpenPHD2GuiderDriver` supports PHD2 --
   and NINA already does exactly this. NINA's own docs describe MetaGuide integration via
   **MetaMonitor**, a UDP broadcast (default port 1277) of MetaGuide's status: guide star
   intensity/saturation (0-255), guide error, and dither-settle timing -- read by NINA to decide
   when a dither has settled and to display star intensity, per NINA's guiding docs. **This is
   almost certainly read-only telemetry, not command-and-control**: MetaGuide has its own ASCOM
   connection to the mount (its `Setup` panel handles "connection to the mount" directly) and
   drives corrections itself, independent of any external app. That is architecturally unlike
   `OpenPHD2GuiderDriver`, which commands PHD2's start/stop/dither/calibrate over a bidirectional
   JSON-RPC channel because PHD2 expects to be told what to do. A `MetaGuideDriver : IGuider`
   would have to either leave most of `IGuider`'s command surface (`GuideAsync`, `DitherAsync`,
   `ClearCalibrationAsync`, ...) as no-ops against a guider that is already running itself, or the
   interface is the wrong shape for it -- settle this before attempting an implementation. Smaller
   in scope than option 1 (a UDP listener + a settle-status projection, not a whole new guiding
   loop), and independently useful even if option 1 never happens.

Everything below this point is about option 1.

## The technique, precisely (per the 2024 user manual, v6.1.7, PDF supplied by the user -- HTML/PDF
## on smallstarspot.com 403s to automated fetches, extracted locally via `pdftotext`)

MetaGuide runs the guide camera at video rate. Per frame, it computes a **windowed centroid around
the peak-brightness pixel** -- the "Peak" `StarStacking` mode, the manual's own recommendation for
guiding over the classic whole-star center-of-gravity ("Centroid" mode) -- specifically because a
short-exposure guide star is frequently a misshapen, seeing-distorted blob, and a center-of-gravity
calculation "will find the center of mass of that blob and be heavily biased by whatever strange
things are happening near the edge." That is the "hot spot": a small window around the star's
brightest point, not its whole light distribution.

Over a rolling `StackTime` window (frames gathered continuously, not one discrete exposure), each
individual frame gets its own windowed-peak centroid. These per-frame centroids are then **sorted
by quality, and only the best fraction -- the `AccepFrac` parameter -- are kept**. This is the
"Lucky Guiding" the manual names explicitly: literal lucky-imaging frame selection (keep the
sharpest, discard the rest), applied to the guide star rather than to a whole image. The final
position is, in the manual's own words, "the centroid of those [accepted] centroids" -- an average,
but over a quality-filtered subset, not a naive mean of every frame.

Three things this buys, per the manual, and one constraint worth stating precisely rather than
overselling:

- `StackTime` "acts as an effective exposure time for the guidestar, but it is based on analyzing
  each individual video frame" -- long-exposure-grade SNR without ever forming one actual
  long-exposure star (which would show a seeing-elongated blob instead of a clean hot spot).
- Telemetry (the FWHM/intensity/error plots) updates "twice per second, regardless of the effective
  exposure for the centroid," because the window is rolling -- monitoring and tuning are decoupled
  from `StackTime` even where the correction itself is not.
- **`StackTime` must not exceed `GuidePeriod`** (stated as a hard rule in the manual): the averaging
  window cannot outrun the correction cadence. `GuidePeriod` -- seconds between corrections
  actually sent to the mount -- is user-tunable, and the manual's own worked example runs
  corrections "every 1 second," the same order of magnitude as conventional single-exposure
  guiding. **The claimed advantage is centroid accuracy and freshness, not necessarily a much
  higher correction rate than PHD2-style guiding** -- worth being precise about rather than
  overselling: the manual's own numbers don't show many-times-a-second corrections, just a
  correction fed by a materially better (video-averaged, lucky-selected, peak-windowed) position
  estimate than one long CCD exposure could give.

**Not confirmed anywhere in the manual, do not assume:** rolling-shutter-specific sub-frame timing
correction. An earlier draft of this doc speculated about this from general knowledge attributed
to this author under an unconfirmed surname; a full-text search of the actual 2024 manual turns up
nothing on
rolling-shutter handling, so the claim is dropped until it's verified from another source.

`IGuider.ExposureTimeAsync` returning one fixed duration for the whole guiding session is the
concrete evidence that today's TianWen model has no notion of any of this: `BuiltInGuiderDriver.
CaptureGuideFrameAsync` (`Devices/Guider/BuiltInGuiderDriver.cs`) is built the same way PHD2 is --
`StartExposureAsync(exposure, ...)` -> wait for the whole exposure to complete -> read one frame ->
compute a correction -> repeat. One discrete exposure is both the noise-integration window and the
interval between corrections; there is no rolling window, no per-frame quality ranking, and no
peak-windowed "hot spot" centroid (mode) distinct from a whole-star centroid.

## What already exists in TianWen that is directly reusable

This is not a green-field problem -- three pieces of the planetary lucky-imaging work
(`Imaging/Planetary/`) are architecturally adjacent, built for a different purpose but the right
shape:

- **`IVideoCameraDriver.CaptureVideoAsync`** (`Devices/IVideoCameraDriver.cs`) is exactly the
  "stream short exposures at high fps" capture contract MetaGuide needs -- vendor-neutral (ZWO/QHY
  native, Canon Live View, and a universal `RapidExposureVideoAdapter` short-exposure loop for any
  `ICameraDriver`), already shipped for planetary capture. The capture-side building block already
  exists; nothing new needed there.
- **`RollingWindowStacker`**'s O(pixels) sliding-window add/evict (a frame's contribution is
  re-folded out with a *negated weight* on eviction, so +w then -w cancels exactly) is the same
  numerical *pattern* a `StackTime`-style rolling position average needs, just over a 2-vector
  (x, y) instead of a full image. Far cheaper to adapt than to invent from scratch.
- **`IFrameQualityEstimator`/`LaplacianEnergyEstimator` + `LuckyImagingStacker`**
  (`Imaging/Planetary/`) already grade frames by sharpness and keep the best N% -- this is
  literally MetaGuide's `AccepFrac` idea (per-frame quality ranking, discard the rest), already
  implemented and shipped, just applied to whole planetary frames rather than a per-frame
  windowed guide-star centroid. The rank-and-keep-best-fraction *logic* is directly reusable; only
  the thing being ranked (a small ROI's sharpness/peak quality vs. a whole disk frame) differs.
- **A gap worth naming honestly**: TianWen's existing star-centroid measurement (used for
  autofocus and plate solving) is not the same thing as MetaGuide's "Peak" windowed centroid --
  it is tuned for well-formed stars in reasonably long exposures, not for deliberately ignoring a
  short-exposure guide star's distorted edges by windowing around its peak pixel. A video-guiding
  mode would need that specific edge-robust centroiding, not a repurposing of the existing one
  as-is.
- **`PlanetaryRecenterController.Decide`** (`Imaging/Planetary/PlanetaryRecenterController.cs`) is
  an existing pure, stateless, per-frame controller: measured centre-of-mass in, a deadband
  (`DeadbandPixels`) plus a damped `Gain` (0..1, "fraction of the measured offset corrected per
  frame") out, already wired to `MountActions.PulseGuideArcsecAsync` and an ROI-jog fallback.
  **This is architecturally adjacent but not the same technique, and the two must not be
  conflated**: `PlanetaryRecenterController` damps the *correction* (acts on each frame's raw
  offset, but only by a fraction, letting the residual carry over frames); MetaGuide smooths the
  *measurement* (averages the position itself over a window, then corrects toward the smoothed
  value). Both are low-pass filters on a noisy position signal in the frequency-domain sense, but
  they filter at different points in the pipeline and are not interchangeable descriptions of the
  same thing.
- **The pulse-guide actuation primitives are fully built and driver-agnostic already** --
  `StartPulseGuideAsync`/`PulseGuideAsync` (see CLAUDE.md's "A guide pulse is TWO methods" and the
  SkyWatcher background-hold work) are exactly what a video-guiding corrector would call; only the
  capture -> smooth -> decide loop in front of them is missing for a *sidereal guiding* use case
  (`PlanetaryRecenterController` exists for *framing* a planet, not sub-arcsecond tracking
  correction).

## What would actually need building

A new video-guiding mode, most likely a new capture path inside (or alongside)
`BuiltInGuiderDriver` rather than forced through `IGuider`'s PHD2-shaped contract as-is:

1. Stream via `IVideoCameraDriver.CaptureVideoAsync` with a short per-frame exposure over a small
   ROI around the guide star.
2. Per frame, compute a **peak-windowed** centroid over that small ROI -- new, edge-robust
   centroiding distinct from the existing autofocus/plate-solve star measurement (see the gap
   noted above).
3. **Rank frames by quality and keep only the best fraction** (the `AccepFrac`/"Lucky Guiding"
   step) -- adapt the existing `IFrameQualityEstimator` grade-and-keep-best-N% logic rather than
   writing a new one.
4. Average the accepted frames' centroids over a rolling window (a `StackTime` analogue) -- adapts
   `RollingWindowStacker`'s add/evict pattern to a 2-vector rather than a fresh windowing scheme.
5. Drive corrections off the deviation of that averaged position from the lock position, at a
   tunable cadence (a `GuidePeriod` analogue, constrained to be `>=` the rolling window), through
   the existing pulse-guide primitives.

**Open design question, unresolved:** does `IGuider`'s settle/dither contract (settle pixels,
settle time, PHD2-style discrete looping-exposure semantics) map onto this loop at all, or does
video guiding want its own lifecycle with an analogous but differently-computed "locked within N px
for M seconds" settle criterion (evaluated from the rolling average rather than from consecutive
discrete-exposure reports)? Settle this before picking an implementation shape -- forcing PHD2's
vocabulary onto a fundamentally different capture model may be the wrong fit.

## Why it would be worth doing

Per the manual's own framing: better centroid accuracy under bad seeing (the peak-windowed,
lucky-selected centroid specifically avoids the bias a whole-star measurement gets from a
short-exposure star's distorted edges), which the manual credits with letting a **mid-range mount**
achieve guiding results ("1.2 arcsec FWHM") that otherwise need premium mounts and gearboxes --
"inverting the priority of cost" toward the camera and optics instead. Worth being precise that
this is the claimed benefit, not necessarily a correction rate much faster than PHD2-style guiding
(see the `StackTime`/`GuidePeriod` discussion above). Built almost entirely on capture and actuation
machinery this repo already ships for planetary work (`IVideoCameraDriver`, `IFrameQualityEstimator`,
the pulse-guide primitives); the genuinely new pieces are the peak-windowed centroider and the
2-vector rolling-average/accept-fraction loop tying them together.
