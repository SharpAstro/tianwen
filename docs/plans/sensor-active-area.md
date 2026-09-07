# PLAN: the sensor's active area, its overscan, and `*SEC`

**Status: P0 DONE (Canon, 2026-09-07). P1-P5 NOT STARTED.**

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
consulted. **This is unmeasured**, unlike the Canon case: overscan is model-dependent (the large CMOS
bodies have it, many smaller ones report a zero-size rect with effective == full), and no QHY frame
has been examined. P1 exists to replace that speculation with numbers.

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
| P1 | QHY: call `GetQHYCCDEffectiveArea` / `GetQHYCCDOverScanArea` and probe `CAM_IGNOREOVERSCAN_INTERFACE` at connect, log all three. **Measurement only, no behaviour change** | NOT STARTED |
| P2 | `ImageMeta.DataSection` / `BiasSection`; `Image` keeps the raster | NOT STARTED |
| P3 | `DATASEC`/`BIASSEC`/`TRIMSEC` read in `ParseImageMetaFromHeader` + written by the FITS writer, one 1-based converter, round-trip test | NOT STARTED |
| P4 | The crop gates: viewer/save, registration transform, plate-solve CRPix shift | NOT STARTED |
| P5 | Calibration consumes `BiasSection`: per-frame black level + read noise. **The FLAT path first**, since the drift enters the denominator | NOT STARTED |

**P1 first, and on its own.** Whether P2-P5 are worth anything on real hardware depends on what the
attached QHY actually reports, and `CAM_IGNOREOVERSCAN_INTERFACE` may make the capture path need no
crop from us at all. One connect-time query decides the scope of everything after it.

**P1-P5 are GATED ON HARDWARE, deferred 2026-09-07.** Canon (P0) shipped because a raw file carries
its own `SensorInfo` rect, so a fixture answers every question. No vendor SDK path has that property:
overscan geometry is model-dependent and the SDK is the only thing that knows it, so writing P1
without a body attached produces a log line nobody can read and a scope decision nobody can take.
Work resumes when a real QHY (or any other vendor's) camera is connected. The bench queue carries it,
one home per item, in [hardware-validation.md](../todo/hardware-validation.md) under "Gated on gear
but NOT validations".

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
