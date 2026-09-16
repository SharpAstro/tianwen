# PLAN: the sensor's active area, its overscan, and `*SEC`

**Status: P0 DONE (Canon, 2026-09-07). P1 READ on a QHY178M (2026-09-16), which found that body has
no margin at all, so the gate is lifted for it and still down for every other. P2 and P3 DONE the
same day (`FitsSection`, `ImageMeta.DataSection`/`BiasSection`, read and written). P4 and P5 NOT
STARTED, and nothing populates the sections yet.**

A frame off a sensor is usually bigger than the photograph. Cameras record shielded photosites at
one or two edges — the optical black the camera uses for its own black-level calibration — plus, on
some, a partly-shielded transition and a few spare rows and columns. Nothing downstream of the
importer knew that, so the margin travelled all the way to the screen and into every statistic.

This plan covers three things that turned out to be one thing: cropping to the picture, keeping the
overscan for what it is actually good for, and saying both in FITS so other tools agree.

---

## What is already true

**Canon is done (P0).** `Image.TryReadCanonRaw` crops to `CanonRawFile.ActiveArea`, which FC.SDK.Raw
3.1 reads from the MakerNote `SensorInfo` tag. FC.SDK.Raw deliberately does not crop the mosaic
itself — its CR3 decoder is byte-exact against LibRaw's uncropped `unprocessed_raw`, the only reason
to trust it — so the rectangle is metadata and applying it is ours. See the repo `CLAUDE.md`, "A
Canon raw is cropped to its active area on import".

| body | decoded | active |
|---|---|---|
| 5D Mark IV | 6888x4546 | (156, 58) 6720x4480 |
| EOS M50 | 6288x4056 | (276, 48) 6000x4000 |
| EOS R5 | 5248x3510 | (144, 108) 5088x3392 |
| CR2 (6D) | 5568x3708 | (84, 50) 5472x3648 |

**QHY has the bindings and calls none of them.** `QHYCCD.SDK/include/QHYCamera.cs` declares
`GetQHYCCDEffectiveArea` (821), `GetQHYCCDOverScanArea` (825) and
`CONTROL_ID.CAM_IGNOREOVERSCAN_INTERFACE` (680). Zero call sites across all 23 repos. Meanwhile
`QueryCapabilities` takes `GetQHYCCDChipInfo`'s `imageW/imageH` as `MaxWidth/MaxHeight` (the full
raster) and `SetROIFormat` hardcodes the origin:

```csharp
return ToErrorCode(SetQHYCCDResolution(_handle, 0, 0, (uint)width, (uint)height));
```

So a full-frame capture starts at the raster origin and the effective area's own origin is never
consulted.

**Measured 2026-09-16, from the archive rather than from a camera, and it is not benign.**
`Eta Car SII NB / QHYCCD-Cameras-Capture / 2024-03-02` (a ZS61, SII, 41 subs at 240 s) carries its
overscan into both the lights and the flat, and the whole chain reproduces from the raw files:

| stage (column) | 0 | 1 | 2 | 3 | body |
|---|---|---|---|---|---|
| raw flat | 2272 | 2272 | 2304 | 2512 | 19,088 |
| raw dark-flat | 2272 | 2272 | 2272 | 2272 | 2,272 |
| master flat, mean-normalised | **0** | **0** | **0.0019** | **0.0143** | ~0.99 |
| gain when a light is divided by it | inf | inf | **528x** | **70x** | 1.0 |

Four shielded columns sitting at the bias level (the raw bias reads 2304 across the whole frame,
including them), which the dark-flat subtraction takes to zero and normalisation leaves at zero. No SDK
was needed to read any of this, and the raw subs are 3684 x 5544, the same raster as the flat, so the
overscan is genuinely in the captured data rather than an artefact of processing.

**It is per-body, not per-vendor.** The archive's other QHY light set, `2026-02-20 SW8Q Omega Cen + Cen
A + Running Chicken Neb` (QHY294PROC, RGGB, 4164 x 2795, 193 lights at 60 s g1600, 46 in-session flats,
with `SW8_QHY294_BACKFILL` supplying its bias/dark/dark-flat), shows **no overscan band at all**: its
flat's edge columns alternate 1.5e4 / 3.2e4, which is the CFA, against a body of 32,940, and its light's
edges sit within a few percent of its body. So that set is unaffected, and the plan's "overscan is
model-dependent" survives contact with the data.

**That set is also not in the bake**, and not for any reason to do with quality: the bake's
`archiveRoots` are `D:/Astro-Organized/{lights,flats,calibration}` and `D:/Astro-Unsorted`, while it
lives under `D:/Astro-Pics/2026`. 193 frames and 3.2 hours the corpus has never seen.

**One more thing found in that folder, for whoever fixes the provenance skip:** the SII session's
`Light/` holds two previously-stacked outputs beside its 41 raw subs, `SII-SII-session_1.fits`
(5573 x 3747, left columns exactly 0) and `SII-SII-session_1-crop.fits` (5392 x 3584). Neither is ours,
so neither carries a TianWen `SWCREATE`, and a stack's own output sitting in a lights folder is exactly
what `STACK_N`-based skipping exists for. **What it cost:** calibration divides by the flat, so before the 2026-09-16 `FlatEpsilon` fix those
columns multiplied the light by between 70x and a million, and the integrated master peaked at 2.46e8
against a sky of 213 (245,827,712 x 1e-6 is exactly the ADU value that went in). The band survives into
the master at canvas columns 6 to 9, since registration places and dithers the sensor's columns 2 and 3
across about four canvas ones, and the auto-crop rectangle starts at column 20 so it removes most but
not all of it.

**The overscan is an INPUT, not waste, which is the whole point of P5.** Shielded columns are the
sensor's own per-frame black reference, and a per-frame offset is the one thing a master bias cannot
supply: the master is an average of another night's readouts, while the overscan is this exposure's.
So the crop must do what Canon's does, take the overscan off the picture while leaving it reachable,
and **never** set `CAM_IGNOREOVERSCAN_INTERFACE`, which would throw the reference away in the driver
before anything could use it.

A separate latent bug found while reading: `SetROIFormat` resets the start position to `(0, 0)`, so a
prior `SetStartPosition` is silently discarded by a later `SetROIFormat`. Two public methods, one
hidden ordering dependency, no test.

**Nothing anywhere handles `DATASEC` / `BIASSEC` / `TRIMSEC`**, and `Image` has no concept of an
active area at all.

**Two pieces of machinery this plan leans on already exist and are exercised:**

- Registration is matrix-based: `RawLightSource(string Path, Matrix3x2 TransformToCanvas)`. A crop is
  a translation, so for the stacking path an origin shift folds into a transform that is already
  being composed rather than costing a pixel copy.
- The stacking autocrop already does the coordinate half correctly:
  `MasterPostProcessor` crops the integration result and shifts the solved WCS by the crop origin
  (`cw with { CRPix1 = cw.CRPix1 - autocropRect.X, ... }`). That is the precedent to copy.

---

## The model

**Keep the full raster on `Image`; carry the rectangles in `ImageMeta`.** The alternative — crop at
ingest and keep only measured scalars — is right for a FILE, which can be re-opened, and wrong for a
LIVE frame, which cannot. Per-frame bias tracking is the whole reason a camera exposes overscan, and
a frame whose overscan was dropped at ingest has lost it for good.

`ImageMeta` gains two appended, defaulted positional parameters, matching how `Guiding` and
`ColourCalibration` were added:

```csharp
Rectangle? DataSection = null,   // the picture inside the stored array
Rectangle? BiasSection = null    // the usable overscan strip
```

Both are **0-based, in the stored array's own pixel coordinates**, like every other rect in
`TianWen.Lib.Imaging` (`DebayerRegionIntoAsync`'s `sourceRect`, `MasterPostProcessor`'s crop).
`null` means "no declaration", which is every frame that exists today and must stay
byte-for-byte unchanged.

**One crop gate, not a rule every consumer must remember.** The risk in this design is real: star
detection, WCS, the GPU upload and the stacking canvas all currently assume `Image.Shape` is the
picture, and a missed consumer is a silent geometry bug of exactly the class the CRPIX one-pixel
incident was. So the crop is applied in one place per path rather than checked in many:

| path | how the picture is obtained |
|---|---|
| viewer / save | cropped at the document boundary, as the Canon import already does |
| registration + stacking | origin folded into `TransformToCanvas`; no copy |
| plate solve | cropped view, WCS `CRPix` shifted by the origin (`MasterPostProcessor` precedent) |
| calibration | reads `BiasSection` off the uncropped frame — the one consumer that wants it |

---

## `*SEC` in FITS

IRAF's section keywords are the established convention and are what `astropy` / `ccdproc` consume:

| card | meaning |
|---|---|
| `DATASEC` | the picture inside the stored array |
| `BIASSEC` | the overscan strip to measure from |
| `TRIMSEC` | what to keep when trimming (usually `DATASEC`) |
| `DETSEC` / `CCDSEC` | where this sits on the detector, for a ROI or a mosaic |

Format is `'[x1:x2,y1:y2]'`, **1-based, inclusive, FITS axis order**.

**That is the same trap as CRPIX, and this repo has already paid for it once.** Until 2026-09-05 the
pixel origin crossed the header unchanged under a "1-based" comment, so every file TianWen solved was
a pixel off in astropy, PixInsight, Siril and ASTAP (`PIXORIG`, `WcsPixelOriginTests`). A section
string has the identical shape. The rule is therefore the same: **1-based exists only inside the
header**, a `Rectangle` is 0-based everywhere in memory, and the conversion lives in exactly one
helper with a round-trip test.

Parsing goes into **`ParseImageMetaFromHeader`**, the single place a header becomes an `ImageMeta`.
Both read paths — `TryReadFitsFile` and `TryReadFitsHeader` — call it, so a card added there is
present in both by construction, and `FitsPixelScaleTests.TheTwoReadPathsAgreeOnEveryMetadataField`
fails on the next divergence.

---

## Prior art: what PixInsight does, and what the vendors actually say

Two PixInsight forum threads cover this ground for QHY's CMOS bodies, and they change the design
rather than merely confirming it. Both are archived under `Astro-Info` (saved 2026-09-07):
[the procedure thread](https://pixinsight.com/forum/index.php?threads/imagecalibration-and-overscan-area-procedure.14469/)
and [overscan vs optical black](https://pixinsight.com/forum/index.php?threads/imagecalibration-overscan-with-optic-black-area.14139/).

**The drift is not thermal, and that is the whole point.** QHY's own support answer is that "the
on-chip calibration part of the cmos sensor may cause the drift of the whole image when the image is
bright". The bias offset therefore moves with SCENE BRIGHTNESS, frame to frame, rather than slowly
with temperature. A master bias cannot track that by construction, however many frames go into it,
which is the argument for a per-frame reference existing at all.

**It lands in the denominator, which is where the damage is.** Written out, calibration is

```
calibrated = (L - D + drift_L) / (F - DF + drift_F)
```

and QHY's conclusion is that "the drift of light frame will have little impact. But drift of the flat
frame appearing in the denominator can cause over/under calibration." So if this is ever worth
effort, the FLAT path is the payoff and the light path is nearly free. That is the opposite of where
the intuition points, and it is the one thing here worth remembering.

**What `ImageCalibration` actually computes** is one MEDIAN per region, subtracted as a scalar from
every pixel of the mapped target region, applied alike to bias, dark, flat and light. Up to four
regions, each a source rect (measure here) mapped to a target rect (apply there), plus an image
region saying what survives. Overscan calibration runs FIRST and CROPS, so every product downstream
shares one geometry. Rects are x, y, width, height with coordinates starting at zero, which is our
`Rectangle` and not the `*SEC` convention above.

**Three limits, stated by PixInsight's author, all of which we would inherit:**

- **Optical black and overscan are different references and only one may be used.** "I guess that
  either could be used, but definitely not both at the same time. The value computed from the
  optically black area would additionally contain the average dark current, and the value computed
  from the overscan area would not." Canon's margin, the one P0 crops, is optical black: shielded
  photosites that integrate dark current like any other. A level taken there is bias plus mean dark,
  not bias.
- **It is not a dark replacement.** "Since Overscan calibration only subtracts a constant value from
  all pixels in the corresponding target region, fixed pattern noise is not at all corrected by this
  procedure."
- **It buys nothing on a stable body.** "In case of a drifting bias offset, the calibration result
  will be more precise. If the bias offset of your camera is stable, using Overscan calibration in
  PixInisght will not improve the calibration result." This is the external argument for P1 being
  measurement only, and for stopping there if the measurement says stable.

**They are different EDGES, not merely different concepts.** QHY's own annotated 300 s dark
(`Astro-Info/QHY_FRAME_OVERSCAN_OPTICAL_BLACK.png`) puts the optic black area down the LEFT and the
overscan area across the BOTTOM, with the effective area between them. The worked QHY600 rectangles
in the procedure thread agree: an image region starting at `x = 25` leaves 25 optically black columns
on the left, while the overscan SOURCE region is `(0, 6388, 9600, 34)`, thirty-four rows spanning the
full width at the bottom. So a body can expose both at once, on different edges, and the correction
in that thread is driven from the bottom strip while the left strip is simply cropped away.

**A long dark tells them apart by eye, and that is the cheapest P1 measurement.** In that 300 s
frame the hot pixels speckling the effective area run straight through the optic black strip and are
absent from the overscan strip. That is Conejero's "the value computed from the optically black area
would additionally contain the average dark current" made visible: optic black is a photosite with a
lid on it and integrates dark current for the full exposure, overscan is clocked readout with no
photosite behind it. Once a body is attached, expose long, look at both strips, and the geometry
question and the which-reference question are answered at once.

**Whatever statistic we take must be robust.** QHY annotate the overscan strip "some dot lines within
overscan area is normal", which is the same disclaimer as their "does not guranttee the signal
quality in the overscan area". Outliers are expected there BY THE VENDOR, so a median (which is what
PixInsight takes) rather than a mean, and the same caution for any per-row or per-column variant.

**A second algorithm exists, and PixInsight does not implement it.** Rather than one scalar, subtract
per column and per row; STScI's STIS Data Handbook calls this BLEVCORR, section 3.4.4 "Large Scale
Bias & Overscan Subtraction". Worth knowing before anyone assumes the scalar median is the only
shape the correction can take.

**Vendor practice, and a naming trap.** QHY ship the overscan included by default, offer "ignore
overscan area" in the ASCOM driver, and say plainly that they "does not guranttee the signal quality
in the overscan area" (sic). The trap is that the flag's sense is unreliable in the vendor's own
words: for the QHY268C they describe enabling overscan as giving 6252x4176 and NOT enabling it as
6280x4210, which is backwards, since the larger raster is the one carrying the 24 left and 4 right
border columns. **Read the dimensions, never the flag name.** One more reason P1 logs all three
answers rather than trusting any one of them. Note also that practice is split: one QHY600M owner
reports a visible improvement from overscan-calibrating flats and lights, while another simply
removes the region in the driver and reports that everything calibrates fine.

---

## Phases

| # | Scope | Status |
|---|---|---|
| P0 | Canon: `CanonRawFile.ActiveArea` (FC.SDK.Raw 3.1) + crop in `Image.TryReadCanonRaw` | **DONE** 2026-09-07 |
| P1 | QHY: call `GetQHYCCDEffectiveArea` / `GetQHYCCDOverScanArea` and probe `CAM_IGNOREOVERSCAN_INTERFACE` at connect, log all three. **Measurement only, no behaviour change.** Never SET the ignore flag: it discards the black reference P5 wants | **READ 2026-09-16 on a QHY178M** (`QhySensorGeometryProbe`); the gate is lifted for that body and still down for every other |
| P2 | `ImageMeta.DataSection` / `BiasSection`; `Image` keeps the raster | NOT STARTED |
| P3 | `DATASEC`/`BIASSEC`/`TRIMSEC` read in `ParseImageMetaFromHeader` + written by the FITS writer, one 1-based converter, round-trip test | NOT STARTED |
| P4 | The crop gates: viewer/save, registration transform, plate-solve CRPix shift | NOT STARTED |
| P5 | Calibration consumes `BiasSection`: per-frame black level + read noise. **The FLAT path first**, since the drift enters the denominator | NOT STARTED, and now the leading piece: measurable from the archive, no body needed |

**P1 first, and on its own.** Whether P2-P5 are worth anything on real hardware depends on what the
attached QHY actually reports, and `CAM_IGNOREOVERSCAN_INTERFACE` may make the capture path need no
crop from us at all. One connect-time query decides the scope of everything after it.

**P1-P5 were GATED ON HARDWARE, deferred 2026-09-07, and that gate is now partly lifted (2026-09-16).**
The deferral rested on "no vendor SDK path has the property a Canon raw has, so only an attached body
knows the geometry". For the CAPTURE path that still holds. For the ARCHIVE it does not: a frame that
was taken with the overscan in it **carries the overscan**, so the rectangle is measurable from the
files, and it was, off the flat's column medians above. That changes the order of work.

- **P5's flat leg is no longer speculative and is the highest-value piece**, which is what the phase
  table already suspected in writing "the FLAT path first, since the drift enters the denominator".
  It is the leg that produced a 2.46e8 pixel.
- **P1 stays gated**, because generalising past this one camera does need the SDK: overscan is
  model-dependent, and one measured QHY294 says nothing about the next body.
- **A re-bake is what applies any of this to existing data, and the fix does not retroactively repair a
  master.** The raws are intact and still carry their overscan, so nothing is lost and a re-bake can
  both crop and use it, but every master built before then keeps what it was built with.
- **Scope is one session of the two QHY light sets in the archive**, the 2024 ZS61 SII one. The 2026
  SW8Q QHY294 set has no overscan and needs nothing, which is the measurement that says a per-body
  query is still required rather than a rule. The bench queue carries the rest, one home per item, in
  [hardware-validation.md](../todo/hardware-validation.md) under "Gated on gear but NOT validations".

---

## P1 READ on a QHY178M (2026-09-16): no margin at all, and the SDK never reached the box

A body was attached, which lifted the gate for the first time. `QhySensorGeometryProbe` (opt in with
`TIANWEN_QHY_PROBE=1`, measurement only, never SETS the ignore flag):

```
QHY178M-688f524c91b5c8859, SDK v25.9.29.10
  readout     3056 x 2048 px, pixel 2.400 um, 16 bpp
  effective   origin (0, 0) size 3056 x 2048     <- the WHOLE readout
  overscan    origin (0, 0) size 0 x 0           <- empty
  ignore-overscan control: not available
```

**This body ships no margin, so P2 to P5 buy it nothing**, and the archive's 2024 QHY set does carry
one. Two QHY bodies, two answers: **the per-body SDK query is mandatory and no vendor-level rule
exists**, which is exactly what the phase table suspected and is now measured rather than assumed.
A second, independent check agrees: `chipW x chipH` equals `imageW x pixelW` exactly here
(7334.4 = 3056 x 2.4, 4915.2 = 2048 x 2.4), so the readout contains nothing the pixel grid does not
account for. Where those two disagree, the difference IS the margin, which makes it a free test on
any body. **Note the unit**: the SDK returns those dimensions in MICRONS though the field is widely
documented as mm, a 1000x trap for anyone who exposes it. Nothing in TianWen consumes them today.

**Getting there found a bug worth more than the measurement.** `QHYCCD.SDK` packed the native
`qhyccd.dll` into `runtimes/<rid>/native` for the NuGet package and nowhere else, and that layout is
a NuGet resolution mechanism: a ProjectReference consumer got the managed assembly and no native
library. Since `UseLocalSiblings` self-enables whenever the sibling clone is present, **QHY cameras
worked from the published package and had never once worked from a local build**, silently, because
a `DllImport` with nothing to bind looks exactly like no camera being plugged in. Fixed in the
sibling ("the native library now reaches a ProjectReference consumer"); the camera was discovered on
the first attempt afterwards.

## The first frame after `InitQHYCCD` is not like the others (2026-09-16)

Chasing the owner's long-standing report that this camera "randomly doesn't show frames, or old
frames, or super bright frames and then dim frames", `QhyDarkSequenceProbe` takes a dark sequence
and reads each frame's level AND a digest, because a REPEAT (a stale buffer handed back twice) and a
LEVEL EXCURSION (a real readout at the wrong offset) are two different bugs wearing one description.

| | |
|---|---|
| repeats | **none**, 12 of 12 frames distinct. The "old frames" symptom did not reproduce through the raw SDK, which points at the consumer rather than at the SDK |
| frame 0 | median **16**, mean **20.2** |
| frames 1 to 11 | median **40**, mean **39.7 to 41.9**, a spread of 0.4 percent |

**Reproduced 2 runs of 2.** It is not an exposure-time effect: 1 s and 2 s frames read the same mean
(~40), so dark current is negligible at these lengths and what moves is the BIAS. It is not a
parameter lag either, which was the first hypothesis and is refuted: stepping the exposure 1 s, 2 s,
1 s moves the wall time on the exact frame the change was commanded (2951, 3949, 2978 ms), with the
readback agreeing. And it is per-OPEN rather than per-change: within a run the level holds steady
through two exposure changes. **So it is the first readout after `InitQHYCCD`, at about half the
bias level of every frame after it.**

**Two rival explanations were tested and both are refuted**, each by a measurement the earlier ones
could not have made. A STALE BUFFER handed back twice: refuted, 12 of 12 frames carry distinct
digests. A frame carrying the PREVIOUS exposure's content, which is what the owner recalled of this
camera in other software: refuted by stepping the GAIN, where the level moves on the commanded frame
(median 396 to 4 and back to 384) rather than one frame later. **That second test was necessary
rather than belt-and-braces**: the exposure-step test judged immediacy by the WALL CLOCK, and the
frame CONTENT at 1 s and 2 s is identical here because the level is bias-dominated, so a
returned-previous-frame bug would have been invisible to it, and so would the settling frame's own
content. Gain changes what is IN the frame, which is why it can separate them and exposure cannot.

**Two things the driver already does, found while nearly duplicating them.** `DALCameraDriver`'s
connect-time control writes include `EnableDDR = 1` and `BandwidthOverload = 50`, so TianWen has
been enabling the DDR buffer and pinning USB traffic on every DAL camera all along; a QHY-specific
enablement was written, then deleted on finding it. The probes read `CONTROL_DDR` as 0 only because
they are RAW SDK and bypass the driver entirely, which is a thing to remember before concluding
anything about "what the camera does" from them.

**Cooler setpoints PERSIST across a close, and the connect inherits them.** Measured the same
evening: written -10 on one handle, a FRESH handle after a close reads -10 back and `CURPWM` goes
straight to 255, full power, on a body whose TEC has no power at all. The DDR buffer is the
opposite, reading 0 on every fresh open. So cooling is state somebody else owns, and any diagnostic
path must leave it alone rather than configure it. `CONTROL_COOLER` also reports **-100 while
declaring its own range as -50 to 100**, an out-of-band sentinel on a control whose range claims to
be degrees; `DALCameraDriver` maps BOTH `CoolerOn` and `TargetTemperature` onto that one control and
models no sentinel.

A per-frame black level would correct precisely this, which is what the owner suspected, **but this
body exposes no shielded strip to measure one from** (P1 above). So the options are a settling frame
at connect, or flagging the first frame and letting the quality gate drop it. **A settling frame was chosen, measured, and then KILLED by the measurement, which is the most
important paragraph here.** A 100 ms throwaway does clean up a sequence whose excursion happens to
land on frame 0, and the first three sequences taken all looked like that. Repeating the SAME
configuration six times, each already settled, does not:

```
run 1 : 12 12 12 12     run 3 : 16 16 16 36   <- jumps at frame 3
run 2 : 16 16 36 36     run 4 :  8  8  8  8
   <- jumps at frame 2  run 5 : 12 12 12 12   run 6 : 16 16 16 16
```

**The level jumps MID-SEQUENCE, at frame 2 or 3, after settling.** Two of six. So "the first readout
after `InitQHYCCD` is unsettled" is an insufficient model: it was only ever the first frame in the
sequences that happened to be taken, and a settling frame cannot protect against an excursion that
arrives later. Three further readings. The levels are **quantised in steps of 4** and wander between
a handful of discrete states (8, 12, 16, 20, 36, 40) both within a connect and between connects at
identical gain, offset and exposure. The jump is **one-way within a run**, always upward, never back.
And **the DDR buffer makes no difference**: five repeats each read 2 of 5 excursions with it on and
1 of 5 with it off, indistinguishable at that count, so the buffer is worth having for the USB-stall
failure it is actually for and is not this.

**So the owner's first instinct was right and the fix is the one this plan is about.** An unstable
black level that can move at any frame is exactly what a PER-FRAME reference corrects and what a
master bias cannot, the master being an average of another night's readouts. The reason this camera
has been unfixable is P1's finding: it exposes no shielded strip to measure one from. A settling
frame was NOT implemented, because it would cost an exposure per connect and still leave those two
runs broken. Two cautions for whoever takes it:
discarding at EXPOSURE time cannot tell "first frame of the session" from "first frame after a
filter change" and would risk silently binning a long sub, and **one body is not a vendor**, which
is why PHD2 ships "discard initial frames" as a per-camera option rather than a blanket rule. Our
own SDK has no first-frame handling of any kind today.

---

## Traps

- **An odd crop origin re-phases the CFA.** `BayerOffsetX/Y` must move with a `DataSection` whose
  origin is odd, or the Bayer pipeline gets a frame with red and blue exchanged — a plausible picture
  in the wrong colours, not an error. Every Canon body measured offsets evenly, which is exactly why
  FC.SDK.Raw's `ShiftCfa` is unit-tested rather than assumed.
- **`BIASSEC` is the MEASURED usable strip, not the whole discarded margin.** On the 5D Mark IV the
  optically black columns stop at 143 while the active area starts at 156; the twelve between are lit
  but only partly shielded, so they belong to neither. Canon's own `BlackMaskLeftBorder` fields read 0
  on all four files inspected, so the window cannot be read off a tag.
- **Optical black is not overscan, and neither one is a dark.** What a Canon exposes, and what P0
  crops, is optically black PHOTOSITES: shielded, but integrating dark current like every other
  pixel, so a level taken there is bias plus MEAN dark. True overscan is extra clocked reads with no
  photosite behind them, and carries bias alone. PixInsight's author is explicit that the two are
  different references and must not both be used at once (see the prior-art section above). Either
  way, neither substitutes for a dark master: dark current is per-pixel and structured (hot pixels,
  amp glow, corner gradients) and a strip at one edge cannot predict a hot pixel in the
  middle. Measured on a 5D Mark IV frame over `x < 144`, per CFA cell: level 2047.9 to 2048.3 against
  the hardcoded `blackLevel = 2048`, spread 15.6 to 17.9 ADU, so the mean-dark term is under half an
  ADU on that frame, which says nothing about a long exposure at ambient. The spread is the
  interesting half — pure read noise, with no sky shot noise in
  it, which nothing measured from the active area can give.
- **Use columns, not rows, on a Canon.** The top margin is not all masked: `G(r)` reads 2027 with
  three times the noise of its neighbours, and R and G(b) reach 3287 and 4411.
- **No per-row bias correction without re-measuring.** Row means wander 2044.8 to 2050.3, which looks
  like row-wise readout offset — but at 144 samples and sd 16 the standard error of a row mean is
  1.33 ADU, and that range over 18 sampled rows is about what pure noise gives.
- **A dimension assertion is not a crop test.** A crop from the wrong corner is still a photograph.
  `Cr3ImportTests.Cr3_CropsFromTheDeclaredOrigin` pins the offset at three corners and has to SEARCH
  for a pixel where cropped and uncropped reads differ, because the R5 fixture is almost entirely
  zero after black subtraction and both a corner block and a centre block matched on either side.
- **Measure `MaxValue` over the pixels you keep.** The Canon import used to scan the whole mosaic, so
  a margin pixel could have set the peak the stretch pipeline divides by.
