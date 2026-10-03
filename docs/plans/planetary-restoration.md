# Planetary restoration by measurement

**Status: PARTIAL: R0 to R8 done, R9 conditional** (written 2026-09-28, the user's request; R0 2026-09-29: the survey, the FITS video conversion and the tracked lossless crop; R1 2026-09-29: the ephemeris, the limb fit, which telescope; R2 2026-09-29: rendered truth, T1 passed, and a synthetic capture that matches the real one on the disk, its kill line firing on the sky's finest bands; R3 2026-09-30: the metrics, the limb's undershoot validated, the halves' agreement not, pending R7's blur; R4 2026-09-30: per frame, the Laplacian ranks an 8-bit capture's frames near chance and the mid bands rank them well, on the twin and truth-free on the real capture, the choice waiting on open question 4 and the per-point half on #1071; R5 part 1 2026-09-30: phase correlation places 8-bit frames and points three times worse than a plain one, every stack better plain (#1074), and the real capture's warp visible only plain; part 2 2026-09-30: the dewarp cannot follow this capture's warp, 0.6 px over 10 px, the mesh the stack applies recovering 0 to 3 % of it, and the kill line fires; part 3 2026-09-30: the bilinear kernel is the stack's blur, sinc^2 in transfer, and Lanczos-3 lifts band 1 10 to 12 % (#1086), a correlation's peak is climbed rather than fitted by a parabola, a stacked reference rescues phase correlation, and the three-cornered hat compares registrations with no truth where the twin clears the triple, which AutoStakkert's track and our limb fit, both reading the outline, never do; the literature behind what comes next is `docs/architecture/planetary-literature.md`, its follow-ups #1081 to #1085; R5a 2026-09-30: a colour twin of 2024-12-15's Uranus-C capture, calibrated per colour, on which Bayer drizzle to the sensor grid beats the demosaic above a plane's Nyquist and nothing past the sensor grid pays, the adoption #1091 and a smaller drop #1092; on the way, a camera's corrupted readout frame kept out of every grade, and an alignment point's patch cut at the exact global shift; R6 part 1 2026-09-30: the spheroid's projection both ways and a de-rotation that carries the albedo, finished stacks carried to one epoch leaving 0.46 of the difference at 11 minutes, north decided by the stacks' agreement; part 2 2026-09-30: every frame carried to a run's middle inside the stacker on every path, a night's captures joined in time order, the disk fitted on a stack and north decided by the run's quarters, the 16-minute run's halves left at 0.45 of their difference, and two stacks of one camera moved onto each other with one north; part 3 2026-09-30: the belts' latitudes against OPAL within half a degree in five stacks of six, one edge, the SEB's north, north of OPAL's in all six, and a night's drift below what two stacks of one night agree on, about half a pixel, so the winds are not decided; R6 is done; R7 part 1 2026-10-01: the spectral ratio, which finds the free air on the twins once an 8-bit sky's noise is its own pixels' spread, sees the still layer, cannot tell a warp from the seeing over the band the noise leaves, and so claims no r0 for the real capture, whose warp is not the twin's; part 2 2026-10-01: the stack's blur is its lucky frames' own within 0.33 to 0.43 px, and the limb fit's kernel, its halo at its model's bound, reads the finest two bands 17 to 39 % too blurred; part 3 2026-10-01: the limb reads the TOTAL blur, diffraction included, within 6 % in bands 2 to 4 once the twin's diffraction-limited truth is not what it is set against, band 1 uncertain by a fifth, and the SEB's north edge is not moved by the blur; part 4 2026-10-01: `planetary-inverse`, Richardson-Lucy with the limb's kernel over diffraction misses the oracle, restoring bands 2 to 4 about 4 % past the truth with 7.6 times the oracle's undershoot, and band 1 is the kernel model's tail, which no edge here measures, #1120; R8 part 1 2026-10-01: `planetary-ceilings`, the twins' per-frame PSFs written by `planetary-degrade --psf-truth`: one gain per a trous band reaches the best isotropic filter only when the gains are fitted JOINTLY, an exact 2-D kernel is worth 13 to 21 % more, and weighting each frame per frequency only 2 to 4 % on the kept frames; part 2 2026-10-01: `planetary-inverses`, three regularised inverses set to one band 3: Richardson-Lucy and L1-L2, their sky held at zero, leave 0.15 or less of the Wiener's limb undershoot, a single Gaussian rings less than the measured kernel rather than more, and the Wiener held to band 3 by one scale on a power-law prior does worse than the stack it restores, Richardson-Lucy alone nearing part 1's ceiling; part 3 2026-10-01: `planetary-gains`, wavelet gains derived from the stack's own power, its halves' noise, the kernel and the limb fit's disk come within 3 to 11 % of the per-band ceiling with the true kernel and beat every preset, which rings a sixth to a third of the disk below the sky; with the limb's kernel they fail on the calibrated twin, its finest bands being the model's tail (#1120), so R9's condition is not met on this evidence; follow-up 1 2026-10-01: `planetary-ringing`, a linear filter that lifts a band past the truth rings every time and a steep cut rings without lifting anything, a floor at the sky stops both, and AutoStakkert's sharpening is a fixed filter that suits this capture's blur, with no ring cure of its own; follow-up 2 2026-10-01: `planetary-dering`, the limb as its own channel takes the presets' ring under 0.015 of the disk and their limb profile 11 to 64 times truer, and derived gains held to a non-negative composite through the limb's own kernel reach 0.713 on the calibrated twin, the best yet from the capture alone; R7a 2026-10-02: `planetary-ghost`, the ASI290MM's shell is an elongated smear or double image along one axis of the 2022 alt-az mount, in L only, fitted as an elliptical copy beside a free round glow and taken out from 10 px past the limb, its non-round part to 12 to 14 %; inside 10 px every filter's blur is elongated along the same axis, for step 3's kernel, #1139; follow-up 3 part 1 2026-10-02: `planetary-finest-band`, the limb's oversampled edge reads the finest band at 0.3 cycles a pixel within 0.011 of the oracle where the limb fit's kernel said a fifth of it, though 0.05 to 0.06 low at 0.2, and the real capture's finest band is 0.21 where the kernel said 0.007; another year's spectrum is killed by the belts below 0.2 cycles a pixel; part 2 2026-10-02: a physical kernel (Fried's seeing, a Gaussian, a halo) fitted to the edge misses the oracle at 0.3 by 0.06 to 0.09, and R8's gains through the edge or it, within 24 to 43 % of the true kernel's (claimed 15 %), now improve the stack where (b')'s made it twice as bad, the real capture's finest gain 19 down to 5; the kernel past 0.3 cycles a pixel is step 4's, #1140; follow-up 4 part 1 2026-10-02: `planetary-finest-band --moons`, the Galilean moons being in 2022-09-03's field after all, a moon reads the kernel's shape and its directions to 0.45 cycles a pixel but not its level, its halo lying past any square the planet's glow allows, the claims failing on every twin by that normalisation; on the real capture the moons blur with their distance from the disk, tilt anisoplanatism, so Europa only bounds the disk's kernel; and every stack's kernel is wider along the planet's equator, where only the limb places a frame; part 2 2026-10-02: `planetary-elongated`, an elongated kernel off the limb's edge in two sectors fails, the polar limb too poor an edge and the oracle along the equator starved of the planet's power past 0.4 cycles a pixel, so the kill line fires on every twin; part 3 2026-10-02: torchmfbd as a reference (`planetary-lucky-frames`, `planetary-score`) restores the twins 10 to 27 % past the stack but far short of the true kernel's gains, its PSFs the diffraction limit on these undersampled frames, so no port is earned and the defocused burst is a bench item, #1155). Milestone `planetary-restoration`: R0 #1048, R1 #1049, R2 #1050, R3 #1051, R4 #1052, R5 #1053, R6 #815, R7 #1054, R8 #1055, R9 #1056 (conditional).

The user asked for what the deep-sky training effort does, done for planetary lucky imaging:
- which frames are usable;
- how to set the alignment points and the warp;
- how to find the wavelet gains, and how to penalise ringing;
- how to deblur a stack by learning its blur from the lucky frames and inverting it;
- how to derotate by measuring the bands;
- how to dewarp by taking the median of several feature centroids;
- where to find sharp spacecraft and Hubble imagery to aim at.

The request is explicit that this is about the **methodology, not a model**: first principles and meticulous measurement against a truth we trust. A learned stage enters only where the measurements show a classical method has run out (R9).

This plan builds on [planetary-stacking.md](planetary-stacking.md), which already has most of a lucky-imaging stacker:
- frame grading and global phase-correlation alignment;
- feature-placed alignment points with a displacement-mesh dewarp;
- per-point best-of weighting, Bayer drizzle, and a-trous wavelets.

What that plan lacks is exactly what this one is about. It has no truth to tune against: the parameter study behind the AUTO mode (#817) was judged "by eye, or against a known truth, and never by a Laplacian score". It also has no measured blur, and no deconvolution.

## Why this is not the deep-sky training, and what it keeps

**What it keeps** (`model-training-roadmap.md` section 5, `deconvolver-training.md`):
- Every rule of measurement: pre-registered predictions and kill lines, seeded runs, comparison at a matched operating point, and an external truth for every internal metric.
- The C# tool builds the data and does the measuring; Python plots or trains, never measures.
- Ring and skirt metrics measured against a null of the input's own.
- The Richardson-Lucy oracle with the exact kernel as the ceiling: anything that beats it is fabricating.

**What differs:**
- **No stars.** Every deep-sky blur and ringing metric reads star profiles. A planet has a limb, belt edges and moons, so every metric is rebuilt on those (R3).
- **A hard ceiling that is known in advance.** The aperture's diffraction cutoff `fc = D / lambda`. Nothing real exists above it, so power a restoration adds there is fabricated by definition.
  - **The 10 inch Newtonian** (254 mm): at 550 nm, `lambda / D` is 0.45 arcsec, so it is fully sampled at 0.22 arcsec a pixel and finer. Every coarser session is undersampled, which is where drizzle earns its place.
  - **The 102 mm Maksutov:** `lambda / D` is 1.11 arcsec, so it is fully sampled up to 0.56 arcsec a pixel.
  - **A free noise measurement:** where a capture samples finer than its cutoff, the band between the cutoff and Nyquist holds only noise. Coarser captures have no such band and measure noise from frame differences instead.
- **The truth is a spacecraft map, not a longer exposure.** Deep-sky truth came from deeper masters and Gaia. Here it is a Hubble or Cassini map rendered at the capture's geometry (T1), so the rendering itself must be validated (R1, R2).
- **The planet moves.** Jupiter turns 0.605 degrees a minute (System III). At the central meridian of a 45 arcsec disk that is 0.24 arcsec a minute, about 0.8 px a minute at 0.31 arcsec a pixel. A 16-minute run smears by 12 px. Dewarping by the median of feature positions (the user's idea) is therefore only sound in a derotated frame, or over a window short enough that rotation stays under the tolerance (R5, R6).

## The corpus (read-only survey, 2026-09-28)

Read header-only from `D:/SharpCap Captures` and `D:/Astro-Pics`. Nothing is ever written there.

| Target | Best raw material | Other material |
|---|---|---|
| Jupiter | Uranus-C (IMX585) 2024-12-15: 441,558 frames of 320x240 RGGB8, timestamped, about 445 fps, 2.2 ms. ASI462MC 2021-12-16: 45,403 frames of 320x240 RGGB8. ASI462MC 2021-08-19: 7,695 raw full-frame 1936x1096 (in 7z). Uranus-C 2025-01-02: 60,000 frames of 640x480. | 2022 PIPP crops (200x200, about 690,000 frames); 2022-09-03 LRGB mono FITS sequences, ASI290MM, 52,741 frames with per-frame DATE-OBS |
| Saturn | ASI462MC 2021-12-16: 45,537 raw frames (544x548 and 320x240). 2022-10-09: 5,271 raw frames at 250 fps. | ASI290MM mono LRGB 2022-08-27 (162,000 frames, partly in 7z); 2021 full-frame raw in 7z |
| Mars | Uranus-C 2025-01-02: 100,000 frames of 320x240 and 640x480, 1 ms | none |
| Moon | ASI585MC Pro 2024-09-17: 200 frames of 3840x2160 RGGB16 | 2021-05-26 eclipse (ASI294MC, 145 GB in 7z); 2022-11-08 eclipse as PIPP TIFFs only |
| Sun | Uranus-C 2023-04-20: 12,122 frames of 1600x1200 RGGB16, white light, 0.118 ms | ASI183MM 2023-01-07 mono16 (in 7z, no timestamps) |
| Venus | ASI294MM and ASI290MM mono, 2021 and 2023 | none |

- **Image scale** comes from the Jupiter disk in the stacks: 0.21 to 0.52 arcsec a pixel, an effective focal length of 1.16 to 2.8 m. **No capture records the telescope.** The user, 2026-09-28:
  - Most sessions are a **Saxon DeepSky 10 inch collapsible Dobsonian**, a truss Newtonian: 254 mm, 1200 mm, f/4.7, a parabolic primary and a 58 mm secondary (a 23 % linear obstruction), before 2023 on its Dobsonian base, untracked or on an equatorial platform that is not precise (the owner, 2026-09-29). Often with a **Celestron Omni 4-element 2.5x Barlow at a varying working distance**.
  - Some are the **Skywatcher Skymax 102 Maksutov** (102 mm, 1300 mm, f/12.74), bought November 2021, never with a Barlow and always on a driven equatorial mount (the owner, 2026-09-29).
  - A Barlow's magnification changes with its distance to the sensor, so each session's effective focal length is MEASURED by the disk fit (R1), never taken from the nominal 2.5x.
  - At 1.16 to 1.45 m the disk size cannot tell the 10 inch at prime focus from the Maksutov, which R1 settles from the data.
- **References already on disk, for comparison and geometry, never as truth:**
  - AutoStakkert!3 outputs, with `.as3` session files recording every frame's global offset and the alignment-point grid: a ready registration comparison for R5.
  - WinJUPOS `.ims` measurement files and derotated composites for the 2022 Jupiter sessions: an external check of the disk fit and central meridian for R1 and R6.
  - RegiStax wavelet settings.
- **Excluded:**
  - `2023/FC/Jup/020723` is FireCapture's DummyCam, synthetic.
  - Duplicate SERs and AVIs.
  - PIPP outputs wherever a raw original exists: they are cropped and sometimes frame-filtered (PIPP dropped 964 of 1,592 frames from one Jupiter SER).
  - Eclipse sets, for anything but their own study.
  - `2022/Saturn/21_42_31`, which may be Jupiter mislabelled; check before use.

## The truth ladder

Every phase is judged on the highest rung it can reach, and a metric used on a lower rung must first have been validated on a higher one.

### T1 Rendered truth: a sharp map at the capture's geometry

- **Sources:**
  - Hubble OPAL global maps (MAST HLSP `opal`, CC BY 4.0, doi 10.17909/T9G593): Jupiter 2015 to 2025, Saturn 2018 to 2025, about 10 filters a year.
  - Hubble WFCJ (HLSP `wfcj`, CC BY 4.0, doi 10.17909/T94T1H): about 25 Jupiter dates 2016 to 2020, including navigated disk frames.
  - Cassini ISS global maps (Li et al. 2023, PDS4) for Saturn.
  - For the Moon: LRO LROC WAC with a LOLA and Kaguya DEM, from PDS only (the LROC website's products are copyrighted).
- **The render** projects the map onto the oblate disk at the capture's central meridian, sub-observer latitude, axis position angle, phase and scale (R1). It then applies the aperture's diffraction transfer function, so the truth is **what this telescope could have shown in perfect seeing**, never more.
- **What it is truth for:** the restoration pipeline, run on a synthetic capture made from it (R2). **What it is not:** a pixel truth for a real night. OPAL is one date a year and the features drift, so a real capture is compared with it only for geometry and statistics (belt latitudes, the limb), never pixel by pixel.

### T2 A capture's own consistency: split halves

With no truth, two independent stacks of the same capture (disjoint frame sets, the same method) must agree on everything real. Their difference, per wavelet band, is the noise the method leaves, and any structure one half has and the other lacks is not signal. This is the truth-free rung every real-capture measurement uses, validated against T1 on synthetic captures first (R3).

### T3 Outside references for geometry

- WinJUPOS measurements of the user's own captures: disk outline and central meridian.
- The JPL Horizons ephemeris.
- OPAL's belt latitudes, which are stable from year to year where the longitudes are not.

## R0 The corpus as a registry

**Issue:** #1048.

- **What:** a `tianwen planetary-survey` verb.
  - It records every capture's header: size, frames, colour, depth, timestamps, camera, the SharpCap settings.
  - It adds the disk scale from R1, the exclusions above, and a stable identifier per capture.
  - It writes one manifest in the scratch root. It never writes beside the source.
- **Why a verb:** the rule is one job, one tool. The survey's numbers feed every later phase, and a Python parser beside the C# SER reader would be a second answer to "how many frames".
- **A crop of our own** (`tianwen planetary-crop`), because several raw captures are the whole sensor with a small disk in it:
  - Jupiter 2021-08-19: 1936x1096, 16.3 GB.
  - Saturn 2021: 1936x1096, about 47 GB plus a 29.8 GB AVI.
  - Venus 2021-11-08: 4232x4232, 53.7 GB.

  The crop is a fixed-size window tracked on the disk, 15 to 20 times smaller than the source. Unlike the PIPP outputs in the corpus, it:
  - keeps every frame (PIPP silently dropped frames);
  - keeps the window's origin even, so the Bayer phase is the source's;
  - keeps the timestamps;
  - records each frame's window origin beside the SER, so the absolute image motion R2 measures survives the crop.

  The crop happens in scratch and the source is never touched. **The originals are kept** (the user, 2026-09-29, a terabyte having been freed on `D:`): a crop is a WORKING copy, and what it buys is speed. `D:` is a USB hard disk (WD Elements, 4.6 TB), `C:` an NVMe SSD (Samsung 970 EVO Plus, 1 TB, 265 GB free), and every later phase reads its captures many times over, so the crops live on the SSD, under `C:/temp/tianwen-scratch/planetary/crops`, beside the training scratch.
  - **Verbatim, because a working copy must say what its source said.** The crop is `SerReader.CropTo` (SER.Lib 1.2): the source's own header bytes with only the width and height changed, and the trailer, plus anything after the frames, byte for byte. The first crop went through `SerWriter`, which writes a new header: its pixels and decoded timestamps matched, but the header's local start time had become the first frame's UTC, so a 2021 Sydney capture had lost its time zone, and a trailer in local time would have been re-encoded. The verify compares the header and the trailer as bytes, never as the timestamps they decode to.
- **Archives:** a 7z member is unpacked one at a time into scratch, cropped, and its unpacked copy deleted.
- **FITS videos become SERs** (`tianwen planetary-convert`). SharpCap saved the 2022-09-03 Jupiter LRGB captures one FITS file a frame, which was an accident (the user, 2026-09-29), so each is converted into the SER it should have been, and every later phase reads one format. The samples are the files' own, the trailer holds each frame's DATE-OBS (its exposure start) to the tick, and a sidecar keeps what a SER header cannot: the file of every frame and the first frame's cards. The SER is compared with the files' BYTES and their DATE-OBS text, not with what TianWen's FITS reader made of them, before it is kept.
  - **The check found a FITS.Lib bug on the way:** its DATE-OBS writer dropped a millisecond's leading zeros (43 ms written `.43`, read as 430) and its parser cut the fraction to the millisecond. Fixed in FITS.Lib 6.3; `docs/known-limitations.md` has the files already written.
- **Which FITS folders are videos:** a count of files cannot tell one from a night of deep-sky subs, since both come in hundreds. Lucky imaging is short exposures at a high rate, so the survey takes a folder of 500 or more frames only when its DATE-OBS span shows at least 5 frames a second, or, with no times, its frames say they are sub-second exposures. Measured on the corpus (2026-09-29):

  | Folder | Frames | Rate | Exposure | |
  |---|---|---|---|---|
  | 2022-09-03 Jupiter L, R, G, B (SharpCap) | 12,951 to 13,554 each | 216 to 226 fps | 4 ms | video |
  | 2021-05-26 Moon (PIPP) | 2,000 and 3,059 | 17.2 fps | | video, flagged `pipp` |
  | 2021-02-11 ASI462 bias | 1,001 | 44.9 fps | 32 us | video, flagged `calibration` |
  | 2022 Saturn Nebula flats | 600 | 2.1 fps | 150 ms | not a video |
  | 2021 NGC 3521, 2024 eta Car subs | 646, 861 | 0.12, 0.09 fps | 8 s, 10 s | not a video |
  | 2021 M42 processed | 1,205 | no times | none stated | not a video |

  A bias or dark video is KEPT and flagged `calibration` (by its frame type, or a folder named for one: SharpCap's bias frames carry no type), because it is the camera's own noise, which R2's camera model measures read noise from.
- **Space:** the scratch root is `D:/Astro-Dataset/planetary` (the user, 2026-09-28: wherever there is more space), which holds the manifest and each archive member while it is unpacked; the crops go to the SSD (above). Every writing verb keeps **109 GB free** on the drive it writes (`--keep-free`). It checks before each unpack and each write, refuses in words rather than filling the disk, and never carries a budget as a constant: it asks the drive.
- **Done when:**
  - The manifest lists every capture in the corpus table with the same counts, and a re-run on unchanged files is byte-identical.
  - Every FITS video is a SER whose samples and times are its files', read back from the files.
  - A crop, read back, equals the source's window pixel for pixel on every frame, and has the source's frame count and timestamps.

## R1 Geometry: the disk fit and the ephemeris

**Issue:** #1049 (with #815, whose physical-rotation layer and limb fit it builds).

- **First principles:** the planet's orientation comes from the ephemeris and is never measured:
  - the central meridian in Systems I, II and III, the sub-observer latitude and the axis position angle (Meeus chapter 43, cross-checked against Horizons);
  - the phase;
  - the flattening (Jupiter 0.0649).

  The image supplies only the disk's centre and scale: a sub-pixel fit of the limb to an oblate ellipse under a limb-darkening law, never the centre of mass, which a bright belt or the Great Red Spot moves. This is the physical-rotation layer and the limb fit #815 already asks for, shared.
- **Part 1, the ephemeris: done** (`PhysicalEphemeris`, 2026-09-29). Built from the IAU WGCCRE 2015 rotation models (Archinal et al. 2018: the pole and System III in the ICRF) and VSOP87 positions, light-time corrected, rather than Meeus's chapter 43 formulas, which are the check. Jupiter's Systems I and II are their defined rates, 877.900 and 870.270 degrees a day. Saturn has System III; Mars and the rest wait for the phase that needs them. Measured against JPL Horizons at the corpus's ten session times (`PhysicalEphemerisTests`, the fixture `horizons-physical-ephemeris-2026-09-29.txt`):
  - Sub-observer latitude and pole angle within 0.0025 degree, phase within 0.009, diameter within 0.0001 arcsec.
  - The central meridian is a constant 0.0024 degree below Horizons for Jupiter (0.0019 for Saturn) and the sub-solar longitude 0.0050 (0.0036). The offset scales with the planet's orbital speed: Horizons' aberration by the planet's own motion, not modelled, a thousandth of an arcsecond at the disk's centre.
  - **The pole angle is referred to the true pole of DATE**, as Horizons' NP.ang and Meeus's P are. Referred to the ICRF's it was 0.15 degree off in 2024: precession turns the sky's north by about 0.006 degree a year, times sin RA / cos Dec.
  - **Against WinJUPOS** (12.1.2, its `.ims.xml` beside each 2022 stack, 12 image times on two nights): all three systems within 0.019 degree and the Earth's declination within 0.007, WinJUPOS a consistent 0.013 below (about 1.4 s of rotation, its own timing convention). The only outside check of Systems I and II at the corpus's times.
  - **Meeus's central meridians are corrected for phase** (the middle of the lit disk, 57.3 sin^2(i/2) toward the Sun). Ours are the geometric meridian, as Horizons' and WinJUPOS's are: example 43.a agrees within 0.1 degree once his correction is added, and is 0.42 and 0.49 degree off without it.
- **WinJUPOS's measurements** (decoded 2026-09-29): the binary `.ims` holds the image's JD, the site, the image size, the disk outline (centre and equatorial radius in pixels, the centre 0.5 px from the XML's by a pixel-centre convention) and the rotation angle in radians; its `.ims.xml` companion adds the central meridians in all three systems and the Earth's planetocentric declination, which checked the ephemeris above. The outline is the check of the LIMB FIT (part 2). Its own spread is the yardstick: the hand-set and the refined (`_opt`) outline of one image differ by 0.03 to 0.2 px in centre and 0.07 to 0.1 px in radius.
- **Part 2, the limb fit** (`PlanetaryLimbFit`, `tianwen planetary-limb`, 2026-09-29). A forward model fitted to the pixels around the limb by Levenberg-Marquardt (`Stat/LevenbergMarquardt`, shared with R2 and R7): an oblate disk of the ephemeris' apparent axis ratio, Minnaert-darkened (brightness mu0^k mu^(k-1)), lit at the ephemeris' phase from either end of the equator (both fitted, the better kept), blurred by a Gaussian PSF, plus sky. Coarse to fine: a disk over 40 px in radius is fitted binned first. Its start is its own: the region a quarter of the way from sky to disk, its binary centroid and the radius its area gives. `PlanetaryDisk.BoundingBox`'s mean-plus-three-sigma threshold passes only a stack's bright middle (a disk filling a fifth of the frame) and started the real stacks at half the radius.
  - **On synthetic truth** (rendered by the test's own code, pre-registered): the model's own PSF recovers centre, radius and axis exactly; under a Moffat PSF the model does not have, the centre is within 0.003 px and the radius 0.185 px (0.31 %) large; at a 10 degree phase the modelled centre is within 0.001 px, and **a fit that ignores the phase is 2.7 px off**. The phase's brightness asymmetry is first order in the angle, the terminator's bite only second, so the phase is always modelled.
  - **Against WinJUPOS's outlines: the pre-registered test FAILED.** It asked for the centre within 0.2 px and the radius within 0.5 %.

    | Session | Phase | Stacks | Centre (fit - WinJUPOS) | Radius | Axis vs 90 - rotation |
    |---|---|---|---|---|---|
    | 2022-09-03, R 74 px, sigma about 1 px | 5.06 | L, R, G, B | dx +0.03 to +0.38, dy +0.82 to +1.07 px | L -0.51, R +0.36, G +0.23, B -3.85 % | 1 to 2.5 degrees |
    | 2022-09-29, R 183 px, sigma 7.7 px | 0.68 | five | dx +0.13 to +0.33, dy +0.32 to +0.47 px | +1.39 to +1.70 % | 0.5 to 1.2 degrees |
  - **Why the miss is WinJUPOS's outline, not the model, as far as the data can tell** (the decision is left to T1, below):
    - Scanning the phase angle from 0 to 10 degrees, the fit's residual is least at 5 to 6 degrees on L, R and G: the ephemeris' 5.06, found by the images alone. WinJUPOS's centre is where the fit lands at about 2 degrees, as if its outline under-corrected the phase by half. On 2022-09-29 the smaller phase leaves a smaller offset, again along the Sun's direction.
    - The radius disagreement grows with the blur: within 0.5 % where the edge's sigma is about a pixel, 1.4 to 1.7 % larger where it is 7.7 px. An outline set by eye marks the visible edge, which lies inside the unblurred limb by a fraction of the blur; the Moffat-wing bias the synthetic test measured accounts for about 0.6 px of the 3 px there.
    - About 0.4 to 0.5 px along the POLE direction on 2022-09-03 is not the phase's (the Sun lies along the equator). Jupiter's darker polar regions, which a uniform-albedo disk does not have, are the hypothesis.
    - The blue stack (sigma 3.2 px, k 0.67) is the one radius outlier, 3.85 % small: blurrier, and fitted with a flatter limb darkening.
  - **The decider was T1, R2's first step, and it passed** (#1050, R2 part 1 below): an OPAL map rendered at 2022-09-03's geometry (belts and dark poles included), blurred, and fitted with this same code, the rendered geometry known exactly. Against the plan's numbers (centre within 0.2 px, radius within 0.5 %) the fixed fit lands within 0.05 px and +0.17 to +0.44 %. T1 also found four faults in the fit, now fixed, and with them the miss against WinJUPOS shrank to one sign per session, the pattern of an outline set by hand.
- **Which telescope, from the data:** the two apertures differ 2.5 times, and the sharpest frames show it.
  - The averaged power spectrum of a capture's best frames falls to its noise floor at the aperture's cutoff, `D / lambda`, or before it, and the cutoff times the measured scale gives `D` or a bound on it (`PlanetaryPowerSpectrum`, `ApertureCutoff`).
  - The Newtonian's spider vanes add diffraction spikes, a four-fold pattern in the stack's halo, that the Maksutov lacks (`SpiderSignature`).
  - The pupil the T1 render uses follows from this. The Newtonian is 254 mm with a 58 mm secondary (0.23 of the aperture, from the specification) and a four-vane spider. The Maksutov is 102 mm with its secondary spot, whose size is still to be confirmed. Both are checked against the transfer function's mid-frequency dip.
  - **What the spectrum can decide, from the optics alone** (worked out 2026-09-29, before any capture's spectrum was read). The cutoff is `D / lambda`, at the short edge of the passband, where the last power comes from. For ruling the Maksutov out it is reckoned at **400 nm in every plane**: the shortest any plane passes behind a UV/IR cut, since a Bayer dye leaks a little short light and a cutoff reckoned at the dye's nominal edge would let a Maksutov look larger than it is. A Bayer capture's colour planes are sampled at twice the sensor pitch, and the floor is read in each plane's corners, from 0.6 cycles a pixel outwards. So the Maksutov can be ruled out only where 1.15 times its 400 nm cutoff falls short of the corners: a mono capture up to **0.42 arcsec a pixel**, a Bayer one up to **0.21**. At prime focus (0.41 to 0.52) that leaves only mono captures, and a colour capture there, the very case the disk size cannot settle, is left to the spider.
  - **The method, checked on synthetic captures first** (`ApertureFromSpectrumTests`, `OpticsPrimitiveTests`; Kolmogorov phase screens with subharmonics through each pupil, 1,500 frames, r0 8 cm at 550 nm):

    | Capture | Measured cutoff (truth) | Lower bound on D | Maksutov |
    |---|---|---|---|
    | Maksutov, 0.4"/px | 0.297 (0.360) c/px | 84 mm | not ruled out |
    | Newtonian, 0.4"/px | 0.484 (0.896, past the corners) | 137 mm | ruled out |
    | Newtonian, 0.15"/px | 0.281 (0.336) | 213 mm | ruled out; the Newtonian never |

    - **A measured cutoff is where the signal sinks into the noise**, a matter of the capture's signal to noise before it is one of the pupil. At 150 frames both pupils came back at the same 0.25 cycles a pixel. So it is a LOWER bound, never above the pupil's, and "within 15 % of the pupil" was the wrong thing to pre-register: the Maksutov came back 18 % low even at 1,500 frames.
    - **Pooling the rings past the Maksutov's cutoff** was tried instead and is weaker (3.2 and 4.5 standard errors where the bound says plainly 137 and 213 mm): the noise-only rings near the corners carry the smallest errors and swamp the few with signal.
    - **The spider at a real disk's size is unmistakable:** 794 for a Newtonian (at 34.9 degrees, its vanes at 35), 0.67 for its obstruction without the vanes and 0.96 for the Maksutov, with Jupiter's 23" radius at 0.4"/px in a 256 px frame. At a 6" disk the same statistic read 0.6: the halo that close to the disk swamps the spikes.
    - **Two traps the synthetic check found in itself.** A plain FFT phase screen was 28 % short of Kolmogorov's structure function at 4 cm, since scales larger than the screen tilt every separation; subharmonics bring it within 15 %. A small render grid wraps the disk's halo around its edges, and the four periodic copies print a four-fold pattern aligned with the grid (a Maksutov read 73). The stack is therefore rendered once as the ensemble mean (the pupil under Fried's tilt-removed long-exposure transfer function) on a grid four times the frame.
  - **The spider, from the stack's halo:** the azimuthal harmonics of the stack in a full annulus from 1.3 disk radii to the nearest frame edge (a full annulus, since a rectangle's corners reach farther out along its diagonals and would print a four-fold pattern of their own). The statistic is the mean power of harmonics 4 and 8 over the mean power of 3, 5, 6, 7, 9, 10 and 11. Harmonics 1 and 2 are left out: an off-centre disk and its oblateness make them. Saturn is left out altogether, for its rings.
- **Measured against T3:**
  - WinJUPOS's `.ims` outlines and central meridians for the same 2022 Jupiter stacks.
  - Belt-edge latitudes on the projected map against OPAL's, in planetographic latitude as JUPOS uses: `tan(phi_g) = (a/b)^2 tan(phi_c)`, about 3.8 degrees apart at 45 degrees.
- **Pre-registered:**
  - The disk centre agrees with WinJUPOS within 0.2 px, the equatorial radius within 0.5 % and the central meridian within 1 degree.
  - The belt-edge latitudes agree with OPAL's within 1 degree. **Moved to R6** (#815), which builds the projection onto the spheroid that the latitudes are read on.
  - Every session's measured cutoff falls within 15 % of 254 mm or of 102 mm, which classifies it, and the spider cross appears in exactly the sessions classified as the Newtonian.
  - **Revised 2026-09-29, after the synthetic check and before any real capture was read** (the cutoff criterion above failed it):
    - The measured cutoff is the highest frequency at which two neighbouring rings each stand 3 standard errors above the corner floor, the errors taken over frames. Read as a lower bound on D at 400 nm, a bound above 117 mm (102 plus 15 %) rules the Maksutov out. Nothing in the spectrum confirms the Maksutov: a bound below 117 mm may be a Newtonian whose power sank into the noise.
    - The spider: a statistic above 10 is a spider (the Newtonian), below 3 is none (the Maksutov), and in between, or with no full annulus in the frame, is inconclusive.
    - A session is the Newtonian if either says so and the other does not contradict it, and the Maksutov only if the spider is absent and no plane rules it out. A spectrum ruling the Maksutov out beside a spider-less halo is a CONFLICT, reported rather than settled.
    - Only Jupiter: the ephemeris covers Jupiter and Saturn, and Saturn's rings defeat both the limb fit and the annulus.
    - **Added after the FIRST real session, and able only to withhold a verdict:** a spider-less halo is evidence only where fewer than half of the annulus' raw samples sit at their frame's black level. The first session read (2021-12-16 11:11, ASI462MC at offset 0 and gain 130 in RAW8, the disk peaking at 73 ADU) had 90 % of its annulus at exactly 0, and 98 % from 1.9 to 2.2 radii: a clipped sky, where a spike worth a fraction of an 8-bit step never registers. The synthetic check had no quantisation, which is how the rule came to have the blind spot. The gate can turn a "Maksutov" into "undecided" and nothing else.
  - **What the real captures said, and the revisions they forced (2026-09-29).** Fourteen Jupiter captures, the best 2,000 frames of each (`tianwen planetary-aperture`):

    | Night | Camera | Scale | Spider (annulus width) | Verdict |
    |---|---|---|---|---|
    | 2021-12-16, 3 captures | ASI462MC | 0.415 to 0.427"/px | 11:11 clipped (94 % at black); 11:37 0.8 (56 px); 11:43 0.0 (23 px) | undecided; the Maksutov by the owner's dates |
    | 2022-09-03, L R G B | ASI290MM | 0.482 to 0.499"/px | L 9.6, R 18.3, G 10.6, B 2.4 (29 px) | Newtonian |
    | 2022-09-29 ("2x" folder) | ASI462MC | 0.403"/px | 0.1 (18 px, a PIPP crop) | undecided; the Newtonian by the Barlow |
    | 2022-10-09 | ASI462MC | 0.312"/px | no full annulus in the frame | undecided; the Newtonian by the Barlow |
    | 2024-12-15, 3 captures | Uranus-C | 0.493 to 0.511"/px | 16.5, 30.9, 34.3 | Newtonian |
    | 2025-01-02, 2 captures | Uranus-C | 0.192"/px (the Barlow) | 18.2, 11.5 | Newtonian |

    - **The Maksutov verdict is withdrawn: "below 3 is none" failed on a capture of known telescope.** 2022-09-03's four captures came minutes apart through one Newtonian, and its Blue read 2.4 with Jupiter near the frame's edge, an annulus only 29 px wide. The user adds that the ASI462MC was on the Newtonian too, and that most sessions are. So the verdict is one-sided: a spider above 10, or a frames' cutoff past 117 mm, says Newtonian, and nothing in these data says Maksutov. A capture with neither is undecided, and the CONFLICT case goes with the Maksutov verdict. What would confirm the Maksutov lies outside the data: the dates the user owned it.
    - **The misses have narrow annuli.** Every spider-less capture on an unclipped sky but one had an annulus under 30 px wide (a disk near the frame's edge, or a PIPP crop). The exception is 2021-12-16 11:37, 56 px wide on a sky at 9 ADU, and that night was the Maksutov (below).
    - **The image scale is a fingerprint only at prime focus.** The 10 inch alone is 1200 mm, 0.498"/px at the three cameras' common 2.9 um pitch, and both prime-focus nights measure 0.487 to 0.511, within the limb fit's 2 %. Other sessions used the 2.5x Barlow at a varying working distance, whose magnification varies with it.
    - **What the owner knows settles two more (2026-09-29): the Maksutov never had a Barlow and was always on a driven equatorial mount**, while the Newtonian before 2023 was on its Dobsonian base, untracked or on an equatorial platform that is not precise. So a session taken through the Barlow is the Newtonian: 2022-09-29 (its folder is "Jupiter 4ms 2x RGB") and 2022-10-09 (1.92 m, half again the Maksutov's 1.3 m). The two columns above keep the data's own verdict apart from the owner's.
    - **2021-12-16 is the Maksutov: the owner bought it in November 2021**, a month before, and the data agree on every count below. It was tracked (the disk creeps about 0.5 px/s through the 88 s of 11:11, with one 6 s bump of 50 px; untracked it would cross the 320 px window in about 9 s), which either mount could do. Its 1.40 to 1.44 m is 8 to 11 % past the Maksutov's nominal 1300 mm (f/12.74), which back focus gives a Maksutov, since it focuses by moving its primary, and which is well past the limb fit's 2 % on the scale. And 11:37's annulus is 56 px wide on a sky at 9 ADU, where every Newtonian night reads 10 to 34, and it reads 0.8. The field's rotation cannot help: Jupiter was 5 hours west and 14 to 20 degrees up, where an alt-az field turns only 0.6 degree over the three captures, and the limb fit's axis moved 4.4 degrees between two captures of 2024-12-15 tracked to 5 px. So the spider test's one negative on a wide annulus over an unclipped sky was right, and its misses are all narrow annuli, which is the power the next revision of the statistic has to state.
    - **The spider statistic was made robust after the real halos were LOOKED at, before the verdicts were read.** Three moons in 2022-09-03's annulus put power into every harmonic and read 0.4 beside a four-armed pattern plain to the eye. The z-score per ring, the median per angle bin, the profile read at each pixel's own radius (the square grid's own four-fold) and elliptical rings in the disk's shape (an oblate disk's own four-fold) came out of that, each pinned by a synthetic test. Its thresholds stay those of the synthetic check, never tuned to the real statistics.
    - **The halves' shared detail (T2) is reported, never decided on.** The two half-stacks share the sensor's fixed pattern, which reads as detail both hold: 2022-09-03's luminance "ruled the Maksutov out" by the detail alone, at 118 mm against a 117 mm line.
  - Kill line: a miss means the limb model is wrong (limb darkening, the flattening, or the sky background), and no later phase uses the geometry until it is fixed. A cutoff matching neither aperture means the scale is wrong, or a third telescope, which the user is asked about.

### The limb fit's cost (#1106)

**Done** (2026-10-03). A cold `PlanetaryLimbFit.Fit` took 10 to 23 s on a real master (Release), and the best stack paid two after stacking: one in the sharpening, one in the score it prints. A probe of each search gave the reason:
- **The searches do not waste iterations:** each converged in 11 to 34.
- **The model is the cost:** one evaluation renders the disk supersampled over the annulus's box and blurs it twice, 7 to 15 ms on a 49 px disk and 39 ms at full scale on an 81 px one, and a cold fit makes about 1,000 of them over its four searches.

**What changed, with the same bits** (`DiskModel.Evaluate`):
- each evaluation renders, blurs and reads back its rows in parallel bands;
- the column pass adds whole rows a tap at a time;
- a cold fit's searches run at once, the best still chosen in their order.

Every output gathers into its own cell with its taps summed first to last, so nothing changes order:
- the probe printed every parameter of every search to the last digit unchanged on three real masters;
- T1's three renders read exactly the numbers above;
- `planetary-sharpen` wrote byte-identical masters for 2022-09-03 Red and 2022-10-09.

| | Before | After |
|---|---|---|
| A cold fit, 2022-09-03 Red (49 px) | 9.3 s | 1.8 s |
| A cold fit, 2022-10-09 (81 px, binned then refined) | 18.4 s | 3.2 s |
| A cold fit, 2024-12-15 Uranus-C (48 px) | 10.4 s | 2.5 s |
| `planetary-sharpen`, 2022-09-03 Red | 44.1 s | 5.7 s |
| `planetary-sharpen`, 2022-10-09 (three channels) | 81.3 s | 13.3 s |
| `planetary-stack` after the stack, 2022-10-09 | 49.6 s | 10.1 s |

The fits include the process's start. The row-ordered column pass was kept on an alternated A/B of five runs each (medians 3.17 against 4.13 s on 2022-10-09, 1.81 against 2.14 s on Red). The issue's other candidates (a binned coarse fit for small disks too, fewer searches where the caller decides one, a cap on the clearly worse ones) each change the fit and would need T1 again, so they were not taken. `AFitIsTheSameFitEveryTimeThoughItsRowsRunAtOnce` pins that the parallel evaluation cannot change a fit.

## R2 Rendered truth and a seeing model calibrated on the capture

**Issue:** #1050.

- **What:** `tianwen planetary-render-truth` produces T1 for a capture's geometry. `tianwen planetary-degrade` then turns it into a synthetic SER with the capture's own seeing, optics and camera:
  - **Tip-tilt:** the global shift series, measured by `GlobalAligner` on the real capture (plus R0's crop origins), with its spectrum and frame-to-frame correlation. At 445 fps, frames 2.2 ms apart share seeing.
    - The series is two motions: the mount's, smooth and slow (tracking error, or drift), and the seeing's, fast. The smooth part is fitted and removed before the seeing's is measured, and reported as the mount's.
    - The mount changed between sessions: the 10 inch was used on its Dobsonian base and later taken off it (the user, 2026-09-28; 2024-12-15 is after). The measured drift says which mount each session had, rather than assuming it.
  - **Local warp:** the field's amplitude, spatial correlation length and temporal correlation, measured from alignment-point tracks (R5).
  - **Blur:** a per-frame blur distribution fitted to the real capture's quality distribution, as Kolmogorov phase screens through the aperture (aotools and HCIPy are the reference implementations to learn from).
  - **Camera:** the Bayer mosaic, the 8-bit or 16-bit quantisation, read noise and shot noise at the measured gain.
- **First principles:** the synthetic capture is useful only where it is statistically the real one, as the deep-sky noise injection had to match the real band ratio (`reference_injected_noise_shape_calibration.md`). Every term is measured on the real capture, never carried as a constant.
- **Pre-registered:**
  - The synthetic capture matches the real one within 10 % on five statistics: the global shift RMS, the warp correlation length, the quality distribution's shape (its percentiles), the per-band noise, and the lag-1 frame correlation.
  - Kill line: a statistic outside that band means a physical term is missing, and the synthetic capture is not used to choose any parameter until it matches.

### R2 part 1: the render, and T1

**Done** (2026-09-29).

- **The map:** `PlanetMap` reads a global map in OPAL's conventions: planetographic latitude, north in the first row, west longitude with the left edge at 0 and decreasing to the right, 10 samples a degree. Checked on the 2022 map: its Great Red Spot reads at 23 degrees south. OPAL's maps are Minnaert-flattened, and each filter's k is in the readme (F631N 0.999 in 2022 and 2024).
- **The render:** `PlanetaryRender` casts a ray per sample onto the ephemeris' oblate spheroid at the capture's geometry, reads the map at the point hit, and puts back the map's limb darkening, lit from the sub-solar point (`PhysicalEphemeris.SunOnTheDisk`, whose phase angle agrees with the ephemeris' to the thousandth of a degree). Through a telescope it renders at a scale fine enough for the pupil's cutoff, convolves with the pupil's PSF, and bins to pixels. It is pinned against formulas of its own, never against the limb fit: the outline's area to 0.005 %, a meridian and a latitude circle to a hundredth of a pixel, the phase's side, the flux kept through diffraction, and Airy's encircled energy.
- **The verb:** `tianwen planetary-render-truth --map --utc` renders at an image's disk (`--like`) or a given one, optionally through a Moffat seeing, at an output scale (`--upsample`, R5a), and with `--fit` reports the limb fit's error against the geometry rendered.
- **T1, the limb fit on a rendered Jupiter** (R1's decider). A synthetic Jupiter with belts, dark poles and a red spot, at 2022-09-03's geometry, through the Newtonian's pupil and a Moffat seeing the fit's model does not have:

  | Seeing (FWHM, beta) | Centre error | Radius error |
  |---|---|---|
  | 3 px, 3 | 0.05, 0.03 px | +0.36 % |
  | 6 px, 3 | 0.04, 0.04 px | +0.29 % |
  | 6 px, 2 | 0.04, 0.05 px | +0.31 % |

  And OPAL's 2022 F631N map itself, rendered at the same geometry through Moffat seeing of 3, 5 and 7 px: the centre within 0.04 px, the radius +0.44, +0.19 and +0.17 % large. **It passes the pre-registration** (centre within 0.2 px, radius within 0.5 %).
- **What T1 found wrong in the limb fit, and fixed** (`PlanetaryLimbFit`):
  - A cell the limb crosses was lit by its centre alone, so the model jumped and the search stalled in a worse minimum on a phased disk (2.4 % small where the truth fitted to 0.2 %). A cell is now lit by the fraction of it inside the limb.
  - A uniform albedo read the dark polar regions as a smaller disk. The albedo is now a smooth function of latitude, with an odd term for the hemispheres' difference, counted from the observer's latitude.
  - The Sun was put on the equator, where on 2022-09-03 the lit side is 5.2 degrees off it, which moved the centre 0.14 px along the axis. The tilt comes from the ephemeris, and both ends of the axis are tried.
  - A single Gaussian blur against seeing's Moffat wings and the diffraction rings left the radius 0.4 to 0.7 % large. The blur now has a wider second Gaussian, a nuisance term: in every T1 case it holds half the light (its bound) at 1.9 to 57 times the core's width, a pedestal rather than a measured PSF. Its width was first bounded at 8 times the core's, which held a beta-2 Moffat at both bounds and moved the radius to +0.61 %; the bound is 64.
- **Against WinJUPOS again, with the fixed fit:** R1 missed its outlines by up to 1 px and 1.7 %; the fixed fit misses them by:

  | Session | dx | dy | dR |
  |---|---|---|---|
  | 2022-09-03, the Red, Green, Blue and luminance stacks | -0.21 to -0.39 px | +0.53 to +0.82 px | -0.91 to +0.27 % |
  | 2022-09-29, five RGB stacks | +0.44 to +0.66 px | -0.04 to -0.21 px | +0.14 to +0.84 % |

  Each session's offset has one sign across its stacks, and the stacks are wavelet-sharpened, which rings at the limb. An outline WinJUPOS's user sets by hand once a session fits that pattern; the fit's own error on a rendered Jupiter is a tenth of it. So the residual is WinJUPOS's, and R1's question is closed on T1.

### R2 part 2: the synthetic capture

**Done** (2026-09-29), the kill line firing on the sky's finest bands (below). `tianwen planetary-degrade <capture> --map --output` measures the real capture, makes the synthetic one, measures it the same way and prints the comparison; `tianwen planetary-seeing` measures any capture alone.

- **How a synthetic frame is made** (`PlanetaryDegrade`):
  - The map, rendered on the spheroid at the capture's geometry without diffraction, sampled finely enough for the pupil's cutoff at the filter's wavelength (2 samples a pixel for Red on 2022-09-03), and rendered afresh every 2 s as the planet turns.
  - Through the pupil and a phase screen that evolves from the last frame's (`EvolvingPhaseScreen`): each mode and subharmonic is carried by the wind and partly renewed (Srinath et al. 2015). Its structure function matches the static screen's, and with nothing renewed it is the same air moved on. An outer scale (von Karman) takes from the structure function as Tokovinin's first order says (`PlanetaryDegradeTests`).
  - Integrated over the exposure: the pupil's window slides across the same screen in steps of at most a centimetre while the shutter is open (9 cm in 4 ms at 22 m/s). An instant's full-contrast speckle jittered the aligner and every frame-to-frame statistic.
  - The disk moves by the screen's own tilt, on the real capture's slow drift (its one-second running mean, the mount's). Replaying the real capture's measured shifts instead was the first design, and counted the aligner's error twice: once in the replayed shifts and again when the synthetic capture was measured.
  - Optionally, a telescope's own defocus (Zernike, RMS nm), a layer of turbulence at the telescope with its own r0, outer scale and drift, and a local warp that moves surface brightness without changing it (a lossless screen keeps the radiance; a Jacobian, tried, was both unphysical and printed its grid into the frames).
  - Binned to pixels and read out: Poisson electrons, read noise, the offset, rounded and clipped to 8 bits.
  - The telescope's wide scatter: a share of each frame's light spread over the whole fine window by a kernel (1 + (r / core)^2)^(-3/2), past the PSF grid's reach, entering the frame's transfer function beside its PSF.
  - The camera from the real frames, fitted through the 8-bit rounding pixel by pixel (`PlanetaryCaptureStatistics.SkyByPixels`, below): the offset the sky's level 2.5 to 3.5 radii from the disk; the read noise the far sky's frame-to-frame noise (0.175 ADU); the gain from the finest band's noise on the disk (32 e-/ADU, which the capture's own SharpCap settings confirm: gain 50 on an ASI290MM is about 2 e- a 12-bit ADU, sixteen times that in 8 bits). A photon transfer from consecutive frames' differences was tried first and read the seeing's changes rather than shot noise: no slope at all.
  - Written beside the SER: the truth at the reference frame's time (FITS, in the frames' ADU) and each frame's shift and Strehl ratio, the truth R4 grades frames against.
- **Not modelled:** the blur varying over the disk (one PSF a frame, only the warp varies), the camera's fixed pattern, and the filter's width (one wavelength).
- **The five statistics, defined in code before any synthetic capture was measured** (`PlanetaryCaptureStatistics`, one routine for both captures):
  1. **The shift's seeing RMS:** every frame's shift against the sharpest frame (`GlobalAligner`), less a centred one-second running mean (the mount's part, reported as its straight-line rate and its wander about that line), RMS per axis.
  2. **The warp's correlation length:** alignment points whose whole patch lies on the disk, matched on means of consecutive frames after the global shift; each point's own mean and each frame's mean over its points taken out; the correlation of two points' displacements against their separation, and where it falls to 1/e.
  3. **The quality distribution's shape:** the grader's Laplacian score of every frame, its 5th, 25th, 75th and 95th percentiles over its median.
  4. **The per-band noise:** consecutive frames whose relative shift is within a fifth of a pixel of whole, differenced after that whole shift; a trous bands 1 to 4 of the difference, their RMS over the sky (1.3 to 1.6 radii) and their clipped RMS over the disk (inside 0.8), over the square root of two.
  5. **The lag-1 correlation:** of consecutive frames' quality scores.
- **Added as the comparisons needed them:** the flux's variation over quarter seconds and frame to frame; the limb's edge width in the aligned means; the limb fit on the aligned means (its Minnaert k is the planet's, its blur the capture's) and on single frames (every fourth, each where its shift put its disk: the disk's own motion, the aligner's error against it, each frame's blur); the halo, annulus by annulus over the local sky; and the camera's sky levels and noise, each fitted through the rounding pixel by pixel. `--real-statistics` keeps a capture's statistics in a file, versioned so a stale one is measured again.
- **What 2022-09-03's Red capture says** (ASI290MM, 8 bits, 4 ms, 216 fps, 12,990 frames over 60 s, a disk 49 px in radius at 0.497"/px):
  - The aligner reads 1.15 px (0.57") RMS per axis of seeing; the imprecise platform drifts the disk 1.15 px/s and it wanders 4.9 px about that line. By the limb, most of the 1.15 is the aligner's own error (below).
  - **The warp is not measurable in it.** On single frames, and on means of 4, 8 and 16, the points' displacements correlate with nothing beyond the overlap of neighbouring patches, and read 0.6 to 0.8 px RMS. The first attempt took points across the limb and on the moons, 222 px apart over a disk 98 px across, which follow the edge or the moon rather than the warp; only points wholly on the disk are kept.
  - **The Laplacian score is mostly noise here:** its 5th to 95th percentiles span 0.96 to 1.04 of its median, and consecutive frames' scores correlate at 0.07. That is R4's hypothesis seen on a real capture.
- **Calibrating on the capture's first 3,000 frames** (`--frames 3000`, 12 s; `--real-statistics` keeps the real capture's measurement, `PlanetaryCaptureStatistics.SaveAsync`). What the trials found, in the order they found it:
  1. **Replaying the real shifts counted the aligner's error twice**: once in the shifts replayed, and again when the synthetic capture was measured. The disk now moves by the screen's own tilt, on the real capture's one-second running mean (the mount's drift).
  2. **An instant's PSF is too speckled**: its full-contrast speckle jittered the aligner and every frame-to-frame statistic. The PSF is integrated over the 4 ms exposure.
  3. **The Laplacian carries no seeing on this capture**, so the limb's edge width was added: the level just inside the limb over the steepest fall, in the mean of every frame aligned and in the best tenth's. The real capture's lucky tenth is barely sharper (7.28 px against 7.37) where seeing alone made the synthetic one 5 % sharper.
  4. **A static defocus is ruled out**: 150 nm RMS matches the limb, but the frames' truth moves 0.79 px while the aligner reads 2.39 (5.25 at 250 nm), where the real capture reads 0.96. Defocus puts zeros in the transfer function inside the sampled band, where the phase correlation's whitening then weighs noise as much as signal.
  5. **The limb fit on the aligned means separates the planet from the blur**: Minnaert k 1.050 real against 1.033 synthetic, so the rendered limb is right, while the blur's core is 1.58 px in the real best tenth against 0.75 in the synthetic one.
  6. **The aligner's shift is two things, and on this capture the error is the larger**. Fitted alone on single frames, the limb lands on a synthetic capture's truth to 0.04 px RMS where the aligner is off by 0.45 (0.019 against 0.115 in the statistics' own test). By the limb, the real disk moves 0.58 px RMS per axis, not the 0.96 the aligner reads; the aligner errs by 0.71. The first "match" at r0 5 cm was two different mixtures: the synthetic moved 0.81 px and erred 0.41.
  7. **The real frames are blurrier and move less**: what turbulence at the telescope does (the tube's air, the mirror's boundary layer), whose outer scale is about the tube's, so it blurs without moving the disk (`ALayerAtTheTelescopeBlursWithoutMovingTheDisk`: a like loss of peak for 6 % of the free air's tilt). With it drifting (0.5 to 1 m/s), the sky's finest bands rose 1.2 to 1.9 times and the frame-to-frame flux doubled: the real capture has no flickering halo. Held still, the flux and the lag-1 match.
  8. **The camera's terms were wrong twice.** They were read in the ring beside the disk, whose light the synthetic capture then added again, and they were read off an 8-bit sky's rounded values. Such a sky reads one or two values (the far sky 17 in 92 % of its samples, 16 in 8 %), whose mean is not the level and whose spread the mean alone sets. They are now fitted through the rounding (`PlanetaryDegrade.RoundedGaussianFit`) in the far sky, three radii and more out with moons left out: a level of 16.80 ADU and a read noise of 0.21. The ring gives the same noise under 0.25 ADU of halo, where the rounded moments had given 17.00 and 0.19, and the far sky's 16.92 and 0.27.
  9. **A warp is bounded, not measured**: the alignment points read 0.79 px RMS on the real capture and 0.67 to 0.78 on synthetic ones with no warp at all. A warp of 0.35 px with a 20 px correlation tripled the frame-to-frame flux (0.37 % against 0.12 %), because a lossless screen keeps the radiance and a warp whose divergence does not cancel over the disk changes its light; the real capture's steady flux leaves no room for it.
  10. **The telescope scatters light past the PSF grid's reach.** The real sky stands 0.43, 0.15, 0.08 and 0.03 ADU over the local sky at 1.15 to 1.3, 1.3 to 1.6, 1.6 to 2.0 and 2.0 to 2.5 radii (`CaptureStatistics.Halo`, fitted through the rounding pixel by pixel), where the synthetic sky stopped dead at 1.65 radii: the PSF grid of 128 fine samples reaches 32 px. A wide scatter kernel over the whole window, 5 % of the light with a 5" core (`--scatter`, `--scatter-core`), matches the first two annuli.
- **The calibrated twin** (free air r0 8.5 cm at 500 nm, outer scale 4 m, 22 m/s; a still layer at the telescope, r0 2.7 cm, outer scale 0.25 m; 5 % scattered with a 5" core; 4 ms; no warp, no defocus), measured on three seeds:

  | Statistic | Real | Synthetic, 3 seeds | Ratio | Seed spread |
  |---|---|---|---|---|
  | **Shift's seeing RMS** (the aligner's) | 0.963 px | 1.069 px | 1.11 | 0.22 px: within noise |
  | **Warp correlation length** | at most 9 px | at most 3 to 9 px | | bounded in both, measured in neither |
  | **Quality p5, p25, p75, p95 over the median** | 0.963, 0.985, 1.016, 1.039 | 0.964, 0.985, 1.016, 1.039 | 1.000 to 1.001 | |
  | **Quality lag-1** | 0.082 | 0.049 | 0.59 | 0.033: within noise |
  | **Noise, disk bands 1 to 4** | 1.116, 0.265, 0.147, 0.131 ADU | 1.103, 0.264, 0.141, 0.109 ADU | 0.99, 0.99, 0.96, 0.83 | band 4 0.022: within noise |
  | **Noise, sky band 4** | 0.044 ADU | 0.041 ADU | 0.94 | |
  | **Noise, sky bands 1 to 3** | 0.078, 0.018, 0.009 ADU | 0.119, 0.027, 0.012 ADU | 1.52, 1.50, 1.38 | **outside, 11 to 42 spreads** |
  | The disk's motion by the limb | 0.582 px | 0.528 px | 0.91 | 0.003 px |
  | The aligner's error against the limb (seed 1) | 0.708 px | 0.722 px | 1.02 | |
  | Limb edge width, every frame and best tenth | 7.37, 7.28 px | 6.87, 6.77 px | 0.93, 0.93 | 0.18, 0.16 px |
  | Single frames' edge width, p10, p50, p90 (seed 1) | 5.11, 5.39, 5.63 px | 5.01, 5.20, 5.38 px | 0.98, 0.97, 0.96 | |
  | Minnaert k (the mean's limb fit) | 1.050 | 0.979 | 0.93 | |
  | Halo over the local sky, 1.15 to 1.3 and 1.3 to 1.6 radii | 0.434, 0.155 ADU | 0.438, 0.142 ADU | 1.01, 0.92 | |
  | Halo, 1.6 to 2.0 and 2.0 to 2.5 radii | 0.082, 0.029 ADU | 0.044, 0.018 ADU | 0.53, 0.61 | |
  | Flux, over quarter seconds and frame to frame | 0.92, 0.12 % | 0.93, 0.14 % | 1.01, 1.19 | frame to frame 0.02 %: within noise |
  | Camera: the far sky's level and noise, the local sky's level | 16.747, 0.175; 16.772 ADU | 16.770, 0.173; 16.776 ADU | | |

  The five pre-registered statistics in bold; the rest were added as the comparisons found what the five could not tell apart. A seed's spread is the synthetic capture's own sampling noise, which the real value has as well, so a ratio outside 10 % that sits within about two of them is no evidence of a missing term.

- **The verdict, against the pre-registration:**
  - Every statistic of the disk matches, inside 10 % or within its own sampling noise: its motion, its blur (single frames and aligned means), the quality's shape, all four bands of its frame-to-frame noise, its flux, and the halo out to 1.6 radii.
  - **The kill line fires on the sky's three finest bands**: the ring 1.3 to 1.6 radii from the disk flickers about 0.10 ADU more between frames in the synthetic capture than in the real one, which is exactly camera noise at its level (it flips 1.5 % of the time, what a noise of 0.17 ADU at 16.93 gives). What it is not: the free air's PSF (held still, the synthetic ring was unchanged), the camera's terms (they agree to 0.02 ADU in level and 0.002 in noise), or banding (rows through the disk read 0.006 ADU lower than the rest, columns 0.016 higher). The real ring flips as if its 0.15 ADU of halo carried no shot noise, and what the model lacks there is not yet found.
  - So, as pre-registered, no parameter is chosen on the synthetic capture yet. Whether a parameter measured on the disk alone (every one R3 to R8 names) may be chosen on it while the sky's finest bands stay unmatched is put to the user in #1050.
  - **The pre-registration named no sampling error.** At 3,000 frames the lag-1 wanders 67 % from one seed to the next and the aligner's shift 20 %, so a 10 % band on them tests nothing. From here on a statistic is read against its spread over seeds (`--seed`), which the trials above had not done until the last three.
- **Not modelled:** the blur varying over the disk (one PSF a frame, only the warp varies), the camera's fixed pattern, the filter's width (one wavelength), and whatever keeps the real sky's ring from flickering.
- **Found for the later phases:**
  - **R4, R5:** on a disk, the limb fit registers a frame ten times better than the phase correlation (0.04 against 0.45 px on the truth). A frame's global alignment by its limb is the first thing R5 measures.
  - **R4:** the Laplacian score is noise on this capture (its 5th to 95th percentiles 0.96 to 1.04 of the median, its lag-1 0.08), as the plan's hypothesis has it.
  - **R7:** the real capture's blur is mostly static (a still layer at the telescope and a wide scatter), which a deconvolution can measure once, from the stack; the scatter's 5 % is light R7's inverse must not try to put back as detail.
  - **The ASI462MC's captures** need their own camera terms; dark video at the captures' settings (RAW8, high speed, 4 ms, offset 53, gain 121 and 170) would pin them, and the camera is on hand.

## R3 Metrics, validated against the truth

**Issue:** #1051.

Each metric lives in Lib and is shared by the CLI verb and the stack itself (`tianwen planetary-measure`).

- **Fidelity against T1, per wavelet band:** the transfer the pipeline recovered in each band (recovered over true amplitude) and the error it left. This is the MTF of the whole pipeline, and what every parameter is chosen on.
- **Ringing:**
  - The limb's radial profile against the truth's.
  - An undershoot outside the limb below the sky background, in MAD units, against the input's own null (the deconvolver's `ring_excess`, rebuilt on the limb).
  - Overshoot across belt edges.
  - The Gibbs reference is about 9 % for a hard spectral cut.
  - Marziliano's edge-ringing metric and a slanted-edge overshoot are the published forms to compare with.
- **Fabrication:** power above the diffraction cutoff against the input's. A restoration may not raise it.
- **Truth-free versions for real captures:** split-half agreement per band (T2), the limb undershoot, and cutoff power where the capture samples the cutoff.
- **Pre-registered:** each truth-free metric must rank candidate stacks as the truth-based metric does on the synthetic captures (Spearman at least 0.8) before any conclusion on a real capture rests on it. The Laplacian score, which rises with noise, is not a metric of the result anywhere in this plan.

### R3 results

**Done** (2026-09-30): the limb's undershoot passes; the halves' agreement fails and waits on R7's measured blur.

- **The metrics** (`PlanetaryMetrics`; `tianwen planetary-measure <capture> --truth`):
  - Every plane is normalised to a sky of 0 and a disk mean of 1. It is registered onto one disk (the truth's) by the limb fit, and moved by the Fourier shift theorem, exact for a band-limited plane where interpolating blurs the finest band. Distances are counted in radii of the ellipse (`MetricDisk`), so Jupiter's polar limb, 6.5 % inside its equatorial one, is limb and not sky.
  - **Fidelity** per a trous band inside 0.9 radii: the transfer (the least-squares gain of the stack's band on the truth's) and the error left (the difference's RMS over the truth band's).
  - **The halves' agreement** per band: two stacks by the same method from every other frame (`PlanetaryFrameSubset.Half`), their correlation, the power both hold, and the noise.
  - **Ringing:** the limb profile's undershoot below the sky just outside it, and the profile's RMS against the truth's over 0.8 to 1.2 radii. The undershoot is counted in the disk's brightness. Counted in the sky's noise, as this section first had it, one sharpening read 840 to 5,700 sky sigmas as the frames stacked went from 60 to 3,000, because the sky's noise fell while the ring stayed.
  - **Fabrication:** the power past the cutoff, where the grid samples it. It does not at 2022-09-03's prime focus, whose cutoff of 1.1 cycles a pixel lies past Nyquist.
- **The validation**, on 2022-09-03's calibrated twin (R2, the first 3,000 frames): 18 candidates, keeping 2, 5, 10, 20, 50 and 100 % of the frames, each with no sharpening, `PlanetaryDefault` and `Combo`, stacked with alignment points, each with its two halves.

  | Preset | Fidelity error, bands 1 to 5 (keep 5 %) | Transfer, band 3 | Limb undershoot | Limb profile error |
  |---|---|---|---|---|
  | None | 0.80, 0.54, 0.27, 0.11, 0.03 | 0.78 | 0 | 0.037 |
  | `PlanetaryDefault` | 0.93, 0.71, 0.64, 0.52, 0.20 | 1.52 | 0.21 of the disk | 0.143 |
  | `Combo` | 0.92, 0.65, 0.40, 0.31, 0.12 | 1.30 | 0.15 of the disk | 0.104 |

  | Truth-free metric against its truth-based one | Spearman over the 18 |
  |---|---|
  | The limb's undershoot against the limb profile's error | **+0.95** |
  | The halves' correlation against the fidelity error, bands 1 to 5 | -0.65, -0.40, +0.21, +0.32, +0.42 |

- **The limb's undershoot passes** the pre-registration, and may judge ringing on a real capture.
- **The halves' agreement fails it**, and no conclusion on a real capture rests on it (the kill line).
  - Its correlation measures noise and nothing else. It rises with the frames stacked (0.60 to 0.98 in band 1) and is blind to a per-band gain, which scales both halves alike. What separates the candidates here is the sharpening, which it cannot see.
  - A truth-free fidelity needs the blur the pipeline left. With R7's measured PSF, the truth's power in a band follows from the power both halves hold, and the error from the noise between them. The halves' agreement is revisited there.
- **Found for R4 and R8:**
  - **R4:** frame selection barely moves the fidelity on this twin. From 2 % of the frames to all of them, band 1's error goes from 0.806 to 0.813 and band 4's transfer from 0.930 to 0.928, because its blur is mostly static (R2: a still layer at the telescope and a wide scatter).
  - **R8:** both presets over-sharpen this capture. `PlanetaryDefault` returns 1.5 times the truth in band 3, leaves more than twice the error of no sharpening there, and rings a fifth of the disk's brightness below the sky; `Combo` does less of each. R8 derives the gains from the measured blur and noise instead.

## R4 Which frames to keep

**Issue:** #1052 (feeds #817).

- **First principles:** a frame is worth stacking, in a region, by how much true detail it carries above its noise in the bands that matter. On a synthetic capture that is known exactly: each frame's local transfer against T1, per alignment point and band.
- **Measured:**
  - Every candidate estimator ranked against the true per-frame, per-point quality on the synthetic captures: `LaplacianEnergyEstimator`, `GradientEnergyEstimator`, the planned FFT high-band estimator, local correlation with the reference stack, and each of them noise-debiased.
  - The debiasing uses noise measured above the cutoff where the capture samples it, else from frame differences.
  - The keep fraction then comes from the measured trade between the stack's noise (falling with more frames) and its transfer (falling with worse frames), per band, at matched noise.
- **Pre-registered:**
  - On 8-bit captures a noise-debiased high-band estimator ranks frames closer to true quality than the Laplacian (a Spearman gap of at least 0.1).
  - The measured optimal keep fraction lands inside the parameter study's flat 5 to 25 % region.
  - Kill line: if the gap is not there, the Laplacian stays and the plan says so.

### R4 results

**Adopting it:** #1072, waiting on open question 4. **Measured per frame** (2026-09-30): the Laplacian ranks an 8-bit capture's frames barely better than chance, the mid bands rank them well, and a good ranking reaches the stack. The per-point half waits on a twin whose blur varies over the disk (below). Nothing is adopted on the twin until open question 4 is answered.

- **The tool** (`tianwen planetary-grade`):
  - **Every frame is scored by each estimator** on the disk's box, as `FrameGrader` does:
    - the Laplacian and the gradient;
    - `FftHighBandEstimator` in four bands matched to the a trous bands 1 to 4 (fft1 0.25 to 0.5 cycles a pixel, down to fft4 0.03 to 0.06), each raw and debiased by the noise read off the frequency plane's corners;
    - the **reference gain**: the frame's least-squares gain in each a trous band on the stack of every frame.
  - **With a truth, every frame is also scored by its true transfer** in each band, and by the Strehl `planetary-degrade` recorded. The frame is registered onto the truth by `CorrelationRegistrar`: a cross-correlation that is not whitened, its peak climbed by Newton's method, 0.002 px on a test disk.
  - **A transfer is a least-squares gain, so the frame's noise does not bias it.** That is what makes it a per-frame truth on an 8-bit frame, and the reference gain its truth-free twin.
  - **Each selection is then stacked at the same frame counts.** Registered onto the truth by the correlation, only the selection differs; stacked by the stacker's own aligner, the pipeline's loss shows too.
- **Ranking the twin's 3,000 frames**, Spearman against the true transfer, and each score against itself one frame on. In brackets, the range over its three 1,000-frame segments:

  | Estimator | Transfer, band 1 | Transfer, band 2 | Transfer, band 3 | Strehl | Lag 1 |
  |---|---|---|---|---|---|
  | Laplacian | +0.21 (0.16 to 0.25) | +0.19 (0.15 to 0.22) | +0.16 (0.10 to 0.20) | +0.18 | +0.01 |
  | fft1, debiased | +0.08 | +0.07 | +0.06 | +0.08 | 0.00 |
  | Gradient | +0.83 (0.81 to 0.85) | +0.87 (0.86 to 0.88) | +0.86 (0.83 to 0.87) | +0.80 | +0.31 |
  | fft3, raw or debiased | +0.78 | +0.89 (0.88 to 0.89) | +0.89 (0.88 to 0.90) | +0.66 | +0.27 |
  | Reference gain, band 2 | +0.90 | +0.99 | +0.94 | +0.75 | +0.31 |
  | The true transfer, band 2 | +0.92 | 1 | +0.92 | +0.78 | +0.30 |

- **The real capture, with no truth** (2022-09-03 Red, the same first 3,000 frames), each estimator against the reference gain:

  | Estimator | Ref. gain, band 1 | Ref. gain, band 2 | Ref. gain, band 3 | Lag 1 | Lag 10 |
  |---|---|---|---|---|---|
  | Laplacian | +0.12 | +0.11 | +0.09 | +0.08 | +0.01 |
  | Gradient | +0.71 | +0.86 | +0.91 | +0.91 | +0.42 |
  | fft3, debiased | +0.77 | +0.92 | +0.85 | +0.89 | +0.27 |
  | Reference gain, band 2 | +0.88 | 1 | +0.92 | +0.94 | +0.35 |

- **What ranks and what does not:**
  - **The Laplacian ranks the frames barely better than chance**, on the twin and on the real capture alike, and its score is white from one frame to the next where every measure of the seeing is coherent. At 8 bits a frame's finest scale is its noise, and so is a score read there: fft1, the finest FFT band, behaves the same.
  - **The mid bands rank.** The gradient (Sobel is a smoothed derivative) and fft3 (0.06 to 0.12 cycles a pixel) reach +0.87 to +0.89 against the true transfer in bands 2 and 3. The reference gain ranks best, +0.99 in its own band, and needs no truth: it is the true transfer read against the stack instead.
  - **Debiasing changes nothing here.** Raw and debiased FFT bands rank alike (fft3 +0.89 both), because the twin's brightness and noise are steady and the corners take the same from every frame. It can matter only where a frame's noise changes against its brightness: transparency, a gain change.
  - **The real capture agrees, with no truth at all.** The gradient, fft3 and the reference gain rank its frames alike (0.85 to 0.92 in band 2), and each is coherent over about ten frames (lag 1 0.89 to 0.94), as the seeing is at 250 frames a second. The Laplacian agrees with none of them (at most +0.12), and its lag 1 is 0.08.
- **The stacks**, registered onto the truth so only the selection differs. The fidelity error by band, at the keep that minimises it among 2, 5, 10, 20, 50 and 100 %:

  | Selection | Band 1 | Band 2 | Band 3 | Best keep, band 1 |
  |---|---|---|---|---|
  | Every frame (3,000) | 0.746 | 0.512 | 0.251 | |
  | Every n-th frame, any count (no selection) | 0.746 | 0.506 | 0.249 | 100 % |
  | Laplacian | 0.723 | 0.496 | 0.246 | 20 % |
  | Gradient | 0.649 | 0.433 | 0.225 | 5 % |
  | fft3, debiased | 0.660 | 0.435 | 0.224 | 5 % |
  | Reference gain, band 2 | 0.645 | 0.422 | 0.223 | 5 % |
  | The true transfer, band 2 (the oracle) | 0.640 | 0.420 | 0.224 | 5 % |

  - **A good selection reaches the stack.** At 5 %, band 1's transfer rises over no selection by +0.118 ± 0.012 with the gradient and +0.120 ± 0.013 with the reference gain (the three segments' spread), about 40 % of its value; band 2's by +0.064 ± 0.005 and +0.073 ± 0.008. The Laplacian's rises by +0.028 ± 0.011 and +0.014 ± 0.009.
  - **This is why R3 found that selection barely moves the fidelity**: its selections were the Laplacian's. What R3 read as a static blur was mostly a selector near chance.
  - **The stacker's own aligner costs as much again.** Every frame stacked by it leaves band 1's error at 0.805 against 0.746 registered onto the truth (band 2: 0.549 against 0.512). That is R5's.
  - **The optimal keep differs by band**, measured from 0.33 % to 100 % (10 to 3,000 frames) with the reference gain's selection:
    - band 1, the finest and the one the noise limits, is best from 5 to 10 % (150 to 300 frames), a flat bottom (0.645, 0.648), rising to 0.700 at 1 % and 0.692 at 50 %;
    - band 2 is flat from the best 10 frames to the best 60 (0.419 to 0.422) and rises slowly beyond (0.430 at 5 %, 0.451 at 20 %);
    - bands 3 and 4 are best from the fewest frames and move by less than 0.01 anywhere below 20 %.
  - **So one keep is a compromise.** At 5 % band 1 is at its best and band 2 gives up 0.011 of error. A stack whose coarse bands came from fewer frames than its finest would take each band's best: for R8, which sets the gains per band (#1055).
  - **A keep is a count of frames, not a fraction.** The noise that sets band 1's optimum falls with the frames stacked, so a longer capture keeps a smaller fraction.
- **The pre-registered claims:**
  - **A noise-debiased high-band estimator ranks the frames closer to their true quality than the Laplacian, by at least 0.1: it does, by 0.7** (fft3-debiased +0.89 against +0.19 in band 2, every segment above +0.88 against every one below +0.23). The debiasing is not why; the band is.
  - **The optimal keep lands inside 5 to 25 %: for the band that decides it, at its lower edge.** Band 1's is 5 to 10 %; the coarser bands' lie below 1 % but hardly depend on it. The twin's frames also differ less than the real capture's (below), which moves the real optimum toward fewer frames, not more.
  - **The kill line does not fire**, so the Laplacian does not stay by default. Which estimator replaces it, and at what keep, is chosen on the twin, so it waits on open question 4 (#1072); the real capture's agreement above is the evidence that does not.
- **Found for R2:** the twin was calibrated on the quality's spread and its lag 1 as the Laplacian read them, and both runs now show that reading to be noise (0.082 real, 0.013 synthetic). Read by the reference gain instead, the twin matches the real capture on neither:
  - **The real frames differ more.** Band 2's reference gain, p5 to p95 over its median, spans 0.87 to 1.10 on the real capture and 0.91 to 1.06 on the twin. The twin therefore understates what selection can gain on the real capture, not overstates it.
  - **The real quality is coherent for about ten frames** (lag 1 0.94, lag 10 0.35), the twin's for one or two (lag 1 0.31). Its free air, at 22 m/s, turns the quality over far faster than the sky did. A ranking of single frames does not care; anything that follows the seeing from frame to frame does.
  - Both are tracked with the per-point twin (#1071).

#### R4 per-point quality needs a twin whose blur varies over the disk

**Issue:** #1071. **Parked 2026-10-02**: the layered twin and the per-point tools are built, the calibration part way (below).

`PlanetaryDegrade` applies one PSF to a whole frame, and the calibrated twin has no warp. On it, a point's quality is its frame's up to noise, and no per-point estimator can be ranked. The alignment-point stack weights each pixel by `FrameSharpnessMap`, a smoothed Sobel energy (the family that ranks frames well), and each frame by the Laplacian's score (which does not). Measuring the per-point half needs:
- the free air at an altitude, so points a few arcseconds apart look through different parts of it, calibrated against the real capture's warp (R2: 0.787 px RMS a point, its correlation length at most 9 px);
- the quality's spread over the frames and its coherence from one to the next matched to the real capture's, both read by the reference gain.

**How it is built and measured, set down 2026-10-02 before anything is built:**
- **Part 1, the layer (`planetary-degrade --high-r0 --high-altitude --high-wind --high-outer-scale`).** A third screen, the free air at an
  altitude h, beside the free air at the pupil and the still layer at the telescope, which stay as they are (every point of the disk
  sees them alike). A point of the disk at angle theta from the disk's centre looks through it shifted by h theta, so its PSF differs
  from its neighbour's once h times their separation nears the aperture.
  - **The PSF at a grid of field points** (`--field-grid`, 6 px by default, so every other point is a 12 px alignment point) over
    the disk and the PSF's reach, each integrated over the exposure as today's is, through the high screen at its own footprint and the
    two common screens.
  - **Each point's PSF split in two:** its tilt, which is the warp (interpolated smoothly between the points and applied as the warp is
    today, a lossless screen keeping the radiance), and its tilt-removed blur, whose images are blended by the points' tent weights
    (the interpolated-PSF model of Nagy and O'Leary 1998). Blending PSFs that carry their tilts would put two peaks where one moved.
  - **Recorded beside the capture** (`<capture>.field`): every frame's tilt at each field point and the point's true quality in
    each band (below), the per-point truth. A grid of PSFs a frame would be some 90 MB, so what part 3 reads is computed as the frame
    is made.
  - With no high layer the frames are made exactly as today, sample for sample (a test pins it); `--warp-rms` and the layer are
    exclusive, the layer's tilts being the warp.
  - Tested: two points' tilts decorrelate with their separation as the footprints' overlap says (a point's own tilt at zero
    separation, near none once h times the separation passes the aperture); and a frame's tilt-removed blur varies over the disk only
    with the layer on.
- **Part 2, the calibration**, on 2022-09-03 Red's first 3,000 frames, from the calibrated twin with its free air split between the
  pupil and the altitude, every statistic read against its spread over three seeds:
  - the disk's motion by the limb (real 0.582 px), as today;
  - the warp read plain (R5 part 1's statistic): its RMS a axis (real 0.372), lag 1 (0.173) and correlation at 9 px (+0.01);
  - the reference gain's spread in band 2, p5 to p95 over its median (real 0.87 to 1.10), and its lag 1 (0.94) and lag 10 (0.35);
  - and the rest of R2's table as the calibrated twin matches it (the limb's widths, the noise, the flux, the halo).
- **Part 3, the per-point measurement (`planetary-grade --points`):**
  - **A point's true quality** in an a trous band is its tilt-removed PSF's transfer over the diffraction limit's, weighted over the
    band's frequencies by the truth's power in a 16 px patch about the point: the least-squares gain a noise-free frame's patch would
    have against the truth's there, which is how R4 defined a frame's.
  - **The points** are the twin's own field points every 12 px (the stacker's alignment-point spacing) whose 16 px patch lies wholly
    on the disk, so the truth is read where it was computed and never interpolated.
  - **Each estimator ranked against it**, Spearman over the frames at each point, the median over points: `FrameSharpnessMap`'s
    smoothed Sobel energy at the point (what the alignment-point stack weights by today), the gradient on the patch, fft3 on the
    patch, the reference gain on the patch (against the stack of every frame), and the frame's own whole-disk gradient (the per-frame
    selection R4 measured) as the baseline.
  - **Then stacked:** each point's best frames by the patch's reference gain against the whole frames' best by the gradient, at the
    keeps 5, 20 and 50 %, registered onto the truth, scored by band.
- **Pre-registered:**
  - **The share of a point's band 2 true quality that is its own** is at least 20 % on the calibrated layered twin: with each point's
    mean over the frames taken out (its patch's own contrast), the variance left once each frame's mean over the points is taken out
    too, over the variance before it, over all frames and points. Kill line: under it, a point's quality is its frame's on this capture's seeing, the stacker weights per
    frame, and per-point weighting is noise.
  - The reference gain on a patch ranks a point's frames at least 0.1 above the frame's whole-disk gradient (median Spearman).
  - `FrameSharpnessMap` ranks within 0.05 of the gradient on the patch (both smoothed Sobel energy).

##### R4 per-point: where it stands (2026-10-02, parked)

**Parked** on 2026-10-02 for the enhanced pipeline (the user's goal that day): parts 1 and 3's tools are built, part 2's calibration is
part way, and the claims are not yet read. Until they are, the alignment-point stack's per-point weighting is not a default.

- **Part 1, the layer, done** (`planetary-degrade --high-r0 --high-altitude --high-wind --high-outer-scale --field-grid`,
  `PlanetaryDegrade.Layered`, `SyntheticFieldFile`):
  - Without the layer every frame is made as before, byte for byte (a 64-frame twin with every feature on, before and after).
  - At no altitude the layered path makes the one-PSF frames up to one ADU (`ALayerEveryPointSeesAlikeMakesTheOnePsfFrames`).
  - Two points' tilts correlate as von Karman theory says for their footprints' separation: 0.754, -0.293 and -0.082 at 6.25, 25 and
    50 cm on a 1 m outer scale, against 0.775, -0.276 and -0.151 (`APointsTiltDecorrelatesFromAnothersAsTheirFootprintsPart`).
  - A 3,000-frame twin takes about an hour (773 lit points a frame, 10 ms for each PSF and 8 for its blur): built where the pupil
    transmits, its rows only transformed, each lit point's object patch transformed once a render.
- **Part 3's tools, done** (`planetary-grade --points`, `PlanetaryPointQuality`, tested), and run only on a 64-frame smoke twin, which
  measures nothing.
- **Part 2, the calibration so far** (2022-09-03 Red's first 3,000 frames; one seed each, against the real capture):

  | Twin | Pupil wind | Still layer renews | High layer | Band 2 reference gain, p5 to p95 over its median; lag 1, 2, 10 | Warp RMS, lag 1 | Disk's motion by the limb |
  |---|---|---|---|---|---|---|
  | real | | | | 0.868 to 1.103; 0.944, 0.852, 0.346 | 0.399, 0.151 | 0.583 px |
  | one PSF | 3 m/s | never | none | 0.899 to 1.065; 0.888, 0.752, 0.097 | | 0.484 |
  | one PSF | 6 m/s | never | none | 0.898 to 1.072; 0.781, 0.502, 0.087 | | 0.517 |
  | one PSF | 3 m/s | 150 ms | none | 0.882 to 1.113; 0.903, 0.802, 0.322 | | 0.523 |
  | one PSF | 3 m/s | 400 ms | none | 0.879 to 1.103; 0.924, 0.839, 0.455 | | 0.507 |
  | layered A | 2 m/s | 200 ms | 13 cm at 10 km, 15 m/s | 0.901 to 1.094; 0.916, 0.838, 0.363 | 0.428, 0.069 | 0.519 |
  | layered B | 2 m/s | 200 ms | 10 cm at 10 km, 10 m/s | 0.906 to 1.086; 0.912, 0.831, 0.353 | 0.434, 0.107 | 0.521 |

  - **No wind gives the real quality's coherence; the still layer renewing in place does** (`--local-renew-ms`, about 200 ms). Frozen
    flow at the pupil leaves band 2's lag 10 near 0.09 at 3 or 6 m/s against the real 0.35, and widens the coarse bands' spread too
    little; renewing the layer at the telescope over 150 to 400 ms brackets both, with the frame-to-frame flux unchanged.
  - **The high layer's warp turns over faster than the real one** at 10 to 15 m/s (lag 1 0.07 to 0.11 against 0.151), at about the
    real RMS (0.43 against 0.40); the disk moves 11 % too little in every trial.
  - Both layered twins match the limb's widths within 1.5 %, and the halo and sky terms as R2's twin did (the sky's finest bands and the
    outer halo stay R2's open items).
  - Two more trials were running when it was parked (the high layer 12 cm at 6 and 4 m/s, the pupil's outer scale 8 m); their logs are
    `layered/cal/trialC.log` and `trialD.log` in the planetary scratch. Then three seeds of the closest, and part 3 on it.

#### R4 keeps are scored after restoration

**Issue:** #1083 (the literature: `docs/architecture/planetary-literature.md`, theme A). **Done 2026-10-02**: once sharpened, the best
keep is about half the frames on every twin, where the raw stack's is 5 to 10 %; per-frequency selection and Fourier burst accumulation earn
nothing to adopt (results below; the keep is #1072's).

R4's keeps minimise the error of a raw stack, and a raw stack's band 1 error is mostly the blur R8 is there to remove. Decomposed from R4's own numbers (theme A section 1.4, derived), band 1's error^2 at a 5 % keep is about 0.34 transfer deficit, 0.06 a residual that does not average down, and 0.015 frame noise. So the keep is chosen where it is used:
- R4's selections, per-band matched weights (each frame by its transfer) and a Fourier burst accumulation exponent sweep (Delbracio and Sapiro 2015), each scored after an oracle per-frequency Wiener, then after R8's gains;
- the oracle ceiling of per-frequency selection (Garrel, Guyon and Baudoz 2012; Mackay 2013) from the twin's noise-free frames, warp on and off, before anything is built.
- **Pre-registered:** band 1's best keep moves from 5 to 10 % to half the frames or more; the ceiling of per-frequency selection is under 15 % in band 1's transfer on this twin, whose frames vary little (D/r0 about 3), where the literature's large gains came from D/r0 of 7 to 30.
- **How it is measured, set down 2026-10-02 before anything was built** (`tianwen planetary-keeps`, on R8 part 1's `MultiFrameBound`):
  - **The twins:** the calibrated one and the one without its still layer (R8 part 1's, each with every frame's PSF), and the warped one
    (0.65 px over 10 px) made again with its PSFs, for "warp on". The first 3,000 frames, a 256 px window.
  - **The keeps:** 1, 2, 5, 10, 20, 50 and 100 % of the frames, ranked by the gradient as the stacker ranks them. Each is summed at the
    frames' true shifts and scored twice: as it is, and restored by the oracle Wiener with its own exact transfer (R8 part 1's 5a). Then
    `planetary-gains --keep` at the same keeps scores the stacker's own stack after R8's derived gains through the true kernel.
  - **Matched weights:** every frame weighted per frequency by the magnitude of its true transfer, and restored the same way.
  - **Fourier burst accumulation:** every frame weighted per frequency by the magnitude of its own spectrum to the power p, p = 0, 1, 2,
    4, 8 and 11 (Delbracio and Sapiro 2015; p = 0 is a plain average), the weights normalised per frequency, restored by the oracle
    Wiener with the weighted sum's own transfer.
  - **The ceiling of per-frequency selection:** in band 1's frequencies (0.25 to 0.5 cycles a pixel), the mean magnitude of the true
    transfer of the best 5 % of frames chosen afresh at each frequency, over that of the best 5 % by the gradient. Noise-free by
    construction: it reads the transfers, never a frame.
  - **Read against the claims:** band 1's error at each keep, raw and restored, its best keep; and the ceiling's excess over one.

##### R4 keeps after restoration: results (2026-10-02)

**Measured** with `tianwen planetary-keeps` on the three twins (3,000 frames each, every frame's PSF known), and `planetary-gains --keep`
at the same keeps on the stacker's own stack. A second run of `planetary-keeps` reproduced every row.

- **Band 1's error at each keep, the frames summed at their true shifts** (ranked by the gradient), raw, then restored by the oracle
  Wiener with the sum's own transfer:

  | Twin | | 1 % | 2 % | 5 % | 10 % | 20 % | 50 % | 100 % |
  |---|---|---|---|---|---|---|---|---|
  | calibrated | raw | 0.699 | 0.668 | **0.655** | 0.659 | 0.672 | 0.702 | 0.752 |
  | | restored | 0.500 | 0.439 | 0.362 | 0.307 | 0.261 | 0.217 | **0.206** |
  | without its still layer | raw | 0.400 | 0.324 | 0.284 | **0.274** | 0.282 | 0.313 | 0.384 |
  | | restored | 0.331 | 0.272 | 0.211 | 0.178 | 0.152 | 0.132 | **0.127** |
  | warped | raw | 0.833 | 0.794 | 0.783 | **0.779** | 0.782 | 0.797 | 0.823 |
  | | restored | 0.672 | 0.617 | 0.567 | 0.529 | 0.504 | 0.480 | **0.473** |

- **The stacker's own stack** (`planetary-gains --keep`), band 1's error and in brackets the four bands' sum: as stacked, after R8's gains
  through the true kernel, and after them through the limb's edge (follow-up 3's kernel, the one a real capture can measure):

  | Twin | | 1 % | 2 % | 5 % | 10 % | 20 % | 50 % | 100 % |
  |---|---|---|---|---|---|---|---|---|
  | calibrated | stack | 0.683 (1.430) | 0.658 (1.412) | **0.651** (1.415) | 0.657 (1.432) | 0.670 (1.460) | 0.699 (1.515) | 0.748 (1.613) |
  | | true kernel | 0.634 (0.843) | 0.583 (0.760) | 0.499 (0.649) | 0.436 (0.578) | 0.398 (0.533) | **0.386** (0.528) | 0.445 (0.600) |
  | | the edge | 0.710 (1.029) | 0.691 (0.989) | 0.627 (0.916) | 0.564 (0.853) | 0.510 (0.791) | **0.387** (0.601) | 0.485 (0.762) |
  | without its still layer | stack | 0.384 (0.599) | 0.322 (0.533) | 0.293 (0.511) | **0.288** (0.512) | 0.296 (0.530) | 0.325 (0.581) | 0.388 (0.686) |
  | | true kernel | 0.369 (0.462) | 0.299 (0.370) | 0.235 (0.289) | 0.205 (0.250) | 0.185 (0.225) | 0.171 (0.208) | **0.167** (0.204) |
  | | the edge | 0.385 (0.528) | 0.315 (0.447) | 0.252 (0.377) | 0.224 (0.347) | 0.203 (0.323) | 0.188 (0.306) | **0.178** (0.301) |
  | warped | stack | 0.816 (1.650) | 0.785 (1.614) | 0.776 (1.616) | **0.773** (1.622) | 0.778 (1.636) | 0.791 (1.668) | 0.817 (1.736) |
  | | true kernel | 1.038 (1.405) | 1.034 (1.323) | 0.919 (1.160) | 0.770 (0.981) | 0.661 (0.864) | **0.558** (0.757) | 0.559 (0.782) |
  | | the edge | 0.940 (1.382) | 0.931 (1.321) | 0.828 (1.179) | 0.711 (1.042) | 0.593 (0.906) | **0.481** (0.720) | 0.495 (0.751) |

- **Weighting every frame per frequency instead of keeping some**, each restored by the oracle Wiener with its own transfer, band 1's
  error and the sum:

  | Twin | Every frame, plain | Matched weights | FBA p = 1 | p = 2 | p = 4 | p = 8 | p = 11 |
  |---|---|---|---|---|---|---|---|
  | calibrated | 0.206 (0.234) | **0.191** (0.219) | 0.381 (0.526) | 0.630 (0.892) | 1.000 (1.455) | 1.097 (1.776) | 0.944 (1.663) |
  | without its still layer | 0.127 (0.143) | **0.125** (0.141) | 0.291 (0.362) | 0.508 (0.635) | 0.865 (1.086) | 1.107 (1.462) | 1.029 (1.442) |
  | warped | 0.473 (0.656) | 0.466 (0.649) | **0.400** (0.524) | 0.433 (0.659) | 0.640 (1.112) | 0.879 (1.722) | 0.859 (1.850) |

  FBA's p = 0 is the plain average and reads as it, row for row.
- **The ceiling of per-frequency selection** in band 1, the best 150 frames at each frequency: over the 150 the gradient kept, as
  pre-registered, 1.220, 1.091 and 1.311 on the calibrated twin, the one without its still layer and the warped one. **Post hoc, not
  pre-registered:** over the 150 whole frames whose true band 1 transfer is highest, 1.132, 1.045 and 1.132.
- **The claims, as pre-registered:**
  - **Band 1's best keep moves from 5 to 10 % to half the frames or more: it holds on every twin and every reading.** Restored by the
    oracle, every frame helps (100 % everywhere); on the stacker's own stack after R8's gains, the best keep is half the frames (calibrated,
    warped, where 100 % ties) or all of them (without its still layer), through the true kernel and through the edge alike. Unrestored,
    the best stays at R4's 5 to 10 %.
  - **The ceiling of per-frequency selection is under 15 %: it holds only on the twin without its still layer** (9.1 %), and fails on
    the calibrated twin (22.0 %) and the warped one (31.1 %).
- **What the numbers say:**
  - **The keep belongs to the sharpening.** A raw stack keeps few frames because its band 1 is mostly blur, and each frame added
    blurs it more; restoration divides the blur out, and then every frame lowers the noise it has to lift. Through the edge, the kernel
    a real capture can measure, half the frames leave a third less error than 5 % on every twin (sums 0.601 against 0.916, 0.306 against
    0.377, 0.720 against 1.179).
  - **The stacker's own stack is best at half the frames where the true-shift sum improves to the last**: the worst half costs the
    stacker something the true shifts do not, which this does not separate (its registration of the worst frames is R5's).
  - **The ceiling is mostly the gradient's ranking.** Over the truly sharpest whole frames, choosing afresh at each frequency gains
    4.5 to 13.2 %; the rest of the pre-registered excess is frames the gradient ranked wrong, twice as many on the warped twin.
  - **And a ceiling of transfer is not a gain in a restored stack.** Matched weights, which read each frame's true transfer at every
    frequency, leave 1 to 7 % less band 1 error than every frame summed plainly, because the restoration already takes every frame.
  - **Fourier burst accumulation over-restores where the transfer is exact.** Its weights read each frame's own spectrum, noise
    included, so the weighted sum holds more than the weights' average of the frames' transfers says, and a Wiener built on that
    average lifts band 1 past the truth (1.22 and 1.18 at p = 1) and doubles its error. On the warped twin the PSFs leave the warp out,
    the plain restoration stops at 0.62 of band 1, and the same overshoot fills part of it: p = 1 is that twin's best row (interpretation:
    a missing blur compensated, not a selection that gains).
- **What it leaves:** the keep for a sharpened stack is about half the frames, and setting it is #1072's, which still waits on open
  question 4 (the keep is measured on the twins). Per-frequency selection and FBA earn nothing to adopt. #1083 closes with this.

## R5 Alignment points and the dewarp

**Issue:** #1053 (feeds #817).

- **First principles:**
  - The warp field has a spatial correlation length set by the seeing (the isoplanatic angle, typically a few arcseconds). Alignment points spaced wider than half of it cannot follow the field.
  - A patch needs enough texture for its correlation to peak cleanly; the features' contrast over noise decides that.
  - Both are measured per capture, never set as a constant 24 px spacing and 32 px patch.
- **The user's centroid idea,** stated to be tested: a feature's true position is the median of its positions over many frames, since the seeing's tip and tilt average out.
  - Warping every frame so its features land on those medians removes the distortion of any single reference frame.
  - The medians are taken in a derotated frame (R6), or over a window whose rotation smear stays under 0.25 px.
  - The candidate references: the best frame, the stack, and the medians, iterated (stack, re-measure, stack).
- **Measured on the synthetic captures, whose true warp is known:**
  - the residual displacement RMS after the dewarp;
  - the stack's transfer per band;
  - bilinear against Lanczos-3 resampling in the mesh warp (bilinear costs about 1 px FWHM in quadrature, `reference_stack_blur_read_the_warped_frames.md`);
  - our alignment against AutoStakkert's `.as3` offsets and grid on the same real capture (T2 comparison, never truth).
- **Pre-registered:**
  - At 0.31 arcsec a pixel and finer, the measured warp correlation length is shorter than twice the default 24 px spacing.
  - The median reference cuts the residual warp RMS by at least 20 % against the best-frame reference.
  - Kill line: if not, the parameter study's finding that AP count barely matters stands, now with a measurement behind it.

### R5 results, part 1: the registration and the warp it can see

**Measured** (2026-09-30) on 2022-09-03's twin with a known warp injected (`planetary-degrade --warp-rms` 0.25, 0.5 and 1.0 px, its default length and frame-to-frame correlation, otherwise the calibrated twin), and on the real capture. **Adopting plain correlation:** #1074. Part 2 (the median reference, and a dewarp that can follow the warp) is still #1053's.

- **The registration follows the noise.** The global aligner and the alignment points register by phase correlation, which weights every frequency alike; on a single 8-bit frame the finest frequencies are noise, and whitening hands them the peak.
  - **A controlled test, not the twin** (`AlignmentPointMatchingTests`): a banded disk at the twin's level and noise (47 ADU over the sky, 1.25 ADU a pixel, rounded), moved by a known sub-pixel shift. A 16 px patch is placed to **1.11 px RMS a axis whitened, and 0.35 px by a plain cross-correlation**, which is also the maximum-likelihood shift under white noise.
  - `PhaseCorrelation` takes `whiten: false` (the tile's mean under the window taken out first); `GlobalAligner`, `AlignmentPointMatcher`, `PlanetaryStackOptions` and `CaptureStatisticsOptions` carry it, whitened by default until #1074.
- **Every stack is better registered plain**, keep 5 % (150 frames), the fidelity error in bands 1 and 2:

  | Warp injected | Global, whitened | Points 12 px, whitened | Global, plain | Points 12 px, plain | Points 12 px, plain, no per-point weight |
  |---|---|---|---|---|---|
  | None | 0.796, 0.534 | 0.791, 0.531 | 0.764, 0.513 | 0.751, 0.500 | 0.760, 0.508 |
  | 0.25 px | 0.815, 0.548 | 0.809, 0.544 | 0.786, 0.528 | 0.770, 0.514 | 0.777, 0.520 |
  | 0.5 px | 0.842, 0.577 | 0.835, 0.574 | 0.809, 0.541 | 0.803, 0.533 | 0.807, 0.538 |
  | 1.0 px | 0.931, 0.705 | 0.917, 0.695 | 0.877, 0.605 | 0.872, 0.600 | 0.875, 0.599 |

  - **Plain wins at every warp and in every band**, 0.03 to 0.05 in band 1. Most of it is the global aligner's: whitened, it is 0.45 px off the synthetic truth (R2).
  - **The alignment points add little over a global stack**, 0.005 to 0.014 in band 2 plain, the same with no warp at all. There they only refine the global aligner's own error.
  - **The dewarp cannot follow the warp yet.** A 1 px warp costs band 2 0.10 of error (0.500 to 0.600, plain) and the points win back almost none of it. Each frame's match is as uncertain as the warp is large; part 2 is a dewarp that pools each point's displacement over the frames the warp stays coherent for.
  - **Spacing:** plain, 12, 24 and 48 px stack alike (band 2 0.500, 0.503, 0.502 with no warp). Whitened, a wider spacing is worse (0.531 to 0.582), fewer noisy points leaving the mesh less to average.
- **The warp statistic sees a warp only plain**, with the same points (16 px patches 12 px apart; `planetary-seeing` once read with 32 px patches, which leave two points on this disk, and now takes the one default `planetary-degrade` does):

  | | No warp | 0.25 px | 0.5 px | 1.0 px | The real capture |
  |---|---|---|---|---|---|
  | Whitened, RMS a axis | 0.745 (seed 2: 0.754) | 0.762 | 0.789 | 0.797 | 0.787 |
  | Plain, RMS a axis | 0.300 | | 0.347 | 0.487 | **0.372** |
  | Plain, lag 1 | 0.012 | | 0.118 | 0.281 | **0.173** |

  - Whitened, the statistic is the matching's noise: 1 px of warp moves it by 0.04. R2 calibrated the twin's warp on that reading, so the twin has none.
  - Plain, the warp shows, and **the real capture has one**: coherent from one frame to the next (lag 1 0.173, where the no-warp twin reads 0.012), between the 0.5 and 1 px twins. Part 2 calibrates the twin's warp on this reading.
  - Plain, the statistic reads about 0.4 of the warp injected (0.39 px over the no-warp twin's in quadrature at 1 px), what the frame's mean over its points and a 16 px patch's averaging leave of a field 20 px long.
- **For R4:** R4's pipeline stacks lost against the stacks registered onto the truth by the global aligner's error, and a global stack registered plain gains 0.02 to 0.10 of band 2's error back (above). Its error in pixels, plain, is part 2's to measure.

### R5 results, part 2: the dewarp cannot follow this capture's warp

**Measured** (2026-09-30) on 2022-09-03's twin with its warp calibrated plain, against the true warp `planetary-degrade` now records beside a capture (`<capture>.warp`). **The kill line fires for this capture:** the alignment points, their spacing, pooling and the reference geometry make no measurable difference; the parameter study's finding stands, now with a measurement behind it. Bilinear against Lanczos-3 resampling and the comparison with AutoStakkert's alignment are part 3, still #1053's.

- **The twin's warp, calibrated on the plain statistic** (read plain, R5 part 1), one parameter at a time:

  | | Real | 0.65 px, 20 px long | **0.65 px, 10 px long** |
  |---|---|---|---|
  | RMS a axis | 0.372 | 0.355 | 0.364 |
  | Lag 1 | 0.173 | 0.194 | 0.215 |
  | Correlation at 9 px | +0.01 | +0.13 | +0.05 |

  The 10 px twin is the calibrated one. Its lag 1 reads a little high: the real warp turns over a little faster from frame to frame than the twin's (its correlation 0.9 a frame).
- **The tools:**
  - **Pooling** (`PlanetaryStackOptions.WarpPoolFrames`): every frame's points are read first, in capture order (`AlignmentPointTracks`). A frame's warp is each point's averaged over the frames either side, since a warp stays coherent for a few frames while one frame's match is as uncertain as the warp is large.
  - **The median geometry** (`MedianGeometry`, the user's centroid idea): each point's median warp over the capture is taken out, so the stack lands where a feature lies on average, not where the reference frame's own warp put it.
  - **The mesh's reach** (`MeshInfluence`) and `planetary-measure --pool --geometry --mesh-spacing --mesh-influence`.
  - **`tianwen planetary-dewarp`**: the points read as the stacker reads them, against the recorded warp. It reports both the points' reading and the mesh the stack applies, sampled every 4 px over the disk they cover.
- **What the dewarp recovers of the calibrated twin's warp** (3,000 frames, px RMS a axis; the warp left undewarped is 0.83 over the disk on the reference geometry, 0.60 on the median):

  | Grid | The points' reading | The mesh the stack applies | Pooled over 1 to 4 frames |
  |---|---|---|---|
  | 32 px patches, 12 px apart; 24 px nodes, 48 px reach | 16 % of the warp (0.67, 0.72) | **1 %** (0.82, 0.83) | at most 3 % more at the points, nothing in the mesh |
  | 16 px patches, 8 px apart; 8 px nodes, 12 px reach | 11 % (0.82, 0.79) | **3 %** (0.79, 0.82) | nothing |

  - The 1 px twin: the points read 15 to 19 %, the finer grid 8 to 9 %.
  - **Two limits, one for each patch size.** A 32 px patch averages over a warp that varies over 10 px; a 16 px patch is too noisy to place, and pooling does not rescue it, so noise is not the only limit. The mesh then blends the points over its reach and keeps almost nothing.
- **The stacks agree**, keep 5 %, plain, band 1's error:

  | Warp | Global | Points 12 px | Pooled 1, 2, 4 | Median geometry | Fine grid, pooled 0 to 4 |
  |---|---|---|---|---|---|
  | Calibrated | 0.832 | 0.825 | 0.826 (pooled 2) | 0.820 to 0.822 (the 20 px twin) | 0.827 to 0.828 |
  | 1 px | 0.877 | 0.872 | 0.871 to 0.872 | 0.871 to 0.872 | |
  | None | 0.764 | 0.751 | 0.756 to 0.757 | 0.753 | 0.756 to 0.757 |

  - Every dewarp is within 0.005 of every other. The points beat a global stack by 0.005 to 0.013 only by refining the global aligner's own error; pooling costs a little with no warp, averaging away corrections that were real.
- **The pre-registered claims:**
  - **The warp's correlation length is shorter than twice the default 24 px spacing:** at 0.49"/px as well, not only 0.31"/px and finer. It is 9 px or less on the real capture, read plain, the twin's calibrated at 10.
  - **The median reference cuts the residual warp by at least 20 % against the best-frame reference: it does not.** Against the truth's own geometry the points' reading is 0.513, 0.555 px on the median geometry and 0.570, 0.592 on the reference frame's, 8 % less; the mesh the stack applies moves neither.
  - **The kill line fires.** The warp, 0.6 px RMS over 10 px, stays in every stack as blur, which R7's measured PSF has to carry. A capture whose warp varies over more than a patch, finer scales and larger disks, is where the dewarp would be tested again.

### R5 results, part 3: the resampling kernel, the peak, the reference and AutoStakkert

**Measured** (2026-09-30) on 2022-09-03's twins (the calibrated warp, and none) and its real capture, beside AutoStakkert 3.1.4's session file of the same capture. Stacks at keep 5 %, plain correlation, no sharpening; bands 1 and 2 as transfer / error. The literature behind the follow-ups: `docs/architecture/planetary-literature.md`. **R5 closes here**; what the literature ranks next is below.

- **The resampling kernel is the largest lever part 2 left.** A stack of frames resampled bilinearly at sub-pixel phases spread evenly is convolved, on average, with the bilinear triangle, whose transfer is sinc^2 an axis: 0.81 at 0.25 cycles a pixel, 0.41 at Nyquist. Lanczos-3's stays at 1.0 to 0.3 cycles a pixel (`PlanetaryResamplingTests` pins both). `PlanetaryStackOptions.Interpolation`, `planetary-measure --interpolation`:

  | Stack | Calibrated, bilinear | Calibrated, Lanczos-3 | No warp, bilinear | No warp, Lanczos-3 |
  |---|---|---|---|---|
  | Global | 0.193 / 0.833, 0.498 / 0.559 | 0.215 / 0.817, 0.513 / 0.542 | 0.262 / 0.764, 0.537 / 0.513 | 0.294 / 0.740, 0.551 / 0.497 |
  | Points 12 px | 0.207 / 0.824, 0.508 / 0.547 | 0.217 / 0.819, 0.514 / 0.540 | 0.280 / 0.752, 0.548 / 0.502 | 0.295 / 0.742, 0.554 / 0.494 |

  - **Pre-registered** from the two kernels' transfer weighted over each band by the stack's spectrum: band 1's transfer 12 to 39 % higher, band 2's 5 to 8 %. **Measured:** 10 to 12 % on a global stack, 2 to 3 % in band 2, under the low end. What the seeing leaves of band 1 sits at the band's low end, where bilinear costs least.
  - **Band 1's error falls 0.014 to 0.024** on a global stack: more than every dewarp of part 2 (0.005), and as much as plain correlation won in part 1.
  - **A global stack resampled by Lanczos-3 matches the points' stack.** Much of what the points seemed to add over a global stack was the bilinear blur, which the mesh path suffers less.
  - **The clamp changes nothing on a planet**: `Lanczos3Clamped` equals `Lanczos3` to the digit on both twins, since a smooth disk trips it nowhere.
  - **The real capture shows no ringing** (the limb undershoot stays 0). Its halves' band 1 agreement falls (0.751 to 0.626 on a global stack): the band's noise passed with its signal, which R3 found that metric sees where it cannot see the blur.
  - **Adopting Lanczos-3** as the default changes every user's stack, the user's call: #1086, with the stacked reference below.
- **A correlation's peak is climbed, never fitted by a parabola.** A disk's autocorrelation peaks in a rounded cone, and the parabola through three samples locks toward the whole pixel.
  - On a banded disk whose belts run along x, so that the limb alone places x, the plain global aligner misplaced frames by 0.198 px RMS with no noise at all, 0.272 with the twin's (0.040 and 0.052 across the belts).
  - Climbed by Newton's method on the Fourier-interpolated surface (R4's registrar's climb, now `PhaseCorrelation.ClimbPeak`, shared): 0.070 and 0.123 (0.009 and 0.029). What remains with no noise is not yet attributed; the window, which stays put while the disk moves under it, is the suspect.
  - The twin's stacks do not move (band 1 0.193 / 0.833 against 0.195 / 0.832): its texture places frames along the belts as well, so the fixture is the worst case. A 16 px patch reads 0.36 px as before, the literature's prediction for a textured patch (theme B, section 2.3). The whitened path is untouched.
- **A stacked reference** (`PlanetaryStackOptions.ReferenceFrames`, AutoStakkert's choice, one of R5's three candidate references):
  - On the banded fixture it helps once the climb has taken the estimator's own bias out: 0.116, 0.033 px against the best frame, 0.085, 0.026 against a stack of 40.
  - On the twin, against the recorded motion: plain 0.201, 0.225 px against the best frame and 0.194, 0.218 against a stack of 300; whitened 0.968, 1.120 and 0.546, 0.687. The stack rescues phase correlation and barely moves a plain one.
  - The twin's stacks do not move (within 0.001). A 0.2 px registration error costs band 1 about 5 % of its transfer, where the warp's 0.6 px costs 36 % (a Gaussian misregistration passes exp(-2 pi^2 sigma^2 f^2)).
- **Registrations compared with no truth** (`tianwen planetary-registration`): every frame registered several ways, each pair's difference being the sum of their errors, and the three-cornered hat (`ThreeCorneredHat`, Gray and Allan 1974) splitting any three into each one's own.
  - **The hat is checked on the twin against its recorded motion:**

    | Registration | Its error against the truth, px RMS x, y |
    |---|---|
    | Plain, the best frame | 0.201, 0.225 |
    | Plain, a stack of 300 | 0.194, 0.218 |
    | Whitened, the best frame | 0.968, 1.120 |
    | Whitened, a stack of 300 | 0.546, 0.687 |
    | The limb fit, every fourth frame | 0.243, 0.232 |

  - **It is silently wrong where two errors are shared.** Plain correlation and the limb fit differ by 0.281, 0.245 px against the 0.315, 0.323 their truths add to (a shared error correlated about 0.2), so the triple with the whitened stack reads plain at 0.085 against its true 0.201 and nothing in the answer says so (`RegistrationComparisonTests` pins the case). So a triple is believed only where the twin clears it.
  - The limb fit is no better than plain correlation (0.24 against 0.20 px). R2's 0.04 px was its formal error, not its real one.
- **AutoStakkert on the same capture** (`AutoStakkertSession` reads the `.as3`; its session of 2022-09-04 for 2022-09-03's Red, on PIPP's centred copy of the 12,990 frames):
  - **What it did:** the frames graded by gradient, noise-robust level 6 (a pre-blur, R4's finding that the raw Laplacian is noise); registered against a stack of the best 8,572; 41 points at three scales (26 of 24 px, 14 of 32, one of 72); drizzle 1.5x.
  - **Its planet track and our limb fit agree to 0.118, 0.071 px**: both read the outline, so the hat cannot split them, and solves AutoStakkert's variance below zero.
  - **Plain correlation against a stack of 1,000 differs from its track by 0.367, 0.371 px, and frame to frame by only 0.283, 0.249** (each difference less its mean over 25 frames either side, `RegistrationComparison.FastDifferenceVariance`). The fast part is what the twin predicts (its plain correlation and limb fit differ by 0.248, 0.208 frame to frame, which the twin's truth splits as 0.20 and 0.24 px of error each). The slow 0.23 to 0.28 px the twin does not have is the texture moving against the limb: Jupiter's rotation moves the texture 0.53 px a minute at the centre while the limb stays, over a 60 s capture.
  - **On the real capture the reference matters, as it did not on the twin.** Against AutoStakkert's track, independent of both, the best-frame reference differs frame to frame by 0.396, 0.661 px and a stack of 1,000 by 0.283, 0.249: the best frame's registration error variance is larger by 0.077 and 0.375 px^2. The twin's frames differ too little for a bad reference to show (its two references differ by 0.05 px); the real best frame is the Laplacian's pick, which R4 found picks noise. Adopting a stacked reference: #1086.

#### R5 follow-ups the literature ranks

- **#1081:** every frame re-measured against the stack on the median geometry, with points 4 px apart and a matched interpolator (a 4 px reach, and a kriging with the warp's covariance). Part 2 changed spacing, reach and reference one at a time; Hardie et al. 2021's model says only all three together pay, 15 to 22 % of the warp recovered against 3 % today.
- **#1082:** an efficient point estimator judged against its Cramer-Rao bound (the maximum-likelihood weight, a square-difference match with a 2-D quadratic fit, correlation surfaces pooled over frames).
- **#1086:** Lanczos-3 and a stacked reference as the planetary defaults, the user's call.

#### #1081: dense points, the stack's geometry and kriging, together

**Pre-registered** (2026-10-03), on 2022-09-03 Red's calibrated twin (`c065l10`: a warp of 0.65 px RMS an axis, falling to 1/e over
10 px), 3,000 frames, plain correlation against a stack of the best 1,000 (the defaults since #1086), on the median geometry:

- **What is measured** (`tianwen planetary-dewarp --max-ap --krige-rms --krige-length`):
  - a point's reading against the true warp at the point and averaged over its patch's window (the slope, and the error about it:
    the point error proper);
  - what three ways of carrying the readings to the disk leave of the warp, scored on the odd frames: the stack's blend on 4 px
    nodes with a 4 px reach, a kriging with the twin's own covariance and the measured point error, and the best linear weights of
    the readings near each place, fitted against the truth on the even frames;
  - then a second pass, every frame re-measured against the stack the first pass made, and the stacks scored by
    `planetary-measure`.
- **Predicted** (#1081, Hardie et al. 2021's filter model, `docs/architecture/planetary-literature.md` theme B): the 16 px point
  error falls from 0.35 toward 0.25 px against the stack; the field recovers 11 to 19 % of the warp with the 4 px blend and 15 to 22 %
  with kriging, against 3 % in part 2; a second pass adds under a fifth of the first's gain.
- **Falsified if** the point error stays at 0.35 against the stack (then it is bias, and #1082's estimator is where the gain is), or
  kriging at 4 px stays at 3 % (then the loss is in how the field is applied).
- **Added before the run:** the best linear weights bound every linear interpolation of these readings. If they too recover under
  10 %, the readings are the limit and no interpolator pays.
- **Found before the run: part 2's fine grid was 64 points.** `PlanetaryStackOptions.MaxAlignmentPoints` (64) capped every grid,
  and neither `planetary-dewarp` nor `planetary-measure` could raise it, so "16 px patches 8 px apart" was 64 points of a possible
  137, and 4 px apart was 64 too. Re-measured on today's code (a stacked reference since #1086, patches cut at the exact shift since
  R5a), the mesh recovers 0 % of the warp at 32 px patches 12 px apart, 2 % at 64 points 8 px apart, and **4 % with all 137**.
- **The stacks, set down after the readings were measured and before any stack ran:** keep 5 %, plain correlation, Lanczos-3, no
  sharpening, on the median geometry, scored by `planetary-measure` against the twin's truth. A mesh stack (`ap-flat`) of 16 px
  patches 4 px apart on 4 px nodes with a 4 px reach, its points' residuals scaled by the gain the dewarp fitted
  (`PlanetaryStackOptions.MeshGain`), must beat both the global stack and the same mesh at a gain of one by more than 0.005 in
  band 1's error, part 2's spread among every dewarp. If not, the warp it recovers does not reach the stack.

**Measured** (2026-10-03). **Dense points pay in the stack (band 1's error 0.776 against 0.796 for a global stack and 0.797
for today's grid); the gain the dewarp fits does not, nor does a second pass, nor kriging as specified.** The readings are the
limit, and not by noise: a point's plain correlation shrinks the warp it reads.

- **A point reads a sixth of the warp it sees.** Against the true warp averaged over its own window, a reading's slope is 0.16 at
  16 px patches (the window a Gaussian of 2.1 px), 0.30 at 32 px and 0.43 at 64 px, with an error about that slope of only 0.15 to
  0.19 px. The readings are precise and strongly shrunk, not noisy:
  - **The shrink is the estimator's, noise or no noise.** On a banded disk moved by a known rigid shift, with no noise at all, a 16
    px patch reads 0.54 of it (`AlignmentPointMatchingTests`): both patches are Hann-windowed where the point is, and the windows'
    own correlation, which peaks at no shift, pulls the reading toward zero wherever the texture under it is smooth. On the twin,
    against a reference the warp has blurred, the pull is three times stronger.
  - **Shifting the window undoes the shrink and costs more than it gains.** Cutting the moving patch again where the shift read so
    far puts it lifts the slope from 0.55 to 0.80 over three passes (0.85 over six), but the error against the truth rises from
    0.31 to 0.39 px: the shrunk reading is a shrinkage estimator, already nearer the truth in mean square. #1082's estimators are
    where any gain in the readings lies.
- **What carrying the readings to the disk leaves of the warp** (on the median geometry, scored on the odd frames):

  | Grid | Blend | The blend times one gain | Kriging, errors independent | Kriging, errors overlapping | The best linear weights |
  |---|---|---|---|---|---|
  | 16 px patches 4 px apart, 494 points | 9 % | 17 % (2.99, 3.31) | 2 % | 15 % | 29 % |
  | 32 px patches 8 px apart | 5 % | 9 % | 12 % | 19 % | 27 % |
  | 16 px patches 8 px apart, 137 points | 4 % | | 11 % | | 22 % |
  | 64 px patches 8 px apart | 6 % | | 1 % | | 23 % |

  - The best linear weights are fitted against the truth on the even frames: no linear interpolation of these readings does
    better, and they recover 29 %, three times the blend. **The readings are not the hard limit**: the blend of shrunk readings can
    recover little more than the shrink.
  - **Kriging as specified fails at 4 px** (2 %): patches 4 px apart share three quarters of their pixels, so their errors are
    correlated, which the model took as independent. With the errors correlated as the windows overlap it recovers 15 to 19 %, in
    the predicted range.
- **The verdicts on the pre-registration:**
  - **The point error is not 0.35 falling toward 0.25.** It is 0.15 px about a slope of 0.16: bias, not noise, so the falsifier's
    branch holds (the estimator, #1082, is where the readings could gain).
  - **The field's recovery:** the 4 px blend 9 % (predicted 11 to 19 %), the blend times one gain 17 %, kriging with overlapping
    errors 15 to 19 % (predicted 15 to 22 %). **Kriging at 4 px as first specified stays at 2 %, so that falsifier fires**, and its
    cause is the error model, not how the field is applied.
  - **The ceiling** (added before the run): 29 %, above 10 %, so the readings are not the limit of a linear interpolation.
  - **A second pass adds nothing:** matching against the stack the first pass dewarped (`PlanetaryStackOptions.RemeasureAgainstStack`)
    reads a slope of 0.151 against 0.149 and recovers what the first did, and its stack is band for band the first's (0.254 / 0.776).
    The reference is already a stack of 1,000 (#1086), and a dewarp that recovers a tenth of the warp moves it by too little.
- **The stacks** (keep 5 %, band 1 transfer / error):

  | Stack | Calibrated twin (0.65 px, 10 px) | No warp | 1 px twin (20 px) |
  |---|---|---|---|
  | Global | 0.233 / 0.796 | 0.334 / 0.693 | 0.173 / 0.853 |
  | Today's grid (32 px patches 24 px apart, 64 points) | 0.231 / 0.797 | 0.335 / 0.692 | 0.176 / 0.851 |
  | Dense (16 px patches 4 px apart, 494 points, 4 px nodes and reach) | **0.254 / 0.776** | 0.333 / 0.695 | **0.220 / 0.811** |
  | Dense, the dewarp's gain (3.15) | 0.247 / 0.787 | | |
  | Dense, a second pass | 0.254 / 0.776 | | |

  - **The pre-registered stack rule fails:** at the dewarp's gain the dense stack beats a global one by 0.009 but loses to itself at
    a gain of one by 0.011. The gain that leaves the least warp at the points is not the one that makes the sharpest stack.
  - **Post hoc, labelled so:** the gain on each residual's departure from the points' mean only, the mean kept as read (the variant
    `MeshGain` now is), reads 0.248 / 0.786 at 3.15 and 0.263 / 0.771 at 2. A gain chosen by trying gains on the twin it is judged
    on is not evidence, so `MeshGain` stays an option at one.
  - **Dense points are the finding:** 0.020 better in band 1's error than a global stack on the calibrated twin, four times part 2's
    spread among every dewarp, where today's grid matches the global stack. With no warp they cost 0.002, within the spread. On the
    1 px twin, a warp correlated over 20 px, they gain twice as much: 0.042 against the global stack, 0.040 against today's grid.
  - **Adopting them as the stack's default is #1195**, judged as #1072, #1074 and #1086 were: on the real captures #1159 validated,
    by eye, and with what a mesh of 500 points on 4 px nodes costs a stack in time.

### R5a Drizzle, where the sampling calls for it

**Issue:** #1064.

- **First principles** (the user's question, 2026-09-29). The 10 inch passes detail up to `D / lambda`, 2.24 cycles a pixel per arcsecond of pixel scale at 550 nm, and a grid samples only to 0.5 cycles a pixel; past that the detail aliases, and only drizzle, fed by the frames' sub-pixel shifts, can win it back. A Bayer plane samples at twice the pitch.

  | Capture | Scale | Pupil's cutoff on the sensor grid | On a Bayer plane |
  |---|---|---|---|
  | 2022-09-03, ASI290MM, prime focus | 0.49"/px | 1.10 c/px | (mono) |
  | 2024-12-15, Uranus-C, prime focus | 0.51"/px | 1.14 c/px | 2.3 c/px |
  | 2022-09-29, ASI462MC, Barlow | 0.40"/px | 0.90 c/px | 1.8 c/px |
  | 2022-10-09, ASI462MC, Barlow | 0.31"/px | 0.70 c/px | 1.4 c/px |
  | 2025-01-02, Uranus-C, Barlow | 0.19"/px | 0.43 c/px (sampled) | 0.86 c/px |
  | 2021-12-16, Maksutov | 0.42"/px | 0.37 c/px (sampled) | 0.75 c/px |

  In ordinary seeing the pupil is not what limits, the frames are: R1 measured the best frames' averaged spectrum reaching 0.4 to 0.6 cycles a plane pixel on the colour nights and about 0.5 a pixel on the 2022 mono night, which is at the grid's limit and so aliased. The predictions: Bayer drizzle to the sensor grid pays on every colour capture; 1.5x past the sensor grid pays only at prime focus, where the pupil reaches 1.1 cycles a pixel, and only on nights whose frames carry signal past 0.5 cycles a pixel of the sensor grid; 3x would need signal past 0.75 and, in ordinary seeing, only costs noise; at 0.19"/px nothing past the Bayer drizzle pays.
- **The truth for a drizzled stack is rendered at its output scale** (`tianwen planetary-render-truth --upsample 1.5`), never a resampled copy of the capture-scale truth, so a drizzle is scored on the grid it produced.
- **Measured on R2's synthetic captures:** a plain stack resampled to the output grid (Lanczos-3), Bayer drizzle to the sensor grid, and drizzle at 1.5x and 3x, each against the truth at its output scale, per band and at matched noise (R3's metrics); on the real captures, the split-half detail (T2) per band.
- **Pre-registered:**
  - Bayer drizzle to the sensor grid beats the demosaic above a plane's Nyquist on every colour capture.
  - 1.5x beats the sensor grid only where the frames' measured cutoff passes 0.5 cycles a pixel of the sensor grid.
  - 3x does not beat 1.5x at matched noise in ordinary seeing.
  - **Added 2026-09-30, before any drizzle was measured** (theme C of the literature): a Gaussian misregistration of sigma passes exp(-2 pi^2 sigma^2 f^2), so the 0.6 px warp R5 could not follow passes 0.64 of the signal at 0.25 cycles a pixel, 0.17 at 0.5 and 0.018 at 0.75, the band a 1.5x drizzle adds. On 2022-09-03 and every capture with a warp like it, 1.5x does not beat the sensor grid, while Bayer drizzle to the sensor grid, recovering 0.25 to 0.5 cycles a pixel on a colour plane, still can.
  - Kill line: a drizzle that wins where the frames' cutoff stays under the input grid's Nyquist is fabricating detail, the RL oracle's rule, and the metric is checked before anything is concluded.
  - **Added 2026-09-30 for 2024-12-15's Uranus-C twin, before its first drizzle was stacked.** R1 measured the colour nights' best frames to 0.4 to 0.6 cycles a plane pixel, 0.2 to 0.3 a sensor pixel: past a plane's Nyquist, under the sensor grid's. So on the twin, each colour against its own truth:
    - (a) Bayer drizzle to the sensor grid beats the demosaic in band 1 (0.25 to 0.5 cycles a pixel) by transfer, the demosaic's interpolation being the loss it avoids, and gives back no more than a little of it in error to its extra noise;
    - (b) a 1.5x drizzle beats the 1x drizzle resampled to 1.5x (Lanczos-3) in no band;
    - (c) 3x beats 1.5x in no band.
    - Each is compared on the grid the finer stack makes, since a wavelet band is counted in output pixels: band 1 at 1.5x is 0.375 to 0.75 cycles a sensor pixel.

### R5a results: the colour twin, and two bugs the real capture found

**Measured** (2026-09-30) on 2024-12-15's Uranus-C capture at 12:36:43 UTC (320 x 240 RGGB, 8 bits, 444.7 frames a second, the first 3,000 frames) and its calibrated colour twin.

- **The colour twin** (`planetary-degrade` on a colour capture, `--bayer-maps` OPAL 2024d's F631N, F502N and F395N, Minnaert k 0.999, 0.950, 0.850; `planetary-seeing --plane r|g|g2|b`; `CfaPlaneStream`):
  - each photosite colour measured on its own plane, with its own levels, its own gain, and a truth at its own wavelength, where its own disk is;
  - **one atmosphere for three colours is more than one seed**: the screen is drawn at the finest pupil spacing and the largest size any colour needs, and each colour's pupil reads it at its own spacing (`DegradeOptions.ScreenSpacingM`);
  - **the dispersion is measured, not modelled**: the four planes stacked on one registration, each colour's limb against green's in the same stack. Red lies 0.39, 0.85 sensor px from green and blue 0.56, 1.16 the other way, 1.1" red to blue at an altitude of about 34 degrees;
  - **each colour's gain is its own**: the camera applies its white balance as a digital gain before its 8 bits, so red reads 14.5 e-/ADU, green 15.4 and blue 11.4. Green's gain for all three gave blue's twin 0.86 of its real noise.
- **A glitch frame poisoned every reading of the real capture.** Four frames of its 30,000 (2102, 10806, 16845, 22543) carry their top two to four sensor rows at 255 right across, over a sky of 6. That line is the sharpest thing a Laplacian sees, so frame 2102 was every colour's best:
  - every stack of those frames was registered against it and weighed it highest;
  - the capture statistics took it for their reference, and its line pulled the disk's first estimate 6 plane px off, a quarter of the radius, which moved every ring read around the disk (the halo, the sky, the alignment points' reach, so the real planes matched no points at any patch size);
  - in blue, whose photosites the rows also cover, the aligner read it 62 px off, which alone doubled the seeing's reading.
  - **The fix:** `FrameGrader.IsCorruptReadout`, a band of rows at full scale ending abruptly in a mostly dark row (so an overexposed Moon, whose saturated rows fade, is never one), scores zero in the batch stacker, the live one and the capture statistics; the statistics keep such a frame out of their reference, means, sampled limbs and quality distribution, and take its shift and light from its neighbours.
- **An alignment point's patch was cut at the ROUNDED global shift.** A point's residual then had to carry the shift's own fraction as well as the warp, and along the belts, where a 16 px patch holds nothing to place it by in x, it locked to the whole pixel, and so did every frame's mesh.
  - A mesh stack was misregistered by up to half a pixel along the belts. A 3x Bayer drizzle, which needs the frames' offsets spread over the pixel to fill each colour's grid, left 17 % of red's and blue's disk empty, every empty cell in one column of the red photosite's 6 px repeat (band 1's error read 87).
  - **The fix:** `PlanetaryTile.ExtractLumaAt` cuts a frame's patch at the exact sub-pixel shift (separable Lanczos-3, a whole-pixel centre bit for bit the old patch), and the mesh is built on the exact shift. On a disk whose belts run along x only, moved (0.4, 0.3), the mesh read 0.071, 0.235 px plain and 0.123, 0.136 whitened before, and 0.399, 0.299 and 0.434, 0.278 after (`AMeshKeepsTheGlobalShiftsFractionWhereAPatchCannotPlaceIt`).
- **The calibration, one step at a time, against the real capture's statistics with both fixes in** (each colour's ratio of twin to real):

  | Twin | r0 at 500 nm | Warp | Gain | Seeing's motion | Single frames' blur (median) | The aligner against the limb |
  |---|---|---|---|---|---|---|
  | 1 | 8.5 cm (2022-09-03's) | none | green's | 0.58 to 0.59 | 0.60 to 0.67 | 0.46 to 0.59 |
  | 2 | 5.0 cm | none | green's | 0.87 to 0.88 | 0.71 to 0.93 | 0.51 to 0.56 |
  | 3 | 4.3 cm | 0.45 px | green's | 0.99 to 1.01 | 0.82 to 1.00 | 0.78 to 0.82 |
  | 4 | 4.3 cm | 0.62 px | green's | 1.01 to 1.03 | 0.98 to 1.02 | 0.94 to 1.03 |
  | **5** | **4.3 cm** | **0.62 px** | **each colour's** | **1.02 to 1.03** | **1.01 to 1.06** | **1.00 to 1.03** |

  - The warp's own statistic cannot calibrate on these planes: a disk 24 px in radius holds 8 to 13 points 8 px apart, and taking each frame's mean over so few strips out most of a warp correlated over 10 px, leaving a noise floor of 0.12 to 0.25 px with no warp at all. The aligner's error against the limb, which the warp moves, stands in for it, and the single frames' radius RMS agrees. The 0.62 px it asks for is R5's 0.65 on 2022-09-03, the same telescope and site.
  - **Twin 5 and its second seed** agree within 10 % on every colour's motion, blur, edge width, limb, halo, quality and finest noise, each other within a few percent. Outside the band: the quality's lag 1 (1.25 to 1.7, where its seed spread at 3,000 frames is 67 %), red's sky noise beside the disk (0.80) and the flux's frame-to-frame RMS (2.4 to 2.8: the degrader's warp moves surface brightness without its Jacobian, a gain jitter of 0.2 % that no resolution measure sees).
  - **The limb fit cannot tell north from south a week from opposition** (a phase of 1.8 degrees), so the twin's north angle turned over from 90 to 265 degrees between runs. Nothing here depends on it; R6's de-rotation does.

#### R5a drizzle against the demosaic

**Measured** on twin 5 and its second seed (`planetary-measure --drizzle 1,1.5,3`, 3,000 frames, plain correlation, no sharpening), each colour of each stack against its own colour's truth, and each finer stack against the truth rendered at its own scale. Band 1 (0.25 to 0.5 cycles an output pixel) as transfer / error, keep 5 %, global stacks; the two seeds side by side:

| Stack | Red | Green | Blue |
|---|---|---|---|
| Demosaiced, bilinear (the default) | 0.103 / 0.936, 0.119 / 0.923 | 0.126 / 0.893, 0.139 / 0.881 | 0.091 / 0.927, 0.097 / 0.923 |
| Demosaiced, Lanczos-3 | 0.124 / 0.925, 0.146 / 0.911 | 0.147 / 0.875, 0.168 / 0.857 | 0.105 / 0.917, 0.115 / 0.911 |
| **Bayer drizzle to the sensor grid** | **0.148 / 0.892, 0.161 / 0.894** | **0.152 / 0.868, 0.180 / 0.843** | **0.111 / 0.909, 0.135 / 0.888** |
| On the 1.5x grid: the sensor-grid drizzle resampled | 0.091 / 0.938, 0.091 / 0.963 | 0.093 / 0.922, 0.110 / 0.907 | 0.066 / 0.951, 0.084 / 0.935 |
| On the 1.5x grid: drizzle at 1.5x | 0.096 / 1.002, 0.091 / 1.016 | 0.096 / 0.928, 0.112 / 0.916 | 0.067 / 0.966, 0.086 / 0.952 |
| On the 3x grid: the sensor-grid drizzle resampled | 0.047 / 0.986, 0.040 / 1.013 | 0.045 / 0.967, 0.053 / 0.962 | 0.029 / 0.986, 0.040 / 0.980 |
| On the 3x grid: drizzle at 3x | 0.056 / 1.236, 0.040 / 1.245 | 0.050 / 1.028, 0.054 / 1.021 | 0.031 / 1.074, 0.043 / 1.066 |

- **(a) holds: Bayer drizzle to the sensor grid beats the demosaic above a plane's Nyquist.** Band 1's error falls 0.018 to 0.044 against the default bilinear demosaic, and 0.007 to 0.033 against a Lanczos-3 one, in every colour on both seeds (the seeds move a stack by up to 0.02, but never the sign of a difference within one); keep 20 % reads the same (0.005 to 0.034 against Lanczos-3). It is measured against the truth, so the kill line's fabrication does not arise.
  - **It costs the lower bands against Lanczos-3**: a drop of a whole pixel (pixfrac 1) blurs as a bilinear kernel does, so bands 2 and 3 lose 0.007 to 0.018 of error to a Lanczos-3 demosaic, while still beating the bilinear one in band 2 (0.609 against 0.636 in red).
  - **The alignment points change nothing** (every drizzle and demosaic within 0.002 of its global stack), as R5 found for this warp.
- **(b) holds: 1.5x beats the sensor-grid drizzle resampled to 1.5x in no band.** Band 1's error is 0.006 to 0.064 worse (the finer grid's noise), bands 2 and 3 within 0.002. The theme C prediction stands: with 0.62 px of warp, 1.5x does not beat the sensor grid.
- **(c) holds: 3x adds nothing past the sensor grid.** Against the sensor-grid drizzle resampled to 3x it loses bands 1 and 2 (band 1's error 1.02 to 1.25, more error than the band holds signal) and gains 0.003 to 0.005 in band 3, which is the comparison's own Lanczos-3 resampling (its transfer falls toward 0.9 at the frequencies that band covers), not detail. It was compared with the resampled sensor grid, not with 1.5x.
- **The split-half detail (T2) of the real capture is not reported**: R3 found the halves' agreement ranks noise, never the blur, so it cannot judge a stack until R7's measured PSF.
- **What it asks of the stacker, the user's call** (#1091): Bayer drizzle to the sensor grid for a colour capture wins the finest band a colour plane cannot sample and loses a little below it to a Lanczos-3 demosaic, so the choice is tied to #1086's Lanczos-3. A smaller drop (pixfrac under 1), which should keep drizzle's band 1 without its bilinear blur below, is measured next on the same twin (#1092).
- **Judged once sharpened, the demosaic stays the colour default** (2026-10-03, #1159's pre-registered judgement on the same twin, each master sharpened as the pipeline sharpens; in full in "A colour master's finest band", below).
  - **The first run favoured drizzle**, by summed error over bands 1 to 4 and the three colours: 5.79 and 4.58 against the demosaic's 6.31 and 7.05.
  - **That margin was band 1's alone**, and band 1 was broken in both arms: its derived gain (12 to 20) left it worse than the unsharpened stack in every colour, and lifted the CFA's residue into a 2-pixel lattice, which drizzle shows as much as the demosaic does.
  - **With the finest band kept as stacked on a colour master (#1187)**, both arms improve by a third, and drizzle no longer wins on both seeds: 4.49 against the demosaic's 4.37 on seed 1, 4.45 against 4.67 on seed 2.
  - **R5a's band 1 advantage does not survive into the sharpened master.** The default stays the Lanczos-3 demosaic; `planetary-stack --drizzle 1` remains for anyone who wants it.

## R6 De-rotation

**Issue:** #815 (planetary-stacking.md's phases 10 and 11, measured here).

- **What:** the phases #815 plans, on R1's geometry.
  - 6a: each frame reprojected to the capture's mid epoch before stacking.
  - 6b: finished stacks and colour channels to a common epoch.
  - Each reprojection runs through the oblate spheroid in longitude, never a flat image rotation.
  - The image's north comes from somewhere other than the limb fit near opposition: its sun side decides north from south, and a week from opposition (a phase of 1.8 degrees) the fit turned it over by 175 degrees between runs of one capture (R5a). The rendered truth matched at both orientations, or the camera's recorded rotation, settles it.
- **Measured:**
  - Two stacks from the two halves of one long run (the 16-minute, 441,558-frame Jupiter run), derotated to one epoch, must agree better than the same pair without derotation.
  - WinJUPOS's own derotated composites of the 2022 captures are the external comparison.
  - What remains after derotation is physics, not error: zonal winds against System III, and the Great Red Spot's drift. It is measured and reported, never fitted away.
- **The user's "measure the bands":** belts are the check on the geometry (R1). The rotation itself comes from the ephemeris, since belts move with their own winds and would bias a rotation fitted to them.
- **Pre-registered:**
  - R1's belt check, moved here with the projection it needs: the belt-edge latitudes on the projected map of the 2022 Jupiter stacks agree with OPAL's within 1 degree, in planetographic latitude.
  - Derotation cuts the half-to-half belt difference RMS by at least half on the 16-minute run.
  - **Added 2026-09-30, before the first pair of 2024-12-15's stacks was compared** (`planetary-derotate`, single-file stacks of 3,000 frames each, 6.7 s): without de-rotation the difference grows with the gap (3.3 degrees of rotation at 5.4 minutes, 6.6 at 11, 18.8 at 31); de-rotated, what is left is the two stacks' own noise and seeing, about the same at every gap. So the 11-minute pair meets the half and the 31-minute pair beats it clearly. Jupiter's fastest jet drifts about 0.008 degrees a minute against System III, a quarter of a degree over 31 minutes, too little to show.
  - **Added 2026-09-30, before part 2 (6a) was measured** on a twin or the real run; the unit tests' synthetic captures had been stacked:
    - **The twin**: R5a's colour twin of 12:36:43 (`uc-g4`'s recipe, seeds 1 and 2) with its 3,000 frames spread over 16 minutes (`planetary-degrade --span-minutes 16 --truth-at middle`), scored against the truth at its middle; keep 5 %, global, plain correlation, Lanczos-3, demosaiced, green. As taken, the disk's middle moves 2 sub-plane pixels either side of the epoch, so band 3's error rises above 0.5 and band 4's above 0.3 (`uc-g4` itself: 0.35 to 0.37 and 0.17 to 0.18 over the two seeds). Every frame de-rotated, both come back within twice that seed spread: band 3 at most 0.39, band 4 at most 0.20. On `uc-g4` (6.7 s, 0.07 degrees) de-rotated and as taken agree within 0.005 in every band.
    - **The run**: 12:52:23 to 13:08:55, twelve captures and 311,558 frames at one gain, halved at its middle, 8 minutes (5 degrees) between the halves' middles; keep 5 %, global. Each half stacks about 7,800 frames, so its noise is well under part 1's 150-frame pairs' and the rotation dominates: finished stacks carried to one epoch (6b) and every frame carried to the run's middle (6a) each leave at most half the difference as taken (the plan's claim), 6a within a tenth of 6b or better. The whole run stacked with every frame de-rotated has more power than as taken in bands 1 to 3: the same frames and the same noise, and the rotation's smear gone.
    - **North**: the run's first and last quarters agree better with the limb fit's north than turned over, as every pair in part 1 did.
  - The residual drift profile matches the known zonal wind profile's shape.
  - **Added 2026-09-30, before any belt was read** (part 3, `tianwen planetary-belts`):
    - **The profile**: a stack's zonal albedo in 0.25-degree bins of planetographic latitude, from the pixels within 40 degrees of its central meridian whose emission cosine is above 0.5, each divided by Minnaert's lighting with the limb fit's k. OPAL's is the zonal mean of the map nearest in time, blurred in latitude by the stack's own limb PSF. A belt edge is an extremum of the profile's latitude derivative (smoothed over 0.75 degrees), between 40 S and 40 N, at least a fifth of the strongest.
    - **The stacks**: 2022-09-03's Red (against OPAL's 2022 F631N map of 12 November, 70 days later); 2022-09-29's 46-minute run and 2022-10-09's 21-minute run, every frame de-rotated (red against F631N; 44 and 34 days); and 2024-12-15's 16-minute run (red against the 20 November 2024 F631N map, green against F502N; 25 days).
    - **The claims**: every edge found in both lies within 1 degree of OPAL's (the plan's), and the best single latitude offset between the two derivative profiles is within 0.5 degrees. Belts move over weeks, so one edge alone beyond a degree is weather as much as geometry; the offset is the geometry's.
  - **Added 2026-09-30, before any drift was read** (part 3, `tianwen planetary-drift`), after the first belts (2022-09-03 and 2022-10-09) and before the drift verb had run on anything real:
    - **The measurement**: a run's first and last thirds, each stacked with every frame carried to its own middle, projected onto planetographic latitude and System III longitude at their own instants, and each 0.25-degree row's shift along longitude between them, averaged in 2-degree bands and read as the eastward wind over the time between the thirds' middles.
    - **The truth**: OPAL publishes maps, not winds, so the one published number used is Tollefson et al. 2017 (Icarus 296): the North Temperate Belt jet, the strongest eastward jet, at 23.5 to 24 N planetographic, 144 to 160 m/s against System III. Over 2022-09-29's 46 minutes (thirds' middles about 31 minutes apart) that is about 0.24 degrees of longitude, 0.3 px of an 80 px disk.
    - **The floor**: the red and the green channels read the same winds with their own noise, so half the RMS of their difference over every band is each one's error.
    - **The claims**: on 2022-09-29, if the floor is under 50 m/s, the strongest eastward wind between 15 and 35 N lies within 2 degrees of 24 N and reads 100 to 220 m/s, in both channels; with the floor above 50 m/s the run cannot decide the plan's claim, and says so. On 2024-12-15's 16 minutes (thirds 11 minutes apart, 0.09 degrees at the jet) the floor is above the jet: nothing is claimed there but the floor.

### R6 results, part 1: finished stacks carried to one epoch (6b)

**Measured** (2026-09-30) on 2024-12-15's Uranus-C session: single-file stacks (the first 3,000 frames, keep 5 %, plain correlation, Lanczos-3), each at its file's middle, compared inside 0.9 radii of the later one's disk, each on its own disk level. The pairs were measured again after part 2 found that two stacks of one camera moved onto each other with each its own fitted north are turned by the fits' difference as well; both now take the second's (0.4 degrees apart on the 11-minute pair, more on the short ones), and the table is the second measurement. Part 3, below, has the belt latitudes against OPAL and the drift a night can show.

- **The geometry both ways** (`PlanetaryProjection`): a pixel to the planetographic latitude, west longitude and lighting it sees, and a latitude and longitude back to a pixel. `PlanetaryRender` casts its rays through it, the same arithmetic, so every render is unchanged.
- **A de-rotation carries the ALBEDO** (`PlanetaryDerotation`): each output pixel's latitude and longitude at the target instant, found where they lay at the source instant and sampled there by Lanczos-3, divided by Minnaert's lighting where it was and multiplied by the lighting where it goes, with the limb fit's k. On a map rendered ten minutes apart, 0.0706 RMS falls to 0.00044; carried as brightness, 46 % of it went.
- **A pixel is de-rotated only from a source inside 0.9 radii** (`Derotation.Covered`). Nearer the limb a stack is its seeing-blurred edge, not Minnaert's law, and over 31 minutes the side the rotation turns into view read its sources out there: relit samples reached 195 times the stack's peak and the de-rotation doubled the difference (1.83 of none) before the limit.
- **North is decided by the agreement, and the limb fit's was right on every pair** (`tianwen planetary-derotate`); turned over, the planet turns backwards and every pair gets worse (1.23 to 1.59 of none).
- **The pairs**, over the pixels the de-rotation covers:

  | Pair | Apart | Rotation | Not de-rotated | De-rotated | Of none |
  |---|---|---|---|---|---|
  | 13:02:24 and 13:07:47 | 5.38 min | 3.25 deg | 0.01074 | 0.00675 | 0.628 |
  | 12:56:44 and 13:02:24 | 5.67 min | 3.43 deg | 0.01068 | 0.00602 | 0.564 |
  | 12:56:44 and 13:07:47 | 11.05 min | 6.68 deg | 0.01719 | 0.00784 | **0.456** |
  | 12:36:43 and 13:07:47 | 31.06 min | 18.78 deg | 0.02855 | 0.01934 | 0.678 |

  With each stack's own north the first measurement read 0.727, 0.676, 0.463 and 0.677: the short pairs gained most, their fits' difference being the larger share of their rotation.

- **The pre-registered claims:**
  - **The difference without de-rotation grows with the gap**: it does, 0.011 to 0.029.
  - **De-rotated, what is left is the same at every gap**: 0.0060 to 0.0078 for the three pairs that share their camera's settings. The 31-minute pair's 0.019 is not the rotation: its earlier capture was taken at an analogue gain of 215 against 263 (4.8 dB less, its stack peaking at 0.41 of full scale against 0.71) and 2.9 degrees warmer, so the two stacks differ in what the camera made of them.
  - **The 11-minute pair meets the plan's half**: 0.456. **The 31-minute pair beats it clearly: it does not** (0.678), for the camera's reason above; a pair 31 minutes apart at one gain would say more, and this session has none.

### R6 results, part 2: every frame carried to the run's middle (6a)

**Measured** (2026-09-30) on 2024-12-15's 16-minute run at one gain, 12:52:23 to 13:08:55: twelve captures joined in time order (`PlanetaryFrameSequence`), 311,558 frames. Every stack keeps 5 %, global, plain correlation, Lanczos-3. `PlanetaryStackOptions.Derotation` carries each frame to the run's middle, on the global, alignment-point and Bayer drizzle paths; `tianwen planetary-stack --derotate` stacks a run so, and `tianwen planetary-derotate-run` measures it.

- **How a frame is carried** (`FrameDerotator`): the de-rotation to the epoch is a per-pixel field (`DerotationField`, part 1's rule and arithmetic) beneath the frame's registration in the displacement mesh the stack resamples it by, each sample relit as it lands; alignment points are cut where the rotation and the shift put them, and a drizzle's forward map takes one fixed-point step over the field, each raw sample relit before it is scattered. Every frame is registered against the reference turned to its own instant (every 10 s), so a whole-disk correlation never splits the difference between the belts and the limb.
- **The disk comes from a stack of the best frames as taken, never one frame.** The limb does not turn with the planet, and one 8-bit frame's fit put north anywhere from 260.5 to 268.2 degrees on three of 12:56:44's best frames (a stack of 150: 263.8), which tilts every frame's rotation by the error. It is one cold limb fit a stack, about 2 to 3 s since #1106 ("The limb fit's cost", under R1).
- **North is decided by the capture**, as part 1 decided it by two stacks: the best frames of the run's first and last quarters, the earlier carried to the later's instant both ways round (`PlanetaryNorthDecision`). On a synthetic capture one frame's fit had it turned over.
- **The first pass found two faults**, both fixed before the numbers below. Each half took its north from its own best frame (263.7 and 266.6 degrees), and the halves were moved onto each other with each its own: the 2.9 degrees between them turned the image, and alone put the de-rotated halves 0.018 apart, 1.56 of as taken. Two stacks of one camera now share one north, in this verb and in part 1's.
- **The halves**, split at the run's middle (13:00:39.2) into 131,558 and 180,000 frames, 8.56 minutes apart as taken, over the 5,620 pixels inside 0.9 radii all three ways cover:

  | Halves | Difference | Of as taken |
  |---|---|---|
  | As taken | 0.01176 | 1 |
  | Finished stacks carried to one epoch (6b) | 0.00548 | 0.466 |
  | The same, north turned over | 0.02043 | 1.738 |
  | Every frame carried to the run's middle (6a) | 0.00533 | **0.453** |

- **The whole run**, 15,578 frames, each wavelet band's power inside 0.8 radii with every frame de-rotated against as taken, the same frames and so the same noise: 1.095, 1.037, 1.005, 1.001 and 1.000 in bands 1 to 5. Band 1 is mostly noise at 8 bits, and a resample through a field that varies across the disk smooths the noise a little less than a pure shift, so its 9.5 % is not all detail; bands 2 and 3 are the planet's.
- **The twin**: R5a's colour twin of 12:36:43 with its 3,000 frames spread over 16 minutes (`planetary-degrade --span-minutes 16 --truth-at middle`), and R5a's own at 6.7 s as the control; green's error by band against the truth, global, demosaiced:

  | Twin | Stacked | Band 1 | Band 2 | Band 3 | Band 4 |
  |---|---|---|---|---|---|
  | 16 min, seed 1 | as taken | 0.884 | 0.631 | 0.366 | 0.173 |
  | | de-rotated | 0.871 | 0.618 | 0.362 | 0.173 |
  | 16 min, seed 2 | as taken | 0.861 | 0.587 | 0.337 | 0.165 |
  | | de-rotated | 0.853 | 0.579 | 0.335 | 0.165 |
  | 6.7 s, seed 1 | either | 0.875 | 0.622 | 0.365 | 0.177 |
  | 6.7 s, seed 2 | either | 0.857 | 0.590 | 0.349 | 0.172 |

  The alignment points and the drizzle move the same way, by the same amounts (bands 1 and 2 down 0.007 to 0.013 in all eight, band 3 down 0.002 to 0.005, bands 4 and 5 within 0.001). The twin does turn: its halves, 75 frames each, differ by 0.013 and 0.011 as taken, and north turned over puts them 1.68 and 1.84 of that apart. Against this seeing its turn costs little.
- **The pre-registered claims:**
  - **The twin as taken, band 3 above 0.5 and band 4 above 0.3: it is not** (0.34 to 0.37, and 0.17): the prediction was wrong, and the twin says why. Sixteen minutes smear the middle of the disk by about 4 sensor pixels, but the stacks sit 0.067 RMS from a diffraction-limited truth, most of it the seeing's blur, and the rotation changes a stack by 0.006.
  - **The twin de-rotated, band 3 at most 0.39 and band 4 at most 0.20**: it is, and a little better than as taken in bands 1 to 3.
  - **On `uc-g4` (6.7 s), de-rotated and as taken within 0.005 in every band**: within 0.001.
  - **The run's halves, 6b and 6a each at most half of as taken**: 0.466 and 0.453. **6a within a tenth of 6b or better**: better, by 3 %.
  - **The whole run de-rotated has more power in bands 1 to 3**: it does (1.095, 1.037, 1.005).
  - **North, the quarters agreeing better with the limb fit's than turned over**: they do, in both halves and the whole run (0.0087 against 0.0181, 0.0047 against 0.0172, 0.0124 against 0.0279), all at 264.1 degrees.

### R6 results, part 3: the belts against OPAL, and what a night's drift can show

**Measured** (2026-09-30). `tianwen planetary-belts` stacks a capture, or a run with every frame de-rotated, reads its zonal profile (`PlanetaryBelts`) and compares its belts' edges with OPAL's, blurred by the stack's own limb PSF. `tianwen planetary-drift` stacks a run's first and last thirds, each carried to its own middle, projects both onto planetographic latitude and System III longitude (`PlanetaryZonalDrift`) and reads each band's shift between them as a wind. Two options measure the measurement: `--truth` renders OPAL's map, still, at both stacks' disks and instants and reads it the same way, and `--same-instant` stacks the captures beginning in the run's middle third alternately, both carried to one instant. Every stack keeps 5 %, global, plain correlation, Lanczos-3.

- **The belts**, stack minus OPAL in planetographic degrees (SEB and NEB are the South and North Equatorial Belts, s and n their south and north edges; a dash where the stack has no edge of the same sense within 4 degrees):

  | Stack | OPAL map, days after | Offset | SEB s | SEB n | NEB s | NEB n | 33 to 34 N | Within 1 deg |
  |---|---|---|---|---|---|---|---|---|
  | 2022-09-03 red, one capture, 12,990 frames | F631N, 70 | +0.18 | +1.16 | +0.24 | +0.18 | -0.47 | +0.92 | 4 of 8 |
  | 2022-09-29 red, 46 min, 540,651 frames | F631N, 44 | **+0.55** | -0.97 | +0.71 | +0.97 | +0.62 | - | 4 of 5 |
  | 2022-09-29 green | F502N, 44 | +0.08 | -1.02 | +1.74 | +0.52 | -0.19 | -0.85 | 3 of 5 |
  | 2022-10-09 red, 21 min, 147,032 frames | F631N, 34 | -0.37 | -0.34 | +1.93 | -1.54 | -0.38 | - | 2 of 5 |
  | 2024-12-15 red, 16 min, 311,558 frames | F631N, 25 | +0.00 | -0.33 | +2.59 | +0.07 | -0.44 | +0.23 | 5 of 6 |
  | 2024-12-15 green | F502N, 25 | -0.25 | -0.88 | +1.17 | -0.49 | -0.68 | +0.24 | 4 of 6 |

  2022-09-03's stack, the sharpest (a limb PSF of 1.84 degrees against 2.3 to 3.0), is not de-rotated; north is the end of its axis the map correlates with (0.852 against 0.217 turned over), and its map shows three weaker edges it does not (31 S, 25 S and 12 S). In 2024 the NEB's north edge lies at 20.8 N, and the map has an edge at 27 S (red +0.04, green none). 2022-09-29's green is beside the plan's list: the same frames read against the other filter's map.
- **The pre-registered claims:**
  - **The best single offset within 0.5 degrees**: five stacks of six (0.00 to 0.37). 2022-09-29's red misses at +0.55, and its green, the same frames, reads +0.08. The projection puts a latitude where the planet does to about half a degree.
  - **Every edge within 1 degree of OPAL's: in no stack.** One edge carries most of the miss. The SEB's north edge, which faces the equator, lies north of OPAL's in all six stacks (+0.24 to +2.59). The SEB's south edge lies south in five (-0.33 to -1.02), so in five of the six the stack's SEB is 1.7 to 2.9 degrees wider than OPAL's. The exception is 2022-09-03, the one capture not de-rotated and 70 days from its map, where the SEB is 0.9 narrower.
- **Why is not settled.** Three candidates:
  - The belts change over the 25 to 70 days between a stack and its map.
  - OPAL's zonal mean covers every longitude, where a stack sees 80 degrees of them.
  - Every stack shows every edge weaker than its map, at 0.24 to 0.91 of the map's slope (the SEB's north edge 0.24 to 0.73). So the stacks' blur is wider than the limb PSF the map was blurred by, and a wider blur moves a weak edge further than a strong one. R7 measures that blur, and re-reading these belts with it is added to R7's claims.
- **The drift**: RMS over the 2-degree bands within 30 degrees of the equator, in pixels along a circle of latitude on the disk and as a wind. Nothing here is noise in the ordinary sense, and nothing is the planet: the twins turn with System III alone, with no wind at all.

  | Stacks compared | Frames | Apart | Red | Green | The still map |
  |---|---|---|---|---|---|
  | 2022-09-29, first and last thirds | 6,759 and 10,890 | 33.3 min | 0.47 px, 274 m/s | 0.39 px, 229 m/s | 70 m/s |
  | 2022-09-29, alternate captures of the middle third, at one instant | 5,260 and 4,505 | none | 0.24 px | 0.19 px | 0.29 deg |
  | 2024-12-15, first and last thirds | 3,578 and 6,173 | 11.0 min | 0.50 px, 1,130 m/s | 0.30 px, 669 m/s | 92 m/s |
  | R6 part 2's 16-minute twins, first and last thirds, seeds 1 and 2 | 50 and 50 | 10.7 min | | 0.45 and 0.44 px, 1,050 and 1,037 m/s | 90 and 80 m/s |

  The North Temperate Belt's jet, 150 m/s at 24 N, moves 0.26 px on 2022-09-29's disk over its 33 minutes and 0.07 px on 2024-12-15's over 11.
- **The pre-registered claims:**
  - **2022-09-29, with its floor under 50 m/s, the strongest eastward wind between 15 and 35 N within 2 degrees of 24 N at 100 to 220 m/s in both channels.** The floor, half the RMS of red less green over every band, is 69 m/s (58 within 30 degrees): above 50, so by its own rule the run cannot decide the claim, and says so. For what it is worth, that wind reads 525 m/s at 28.9 N in red and 544 at 26.9 N in green.
  - **2024-12-15, the floor above the jet**: 501 m/s over every band, as predicted.
- **The floor misses the error, and the error is the stacks'.** 2022-09-29's red and green agree to 58 m/s, and both read the equator at -315 to -514 m/s, where the Equatorial Zone blows about 100 m/s EASTWARD. The error is achromatic, which a red-green difference cannot see. Where it comes from:
  - **Not the projection or the correlation**: OPAL's map, still, reads 70 m/s over the same bands, about 0 at the equator.
  - **The stacks themselves**: two stacks of different captures carried to ONE instant already disagree by 0.19 to 0.24 px, both channels alike (0.42 and 0.37 px at 13 N). By 33 minutes apart that is 0.39 to 0.47 px, and the twins, a planet with no wind at all, read 0.45 px at 11 minutes.
  - **Weak texture adds more**: where a row has little of it (20 to 40 N, and south of 37 S), the still map itself reads up to 0.9 degree, and the correlation's peak there is set by whatever small difference the two images have.
  - So two stacks of one night differ in their local geometry by about half a pixel, more than a jet moves over any run in this corpus. A wind is read from features' longitudes over days to months (WinJUPOS's drift charts); this plan does not measure winds.
- **The plan's other R6 claims.** "The residual drift profile matches the known zonal wind profile's shape" is not decided by these captures, for the reason above. WinJUPOS's only de-rotated images on disk are 2022-09-03's LRGB composites: four channel stacks over 4.6 minutes, each coloured under L's luminance, so a difference channel by channel would measure the composition, not the de-rotation. The geometry a de-rotation rests on, the central meridians and the disk, was checked against WinJUPOS in R1 (within 0.019 degree).

## R7 The blur, measured twice, and its inverse

**Issue:** #1054.

- **First principles:** a stack is the true scene convolved with a residual blur (seeing left after selection and dewarp, the optics, the resampling). The user's idea measures that blur from the lucky frames: the sharpest frames, dewarped, carry less of it, so the kernel `K` with `stack = K * lucky` is the stack's extra blur. It is estimated as a cross-spectrum over many patches, `K(f) = S_stack(f) S_lucky*(f) / (|S_lucky(f)|^2 + noise)`, radially averaged. This is a single-kernel case of multi-frame blind deconvolution (Hirsch et al. 2011, MOMFBD).
- **Measured twice, independently:**
  - (a) The lucky-against-stack kernel above.
  - (b) The limb's edge spread function: the disk's outer edge, modelled with its limb darkening and convolved with a PSF, fitted to the stack. This is the planetary slanted edge.
  - Where a Galilean moon is in the field, its known disk (from the ephemeris) is a third probe.
  - On the synthetic captures the true residual kernel is known.
- **The inverse:**
  - Richardson-Lucy and Wiener with the measured kernel, capped at the diffraction cutoff.
  - Scored against T1 with R3's fidelity, ringing and fabrication metrics.
  - The ceiling is the RL oracle with the TRUE kernel: a result beating it is fabricating.
- **Pre-registered:**
  - The two kernel estimates agree within 10 % in FWHM on the real captures, and each lies within 10 % of the true kernel on the synthetic ones.
  - RL with the measured kernel reaches at least 80 % of the oracle's per-band gain without failing the ringing gate.
  - Kill line: estimates that disagree mean the model of one probe is wrong. The deep-sky deconvolver's tolerance was measured at about 10 % (E7.1), which is why the kernel must be measured, not guessed.

### R7 revised before measurement: a static blur cancels out of a ratio of frames

**Issue:** #1084. Revised 2026-09-30, before either probe was run, from theme C of the literature (`docs/architecture/planetary-literature.md`).

- **Why the first pre-registration cannot hold.** If a frame is S T_i O, a static blur S and a varying T_i, then the lucky frames against the stack are T_lucky / mean T: S has cancelled. Probe (a) therefore sees only the stack's EXTRA blur over its lucky frames, while probe (b), the limb, sees the total, the still layer and the scatter the twin says dominate. The real capture's lucky tenth is only 1 % narrower at the limb than every frame (7.28 px against 7.37). The two cannot agree within 10 %, whatever the kernels.
- **Measured three ways instead:**
  - the spectral ratio, abs(mean F)^2 / mean abs(F)^2 over the frames (von der Luehe 1984), which gives r0 of the varying air from the resolved disk, truth-free and blind to the static part as (a) is;
  - (a), the lucky frames against the stack, the stack's extra blur;
  - (b), a joint fit of the limb AND the halo, the total: fitted apart they trade the core against the wing (Wedemeyer-Boehm 2008, Hinode's PSF from a Mercury transit). The twin's scatter has the transfer exp(-2 pi a f) with a = 10 px, which lifts bands 1 to 3 by only 1.053 once inverted.
- **Pre-registered, replacing the agreement above:**
  - the spectral ratio's r0 matches the twin's free air (8.5 cm at 500 nm) within 15 % and ignores the still layer;
  - (a)'s width is under 0.3 of (b)'s on the twin and the real capture;
  - (b) composed from (a), the still layer and the scatter matches the twin's true kernel within 10 %, and on the real capture (b) is the kernel R7's inverse uses.
  - The oracle ceiling and the ringing gate stand as written.
  - **Added 2026-09-30, before part 1 (the spectral ratio) was built or measured** (`tianwen planetary-spectral-ratio`):
    - **The measurement**: every frame registered by a plain correlation against the capture's sharpest, a window about the disk tapered to the sky, and each ring's ratio of the frames' mean spectrum squared (from the cross-frame terms alone, so the noise drops out of it) to their mean power less the camera's noise.
    - **The theory**: the same ratio of the twin's own seeing model (`planetary-degrade`'s screens, exposure and pupil, one code for both), with the free air alone, its r0 fitted over the rings where the frames' power is at least four times the noise.
    - **The claims**:
      - On the calibrated twin (`red-3k-final`: 3,000 frames, free air 8.5 cm at 500 nm, a still layer of 2.7 cm, no warp): 8.5 cm within 15 % (7.2 to 9.8).
      - The same twin made without its still layer: within 5 % of the first.
      - On the real capture it was calibrated on (2022-09-03's Red, the same 3,000 frames): within 20 % of 8.5 cm (6.8 to 10.2), the twin's calibration being right.
      - The twin with its warp calibrated (`c065l10`, 0.65 px) is measured to show what an uncorrected warp costs the ratio, and nothing is claimed for it.
    - **Added 2026-10-01, after the claims above failed and the method was corrected on the twins, before the corrected method was run on the real capture**. The sky's noise now comes from its own pixels' spread from frame to frame, and the fit may take a warp's factor exp(-4 pi^2 s^2 f^2).
      - The twins, where the truth is known:
        - the one without its still layer reads 8.99 cm;
        - the calibrated one reads 9.06 with the still layer in the theory, and 5.71 without it;
        - the warped one cannot tell its warp from the seeing: fitting both takes r0 to the grid's end and the warp to 0.74 px.
      - Predicted for 2022-09-03's Red:
        - its corrected ratio lies within 0.03 of the warped twin's at every ring fitted, the two sharing their warp by R5's calibration;
        - fitting seeing and warp together is as degenerate there, so no r0 is claimed for the real capture.
  - **Added 2026-09-30 from R6 part 3, before either probe was run**: R6's belts re-read against OPAL blurred by (b) instead of the limb fit's PSF bring the SEB's north edge within 1 degree of OPAL's in at least four of R6's six stacks. If they do not, the edge's miss is not the blur, and the belts' own change over weeks is what is left.
  - **Added 2026-10-01, before part 2 (probes (a) and (b)) was built or measured** (`tianwen planetary-blur`):
    - **The stacks**: each capture's 3,000 frames graded by the gradient (R4; the Laplacian ranks 8-bit frames near chance, so its best are not the lucky ones), stacked global, plain, Lanczos-3.
      - The lucky frames are the best 1 %, split alternately by rank into two halves, L1 and L2.
      - The stack, S, is the frames ranked 1 to 5 %, so no frame of S is lucky and the noise of neither enters the other's cross terms.
    - **Probe (a)**: in each a trous band inside 0.9 radii, S's transfer over the lucky frames', the cross term of S with L1 over that of L1 with L2 (all three registered onto S, normalised).
    - **Probe (b)**: the limb fit on S, its core and its Gaussian halo fitted together (R1's fit), read as band transfers by blurring the twin's truth with that PSF.
    - **The truth**, on the twins: S's transfer against its truth in each band (R3's fidelity).
    - A kernel's **width** is the Gaussian sigma whose band transfers fit its own best.
    - **The claims**, on the calibrated twin (`red-3k-final`), the warped one (`c065l10`) and 2022-09-03's Red:
      - (a)'s width is under 0.3 of (b)'s, on all three.
      - On the twins, (b)'s transfer lies within 10 % of the truth's in bands 1 to 4. The plan's "(b) composed from (a), the still layer and the scatter" is read on the twins as: the lucky frames' own transfer against the truth, times (a), within 10 % of S's against the truth in bands 1 to 4. The still layer and the scatter are what the lucky frames' own kernel holds.
  - **Added 2026-10-01, after part 2 found (b)'s halo at its bound, before part 3's kernel was built or measured** (`tianwen planetary-blur`, again):
    - **The kernel**:
      - The limb fit's geometry is kept: centre, radius, axis, limb darkening and zonal albedo, rendered sharp by the fit's own model.
      - The blur is refitted with a Gaussian core CONVOLVED with the scatter's shape, a delta and a wing (1 + (r/a)^2)^(-3/2): transfer exp(-2 pi^2 s^2 f^2) ((1 - h) + h exp(-2 pi a f)).
      - It is fitted over 0.8 to 2 radii, where the scattered light shows in the sky and the limb fit's annulus stopped at 1.2.
    - **The claims**:
      - On both twins, its band transfers lie within 10 % of the truth's in bands 1 to 4, where (b)'s missed by 17 to 39 % in bands 1 and 2.
      - On 2022-09-03's Red it is reported, the kernel the inverse will use.
  - **Added 2026-10-01, before part 4 (the inverse) was built or measured** (`tianwen planetary-inverse`), after part 3 found that a limb kernel is the total blur, diffraction included:
    - **The stack**: 2022-09-03's calibrated twin, its 3,000 frames graded by the gradient, the best 5 % stacked global, plain, Lanczos-3, normalised and registered onto the truth.
    - **The kernels**, each an isotropic transfer on the grid:
      - the oracle: the stack's own transfer against the truth, ring by ring (the cross spectrum over the truth's power, which the stack's noise leaves unbiased);
      - the measured: part 3's (b') divided by the pupil's own diffraction transfer, and (b) the same way beside it.
    - **The inverse**: Richardson-Lucy in the Fourier domain, up to 40 iterations with each kernel. The oracle's count is where its fidelity error, summed over bands 1 to 4, is least, and every measured kernel is read at that count.
    - **The claim**, R7's: RL with (b') reaches at least 80 % of the oracle's gain in each of bands 1 to 4, a band's gain being what its transfer rises by over the stack's, without failing the ringing gate.
      - The gate: a limb undershoot (R3) no deeper than the oracle's by more than a quarter, and under 0.05 of the disk.
      - R3's fabrication metric cannot be read here: the cutoff lies past Nyquist.
    - **Reported beside it**: (b) the same way; a Wiener filter with (b') at the band 3 transfer the oracle's RL reaches; and on 2022-09-03's Red, RL with its (b') at the oracle's count, its undershoot the one truth-free check.

### R7 results, part 1: the spectral ratio

**Measured** (2026-09-30 and 10-01) with `tianwen planetary-spectral-ratio` (`PlanetarySpectralRatio`), each capture's first 3,000 frames.
- **The measurement**: every frame registered plain against the mean of all of them, where the pre-registration said the sharpest frame (an 8-bit frame is too noisy a reference, R5). Then a 256 px window about the disk, tapered to the sky, and ring by ring the frames' mean spectrum squared, from their cross terms, over their mean power less the noise.
- **The theory**: the twin's own seeing (`SeeingPsfSequence`), each PSF registered on its centroid. `planetary-degrade` now makes its frames through that same class; a 128-frame twin with the still layer, warp and scatter on came out byte for byte the same.
- **The fit**: r0, over the rings whose power is four times the noise.

- **As pre-registered** (the camera model's noise, the free air alone):

  | Capture | Ratio at 0.10 c/px | Fitted r0 at 500 nm |
  |---|---|---|
  | Calibrated twin (free air 8.5 cm, still layer 2.7 cm) | 1.02 | 40 cm, the grid's end |
  | The same without its still layer | 1.01 | 40 cm |
  | The calibrated twin with its warp (0.65 px) | 0.83 | 2.67 cm |
  | 2022-09-03's Red | 0.85 | 2.57 cm (2.98 with the still layer in the theory) |

  **All three claims fail.** The ratios say why:
  - Over one on the unwarped twins: the noise was taken off 20 % too high. The camera model gave an 8-bit sky the rounding's twelfth of an ADU squared, but 2022-09-03's sky rounds to one value nine times in ten and spreads half as much, and the sky is 88 % of the window.
  - The rings above the noise end at 0.12 to 0.16 cycles a pixel, where 8.5 cm lowers the ratio only to 0.98.
  - A warp the registration leaves takes the ratio down by its own factor.
- **Corrected, on the twins first** (the sky's noise from its pixels' own spread from frame to frame, and the fit free to take a warp's factor exp(-4 pi^2 s^2 f^2)). The real capture's prediction was then added before it was run. r0 at 500 nm, and the warp per axis:

  | Capture | Free air alone | Free air and warp | Still layer in the theory | Still layer and warp |
  |---|---|---|---|---|
  | Calibrated twin | 5.71 cm | 5.84 cm, 0.05 px | **9.06 cm** | 9.70 cm, 0.06 px |
  | Without its still layer | **8.99 cm** | 9.85 cm, 0.06 px | 22.3 cm | 29.8 cm, 0.07 px |
  | With its warp (0.65 px) | 2.54 cm | 40 cm (the end), 0.74 px | 2.98 cm | 40 cm, 0.74 px |
  | 2022-09-03's Red | 2.45 cm | 3.08 cm, 0.56 px | 2.82 cm | 3.85 cm, 0.58 px |

- **What it found:**
  - **The ratio sees the still layer**, which the pre-registration said it ignores. A still PHASE in the pupil adds to the air's before the PSF forms, so it is not a static blur S multiplying each frame's transfer, and it does not cancel. Only a static convolution does: the scatter, the pixel. With the still layer left out of the theory the calibrated twin reads 5.71 cm; with it, 9.06.
  - **The warp cannot be told from the seeing over the band the noise leaves** (to 0.12 cycles a pixel on these 8-bit frames). Fitting both on the warped twin takes r0 to the grid's end and puts all of the fall in a warp of 0.74 px, against a true 0.65.
  - **The real capture is not the warped twin.** Its corrected ratio stands 0.04 to 0.11 above the twin's between 0.04 and 0.09 cycles a pixel and falls later (the pre-registered 0.03 fails). The twin's warp, a field correlated over 10 px, moves detail at scales of 10 to 25 px more than the real capture's does. The ratio cannot say whether the difference is in the warp's spectrum or in the seeing.
  - So **no r0 is claimed for the real capture**. The spectral ratio measures the free air only on frames whose warp is followed, which R5 found this capture's is not, or on a capture without one.

### R7 results, part 2: the lucky frames against the stack, and the limb's kernel

**Measured** (2026-10-01) with `tianwen planetary-blur`, each capture's first 3,000 frames graded by the gradient. The lucky 30 are split into two halves of 15, and the stack is the next 120 (global, plain, Lanczos-3). Band transfers are read inside 0.9 radii, in R3's bands.

| | Calibrated twin | Warped twin | 2022-09-03's Red |
|---|---|---|---|
| The limb fit on the stack | core 0.96 px, halo 49.9 % of 4.65 px | core 1.19 px, halo 49.9 % of 4.66 px | core 1.19 px, halo 49.9 % of 4.48 px |
| (a)'s width | 0.371 px | 0.332 px | 0.432 px |
| (b)'s width | 2.027 px | 2.303 px | 2.530 px |
| (a) over (b) | **0.183** | **0.144** | **0.171** |
| The true kernel's width | 1.362 px | 1.733 px | |

| Band | (a), twin / warped / real | (b) over the truth, twin / warped | The lucky frames' own times (a), over the truth |
|---|---|---|---|
| 1 | 0.953 / 0.985 / 0.919 | **0.610** / **0.675** | 1.029 / 1.025 |
| 2 | 0.983 / 0.978 / 0.993 | **0.827** / **0.834** | 1.008 / 0.998 |
| 3 | 0.997 / 0.991 / 1.001 | 0.935 / 0.929 | 1.002 / 0.999 |
| 4 | 0.999 / 0.998 / 1.000 | 0.988 / 0.985 | 1.000 / 1.002 |

- **The pre-registered claims:**
  - **(a)'s width under 0.3 of (b)'s: it is, on all three** (0.14 to 0.18). The stack of the frames ranked 1 to 5 % is blurrier than its lucky frames by 0.33 to 0.43 px of equivalent Gaussian, and within 2.5 % of them from band 2 on. The lucky frames' own blur is nearly all of the stack's.
  - **(b) within 10 % of the truth in bands 1 to 4: it is not.** In bands 1 and 2 it reads 0.61 to 0.83 of the true transfer; in bands 3 and 4 it holds (0.93 to 0.99).
    - The limb fit's halo sits at its model's bound on all three captures: 49.9 % of the light, the most its two Gaussians allow.
    - The edge wants more of the light in the wing than the model can put there, so the kernel reads the fine bands blurred by 17 to 39 % more than they are.
    - Its equivalent Gaussian is 1.5 and 1.3 times the truth's.
  - **The lucky frames' own transfer times (a), within 10 % of the stack's: it is, within 3 % in every band on both twins.** The stack's blur is the lucky frames' own blur, with the still layer and the scatter inside it, plus (a)'s small remainder.
- **What it leaves the inverse**: taken as fitted, (b) would boost bands 1 and 2 by 1.2 to 1.6 times more than the blur calls for. The twin's scatter follows (1 + (r/a)^2)^(-3/2), which no pair of Gaussians follows. So part 3 fits the limb's kernel with a wing it can widen, checks it on the twins the same way, and only then inverts.

### R7 results, part 3: the limb reads the total blur, diffraction included

**Measured** (2026-10-01) with `tianwen planetary-blur` on part 2's stacks.
- **(b')** is `PlanetaryLimbKernel`: the limb fit's geometry kept and rendered sharp by the fit's own model (`PlanetaryLimbFit.SharpModel`), and the blur refitted over 0.8 to 2 radii as a Gaussian core convolved with a delta and the scatter's wing.
- The fit recovers a known kernel of that shape: a 1.1 px core and 12 % in a 9 px wing came back as 1.105 px and 12.0 % in 8.74 px.

| | Calibrated twin | Warped twin | 2022-09-03's Red |
|---|---|---|---|
| (b'): core, and the wing's share and scale | 1.09 px, 83 % at 1.43 px | 1.47 px, 81 % at 1.39 px | 1.64 px, 78 % at 1.18 px |
| (b') over the truth, bands 1 to 4 | 0.40, 0.76, 0.91, 0.94 | 0.43, 0.75, 0.90, 0.94 | |

- **The pre-registered claim, (b') within 10 % of the truth in bands 1 to 4: it is not**, and it reads worse than (b). The fit did not use its wing for a wide scatter: it put four fifths of the light in a wing of a = 1.4 px, a second core.
- **Why both kernels miss, found afterwards.**
  - The twin's truth is rendered through the telescope's diffraction limit, while a limb kernel is fitted against a sharp disk. So the kernel is the stack's TOTAL blur, and the truth's transfer leaves the diffraction out. The comparison the claims set up was between two different things.
  - Rendered again without diffraction (`planetary-blur --map`), the same scene gives the stack's total true transfer, and diffraction's own is 0.546, 0.822, 0.946 and 0.984 in bands 1 to 4.

  | Band | Total true, twin / warped | (b) over the total | (b') over the total |
  |---|---|---|---|
  | 1 | 0.188 / 0.118 | 1.22 / 1.47 | 0.81 / 0.94 |
  | 2 | 0.475 / 0.430 | 1.04 / 1.06 | 0.95 / 0.95 |
  | 3 | 0.765 / 0.760 | 0.99 / 0.99 | 0.97 / 0.96 |
  | 4 | 0.920 / 0.921 | 1.01 / 1.00 | 0.95 / 0.95 |

  Against the total, both kernels read bands 2 to 4 within 6 % on both twins. Band 1 is where they part: (b) 22 to 47 % too sharp, (b') 6 to 19 % too blurred. So what the limb measures is the total blur, and a kernel for restoring toward the diffraction limit is the limb's divided by the pupil's own transfer, which is known.
- **R6's belts re-read with (b), as R6 part 3 asked** (`tianwen planetary-belts --kernel limb`: OPAL's map blurred by the limb fit's core AND halo, where R6 took the core alone). The SEB's north edge, stack minus OPAL:

  | Stack | R6, the core alone | R7, the core and the halo |
  |---|---|---|
  | 2022-09-03 red | +0.24 | +0.36 |
  | 2022-09-29 red | +0.71 | +0.72 |
  | 2022-09-29 green | +1.74 | +1.75 |
  | 2022-10-09 red | +1.93 | +1.93 |
  | 2024-12-15 red | +2.59 | +2.59 |
  | 2024-12-15 green | +1.17 | +1.17 |

  **The claim, within 1 degree in at least four of the six: two**, as before.
  - On five stacks the halo the limb fit finds is 49 to 56 degrees of latitude wide, which only lowers the map's contrast and moves no edge.
  - So by the claim's own terms the edge's miss is not the blur the limb sees, and the belts' own change over the weeks between a capture and its map is what is left.

### R7 results, part 4: the inverse with the measured kernel

**Measured** (2026-10-01) with `tianwen planetary-inverse` (`PlanetaryInverse`).
- **The stack**: each capture's best 150 of 3,000 frames by the gradient, stacked global, plain, Lanczos-3, registered onto the truth.
- **The kernels**:
  - the oracle, read ring by ring against the truth;
  - (b') and (b), each divided by the Newtonian pupil's own transfer at 650 nm, kept at most one and zero past the cutoff.
  - `PlanetaryInverse.Diffraction` gives a clear pupil 0.395 at half its cutoff, where the textbook gives 0.391.
- **The inverse**: Richardson-Lucy in the Fourier domain, every kernel read at the count where the oracle's error, summed over bands 1 to 4, is least: 8 steps of 40 on the calibrated twin, 9 on the warped one.

| The kernel at 0.1, 0.2, 0.3 cycles a pixel | Calibrated twin | Warped twin | 2022-09-03's Red |
|---|---|---|---|
| The oracle | 0.54, 0.37, 0.28 | 0.50, 0.26, 0.11 | |
| (b') over diffraction | 0.48, 0.18, 0.05 | 0.42, 0.09, 0.01 | 0.43, 0.08, 0.01 |
| (b) over diffraction | 0.51, 0.36, 0.19 | 0.46, 0.25, 0.08 | 0.47, 0.26, 0.09 |

Band transfers against the truth, the calibrated twin / the warped one:

| Band | The stack | RL, the oracle | RL, (b') | RL, (b) | Wiener, (b') |
|---|---|---|---|---|---|
| 1 | 0.383 / 0.259 | 0.788 / 0.533 | 0.708 / 0.472 | 0.755 / 0.520 | 1.347 / 0.677 |
| 2 | 0.601 / 0.550 | 0.991 / 0.930 | 1.029 / 0.961 | 0.995 / 0.944 | 1.186 / 1.094 |
| 3 | 0.815 / 0.811 | 1.028 / 1.025 | 1.040 / 1.052 | 1.024 / 1.031 | 1.028 / 1.025 |
| 4 | 0.936 / 0.938 | 1.018 / 1.016 | 1.040 / 1.041 | 0.990 / 0.993 | 1.032 / 1.024 |
| Of the oracle's gain, bands 1 to 4 | | 1 | 0.80, 1.10, 1.06, 1.27 / 0.78, 1.08, 1.13, 1.32 | 0.92, 1.01, 0.98, 0.66 / 0.95, 1.04, 1.03, 0.71 | |
| Error, bands 1 to 4 (calibrated twin) | | 0.472, 0.114, 0.038, 0.020 | 0.463, 0.131, 0.048, 0.041 | 0.420, 0.128, 0.057, 0.022 | 0.762, 0.312, 0.046, 0.034 |
| The limb's undershoot | 0.0000 / 0.0000 | 0.0011 / 0.0010 | 0.0084 / 0.0076 | 0.0000 / 0.0000 | 0.0082 / 0.0075 |

The Wiener filter is set to the band 3 transfer the oracle's RL reaches (noise-to-signal 0.0077 and 0.0145).

- **The claim, RL with (b') at 80 % of the oracle's gain in each of bands 1 to 4 without failing the ringing gate: it fails, on the gate on both twins and in band 1 on the warped one.**
  - Band 1 reaches 0.80 and 0.78 of the oracle's gain.
  - Bands 2 to 4 overshoot: 1.06 to 1.32 of the oracle's gain, transfers of 1.03 to 1.05, past the truth.
    - Part 3 read (b') 5 % too blurred in bands 2 to 4 against the total, and a restoration by it lands about that far past the truth.
    - This is the deep-sky deconvolver's tolerance (about 10 %, E7.1) seen from the other side: a kernel too wide by a few percent restores past the truth by as much.
    - Band 4's error doubles (0.041 against 0.020).
  - The gate: the limb's undershoot is 0.0084 and 0.0076, 7.6 times the oracle's, against a quarter more allowed. It is under 0.05, below 1 % of the disk.
- **Reported beside it:**
  - **(b) passes the gate** (no undershoot) and reaches 0.92 to 1.04 of the oracle's gain in bands 1 to 3, but 0.66 and 0.71 in band 4.
    - Band 4's gain is a ratio of small numbers: the oracle's own RL ends 1.8 % past the truth there and (b) 1 % short of it.
    - (b)'s band 1 error is the lowest of the three (0.420 against the oracle's 0.472). RL with the true kernel, at the count that is best summed over the bands, raises band 1's noise the most, so the oracle is a ceiling on the gain, not on each band's error.
  - **Wiener with (b')**, at the oracle's band 3, takes band 1 to 1.35 on the calibrated twin: a flat noise-to-signal lifts the finest band's noise past the truth (error 0.762), and its undershoot is the same as RL's with (b').
  - **2022-09-03's Red**, RL with its (b') at 8 steps: core 1.58 px, 78.5 % in a 1.18 px wing. It lifts bands 1 to 4 by 1.58, 1.51, 1.24 and 1.09 over the stack, and the limb's undershoot from 0 to 0.0048, less than on either twin.
  - **No Galilean moon is in this capture's 800 x 600 field**: nothing past 1.6 radii stands more than 3 ADU over the sky in a mean of 200 frames, where the disk peaks at 75. The plan's third probe has no capture here (#1120). **Wrong** (R8 follow-up 4): Europa, Ganymede and Io are all in it, 1.95, 3.52 and 5.10 radii out.
- **What it found:**
  - **The finest band is the kernel's MODEL, not the data's.**
    - At 0.3 cycles a pixel the true kernel over diffraction passes 0.28 on the calibrated twin. (b') passes 0.05 there, and (b) 0.19.
    - A Gaussian core falls as exp(-f^2), and both models put one there; the stack's own blur falls more slowly.
    - The edge fixes the kernel where it carries signal (bands 2 to 4, part 3) and the model's shape extrapolates the rest.
  - **At a fixed count, RL restores a low transfer slowly.** (b') is the lowest kernel in band 1 and restores it the least. The count acts as a filter on the kernel itself, so a kernel too low in a band under-restores it rather than ringing there. The ringing came from bands 2 to 4, restored past the truth.
- **What it leaves:**
  - R7's inverse with the measured kernel falls short of its claim. The limb gives the stack's blur within about 5 % in bands 2 to 4, an inverse with it overshoots them by as much, and band 1's transfer is what no edge model here constrains.
  - R8 (#1055) takes H per band: from the limb over diffraction in bands 2 to 4, while band 1's is the open term. The oracle's 0.28 against the models' 0.05 to 0.19 at 0.3 cycles a pixel is its size, and a near-point probe (#1120) is how it could be measured.

### R7a A ghost in the camera train, fitted and subtracted

**Issues:** #1061; the check at the telescope, #1062 (bench).

- **Found 2026-09-29** in the halos R1's spider test was measured in: the ASI290MM mono stacks of 2022-09-03 carry a faint, sharp-edged, lopsided shell around Jupiter, not aligned with the belts. What is known:
  - **It is in the raw frames, not the stacking.** Re-stacked on each frame's disk centroid it is unchanged, and the user has seen such shells in AutoStakkert's stacks.
  - **It rides with Jupiter.** The planet drifted about 100 px across the sensor during the luminance capture and the shell stays sharp in a stack aligned on the planet. So it is Jupiter's own light, not stray light through the open truss, which would stay put on the sensor.
  - **It depends on the filter:** strongest in L, clear in R, faint in G and B. A collimation error in the mirrors would look the same through every filter.
  - **Only this camera has shown it so far.** The ASI462MC and Uranus-C Jupiter stacks have round halos. The same ASI290MM's Venus of 2023-06-12 shows an offset copy of the planet. Nothing permanent in the telescope is the difference: the Uranus-C night of 2024-12-15 is the same Newtonian at the same prime focus (its spider reads 16 to 34 in R1, at 0.49 to 0.51"/px), and shows no shell. What changes from night to night, its collimation, remains (below). The fit decides, since a median of the sky by distance from the limb cannot, the seeing halo and the image scale changing from night to night.
  - **Its size:** the shell reaches 20 to 25 px past the limb, a blur circle about 130 um across. In the f/4.7 beam that is a focus error of about 0.6 mm, which a reflection between surfaces about 0.3 mm apart gives (the sensor and its cover glass). A hypothesis, which #1062 tests at the telescope.
  - **Or collimation** (the user, 2026-09-29). A tilted mirror gives coma, a flare fanning out to one side with a bounded end, which laid over a disk is a lopsided halo with an edge. It rides with the planet, and it belongs to a NIGHT, since a truss Newtonian's collimation changes each time it is set up, which would explain the clean Uranus-C night on the same telescope as well as the camera would. Two things tell it from a reflection. Coma is part of the blur, so it smears the planet's own limb on the flare's side, where a reflection leaves the limb sharp; the unexplained 0.4 to 0.5 px of R1's limb fit on this night is the lead to follow. And it is removed differently: a reflection is ADDED light, subtracted as a fitted copy, while coma is an asymmetric KERNEL, removed by R7's deconvolution with the kernel measured, never by a subtraction. The fit therefore tries both, the copy and an asymmetric kernel, and the residual beyond and at the limb decides. The filter ranking weighs against coma (a mirror's geometry is the same through every filter) but was read by eye, and the fit measures it.
  - **Saturn on the same camera, 2022-08-27, has a glow but not a shell.** Beyond the rings the sky is the same in L, R, G and B to within 15 % at every distance out (about 0.16 % of the planet's peak at 20 to 40 px), it falls smoothly with no edge, and so it does not rank by filter as the Jupiter shell does. It is the "second ring" the user has seen in stretched Saturn stacks. A glow the same through every filter is neither the filters nor the atmosphere (a seeing halo shrinks toward the red), which leaves scatter in the optics. So two things can be present at once, a sharp shell that depends on the filter and a broad glow that does not, and the model needs a term for each.
  - **A sky clipped at black proves no absence here either**, the spider test's lesson again. The ASI462MC Saturn of 2021-12-16 peaks at 9 ADU of 255 with 97.5 % of its pixels at 0, so it cannot show a glow, and it was the wrong comparison. The fit is gated the way the spider is, on the share of the annulus at black.
- **The model, as the comet work separated a comet from its stars:** the ghost is a copy of the planet itself, scaled, shifted, and blurred by a defocus disk: three or four numbers per channel.
  - Beside it, a broad glow: the planet convolved with a smooth, round, falling kernel (a power law in distance is the usual shape of scatter off dust and micro-roughness). Without that term the copy absorbs the glow and reads a ghost where there is only scatter.
  - They are fitted beyond the limb, where the ghost and the glow are alone.
  - The fitted copy is subtracted everywhere, the disk included. Masking beyond the limb alone would hide the shell and leave its copy on the disk.
  - It precedes the blur measurement above, whose kernel would otherwise carry the ghost.
- **Pre-registered:**
  - On a synthetic stack with a ghost of known strength, offset and defocus injected, each comes back within 10 %, and the subtraction leaves no shell above the halo's noise.
  - On the real 2022-09-03 stacks the residual beyond the limb shows no shell, and the fitted strength ranks L above R above G and B.
  - On the Saturn stacks of 2022-08-27 the glow term is the same in the four filters within 15 %, and the copy's strength is small beside it.
  - On the Uranus-C stacks of 2024-12-15, the same Newtonian at the same focus, the copy's strength is small beside the ASI290MM's: the shell is not a permanent feature of the telescope (the camera train's, or that night's collimation).
  - Coma or reflection: the model that leaves the smaller residual at the limb and beyond it on 2022-09-03 names the cause, and that cause decides whether the shell is subtracted (a copy) or handed to R7's kernel (coma).
  - Kill line: a ghost whose fit changes across a capture (a reflection that moves) is not one copy, and the model is revised before anything is subtracted.
- **Clarified 2026-10-01, before it was built or measured** (`tianwen planetary-ghost`; taken up after R8's follow-ups 1 and 2, before follow-up 3, whose limb kernel on 2022-09-03's Red would carry the shell):
  - **The stacks**: each capture stacked as R7 part 4's are (the best 5 % by the gradient, global, plain, Lanczos-3), every frame, the whole frame kept (the PIPP crops are 200 to 320 px). A colour capture is read per plane of its master.
  - **The plane**: the sky at zero (the median of an 8 px border), the object's 99.5th percentile at one.
  - **The copy's source**, P: the stack where it stands above 2 % of that peak, the planet and Saturn's rings alike.
  - **The model**: a constant sky; the ghost, a times P through a uniform disk of radius rho, moved by (dx, dy); and the glow, b times P through (1 + r)^-q, unit sum. Fitted by Levenberg-Marquardt over every pixel at least 3 px outside P's mask, the border band left out.
  - **Coma, the alternative**: c times P through a flare, a uniform line from the centre of length L at angle theta, in the ghost's place, with the same glow and sky. The model with the smaller residual over the 6 px just outside the mask, and over the rest, names the cause.
  - **"No shell"**: in rings 2 px wide from 3 to 40 px outside the mask, the full model's residual has a mean within three standard errors of zero in every ring.
  - **The synthetic**: R7 part 4's stack of the calibrated twin, which has the twin's scatter but no ghost, with a ghost added at a = 0.02, (dx, dy) = (5, -3) px and rho = 18 px. Recovered within 10 % in a and rho and within 1 px in the shift, and the twin itself, with nothing added, fits a ghost under a tenth of that.
  - **The kill line, made concrete**: the first and second halves of a capture's frames, stacked and fitted apart, agree within 10 % in a and rho and within 1 px in the shift.
- **Revised 2026-10-01, after the clarified model failed its synthetic, before the runs that decide the revision** (each failure below was read on the calibrated twin; the real fits of the first model were seen and are not read):
  - **The clarified model failed at once.** The twin, which has no ghost, fitted a copy of 0.84 of the planet, unshifted, rho 7 px: the copy stood in for the planet's own blurred limb below the 2 % threshold, which the source cuts off and which fills the first pixels past it. The injected ghost came back as 0.59 at (0.5, -0.2) px.
  - **Four revisions failed on the twin too.** A glow with a free core (the twin fitted -0.0245); a glow free to take any round shape (0.28, shifted 1.2 px); an elliptical copy beside two power laws (-0.0061, and the power laws cancelling at their bounds); the same from 8 px out (-0.0021, close, but the pre-registered round ghost came back 21 % strong and 16 % small). Each says the same: **no round glow tells a copy's round part from scatter.** A glow free enough to fit the twin's halo takes the copy's round part, and what is left fixes the copy's strength only together with its shift or its shape.
  - **What the stacks show once their round part is taken out** (post hoc, read by eye first, the user's lead that the 2022 Newtonian was on an alt-az mount and the 2024 and 2025 nights on an equatorial one): the quadrupole of what is not round, by band of distance past the limb (1e-4 of the peak):

    | stack | 1-3 px | 3-6 | 6-10 | 10-15 | 15-20 | 20-30 | axis |
    |---|---|---|---|---|---|---|---|
    | calibrated twin | 1.9 | 2.4 | 0.9 | 0.3 | 0.1 | 0.2 | |
    | 2022-09-03 L | 17.8 | 18.6 | **24.8** | **23.2** | **10.0** | 2.6 | 117 to 130 deg |
    | 2022-09-03 R | 23.9 | 25.3 | 5.9 | 0.9 | 0.8 | 1.2 | 109 to 128 deg |
    | 2022-09-03 G | 17.7 | 9.6 | 1.5 | 0.9 | 1.4 | 2.0 | 107 to 129 deg |
    | 2022-09-03 B | 15.1 | 9.0 | 1.5 | 1.0 | 1.5 | 2.3 | 101 to 120 deg |
    | 2024-12-15 Uranus-C, plane 0 (equatorial) | 0.9 | 1.8 | 2.0 | 2.2 | 1.5 | 1.0 | |

    Every 2022 filter's blur is elongated against the limb along one axis on the sensor, and the equatorial night is round. It is not dispersion (R carries it as L does, where dispersion is worst in B), so a mount moving along one axis is the likelier cause, and it is blur, for a kernel to take, never a subtraction. **Only L carries it out to 20 px: that is the shell**, along the same axis, the same in both halves of the capture (24.8 against 23.6 and 25.2 at 6 to 10 px). There is almost no dipole, so the copy is not moved: an elliptical copy, or a smear along one axis, is the model. And the pre-registered synthetic (a = 0.02, rho = 18 px) was about fifty times fainter than this shell beyond 8 px, under the twin's own structure.
  - **The model (revision 6)**: a copy of the planet through a uniform ELLIPTICAL disk (semi-major axis, axis ratio down to 0.05, angle, shift), beside a free ROUND glow (tents in radius, solved linearly) and the sky, fitted from 8 px past the planet and 10 px clear of its moons (the planet is the largest piece of the object; the moons lie along the equator, so their surroundings read as a quadrupole). **What is taken out is the copy's NON-ROUND part beyond the planet** (the copy less its mean at each distance), the shell: its round part cannot be told from scatter, and on the disk a copy spread over tens of pixels only scales it. The copy's strength is fixed only together with its shape and is not read.
  - **Pass, read on the shell's quadrupole by band, 6 to 20 px past the limb**:
    - the twin, plain: its fitted shell under a tenth of L's in every band;
    - the twin with L's own fitted copy injected: the fitted shell within 10 % of the injected one's in every band where that exceeds 5e-4, its axis within 5 degrees, and the twin's quadrupole after the removal within twice the plain twin's;
    - the kill line: L's halves' shells within 10 % in every band and their axes within 5 degrees;
    - a shell is PRESENT only where the copy lowers the fit's RMS by more than a tenth from the glow alone and, where halves are fitted, they pass the kill line. Prediction: L only, among 2022-09-03's four; none on the Uranus-C night; the ranking L > R > G, B as the shells' 6 to 15 px quadrupole;
    - the removal: L's quadrupole from 6 to 20 px falls under a quarter of what it was;
    - Saturn 2022-08-27 is read but not judged: the rings below the threshold are elongated and outside the source, so a copy will lie along them.
- **Revision 6 read, 2026-10-01: it fails two of its criteria, and the failures name the fix** (the shell's quadrupole in 1e-4 of the peak, bands 6 to 10, 10 to 15 and 15 to 20 px):
  - Passed: the plain twin's shell (0.4, 0.0, 0.0 against L's 44.8, 26.4, 11.5); L's kill line (its halves' shells 45.1 and 44.0, 26.4 and 26.2, 11.4 and 11.6, axes 119 to 121 degrees); L as the only 2022-09-03 shell present (the copy halves its RMS, 0.00012 against 0.00023; R's, G's and B's leave it as it was, and R's halves disagree); the ranking.
  - Failed: L's own copy, injected into the twin, came back as a copy almost exactly (0.0541 against 0.0547, radius 35.0 against 36.0, the same axis ratio and angle), but its removal left the twin's 6 to 10 px quadrupole at 3.8, twice the plain twin's 2.0 being the bound, turned through 90 degrees: over-subtracted. L's own removal took its 10 to 20 px quadrupole from 23.7 and 10.0 to 2.7 and 1.6 but its 6 to 10 px only from 24.4 to 21.1, turned through 90 degrees.
  - Failed, the prediction: Uranus-C's copy lowered its RMS by a third, so a "shell" is present by the rule, but its quadrupole is 2e-4, a tenth of the twin's own: it fits something lopsided there, not L's shell.
  - **Why**: L's shell rises from the limb and then falls (its quadrupole 18.6, 24.4, 23.7, 10.0 from 3 to 20 px), where a filled smear's falls from the limb (59, 45, 26, 11), so the copy fitted beyond 8 px is too strong nearer. A defocused Newtonian pupil is an ANNULUS, the secondary's shadow in it (the user's suggestion that it looks like the secondary), and a hole moves a copy's light out from the limb. And the moons were in the copy's source: the fitted smear drew each moon as a streak the data does not have, and its removal a dark line.
- **Revision 7, before the runs that decide it**: the copy is of the planet alone, and its disk an elliptical annulus with a hole fitted between 0 and 0.9 of its size. The pass criteria are revision 6's, with the injected shell now read directly (`planetary-ghost --inject` prints it), not as the stack's quadrupole less the plain twin's.
- **Revision 7 read, 2026-10-01**: L fitted NO hole (0.00; its halves 0.90 and 0.39, their shells still within 3 %), so the annulus is not what L's shell wants, and it goes. The injected shell came back within 2 % in every band (35.7 against 36.0, 19.2 against 19.3, 7.0 against 7.1, axes equal), yet the twin's quadrupole after the removal read 3.3 at 6 to 10 px, against the plain twin's 1.0. **That residue is the read, not the removal**: the ghost moves the planet's 2 % edge out along its axis, so the round mean is taken about a longer edge, and the planet's own steep tail then reads as a quadrupole across it (the stack with the ghost read 32.5 where the ghost alone read 36.0 and the twin 1.0). A removal is therefore judged against the plane BEFORE the ghost read through the same source, which the verb now prints. And L's removal still overshot inside 8 px, where its copy was never fitted.
- **Revision 8, before the runs that decide it**: the copy's disk is elliptical and filled again; the shell is taken out only where the copy was fitted, from the margin out, falling to nothing over the 4 px inside it (`PlanetaryGhost.Shell`); nearer the limb every 2022-09-03 filter's own blur is elongated, which is the kernel's to take. The pass criteria are revision 6's, the injected twin's removal read against the twin before the ghost through the same source.
- **Revision 8 read, 2026-10-01: every criterion but one passes.** The plain twin's shell is under a tenth of L's; L's own copy injected into the twin comes back within 1 % in every band (36.3 against 36.0, 19.2 against 19.3, 7.0 against 7.1, axes equal), and its removal leaves the twin as it was before the ghost (2.5, 0.9, 0.6 against 3.6, 1.0, 0.7 through the same source); L's halves agree within 3 % and 2 degrees; L alone of 2022-09-03 carries a shell, and the ranking holds (6 to 15 px: L 36, R 9.3, G 2.4, B 1.4). L's removal takes 10 to 15 px to 12 % and 15 to 20 px to 14 %, **but 6 to 10 px only to 58 %, turned through 90 degrees**: still over-subtracted there. And the Uranus-C night is "present" by the RMS rule, its copy lowering the RMS a third while its quadrupole is 2e-4, a tenth of the twin's own structure: the rule was too loose, and Uranus-C has no shell like L's.
  - **Why the one failure**: L's own shell, the edge's bias allowed for (about 4.5 at 6 to 10 px, as on the injected twin), reads about 29, 25 and 10 in the three bands, FLAT out to about 15 px and then falling; one smear puts 46, 27 and 11 there, falling from the limb. Two copies either side of the centre give a flat shell out to their separation: the "defocused double image" the user first described, or a mount that shakes and lingers at its turning points.
- **Revision 9, before the runs that decide it**: the copy may be two halves either side of its centre along the disk's axis (`Separation`, zero for one copy, which is revision 8). The pass criteria are revision 8's.
- **Revision 9 read, 2026-10-02**: L preferred two compact copies, 25.8 px either side of the planet along 120 degrees (each a line 6.6 px long), to one smear: a double image, the user's first description, at an RMS of 0.00011 against 0.00012. But its halves fell one to each description (the first a smear of 34.8 px, the second two copies 26.1 px apart), their shells within 10.7 % at 6 to 10 px, past the kill line's 10 %, and within 1.5 and 0.9 % beyond; L's removal at 6 to 10 px improved only to 48 %. The data does not tell one smear from two copies, so the fit keeps one (revision 8), and a split copy stays something `--inject` can make.

#### R7a results

**Issues:** #1061 (this); the check at the telescope, #1062 (bench); the 2022-09-03 kernel's elongation, step 3 (#1139).

- **Adopted: revision 8.** `tianwen planetary-ghost` fits a copy of the planet through a uniform elliptical disk beside a free round glow and the sky, from 8 px past the planet and clear of its moons, and takes out the copy's NON-ROUND part from the fit's margin out (`PlanetaryGhost.Shell`, `--removed`). Its pass criteria, judged as written:

  | criterion | read | |
  |---|---|---|
  | the plain twin's shell under a tenth of L's | 0.4, 0.0, 0.0 against 45.6, 26.5, 11.2 | pass |
  | L's own copy in the twin comes back within 10 % and 5 degrees | 36.3, 19.2, 7.0 against 36.0, 19.3, 7.1, axes equal | pass |
  | the twin after the removal within twice its state before the ghost | 2.5, 0.9, 0.6 against 3.6, 1.0, 0.7 | pass |
  | L's halves within 10 % and 5 degrees | 45.9 and 44.7, 26.5 and 26.3, 11.1 and 11.4; 117 to 121 degrees | pass |
  | a shell present on L only of 2022-09-03 | L halves its RMS (0.00012 against 0.00023), R, G and B change it under 4 % | pass |
  | the ranking L > R > G, B (6 to 15 px) | 36, 9.3, 2.4, 1.4 | pass |
  | L's removal under a quarter, 6 to 20 px | 6 to 10 px 58 %, turned through 90 degrees; 10 to 15 px 12 %; 15 to 20 px 14 % | **fails 6 to 10 px** |
  | none on the Uranus-C night | its copy lowers the RMS a third, its quadrupole 2e-4 | **fails the rule**, not L's shell |

  (The shell's quadrupole by band of distance past the limb, 1e-4 of the peak, bands 6 to 10, 10 to 15 and 15 to 20 px.)
- **What it was**: an ELONGATED shell along 120 degrees on the sensor (from +x toward +y), centred on the planet, out to about 20 px past the limb, in L of 2022-09-03 only, the same in both halves of the capture. The 2022 Newtonian was on an ALT-AZ mount; the same Newtonian on an equatorial mount in 2024 and 2025 shows no shell (the Uranus-C night's quadrupole is the twin's). Every 2022-09-03 filter's blur is also elongated along the same axis next to the limb (1 to 6 px). A reflection between the sensor and its cover glass, the hypothesis R7a began with, would be a round defocused copy; this is a smear or a double image along one axis of the mount, the shape of a mount that shakes along one axis and lingers at its turning points, or of an image doubled along it. #1062 checks it at the telescope.
- **Removed, from 10 px out**: L's shell falls to 12 % at 10 to 15 px and 14 % at 15 to 20 px of what the stack read, and the stack's halo is round at a normal stretch and at ten times one. **Not removed, inside 10 px**: no copy fits L's non-round part there (one smear and two copies both overshoot it, by about half), and it is where every filter's own blur is elongated, which is the kernel's to take: the 2022-09-03 kernel step 3 (#1139) reads off the limb must be ELONGATED along 120 degrees, or it carries this into every restoration of that night.
- **Five measurement lessons, each found by a failure here**:
  - **A round glow cannot tell a copy's round part from scatter.** Any glow free enough to fit a real halo takes it, so a copy's strength is fixed only together with its shape; read and take out what is NOT round.
  - **The source's 2 % cut is a near tail the source does not have.** A copy or a glow fitted inside the planet's own blurred limb stands in for it (0.84 of the planet, on a twin with no ghost); fit from where it has died away (8 px here).
  - **A non-round read about the 2 % edge is biased by anything that moves that edge**: a ghost pushes it out along its axis, and the planet's steep tail then reads across it (3.5 at 6 to 10 px for a shell of 36). Compare a removal against the plane before the ghost read through the SAME source.
  - **The moons are on the equator**: keep their surroundings out of every fit and read, or they are a quadrupole of the planet's halo, and keep them out of the copy, or its smear draws each as a streak.
  - **A synthetic sized by guess can be invisible.** The pre-registered ghost (0.02 of the planet, 18 px) left under 2e-4 beyond 8 px, fifty times fainter than L's shell; inject the real capture's own fitted copy.

## R8 Wavelet gains from the measured blur and noise

**Issue:** #1055 (the wavelet half of #817).

- **First principles:** sharpening is an inverse filter. The gain that minimises error at a band with transfer `H` and signal-to-noise `S/N` is Wiener's, `H / (H^2 + N/S)`, applied per a-trous band. `H` comes from R7's kernel, and `N` per band from the split-half difference (T2).
  - **From R7 part 4 (2026-10-01):** the limb's kernel over diffraction gives `H` within about 5 % in bands 2 to 4. Band 1's is the kernel model's tail, 0.05 to 0.19 at 0.3 cycles a pixel against the twins' true 0.28, so band 1's gain rests on a term no edge here measures (#1120).
- **Measured:**
  - The derived gains against every preset (`PlanetaryDefault`, `Bandpass`, `Combo`) at matched noise, on T1 fidelity and R3's ringing gate.
  - On the real captures, the split-half metrics against AutoStakkert's and RegiStax's results as comparisons.
  - This is the AUTO mode's wavelet half (#817), measured rather than chosen by eye.
- **Pre-registered:**
  - The derived gains beat every preset at matched noise on per-band fidelity and pass the ringing gate.
  - Kill line: if a preset wins, the Wiener model is missing a term, most likely the resampling blur or the noise's colour; find which.
  - **Added 2026-10-01, before the derived gains were built or measured** (`tianwen planetary-gains`, after #1085's ceilings and inverses):
    - **The stacks**: R7 part 4's (the best 150 of 3,000 by the gradient, global, plain, Lanczos-3), on the calibrated twin and the one without its still layer, scored against their truths, and on 2022-09-03's Red. Each comes with its two halves (every other frame of the 150, `PlanetaryFrameSubset.Half`), and everything is read in a 256 px window about the disk.
    - **The derived gains**: six a trous layers like the presets, the residual left at 1, no thresholds.
      - The noise N(f), ring by ring, from half the halves' difference, so it carries its colour (the resampling's included).
      - The stack's power P_S(f), ring by ring, and the kernel H(f) over the pupil's diffraction.
      - The Wiener filter they give with no model of the object: W(f) = (1 - N/P_S)+ / H, the object's power being what the stack holds over its noise, divided by the blur, and zero where H is under 0.02 (part 2's cut).
      - The six gains fitted JOINTLY (part 1) to W over every Fourier coefficient of the window, each weighted by the stack's power there. Under this model that weight makes the fit the gains' least expected error, so there is no knob.
      - With two kernels: (b') over diffraction, and the twin's true ring transfer (`PlanetaryInverse.Measure`, R7 part 4's oracle), so that a miss can be told the kernel's from the method's.
    - **The presets**, `PlanetaryDefault`, `Bandpass` and `Combo`, each two ways:
      - as shipped, gains and thresholds;
      - at matched noise: thresholds dropped and each gain moved to 1 + a(g - 1), with a set by bisection so that the halves' half difference, filtered by them, has the RMS inside 0.9 radii that it has filtered by the derived gains with (b').
    - **Scored** by R3's band transfer and error in bands 1 to 4 against the truth, and by the limb's undershoot. Part 1's jointly fitted per-band oracle and part 2's Richardson-Lucy with (b') are reported beside them.
    - **The claims**, the plan's made concrete, on both twins:
      - The derived gains with (b') leave less error, summed over bands 1 to 4, than every preset at matched noise and as shipped.
      - They pass the ringing gate: a limb undershoot at most 1.25 times the jointly fitted oracle's, or 0.001 of the disk if that is larger.
      - With the twin's true kernel they come within 10 % of the jointly fitted oracle's summed error. With (b') they come within 25 %, the miss in band 1, where (b') is its model's tail (R7 part 4).
      - The kill line, the plan's: if a preset wins at matched noise, the model is missing a term. The noise's colour is checked by deriving the gains again with a white N (`PlanetaryInverse.WhiteNoise`), reported beside.
    - **The real capture**: its rises over the stack and its undershoot, beside the presets' at matched noise.
      - The halves' agreement cannot judge it (R3's kill line), so the limb's undershoot is its one truth-free score.
      - AutoStakkert's and RegiStax's results, where the corpus holds them for this capture, are set beside it on the same two.
    - **Revised while it was built, before anything was measured** (`PlanetaryWaveletGainsTests`). As registered, the fit failed its own unit test, a textured disk blurred by a known Gaussian, even with the Wiener filter taken from the truth's own power: 1.63 summed over bands 1 to 4, against the stack's 1.64 and the jointly fitted oracle's 1.20. Three changes, each from what that test showed:
      - **The powers are read inside the disk**, its mean taken out and tapered to zero from 0.8 to 0.9 radii, where the bands are scored; the halves' noise likewise. Over the whole window the limb, a step of the disk's full brightness, holds most of the power at every frequency, and a Wiener filter on it asked for a gain near 20 at 0.25 cycles a pixel.
      - **Only the four layers of the scored bands are fitted**, the two coarser left at 1 with the residual. Each coefficient is weighted by the stack's power times those four layers' transfers squared: the expected error in the bands scored, which is what the jointly fitted oracle minimises against a truth. Weighted by the power alone, the lowest frequencies set every gain.
      - **The planet is its disk plus a texture.** The texture is stationary, as registered. The disk is deterministic: the limb fit's sharp model (`PlanetaryLimbFit.SharpModel`) against itself through the kernel, fitted as part 1's oracle fits a stack against a truth, and the two terms are summed. A gain's cost at the limb, a step just outside the scored region that no spectrum of the interior sees, is counted there. Without it the gains oscillated (0.32, 4.54, -0.66, 1.62) and left 1.89; with it they are 1.07, 2.51, 0.57, 1.08 and leave 1.25, against the oracle's 0.61, 2.64, 0.56, 1.08 and 1.20.
      - So the derived gains are four, finest first, then 1 and 1 for the presets' six layers. The claims stand as registered.

### R8 how far a real per-band gain can reach

**Issue:** #1085 (the literature: theme C, sections C and D).

Before the gains are derived, the ceilings they are judged against:
- **ASTRA-SR's oracle swap, per band:** the stack's Fourier magnitude replaced by the truth's with its phase kept, and the other way round. A long exposure's transfer is real and positive, which is why restoring the magnitude gains most, but the twin's still layer is one static screen whose transfer has a phase: whether a real, isotropic per-band gain can reach the oracle is this measurement.
- **A multi-frame Wiener with the twin's true per-frame PSFs**, the bound on anything multi-frame blind deconvolution or Fourier-domain lucky imaging could add.
- **Three regularised inverses at a matched band 3 transfer, scored on the limb:** per-band Wiener with Conan et al. 1998's power-law object spectrum (the gain formula above with a prior the capture can fit), Richardson-Lucy on offset-subtracted electrons, and an L1-L2 edge-preserving prior (MISTRAL, Mugnier et al. 2004), each with the measured core-plus-scatter kernel and against a single Gaussian.
- **Pre-registered:** the multi-frame Wiener oracle gains under 10 % on the calibrated twin and over 20 % with the still layer off; Richardson-Lucy and L1-L2 leave at most a third and a half of Wiener's limb undershoot; a single Gaussian rings 1.5 times more. No published quantitative ringing metric for planetary sharpening was found (Lewis 2020 measures the Mars edge-rind's width, not its depth), so R3's limb undershoot stays the penalty.
  - **Added 2026-10-01, before part 1 (the ceilings) was built or measured** (`tianwen planetary-ceilings`):
    - **The captures**: the calibrated twin (`red-3k-final`) and the same twin without its still layer (`r7/nostill`).
      - Each is made again by `planetary-degrade --psf-truth`, which writes beside the capture every frame's PSF, the shift it was given and its brightness (`<capture>.psf`).
      - The capture made again must be byte for byte the first one.
    - **The model is checked first**: each frame's spectrum against its true transfer times the truth's, over a fixed window about the disk. If the residual in bands 2 to 4 exceeds the camera's noise by more than half, the transfer model is wrong and the multi-frame claims are not read.
      - Clarified while it was built, before anything was measured: the residual is read in POWER, after one least-squares scale of the model, which is reported. The twin scales its object to the disk level before the blur and its truth after it, so their units differ by about 2 % (0.980 on the unit test's twin, `ATwinsFramesAreTheirTrueTransferTimesTheTruth`), and unscaled that alone put the coarse bands at 2 to 8 times the noise while the scaled reading held at 0.95 to 1.00 in every band.
    - **The stack**: R7 part 4's, the best 150 of 3,000 frames by the gradient, global, plain, Lanczos-3, registered onto the truth.
    - **The ceilings on that stack**, each scored by R3's band transfer and error in bands 1 to 4 and by the limb's undershoot:
      1. per-band oracle gains: each a trous band of the stack times the least-squares gain that brings it nearest the truth's band, inside 0.9 radii. This is the most R8's derived gains can reach;
      2. the per-ring oracle filter: over each ring, the sum of Re(T S*) over the sum of abs(S)^2. This is the best isotropic linear filter;
      3. the magnitude swap, abs(T) with the stack's phase;
      4. the phase swap, abs(S) with the truth's phase.
    - **The multi-frame bound**, over the same 150 frames and over all 3,000, in the Fourier domain on that fixed window:
      - each frame's true transfer G_i is its PSF and the scatter, over the pupil's own diffraction, at the shift it was given;
      - 5a: the frames summed at their TRUE shifts, restored by a single-image Wiener with that sum's own 2-D transfer;
      - 5b: the multi-frame Wiener, the sum over frames of conj(G_i) F_i over the sum of abs(G_i)^2 plus the noise over the truth's ring power, each frame weighted by its camera noise and brightness;
      - both 5a and 5b use the truth's ring power and the camera's noise.
      - Both know the kernels exactly, so 5b's gain over 5a is what weighting each frame per frequency can add to shift-and-add. Multi-frame deconvolution and Fourier-domain lucky imaging both do that weighting.
    - **The claims**:
      - The plan's, read as defined here: 5b over the 150 frames lowers the error summed over bands 1 to 4, against 5a, by under 10 % on the calibrated twin and by over 20 % on the twin without its still layer.
      - (1) reaches within 10 % of (2)'s summed error on both twins: an a trous band is narrow enough for one real gain.
      - In bands 2 to 4, (2) leaves at most 1.25 times the magnitude swap's error on the twin without its still layer. On the calibrated twin it leaves more than that: the still layer's static phase, which no real gain undoes.
  - **Added 2026-10-01, before part 2 (the three regularised inverses) was built or measured** (`tianwen planetary-inverses`):
    - **The stacks**: R7 part 4's (the best 150 of 3,000 by the gradient), on the calibrated twin and the one without its still layer, scored against their truths, and on 2022-09-03's Red, whose undershoot is the one truth-free check. Each is restored in a 256 px window about the disk.
    - **The kernels**, each over the pupil's own diffraction:
      - the limb's core with the scatter's wing ((b'), R7 part 3);
      - a single Gaussian of the same equivalent width (R7 part 2's `EquivalentGaussianSigma`, read on the band transfers (b') gives the stack).
    - **The inverses**, each with one knob:
      1. **a Wiener filter with Conan et al.'s object prior**: a power law, A f^-p, fitted to the stack's own ring power less its white noise floor (read past 0.4 cycles a pixel) and over the kernel's power, between 0.02 and 0.15 cycles a pixel. The knob is a scale on the noise over that prior. It is applied in the Fourier domain; the per-band form is R8's derived gains, #1055;
      2. **Richardson-Lucy with positivity at the sky itself**: the stack with its sky at zero, lifted by a thousandth of the disk only so a division is defined, rather than R7 part 4's tenth. The knob is the count;
      3. **an L1-L2 edge-preserving prior** (Mugnier et al. 2004): least squares to the stack plus mu times the sum over pixels of delta^2 (t / delta - ln(1 + t / delta)), t the gradient's length and delta the noise's gradient scale (the stack's noise times the square root of two), and positivity at zero. It is solved by lagged diffusivity: each pass is a conjugate-gradient solve with the last pass's weights, delta / (delta + t). The knob is mu.
    - **Matched at band 3**: on each twin every knob is set by bisection so the band 3 transfer is 1.00. On the real capture it is set so band 3 rises over the stack by the factor it rose by on the calibrated twin, and the knob's value is reported beside it.
    - **The claims**, the plan's, on both twins:
      - With the measured kernel, Richardson-Lucy leaves at most a third of the Wiener's limb undershoot, and L1-L2 at most half.
      - With the single Gaussian, each inverse's undershoot is at least 1.5 times what it is with the measured kernel.
      - Reported beside them: each inverse's error in bands 1 to 4, against part 1's jointly fitted per-band oracle.

### R8 results, part 1: the ceilings

**Measured** (2026-10-01) with `tianwen planetary-ceilings` (`PlanetaryCeilings`, `MultiFrameBound`).
- **The twins were made again** by `planetary-degrade --psf-truth` (`SyntheticPsfFile`, about 196 MB of PSFs each), and came back byte for byte the twins already measured (SHA-256 alike).
- **The model holds.** Each frame against its true transfer times the truth, over a 256 px window, leaves a residual of 0.74 to 0.79 of the camera model's noise in bands 1 to 4 on the calibrated twin and 0.73 to 0.80 without the still layer. The truth's scale is 0.989 and 0.993.
  - The residual is BELOW the noise model: an 8-bit sky's rounding twelfth overstates its noise (R7 part 1's trap), so both Wiener restorations below carry a noise term about a quarter too high, alike.
- **The stack**: R7 part 4's, the best 150 of 3,000 by the gradient.

Band error against the truth, bands 1 to 4, and the sum (the calibrated twin / the one without its still layer):

| | Calibrated twin | Without the still layer |
|---|---|---|
| The stack | 0.651, 0.439, 0.228, 0.097 (1.415) | 0.293, 0.130, 0.061, 0.026 (0.511) |
| (1) per-band gains, one band at a time | 0.478, 0.202, 0.078, 0.030 (0.788) | 0.237, 0.044, 0.015, 0.007 (0.304) |
| (1') per-band gains fitted jointly, post hoc | 0.439, 0.108, 0.026, 0.012 (0.586) | 0.234, 0.036, 0.008, 0.004 (0.282) |
| (2) per-ring oracle filter | 0.423, 0.104, 0.027, 0.009 (0.562) | 0.242, 0.037, 0.011, 0.003 (0.292) |
| (3) magnitude swap | 0.339, 0.075, 0.017, 0.004 (0.435) | 0.190, 0.024, 0.006, 0.002 (0.222) |
| (4) phase swap | 0.587, 0.436, 0.229, 0.098 (1.350) | 0.224, 0.127, 0.063, 0.026 (0.439) |
| Shift-and-add at the true shifts, 150 frames, unrestored | (1.427) | (0.512) |
| 5a, the same restored with its own 2-D transfer | 0.362, 0.072, 0.011, 0.002 (0.446) | 0.211, 0.033, 0.007, 0.002 (0.253) |
| 5b, multi-frame Wiener, 150 frames | 0.346, 0.070, 0.011, 0.002 (0.429) | 0.206, 0.033, 0.007, 0.002 (0.248) |
| 5a, 3,000 frames | (0.234) | (0.143) |
| 5b, 3,000 frames | (0.199) | (0.138) |

- The gains, bands 1 to 4: one band at a time 2.03, 1.52, 1.20, 1.06 on the calibrated twin and 1.21, 1.12, 1.05, 1.02 without the still layer; fitted jointly 2.12, 2.45, 1.03, 1.10 and 1.28, 1.16, 1.05, 1.04.
- The limb's undershoot: the truth itself 0.0000, every filter on the stack at most 0.0021, the exact-kernel restorations 0.0025 to 0.0028 on the calibrated twin and 0.0006 without the still layer.
- **The claims, as pre-registered:**
  - **Multi-frame over shift-and-add, 150 frames: half holds, and the direction is the reverse of the prediction.** On the calibrated twin it lowers the error by 4.0 %, under the 10 % predicted. Without the still layer it lowers it by 2.1 %, where over 20 % was predicted. Over all 3,000 frames: 15.0 % and 3.3 %.
  - **Per-band gains within 10 % of the per-ring filter: it fails on the calibrated twin** (1.40 times its error) and holds without the still layer (1.04).
  - **The per-ring filter within 1.25 times the magnitude swap in bands 2 to 4 without the still layer, more with it: it fails.** 1.61 without and 1.45 with: no smaller without the still layer.
- **What it found:**
  - **A trous bands overlap, so one gain per band must be fitted JOINTLY.** Fitted one band at a time, each band's least-squares gain ignores what its neighbours' gains put into it, and on the calibrated twin it leaves 40 % more error than the best isotropic filter. Fitted jointly, post hoc, the same four gains come within 4 % of it, and beat it without the still layer (0.282 against 0.292, the bands being read inside 0.9 radii where the filter is fitted over the whole frame). A band is narrow enough; the fitting was what fell short.
  - **The magnitude swap is not a phase test here.** It also takes the noise out of every magnitude, and in bands 2 to 4 the error left is mostly noise, so the swap beats any filter on both twins alike. The phase swap barely moves the stack's error (1.415 to 1.350, and 0.511 to 0.439): the stack's error is in its magnitudes.
  - **The exact 2-D kernel is worth 21 % with the still layer and 13 % without.** 5a's sum of frames at the true shifts is as blurred as the stacker's stack (1.427 against 1.415, and 0.512 against 0.511), and restored with its own 2-D complex transfer it leaves 0.446 against the best isotropic real filter's 0.562 (0.253 against 0.292). That gap is what an isotropic real gain cannot reach: the kernel's anisotropy and phase, the still layer's part of it the larger.
  - **Weighting each frame per frequency adds little; more frames with a known kernel add the most.** On the stack's 150 frames the multi-frame Wiener gains 4.0 % and 2.1 %. With all 3,000 frames and the kernels exact, shift-and-add restored leaves 0.234 and 0.143, about half the 150 frames' error, and the per-frequency weighting then gains 15 % with the still layer against 3 % without (its frames, it seems, differing more at the fine scales; not measured). The plan's falsifier, a large multi-frame gap on the calibrated twin, does not fire on the kept frames: multi-frame blind deconvolution is not worth building on this evidence (R9), while the frames the selection discards hold signal a known kernel could use.
- **What it leaves R8 part 2**: the ceiling for the derived per-band gains is the jointly fitted oracle (0.586 and 0.282), close to the per-ring filter. The derived gains are therefore fitted jointly over the bands, to the Wiener filter the measured blur and noise give, never band by band. The three regularised inverses of #1085 come next, scored the same way.

### R8 results, part 2: three regularised inverses at one band 3

**Measured** (2026-10-01) with `tianwen planetary-inverses` (`PlanetaryInverse.WienerPowerLaw`, `RichardsonLucy`, `L1L2`), on R7 part 4's stacks (the best 150 of 3,000 by the gradient) in a 256 px window about the disk.
- **The kernels.** (b') is the limb's core with the scatter's wing over the pupil's diffraction: a 1.08 px core with 83 % in a 1.44 px wing on the calibrated twin, 0.41 px with 77 % in 0.64 px without the still layer, 1.58 px with 79 % in 1.18 px on the real capture. The single Gaussian of its equivalent width over bands 1 to 4 is 2.13, 0.61 and 2.35 px.
- **The knobs that met band 3 = 1.00** on the twins: the Wiener's noise scale 1,810 and 297, Richardson-Lucy's 3 and 2 steps, L1-L2's mu 9.99 and 3.43. The calibrated twin's mu sits at the top of its range (10), where band 3 reads 1.004. On the real capture, at the calibrated twin's rise of band 3 over the stack (1.227): 579, 4 steps and 2.2.

Band error against the truth, bands 1 to 4, and the sum; and the limb's undershoot (the calibrated twin / the one without its still layer):

| | Calibrated twin | Undershoot | Without the still layer | Undershoot |
|---|---|---|---|---|
| The stack | 0.651, 0.439, 0.228, 0.097 (1.415) | 0.0000 | 0.293, 0.130, 0.061, 0.026 (0.511) | 0.0000 |
| Part 1's per-band gains fitted jointly | (0.586) | | (0.282) | |
| Wiener with the power law, (b') | 0.908, 0.586, 0.113, 0.043 (1.650) | 0.0077 | 0.712, 0.281, 0.030, 0.011 (1.035) | 0.0018 |
| Richardson-Lucy at the sky, (b') | 0.540, 0.221, 0.058, 0.041 (0.861) | 0.0005 | 0.243, 0.039, 0.011, 0.009 (0.302) | 0.0003 |
| L1-L2, (b') | 0.810, 0.363, 0.060, 0.041 (1.274) | 0.0000 | 0.362, 0.077, 0.014, 0.011 (0.464) | 0.0000 |
| Wiener, Gaussian on band 3 (post hoc) | 0.933, 0.630, 0.106, 0.027 (1.694) | 0.0003 | 0.763, 0.272, 0.024, 0.009 (1.068) | 0.0000 |
| Richardson-Lucy, Gaussian on band 3 (post hoc) | 0.640, 0.401, 0.109, 0.020 (1.170) | 0.0000 | 0.258, 0.063, 0.020, 0.008 (0.350) | 0.0000 |

- The transfers with (b'), bands 1 to 4: the Wiener 0.131, 0.589, 1.000, 1.039 and 0.358, 0.824, 1.000, 1.010; Richardson-Lucy 0.543, 0.875, 1.009, 1.021 and 0.992, 1.010, 1.003, 1.006; L1-L2 0.316, 0.772, 1.004, 1.038 and 0.795, 0.959, 1.000, 1.009.
- The real capture, its rise over the stack in bands 1 to 4 and its undershoot, with (b'): the Wiener 0.939, 1.362, 1.227, 1.087 (0.0054); Richardson-Lucy 1.397, 1.422, 1.229, 1.078 (0.0008); L1-L2 1.567, 1.538, 1.227, 1.086 (0.0001). The stack's own undershoot is 0.0000.
- **The claims, as pre-registered:**
  - **Richardson-Lucy and L1-L2 against the Wiener with (b'): it holds on both twins.** Richardson-Lucy leaves 0.062 and 0.144 of the Wiener's undershoot (a third allowed) and L1-L2 0.000 on both (half allowed). The real capture orders them alike: 0.152 and 0.012.
  - **The single Gaussian rings 1.5 times more: not testable as registered.** A Gaussian of (b')'s equivalent width over bands 1 to 4 passes too much of band 3 to be set to it: at the end of each knob's range (the Wiener's noise scale at 1e-4, Richardson-Lucy at its 80 steps, L1-L2's mu at 1e-7) band 3 reads 0.95, 0.95 and 0.93 on the calibrated twin and 0.96 on the other, while band 1 has already reached 65, 1.1 and 6.1 times the truth on the calibrated twin. The ratios the verb printed compare restorations at different band 3 transfers and are not read.
- **Post hoc**, labelled as such: the Gaussian's width fitted on band 3 alone (`--gaussian-band 3`), 2.91 and 1.33 px, so that it can be set to band 3 at all.
  - The Wiener meets band 3 with it on both twins (noise scale 6,590 and 478), and Richardson-Lucy within a step (1.019 at 2 steps, 1.005 at 1). L1-L2 does not: 1.032 at the top of mu's range on the calibrated twin, 0.979 at the bottom on the other, where its 8 passes of 30 conjugate-gradient steps stop short while band 1 has reached 8.3 times the truth. Its rows are left out.
  - **The Gaussian rings LESS, not more.** Matched at band 3, the Wiener's undershoot is 0.0003 against (b')'s 0.0077, and 0.0000 against 0.0018; Richardson-Lucy's 0.0000 against 0.0005 and 0.0003. The prediction is reversed wherever the comparison is matched.
  - It costs error instead: Richardson-Lucy leaves 1.170 against (b')'s 0.861 and 0.350 against 0.302; the Wiener 1.694 against 1.650 and 1.068 against 1.035.
  - On the real capture (2.73 px) the Wiener meets the rise with an undershoot of 0.0038 against (b')'s 0.0054, Richardson-Lucy passes it at 3 steps (1.250) with none against 0.0008, and L1-L2 stops at 1.206 at the bottom of mu's range. Less again, by a smaller margin than on either twin.
- **What it found:**
  - **The order of claim 1 is positivity's.** Richardson-Lucy and L1-L2 both hold the sky at zero, and the trough R3's metric reads lies below the sky, which neither can go; the Wiener has no floor. R7 part 4's Richardson-Lucy with (b'), its floor a tenth of the disk above the sky, dug 0.0084, as deep as the Wiener here. This is a reading across two setups (another count, another band 3), not a measurement made for it.
  - **The trough itself is (b')'s wing, not a gain's ringing.** The wing's profile falls as the cube of the radius, so the kernel claims light far out that the stack's blur may not have, and an inverse puts it back on the disk by taking it off the sky beside the limb. The Gaussian has no such tail and, matched at band 3, its Wiener digs 0.0003 where (b')'s digs 0.0077; R7 part 4's (b), two Gaussians, dug nothing. Not measured with the wing alone, and on the real capture the Gaussian's Wiener still digs 0.70 of (b')'s, so there the wing is not the whole of it.
  - **One knob cannot hold band 3 and spare band 1.** The Wiener set to band 3 leaves MORE error than the stack it restores (1.650 against 1.415, and 1.035 against 0.511). (b') reads bands 2 to 4 about 5 % too blurred (R7 part 4), so the inverse would carry band 3 past the truth, and the noise scale that holds it down (297 to 1,810) cuts band 1 to 0.13 and 0.36 of the truth. L1-L2's mu does the same, less (0.32 and 0.80). Richardson-Lucy's count stops before the inverse is reached, lifts every band together, and is the only one of the three near the ceiling: 1.47 times the jointly fitted per-band oracle with the still layer, 1.07 without.
  - **A matched band 3 measures what each knob does to the bands it was not set on**, so it is a hard test for an inverse whose kernel is wrong at band 3: the Wiener's and L1-L2's knobs spend their range holding band 3 down, and band 1 pays.
- **What it leaves:** #1055's derived gains, fitted jointly over the bands (part 1), set each band's gain from that band's own transfer and noise, where a single scale on a power-law prior with a kernel too wide at band 3 did worse than no restoration at all. Whether they beat the presets is #1055's test, with Richardson-Lucy at the sky and (b') beside them as the inverse to beat (0.861 and 0.302), and a floor at the sky wherever the limb's undershoot is the gate.

### R8 results, part 3: the derived gains against the presets

**Measured** (2026-10-01) with `tianwen planetary-gains` (`PlanetaryWaveletGains`) on R7 part 4's stacks (the best 150 of 3,000 by the gradient) and their halves (every other frame, each half its own best 75), in a 256 px window about the disk.
- **The halves' noise** at 0.125 to 0.375 cycles a pixel is 0.80, 0.43 and 0.94 of the white floor read past 0.4 (the calibrated twin, the one without its still layer, the real capture). Without the still layer that floor still holds signal (the stack's band 1 transfer is 0.78).
- **The presets at matched noise** were set to the noise the derived gains with (b') leave on the halves' half difference. On the calibrated twin that is ten times the stack's (0.0186 of the disk against 0.0019), and `Bandpass` and `Combo` reach the bisection's bound, three times their boost, short of it.

Band error against the truth, bands 1 to 4, summed, and the limb's undershoot (the calibrated twin / the one without its still layer):

| | Calibrated twin | Undershoot | Without the still layer | Undershoot |
|---|---|---|---|---|
| The stack | 1.415 | 0.0000 | 0.511 | 0.0000 |
| Part 1's per-band gains fitted jointly to the truth | 0.586 | 0.0022 | 0.282 | 0.0002 |
| Derived, the twin's true kernel | 0.649 (3.15, 1.98, 1.14, 1.10) | 0.0023 | 0.289 (1.34, 1.10, 1.11, 1.01) | 0.0000 |
| Derived, (b') | 2.659 (11.35, -1.50, 1.76, 1.07) | 0.0043 | 0.337 (1.56, 1.07, 1.06, 1.05) | 0.0014 |
| Derived, (b'), a white noise | 2.565 | 0.0049 | 0.306 | 0.0019 |
| Derived, (b'), the finest layer held at 1 (post hoc) | 1.116 (1, 5.67, -0.74, 1.69) | 0.0211 | 0.336 (1, 1.49, 0.91, 1.09) | 0.0027 |
| `PlanetaryDefault` as shipped | 2.872 | 0.2289 | 3.683 | 0.3156 |
| `Bandpass` as shipped | 2.351 | 0.1675 | 2.712 | 0.2330 |
| `Combo` as shipped | 2.295 | 0.1565 | 2.779 | 0.2395 |
| `PlanetaryDefault` at matched noise | 12.145 (boost x2.35) | 0.8920 | 0.829 (x0.13) | 0.0361 |
| `Bandpass` at matched noise | 9.722 (x3, the bound) | 0.8516 | 3.483 (x0.80) | 0.2660 |
| `Combo` at matched noise | 11.090 (x3, the bound) | 0.8510 | 1.368 (x0.28) | 0.0792 |

- With (b') on the calibrated twin the derived gains' band errors are 2.078, 0.497, 0.057, 0.027: band 1 is restored to 2.13 times the truth.
- **The real capture**, its rise over the stack in bands 1 to 4 and its undershoot:
  - derived with (b'): gains 19.06, -2.97, 1.15, 1.24, rise 6.52, 1.84, 1.22, 1.08, undershoot 0.0122;
  - the finest layer held at 1 (post hoc): 1, 9.24, -3.00, 2.25, rise 2.69, 1.81, 1.17, 1.13, undershoot 0.0369;
  - the presets as shipped rise 0.87 to 1.14 in band 1 and 1.55 to 1.93 in bands 2 and 3, with undershoots of 0.16 to 0.23 of the disk.
  - **AutoStakkert's own sharpening** (its 8 % stack of the same capture, drizzled 1.5 times, and the `_conv` copy it sharpened) rises 3.69, 2.13, 1.37 and 1.10 over its stack. Those bands are in its pixels, 1.5 times finer than ours. Its undershoot is 0.0000. Its sky is not clipped (median 125 counts in both), so this is no trough hidden at zero. No RegiStax result of this capture is in the corpus.
- **The claims, as pre-registered:**
  - **The derived gains with (b') beat every preset at matched noise and as shipped: it holds without the still layer and fails on the calibrated twin.**
    - Without it, the presets leave 8.1 to 10.9 times its error as shipped and 2.5 to 10.4 times at matched noise.
    - On the calibrated twin, `Bandpass` and `Combo` as shipped leave 0.88 and 0.86 of its error, and `PlanetaryDefault` 1.08. At matched noise it beats all three, but only at ten times the stack's noise, a point two of the presets cannot reach.
  - **The ringing gate: it fails on both twins.** The undershoot is 0.0043 against 0.0028, and 0.0014 against 0.0010.
  - **Within 10 % of the jointly fitted oracle with the true kernel: it holds without the still layer (1.026) and misses by a hair on the calibrated twin (1.109).**
  - **Within 25 % with (b'): it holds without the still layer (1.196) and fails on the calibrated twin (4.54)**, the miss in band 1 as predicted, but far larger.
  - **The kill line, a preset winning at matched noise, does not fire.** A white noise gives slightly LESS error than the halves' coloured one (2.565 against 2.659, 0.306 against 0.337), so the noise's colour is not the missing term.
- **What it found:**
  - **The derivation is right when the kernel is.** With the twin's true kernel the gains come within 3 and 11 % of the jointly fitted oracle, ring no more than it, and leave a quarter and a tenth of the best preset's error as shipped.
  - **The missing term is the kernel's finest bands, the one R7 part 4 named.** (b')'s band 1 transfer is its model's tail (0.05 at 0.3 cycles a pixel, where the twin's is 0.28), so the Wiener filter there is about twenty, and the finest gain came out at 11 on the calibrated twin and 19 on the real capture.
    - Held at 1 after the fact, the over-restoration moves to the next layer. The gains swing (5.67 then -0.74, and 9.24 then -3.00), and the limb pays: five times the undershoot.
    - Without the still layer, (b')'s core is narrow enough that its finest bands are near the truth, and everything works.
  - **The presets over-sharpen bands 3 and 4 and under-restore band 1 at once**: transfers of 1.3 to 2.0 in band 3 and 0.17 to 0.63 in band 1. They ring a sixth to a third of the disk's brightness below the sky (R3's finding again), forty to two hundred times the derived gains'.
  - **AutoStakkert's sharpening lifts the fine bands with no trough at the limb at all.** How is not known here; its rises are on another grid and no truth judges them.
- **What it leaves:** R8's question is answered. Gains derived from the blur and the noise reach the per-band ceiling given the kernel, and the limb's kernel is not enough for the finest bands of a capture with a still layer. #817's AUTO mode cannot adopt them with (b') as it is. The kernel's fine bands are the measurement still open (#1120, a near-point probe).
- **R9's condition, read against this** (the plan's: "a measured gap to the RL oracle that a classical method cannot close"): **not met on this evidence.** The gap measured here is the kernel's, which a measurement could close, and with the true kernel a classical per-band filter comes within 3 to 11 % of its ceiling. A learned stage would have to learn the kernel's fine bands, the very thing no edge in these captures measures. This is the reading, not a decision: #1056 is the user's to close.

### R8 follow-ups: the ring and the finest band

R8 left two problems: the limb's kernel is its model's tail in the finest band, and sharpening digs a ring below the sky. A fourth literature review (`docs/architecture/planetary-literature/fine-band-and-ringing.md`, the user's request, 2026-10-01) found ways at both. They are taken as four steps, in order, each pre-registered when it starts, one branch and one PR each.

#### R8 follow-up 1: the ring rule, and AutoStakkert's kernel

**Issue:** #1137.

- **Why:** a non-negative unit-sum kernel has a transfer of at most one at every frequency, so a restoration whose end-to-end transfer against the truth exceeds one anywhere has a composite kernel with a negative lobe, which over a bright disk on a dark sky is a ring (theme D). AutoStakkert's own sharpening of the same capture lifts the fine bands with no ring, and how is not documented.
- **Added 2026-10-01, before it was built or measured** (`tianwen planetary-ringing`):
  - **The restorations**, on R7 part 4's stacks of both twins (the best 150 of 3,000 by the gradient, a 256 px window):
    - the three presets as shipped, and with their thresholds dropped at a quarter, a half and all of their boost;
    - R8's derived gains with (b') and with the twin's true kernel, and part 1's jointly fitted oracle;
    - the Wiener filter with Conan's prior and (b') at three noise scales a decade apart, the middle one part 2's;
    - Richardson-Lucy at the sky with (b') at 2, 4 and 8 steps;
    - AutoStakkert's kernel (below), carried to our grid.
  - **For each**: the band transfers against the truth in bands 1 to 6 and the largest of them; for a linear one, the composite kernel (its filter times the stack's true transfer against the truth, taken to the image) and its negative mass, the share of its sum below zero; and R3's limb undershoot.
  - **AutoStakkert's kernel**: its 8 % stack of 2022-09-03's Red and the `_conv` copy it sharpened (both 1.5 times drizzled), the shift-invariant kernel up to 15 by 15 taps that takes one to the other by least squares, and the residual's RMS inside 0.9 radii, over 0.9 to 1.3 radii and in the sky past 1.5. On our grid its transfer is read at two thirds of each frequency.
  - **The claims:**
    - Every restoration whose undershoot exceeds 0.003 has a largest band transfer above 1.02, and every one whose largest band transfer is at most 1.00 has an undershoot of at most 0.001.
    - Over the linear restorations, the composite's negative mass ranks the undershoots with a Spearman of at least 0.8.
    - AutoStakkert's sharpening is a linear, shift-invariant filter: the residual in the limb annulus and in the sky is at most twice the interior's. Carried to our twins it lifts no band above 1.1 against the truth and digs at most 0.002.
    - Falsified, for the last: a residual concentrated at the limb or in the sky, a mask or a clamp rather than a kernel.
  - **Clarified while it was built, before anything was measured** (`PlanetaryKernelFitTests`): the negative mass is read within 15 px of the composite kernel's centre, the reach of the ring R3 reads (1.0 to 1.3 radii). Over the whole grid, the faint ripple a hard cut in a transfer leaves far out grows with the area: a Gaussian restored to a narrower Gaussian and cut where the blur passes a thousandth read 1.26, more than its whole sum. The stack's true transfer itself is cut where the truth holds no power.

##### R8 follow-up 1 results

**Measured** (2026-10-01) with `tianwen planetary-ringing` (`PlanetaryKernelFit`) on both twins' stacks: 23 restorations each, 46 in all, 34 of them linear.

| Restoration (the calibrated twin / the one without its still layer) | Largest band transfer | Undershoot |
|---|---|---|
| The stack | 0.999 / 1.000 | 0.0000 / 0.0000 |
| The presets as shipped | 1.36 to 1.62 / 1.65 to 2.00 | 0.157 to 0.229 / 0.233 to 0.316 |
| The presets at a quarter of their boost, no thresholds | 1.10 to 1.17 / 1.29 to 1.52 | 0.029 to 0.047 / 0.067 to 0.098 |
| The presets at their full boost, no thresholds | 1.92 to 2.23 / 2.29 to 3.49 | 0.245 to 0.334 / 0.342 to 0.498 |
| R8's derived gains with (b') | 2.133 / 1.048 | 0.0041 / 0.0013 |
| R8's derived gains with the true kernel | 1.031 / 1.005 | 0.0025 / 0.0000 |
| The jointly fitted oracle | 1.014 / 1.004 | 0.0022 / 0.0002 |
| Wiener with Conan's prior and (b'), part 2's noise scale and a decade either side | 1.045 to 1.047 / 1.013 | 0.0077 to 0.0156 / 0.0018 to 0.0066 |
| Richardson-Lucy at the sky with (b'), 2, 4 and 8 steps | 1.028 to 1.033 / 1.010 to 1.075 | 0.0003 to 0.0006 / 0.0003 to 0.0008 |
| AutoStakkert's kernel, carried by 1.50 | 1.753 / 3.895 | 0.0000 / 0.1208 |
| AutoStakkert's transfer measured ring by ring, carried (post hoc) | 1.095 / 1.759 | 0.0001 / 0.0728 |

- **The claims, as pre-registered:**
  - **A ring only with a band transfer above one: it fails, on one restoration of 46.** The Wiener filter at ten times part 2's noise scale on the twin without its still layer digs 0.0066 with no band above 1.013 (its bands run 0.12, 0.52, 0.94, 1.00, 1.01, 1.01). Every other ring deeper than 0.003 comes with a band above 1.02, and both restorations whose largest band is at most 1.00 (the stacks) dig nothing.
  - **The composite's negative mass ranks the undershoots: it fails, and the measure is what failed.** The Spearman over the 34 linear restorations is 0.34, but the stack's own composite, its true blur, which cannot ring, reads 0.11 and 0.88: the stack's transfer measured against the truth is noisy ring to ring in the finest bands, and that noise, not the filter, sets the negative mass.
  - **AutoStakkert's sharpening a linear, shift-invariant filter: by the threshold, no** (the residual over 0.9 to 1.3 radii is 2.06 times the interior's, against two allowed), **and the kernel is not its filter where it matters.** Its stack holds almost no signal in the finest frequencies of its 1.5 times drizzled grid, so the 15 by 15 kernel is free there: a transfer of 9.8 at 0.3 cycles a pixel of its grid and a negative mass of 10.6, numbers of the fit, not of AutoStakkert. Carried to our twins it lifts band 1 to 1.75 and 3.90 times the truth.
- **Post hoc**, labelled as such: AutoStakkert's filter read ring by ring against its own stack (cross spectrum over the stack's power), defined only where the stack holds signal, up to about 0.15 cycles a pixel of its grid (0.23 of ours).
  - There it lifts 1.49 at 0.05 and 3.16 at 0.1; past that the stack holds nothing to read it by, and it is taken as one.
  - The kernel explains the sharpened copy to 1.2 % of its spread inside the disk, 2.4 % over the limb annulus and 0.5 % in the sky: nearly linear, a little less so at the limb.
  - Carried to the calibrated twin, AutoStakkert's measured filter lifts no band past 1.095 and digs 0.0001. Carried to the twin without its still layer, whose stack is already sharp, it lifts 1.61 and 1.76 and digs 0.073.
  - **So AutoStakkert has no ring cure of its own.** Its fixed sharpening suits this capture's blur, which the calibrated twin copies, and stops near the truth; on a sharper stack the same filter rings like the presets.
- **What it found:**
  - **The ring rule holds one way only.** A linear filter that lifts a band past the truth rings, every time here, and the deeper the further past (the presets from a quarter to their full boost: 0.03 to 0.50 of the disk). But a steep cut rings too without lifting anything: a Wiener filter held hard at band 3 is a steep low-pass against the truth, and its Gibbs lobe digs as deep as a sharpening's. On the calibrated twin its coarse bands sit at 1.045, so the claim is met there, but its ring deepens with the cut (0.0077 to 0.0156) while that lift stays where it is. So the fix is the composite kernel held non-negative, as follow-up 2 plans (Magain), not a transfer held under one, which would pass the Wiener's ring.
  - **A floor at the sky beats both.** Richardson-Lucy at the sky lifts band 1 to 1.075 on the twin without its still layer and digs at most 0.0008: positivity clips the negative lobe whatever made it.
  - **The negative mass needs a clean blur.** Read through the stack's measured transfer it measures that transfer's noise; follow-up 2 reads the composite through the twins' true kernels instead (their per-frame PSFs, R8 part 1).
  - **The derived gains with the true kernel ring no more than the oracle** (0.0025 against 0.0022, and none), their largest band 1.031 and 1.005: restoring to the truth and no further is ring-free in practice.

#### R8 follow-up 2: the ring fixed

**Issue:** #1138.

- **Planned:** R8's derived gains fitted under the constraint that the composite kernel toward the pupil's diffraction PSF stays non-negative (Magain, Courbin and Sohy 1998), a small quadratic program, the composite read through the twins' true kernels (their per-frame PSFs), never through a transfer measured against the truth, whose noise follow-up 1 found sets the negative mass; a non-negative composite, not a transfer held under one, which a steep Wiener cut rings past (follow-up 1); the limb fit's disk as its own channel with only the residual sharpened (Lucy 1994, Yuan et al. 2007); PlanetFlow's per-layer weights feathered to zero at the limb. Each against the presets as shipped and at matched noise, on both twins and the real capture. Pre-registered when it starts.
- **Added 2026-10-01, before it was built or measured** (`tianwen planetary-dering`):
  - **The stacks**: R7 part 4's on both twins (scored against their truths) and on 2022-09-03's Red, in a 256 px window.
  - **The sharpenings** each fix is put on: the three presets (their gains and thresholds, the thresholds carried from the master's units to the window's, where the sky is zero and the disk one), and R8's derived gains with the twin's true kernel and with (b'). The presets as shipped, sharpened on the master and clamped at its zero as the stacker does, are reported beside them.
  - **The four fixes**:
    1. **A floor at the sky**: the sharpened plane held at or above the sky's level (zero, normalised), the floor Richardson-Lucy and L1-L2 kept in part 2.
    2. **The limb as its own channel** (Lucy 1994, Yuan et al. 2007): the limb fit's sharp model through (b') taken from the stack, only the residual sharpened, and the sharp model added back through the pupil's diffraction alone, which is non-negative.
    3. **Gains feathered at the limb** (PlanetFlow): each layer's boost scaled by a weight that is one inside the disk and falls linearly to zero at the limb over 2^(j+1) px for layer j, so the sky is the stack's own.
    4. **A non-negative composite** (Magain, Courbin and Sohy 1998): R8's derived gains refitted under the constraint that their filter times the kernel, taken to the image, is at or above zero within 15 px, a quadratic program over the four gains; with the true kernel on the twins and with (b') everywhere.
  - **The true kernel**, on the twins, is the stack's transfer read ring by ring against the truth where the truth holds at least a thousandth of its power at the second ring, smoothed over five rings: follow-up 1 found the raw reading's ring-to-ring noise set the negative mass (the stack's own blur read 0.88). The per-frame PSFs the plan named register at shifts the stacker does not report. **A control**: through it, the stack's own composite reads a negative mass of at most 0.01, or the composite is not read.
  - **Scored**: R3's band error in bands 1 to 4 inside 0.9 radii, the limb's undershoot, and the limb profile's error against the truth over 0.8 to 1.2 radii (a rind left inside the limb is caught there, where the undershoot cannot see it).
  - **The claims:**
    - The floor takes every undershoot to at most 0.001, leaves the band errors inside 0.9 radii unchanged within 1 %, and cuts the presets' limb profile error by at least a third.
    - The limb channel takes the presets' undershoot from 0.16 to 0.32 of the disk to under 0.02 and halves their limb profile error, with the band errors inside 0.9 radii within 5 %.
    - The feathered gains take every undershoot under 0.01, at a cost of at most 10 % in bands 1 and 2 inside 0.9 radii.
    - The non-negative composite with the true kernel digs at most 0.001 and costs at most 5 % of the derived gains' summed error. With (b') on the calibrated twin it leaves the finest gain within 20 % of the unconstrained 11.35: a constraint on a composite is only as good as its kernel, and (b')'s finest band is its model's tail.
    - On the real capture, every fix's undershoot and its rise over the stack per band are reported beside the presets as shipped.

##### R8 follow-up 2 results

**Measured** (2026-10-01) with `tianwen planetary-dering` (`PlanetaryDering`, `PlanetaryWaveletGains.FitNonNegative`, the preparation shared with `planetary-gains` in `PlanetaryWindowedStack`, which reproduces R8's numbers exactly). A master unit is 5.4 to 5.5 disks, so the presets' thresholds were carried by that.

Band error summed over bands 1 to 4 inside 0.9 radii, the limb's undershoot, and the limb profile's error over 0.8 to 1.2 radii (the calibrated twin / the one without its still layer):

| | Error | Undershoot | Limb profile error |
|---|---|---|---|
| The stack | 1.415 / 0.511 | 0 / 0 | 0.031 / 0.009 |
| `PlanetaryDefault` as shipped (the master, clamped at its zero) | 2.872 / 3.683 | 0.229 / 0.316 | 0.152 / 0.197 |
| `PlanetaryDefault` plain, in the window | 3.288 / 4.342 | 0.254 / 0.375 | 0.174 / 0.231 |
| floored at the sky | 2.975 / 3.829 | 0.0017 / 0.0016 | 0.080 / 0.101 |
| the limb as its own channel | 2.335 / 3.173 | 0.0145 / 0.0055 | 0.012 / 0.004 |
| feathered at the limb | 2.611 / 3.433 | 0 / 0 | 0.059 / 0.069 |
| `Bandpass` plain / floored / channel / feathered | 2.983, 2.756, 2.146, 2.372 / 3.624, 3.230, 2.592, 2.777 | 0.204, 0.0001, 0.0152, 0 / 0.284, 0.0001, 0.0058, 0 | 0.152, 0.086, 0.013, 0.054 / 0.200, 0.106, 0.003, 0.060 |
| `Combo` plain / floored / channel / feathered | 2.859, 2.636, 2.113, 2.283 / 3.639, 3.247, 2.694, 2.857 | 0.186, 0.0001, 0.0142, 0 / 0.285, 0.0001, 0.0054, 0 | 0.141, 0.079, 0.013, 0.053 / 0.190, 0.101, 0.003, 0.060 |
| R8's derived gains with (b'), plain | 2.659 / 0.337 | 0.0043 / 0.0014 | 0.015 / 0.003 |
| the same under a non-negative composite through (b') | 0.713 / 1.071 | 0.0103 / 0.0004 | 0.011 / 0.012 |

- The non-negative composite through (b') took the gains to 3.01, 1.60, 1.26, 1.28 on the calibrated twin (from 11.35, -1.50, 1.76, 1.07) and to 0.01, 1.14, 1.02, 1.04 without the still layer (from 1.56, 1.07, 1.06, 1.05).
- **The real capture** (rise over the stack in bands 1 to 4; undershoot): `PlanetaryDefault` plain 1.28, 2.15, 2.16, 1.66 (0.260), floored (0.0020), as a limb channel 1.10, 1.75, 1.58, 1.17 (0.0117), feathered 1.20, 1.91, 1.74, 1.38 (0); `Bandpass` and `Combo` alike (0.19 to 0.21 plain, at most 0.0004 floored, 0.011 to 0.012 as a channel, 0 feathered). R8's derived gains with (b') 6.52, 1.84, 1.22, 1.08 (0.0122); under a non-negative composite, its finest gain 19.06 down to 6.25, 2.55, 1.23, 1.07, 1.03 (0).
- **The claims, as pre-registered:**
  - **The floor:** every undershoot at most 0.001 fails, on `PlanetaryDefault` only (0.0017, 0.0016): what is left is the sky's own noise, amplified 4.8 times and half clipped, raising its mean, not a ring. The band errors within 1 % fails in the good direction: they FELL 7 to 12 %, because a ring outside the disk reaches the coarse bands inside it. The presets' limb profile error cut by a third holds (by half).
  - **The limb as its own channel:** the presets' undershoot under 0.02 holds (0.005 to 0.015; 0.011 to 0.012 on the real capture). Their limb profile error halved holds: 11 to 64 times smaller. The band errors within 5 % fails in the good direction: 26 to 29 % lower.
  - **The feathered gains:** both hold. No undershoot anywhere, and bands 1 and 2 no worse (better).
  - **The non-negative composite with the true kernel: not read.** The control failed: through the true kernel as registered, the stack's own composite has a negative mass of 0.31 and 0.50. The registered cut, where the truth holds under a thousandth of its second ring's power, zeroes every ring past the mid bands on Jupiter, and with it the derived gains' bands 1 and 2 (2.170 summed, against R8's 0.649).
  - **With (b') on the calibrated twin, the finest gain within 20 % of the unconstrained 11.35: it fails, and the constraint did what was not predicted.** It took the finest gain to 3.01 and the summed error from 2.659 to 0.713, within 22 % of the jointly fitted oracle (0.586) and below part 2's Richardson-Lucy at the sky (0.861): the best result yet with a kernel the capture itself gives. The composite reached the finest layer through its leakage into the bands (b') does measure.
- **Post hoc**, labelled as such: the true kernel with R8's cut (`--true-floor 1e-6`). The derived gains reproduce R8 (0.648 and 0.289), but the control still fails (0.11 and 0.89): smoothed or not, a transfer measured against the truth has lobes of its own, and gains held so the composite stays non-negative through it fight those lobes (0.648 to 2.036, 0.289 to 1.167). The constraint needs a smooth kernel; (b') is one.
- **What it found:**
  - **The limb as its own channel is the fix for the presets.** It removes the ring (under 0.015 of the disk), makes the limb's profile 11 to 64 times truer, and lowers the error inside the disk by about a quarter, because the limb's step was what the coarse bands carried inward. Its residual ring, a twentieth to a seventieth of a preset's, is most likely the limb model's misfit (not measured apart).
  - **Feathered gains remove the ring entirely and cost nothing inside**, but leave the limb's profile five to twenty times worse than the channel does: the limb is simply not sharpened. **The floor** removes the trough below the sky but keeps the bright rind inside the limb (a limb profile error of 0.08 to 0.10).
  - **A non-negative composite through a smooth model kernel rescues the derived gains where the kernel's finest band is wrong** (11.35 to 3.01, 2.659 to 0.713), and **over-constrains where the kernel is narrow**: a narrow kernel leaves no room for any finest-layer gain above one (the a trous detail is a zero-sum kernel), so on the twin without its still layer it removed the layer (0.337 to 1.071). Restoring toward a target a little wider than the truth (Magain's target PSF), rather than the truth itself, is the relaxation it needs.
  - **Through a measured kernel, a composite cannot be held non-negative usefully**: its own lobes set the constraint.
- **What it leaves:** the limb channel is ready to be put on the stacker's sharpening, beside the presets the user tuned (#1143, the user's call). The non-negative composite toward a target a little wider than the truth goes into follow-up 3, with the kernel it measures.

#### R8 follow-up 3: the finest band measured

**Issue:** #1139.

- **Planned:** three ways to the kernel's finest band, each judged against the twins' true transfer at 0.3 cycles a pixel:
  - a physical kernel built from what is known (the obstructed pupil, the pixel, the resampling kernel, the warp the alignment points leave, the air's r0 from the spectral ratio), with only a few static aberration terms and the halo fitted to the limb, in Fourier space;
  - a direct, oversampled edge profile over the limb, its albedo flattened by the stack's zonal mean;
  - the stack's spectrum against an OPAL map of another year at the capture's geometry, the object's slope supplied, never fitted (Fetick et al. 2020).
- Then R8's derived gains with the best of them. Pre-registered when it starts.
- **Pre-registered 2026-10-02, before anything was built or measured** (`tianwen planetary-finest-band`), part 1, the two direct reads; the physical kernel (a) and the gains with the best of the three are part 2, pre-registered when it starts:
  - **The stacks**: R7 part 4's (the best 150 of 3,000 frames by the gradient, global, plain, Lanczos-3) of three twins, each against its truth: the calibrated twin (`red-3k-final`), the same without its still layer (`r7/nostill`) and the warped one (`r5/c065l10`, 0.65 px over 10 px). The real 2022-09-03 Red is read, not judged.
  - **The target**: the oracle, `PlanetaryInverse.Measure` of the stack against its truth, at 0.1, 0.2 and 0.3 cycles a pixel (R7 part 4 read 0.54, 0.37 and 0.28 on the calibrated twin, 0.50, 0.26 and 0.11 on the warped one). The truths are rendered through the pupil's diffraction, so every transfer here is over diffraction.
  - **(b) The edge, read directly**:
    - every pixel from 8 px inside to 8 px outside the limb fit's outline, except within 45 degrees of the side the phase darkens, where the terminator bends it; each pixel is divided by the stack's own zonal brightness at its latitude (`PlanetaryBelts.FromImage`, read inside 0.9 radii), so the belts do not move the edge;
    - binned by its signed distance from the outline at a tenth of a pixel, the bins differentiated into a line spread, and the same done to the limb fit's sharp model (`PlanetaryLimbFit.SharpModel`), the true edge;
    - the transfer is the line spread's Fourier transform over the model's, which for a round kernel is its radial transfer.
    - **Its self-check**: read on each twin's truth in place of the stack, it must give one within 0.05 from 0.1 to 0.3 cycles a pixel, or the sharp model's edge is not the truth's and (b) is not read.
  - **(c) The spectrum, against another year**:
    - the stack's ring power inside 0.9 radii (the interior only, R8 part 3's cosine taper), less its halves' noise power, over the power of the 2024d OPAL map in the same filter rendered at the capture's geometry through the pupil's diffraction; the transfer is the root of that ratio. The twins are of 2022b, so the object's spectrum is another year's, its slope supplied, never fitted (Fetick et al. 2020).
    - **Its kill line**: 2024d's own power over 2022b's, both rendered at the geometry with no blur, must stay within 20 % of one from 0.1 to 0.3 cycles a pixel, or a year's texture is not the object's spectrum and (c) is not read.
  - **Pass**: on each twin, (b) and (c) within 0.05 of the oracle at 0.2 and at 0.3 cycles a pixel, and nearer it than the limb's (b'), which read 0.05 at 0.3 against 0.28 on the calibrated twin.
  - **The real capture**: (b) is also read in two sectors, along R7a's axis (120 degrees on the sensor) and across it, to say how elongated that night's kernel is in band 1.
  - **Clarified while it was built, before anything was measured on a twin** (`PlanetaryFinestBandTests`, a limb-darkened disk of radius 50 blurred by a 1.2 px Gaussian):
    - **The edge is read 16 px either side, under a window flat over its middle half.** Read 8 px either side under a Hann window, as written, it read the Gaussian 0.033 high at 0.2 cycles a pixel: the limb darkening's slope inside the disk runs to the window's end. At 16 px it reads it within 0.006 at 0.1, 0.2 and 0.3.
    - **The spectrum is read on each plane less itself smoothed by a 6 px Gaussian.** The disk's own shape holds most of its power and leaks through the taper into every ring alike, which pulled a blurred texture's ratio toward one (0.398 against 0.321 at 0.2). High-passed, it reads within the two textures' own difference, 6 to 15 %, which is what another year's map can at best give.

##### R8 follow-up 3 results, part 1 (2026-10-02)

- **The reads** (`tianwen planetary-finest-band`, each stack the best 150 of 3,000 frames; transfers over the pupil's diffraction at 0.1, 0.2 and 0.3 cycles a pixel):

  | | the oracle | (b'), the limb fit's kernel | (b), the limb's edge | the edge's self-check | (c), the spectrum over 2024d |
  |---|---|---|---|---|---|
  | calibrated twin | 0.541, 0.372, 0.282 | 0.481, 0.182, 0.054 | 0.518, 0.320, **0.271** | holds (within 0.032) | 0.462, 0.346, 0.329 |
  | the twin without its still layer | 0.874, 0.808, 0.743 | 0.862, 0.747, 0.640 | 0.845, 0.748, **0.741** | holds (0.035) | 0.727, 0.752, 0.773 |
  | the warped twin | 0.498, 0.258, 0.116 | 0.419, 0.094, 0.011 | 0.470, 0.197, 0.149 | **fails** (0.117) | 0.424, 0.249, 0.127 |
  | the real 2022-09-03 Red | | 0.431, 0.082, 0.007 | 0.522, 0.266, 0.207 | | 0.486, 0.214, 0.107 |

- **(b), the edge, reads the finest band; by the letter it fails the claim.** At 0.3 cycles a pixel it reads the oracle within 0.011 and 0.002 on the two twins whose self-check holds (the limb's kernel reads 0.054 and 0.640 there, against 0.282 and 0.743). At 0.2 it reads 0.052 and 0.060 low, past the 0.05 claimed. On the warped twin its self-check fails (the truth's own edge reads 0.88 at 0.3 against the limb fit's model), so it is not read there: the limb fit of a warped stack is not that stack's edge.
- **(c), the spectrum, is not read: its kill line fires.** 2024d's texture power over 2022b's, both at the capture's geometry, is 1.33 from 0.10 to 0.15 cycles a pixel and 0.66 from 0.15 to 0.20; a year's belts are not another year's below 0.2 cycles a pixel.
  - Post hoc, not claimed: from 0.2 to 0.3 the two years agree within 5 % (1.005, 1.047), and there (c) reads 0.329, 0.773 and 0.127 at 0.3, each within 0.05 of the oracle, on all three twins. The finest band's texture is a year-free statistic where the coarse bands' is not.
- **The real capture's finest band is there.** Its edge reads 0.207 at 0.3 cycles a pixel where the limb fit's kernel said 0.007: R8 part 3 derived its gains through a finest band the kernel's tail had put at nothing (#1120). Read along R7a's axis and across it, the edge is lower along it at 0.1 and 0.2 (0.547 against 0.575, 0.275 against 0.306), as an elongated blur would be; at 0.3 the two sectors (0.157, 0.016) are a third of the limb each, one of them beside the terminator's arc, and do not agree with the whole (0.207): too noisy to read.
- **What it leaves**: part 2 takes the edge's transfer into R8's derived gains, beside the physical kernel (a), pre-registered when it starts.
- **Part 2 pre-registered 2026-10-02, before any of it was measured** (`planetary-finest-band` prints (a); `planetary-gains` derives with (b) and (a)):
  - **(a) The physical kernel**: a lucky stack's residual seeing as Fried's short-exposure transfer, exp(-A u^(5/3) (1 - u^(1/3))) with u the frequency over the cutoff D / lambda (0.942 cycles a pixel here), times a Gaussian for what the alignment leaves, beside a share of the light in a wide halo (`PhysicalKernel`). Its four numbers are fitted to the edge (b) from 0.02 to 0.35 cycles a pixel and carried on to the cutoff in that shape, where the edge is noise. Built and checked first: a kernel of this shape is fitted back within 0.01 to 0.45 cycles a pixel from an edge read only to 0.35.
  - **The stacks** as part 1's, and R8 part 3's gains (`PlanetaryWaveletGains.Fit`, the Wiener-weighted joint fit with the disk's own term), scored as R8 part 3 scores them: the band error summed over bands 1 to 4 inside 0.9 radii.
  - **Claims**:
    1. (a) reads the oracle within 0.05 at 0.2 and at 0.3 cycles a pixel on the calibrated twin and on the twin without its still layer.
    2. The gains derived with (a) come within 15 % of the same gains with the true kernel (R8 part 3: 0.648 and 0.289, read again in the same run) on both twins.
    3. On the real 2022-09-03 Red, the gains derived with (a) keep the finest gain under 3 (with (b') it was 6.52).
  - **Read, not claimed**: the gains with (b) itself, its noisy finest band clamped to [0, 1], and with (a) under a non-negative composite (follow-up 2's `FitNonNegative`).

##### R8 follow-up 3 results, part 2 (2026-10-02)

- **(a), the physical kernel fitted to the edge**, at 0.1, 0.2 and 0.3 cycles a pixel: 0.542, 0.315, 0.191 on the calibrated twin (the oracle 0.541, 0.372, 0.282), 0.833, 0.766, 0.669 without its still layer (0.874, 0.808, 0.743), 0.479, 0.245, 0.144 on the real capture. It spends its halo on the edge's shape (0.21, 0.14 and 0.35 of the light): Fried's transfer does not flatten as the edge does between 0.2 and 0.3 cycles a pixel (0.320 to 0.271 on the calibrated twin), and with its Gaussian at zero it cannot.
- **The gains** (R8 part 3's, the band error summed over bands 1 to 4; the calibrated twin / the one without its still layer):

  | derived through | error | over the true kernel's | the finest gain, twins / real |
  |---|---|---|---|
  | the stack itself | 1.415 / 0.511 | | |
  | (b'), the limb fit's kernel | 2.659 / 0.337 | 4.10 / 1.17 | 11.35, 1.56 / 19.06 |
  | (b), the limb's edge | 0.916 / 0.377 | 1.41 / 1.31 | 4.08, 1.49 / 5.07 |
  | (a), the physical kernel on the edge | 0.925 / 0.359 | 1.43 / 1.24 | 4.89, 1.49 / 5.78 |
  | (a) under a non-negative composite | 0.874 / 1.016 | 1.35 / 3.52 | 2.59, 0.02 / 3.25 |
  | the true kernel | 0.649 / 0.289 | 1 | 3.15, 1.34 |
  | per-band gains fitted jointly to the truth | 0.586 / 0.282 | | |

- **The claims, as pre-registered: all three fail.** (a) reads the oracle 0.06 to 0.09 low at 0.3 cycles a pixel (claimed within 0.05); the gains through it come within 43 % and 24 % of the true kernel's (claimed 15 %); the real capture's finest gain is 5.78 (claimed under 3).
- **What it did, though**: through the limb's edge, R8's derived gains IMPROVE the calibrated twin's stack (0.92 against its 1.415) where through the limb fit's kernel they made it nearly twice as bad (2.659), and the real capture's finest gain falls from 19 to 5. The edge reads the kernel where its pixels are many (to about 0.3 cycles a pixel); the gains lean on the kernel across all of band 1, to 0.5, where the edge is noise (the real capture's reads 0.325 at 0.4, above its 0.207 at 0.3) and the physical shape carries on from a fit that already missed.
- **What it leaves**: a kernel measured to the cutoff, not the 0.3 cycles a pixel a limb gives. That is what step 4 (#1140) is for: a near-point probe (a moon's shadow, #1120), a defocus burst, or a multi-frame blind deconvolution, each of which sees the kernel itself rather than an edge it was blurred over. Step 3 is done.

#### R8 follow-up 4: heavier probes, only if follow-up 3 falls short

**Issue:** #1140 (parts 1 to 3); the defocused burst, which needs a night, is a bench item, #1155.

- **Planned:** marginal multi-frame blind deconvolution with torchmfbd (MIT; PyTorch's support for the GTX 1070 first); a Galilean moon's shadow on the disk as a near-point probe (#1120 finds the captures); a short defocused burst after a capture, driven through the focuser, simulated on the twin first.
- **Pre-registered 2026-10-02, part 1, before the probe was built or anything was measured with it** (`tianwen planetary-finest-band --moons`): a Galilean moon BESIDE the disk, read as a near-point source. A shadow waits for a capture that has one; the blind deconvolution and the defocus burst are later parts.
  - **The moons are in 2022-09-03's field after all.**
    - Meeus' low-accuracy theory (Astronomical Algorithms, chapter 44; `GalileanMoons`, within 0.006 radii of his example 44.a) puts Europa 1.95 radii out at 12:11 UT, Ganymede 3.52 and Io 5.10. At 0.497"/px they are 2.15, 3.63 and 2.51 px across.
    - All four stacks show a compact source at each, 1.94, 3.49 and 5.10 radii out by the limb fit's 49 px radius.
    - So R7 part 4's "nothing past 1.6 radii stands more than 3 ADU over the sky" was wrong, and #1120's premise with it.
    - Their places also fix the image's north (about 154 degrees, not mirrored), where the limb fit gives only the axis. (Measured: the
      moons chose 159.3 degrees, the limb fit's own end, not mirrored; 154 was a rough reading of three peaks by eye.)
  - **Europa is the probe.** A uniform disk's transfer first reaches zero at 1.22 over its diameter: 0.57 cycles a pixel for Europa, past Nyquist, against 0.49 for Io and 0.34 for Ganymede. Io is read to 0.4 and Ganymede to 0.25, as checks.
  - **The read** (`PlanetaryMoonProbe`):
    - the stack, registered and normalised as the window is; a 33 px square about the moon, less a plane fitted to its rim between 13 and 16 px, under a round taper flat to 12 px;
    - the model: a uniform disk of the ephemeris' diameter, through the pupil's diffraction, smeared along the moon's track over the frames' span, of unit flux; its place is the cross-correlation's climbed peak (`PhaseCorrelation.ClimbPeak`);
    - the transfer in each ring: the cross-spectrum's real part over the model's power, over the read's own flux (its zero frequency). It is the kernel within 16 px, as the edge's is;
    - its noise: half the difference between the same read on the two half-stacks.
  - **The twins**:
    - step 3's three (calibrated, without its still layer, warped), made again with Europa (`planetary-degrade --moons 2.5`: every moon within 2.5 radii, at its ephemeris place as the frames go, in the frames and in the truth);
    - its surface brightness is set once, from the real stack's Europa flux, before any twin's transfer is read.
  - **The target**: the oracle, at 0.1, 0.2, 0.3, 0.4 and 0.45 cycles a pixel.
  - **Pass**: on each twin, Europa reads the oracle within 0.03 from 0.1 to 0.45 cycles a pixel, or within twice its halves' noise where that is larger. That is the band step 3 left: the edge is noise past 0.3.
  - **The real capture** (Red, the first 3,000 frames, as before):
    - Europa, Ganymede and Io agree within twice their noise where each reads (to 0.25), or the kernel is not the same 47" and 125" from the centre. The differential tilt a registration on the disk leaves grows with distance, and Europa, the nearest, would then be only a bound on the disk's kernel.
    - Europa past 0.3 is the measurement this part is for: read, not judged. Beside it, how far a limb darkening of mu^0.2 would move it.
  - Part 2, pre-registered when it starts: R8's gains through a kernel taken from the edge to 0.3 and from Europa past it.

##### R8 follow-up 4 results, part 1 (2026-10-02)

**Measured** with `planetary-finest-band --moons` on step 3's three twins made again with Europa (`planetary-degrade --moons 2.5
--moon-level 0.76`, the level from the real stack's Europa flux, 2.754 over its disk, set before any twin was read), and on the real
2022-09-03 Red, each the best 150 of the first 3,000 frames. The twins' frames are rendered on a grid twice as wide once a moon is
asked for (512 px rather than 256), so they are new twins, not step 3's with a moon added.

| twin | the oracle at 0.1, 0.2, 0.3, 0.4, 0.45 | Europa's read | its noise | its light in the square, of the 2.75 put in |
|---|---|---|---|---|
| calibrated | 0.541 0.357 0.283 0.225 0.201 | 0.790 0.491 0.387 0.415 0.365 | 0.004 to 0.030 | 1.85 (67 %) |
| without its still layer | 0.873 0.810 0.738 0.656 0.479 | 1.028 0.972 0.864 0.756 0.690 | 0.003 to 0.015 | 2.28 (83 %) |
| warped | 0.489 0.257 0.114 0.055 0.035 | 0.717 0.340 0.174 0.091 0.026 | 0.005 to 0.015 | 1.81 (66 %) |

- **The claims, as pre-registered: both fail.**
  - On every twin Europa reads above the oracle at every frequency, by far more than 0.03 or twice its noise: 0.25 too high at 0.1 on the
    calibrated twin.
  - On the real capture the three moons do not agree. At 0.2 cycles a pixel Europa reads 0.279, Ganymede 0.226 and Io 0.161, against
    noise of 0.004 or less, falling with the moon's distance from the disk. So Europa is only a bound on the disk's kernel, as the
    pre-registration said it would then be.
- **Why the twins fail: the read's normalisation, not its shape.**
  - The read is over its own flux, the light inside its square less the rim's plane. A halo puts light past the rim: the still layer's
    and the 5 % scatter's (a 5" core with an r^-3 tail).
  - The square holds 67 %, 83 % and 66 % of what each twin put in, and up to 0.3 cycles a pixel the read's excess over the oracle is
    about one over that: 1.37 to 1.46 on the calibrated twin, 1.17 to 1.20 without the still layer.
  - The pre-registration's "it is the kernel within 16 px, as the edge's is" was wrong. The edge's step is read over the disk, whose
    halo fills the line spread; a point's halo leaves the square.
- **Explored after the claims failed, not pre-registered** (`--moon-reach`, `--moon-quadratic`; the probe's self-check on the truth; a
  read in two sectors; the oracle registered by correlation):
  - **A wider square does not bring the halo back robustly.** At 24 and 32 px the rim lies on the planet's own glow, 14 px past the
    limb at 32 px, where neither a plane nor a quadratic surface fits. The truth's own read then holds 2.47 to 2.89 of the 2.75 put in,
    and one half stack's read went wild (noise 1.1). The 16 px plane is the stable read.
  - **Read on its truth, Europa gives 1.02, 1.00, 0.97, 0.93 and 0.91 at 0.1 to 0.45.** That is the twin's own rendering: a 2 px disk
    drawn on the render's half-pixel grid aliases its sharp edge into the finest band, which the probe's analytic disk does not.
  - **Over its truth's read, Europa's shape is the oracle's on the twin without a still layer.** The ratio to the oracle holds at 1.155
    to 1.18 from 0.1 to 0.4. With the still layer it climbs past 0.3, to about 2 at 0.4 on the calibrated twin (noise there 7 %).
  - **That is not a misregistered oracle.** Registered onto its truth by correlation rather than by the limb fit, every stack lay within
    0.02 px of where the limb fit put it and the oracle did not move by more than 0.002. What makes the still layer's moon differ past
    0.3 is open; its frozen phase screen's PSF is not round, and its phase is one candidate.
  - **Every stack's kernel is anisotropic, along the planet's own axes** (`AnElongatedKernelReadsItsTwoDirectionsInTheirSectors`).
    - In a sector 30 degrees about the planet's axis Europa reads higher than about its equator: at 0.2 cycles a pixel 0.568 against
      0.421 on the calibrated twin, 0.399 against 0.282 on the warped one, 0.303 against 0.233 on the real capture.
    - A frame is placed across the belts by the belts and along them only by the limb (R5 part 3), so the registration's error, part of
      the stack's kernel, is wider along the equator.
    - The oracle is read through the planet's own power, which the belts put across them, so it sees the axis's direction more than a
      moon's read, which weighs every direction alike.
- **The real capture, read post hoc** (`posthoc_tilt` in the scratch, not pre-registered):
  - Io and Ganymede over Europa fit one extra jitter whose variance grows as the square of the moon's distance from the disk's centre.
    Io's extra is 0.69 px^2 at both 0.2 and 0.3, and both moons give the same coefficient, 0.031 px^2 per radius^2.
  - That is tilt anisoplanatism: a layer high enough for 47" to decorrelate its tilt, which a registration on the disk leaves in
    everything off its centre, the limb included.
  - Carried in to the limb on that law, Europa reads 0.145, 0.091 and 0.077 at 0.3, 0.4 and 0.45, where step 3's physical kernel
    extrapolated 0.144, 0.096 and 0.084. Below 0.2 the two disagree, as the normalisation above says they must.
- **What it leaves**: a moon reads the kernel's SHAPE and its DIRECTIONS out to 0.45 cycles a pixel, but not its level, which its halo
  sets outside any square the planet's glow allows; and on a real capture it reads the kernel where it is, which is not where the limb
  is. Neither gives R8's gains the finest band by itself.
  - Part 2 as planned (the gains through a kernel spliced from the edge and Europa) would rest on a scale and a distance law that no
    pre-registered measurement here supports, so it is not run.
  - What the step still holds: the 2-D kernel (R8 part 1 put an exact one at 13 to 21 % past the best isotropic filter, and every stack
    here is elongated along the planet's axes), the multi-frame blind deconvolution and the defocus burst. Those are #1140's to pick
    from.

##### R8 follow-up 4, part 2: an elongated kernel off the limb's edge

- **Pre-registered 2026-10-02, before anything was built or measured** (`tianwen planetary-elongated`): an ELONGATED kernel read off the
  limb's edge, the part of R8 part 1's exact 2-D kernel a capture can measure (its anisotropy, never its phase).
  - **Why.** R8 part 1's exact 2-D kernel left 21 % less error than the best isotropic filter with the still layer and 13 % without, from
    its anisotropy and its phase together. Part 1 found every stack's kernel wider along the planet's equator, where only the limb places a
    frame (R5 part 3).
  - **(e), the elongated kernel**:
    - the edge (step 3's (b)) read in two sectors: the limb points within 30 degrees of the planet's axis (the poles), whose edges profile
      the kernel along the axis, and those within 30 degrees of its equator (the sunlit limb only, the terminator's arc left out as before);
    - step 3's physical kernel (a) fitted to the axis sector alone;
    - an extra Gaussian jitter along the equator, its variance by least squares on the two sectors' log ratio,
      ln(equator / axis) = -2 pi^2 sigma^2 f^2, between 0.08 and 0.3 cycles a pixel where both read above 0.05 (a negative fit is zero);
    - so (e) is (a) on the axis sector times exp(-2 pi^2 sigma^2 (f . e)^2), e the equator's direction in the image.
  - **The reference**: the oracle read in the same two sectors of frequency (the stack against its truth in the modes within 30 degrees of
    each direction), and a 2-D oracle interpolating them as a jitter would: the transfer's logarithm linear in the squared sine of the
    angle from the axis.
  - **The restorations**: Richardson-Lucy at the sky, set to band 3 = 1.00 against each twin's truth (R8 part 2's rule), with four
    kernels:
    - (a), the round physical kernel on the whole edge (step 3 part 2's);
    - (e);
    - the oracle's ring average;
    - the 2-D oracle.
    Each is scored by the band-error sum over bands 1 to 4 inside 0.9 radii.
  - **The twins**: step 3's three, calibrated, without its still layer, and warped. The real 2022-09-03 Red is read for (e)'s anisotropy
    and not judged.
  - **The claims:**
    1. (e)'s transfer along the equator over its transfer along the axis, at 0.2 cycles a pixel, within 0.05 of the sectored oracle's.
    2. **The kill line.** The anisotropy is worth something only where the 2-D oracle's Richardson-Lucy leaves at least 3 % less error
       than the round oracle's. On a twin where it does not, (e) is not judged on the restorations.
    3. Past the kill line, (e) recovers at least half of what the 2-D oracle gains over the round one:
       err((a)) - err((e)) at least half of err(round oracle) - err(2-D oracle).
  - **Clarified while it was built, before anything was measured** (the first run stopped at the axis sector, which read no pixel at all):
    - **The polar limb is flattened by the nearest latitude the zonal profile reads** (`ZonalProfile.Held`), in the two sector reads only.
      The profile is read inside 0.9 radii, so it never reaches the polar limb, every polar pixel's flattening was NaN, and the axis
      sector was empty. Step 3's whole edge lost the poles the same way, silently; (a) is read as it was.
    - **The jitter fit has an intercept**: ln(equator / axis) = c - 2 pi^2 sigma^2 f^2. A pole's albedo held from about 64 degrees can
      put the two sectors' levels apart, and without c that difference would read as a jitter.

##### R8 follow-up 4 results, part 2 (2026-10-02)

**Measured** with `planetary-elongated` on step 3's three twins (the best 150 of 3,000 frames) and the real 2022-09-03 Red.

| twin | the edge, along the axis / the equator, at 0.2 | (e)'s jitter | claim 1: the equator over the axis at 0.2, (e) / the oracle | Richardson-Lucy's error: (a), (e), the round oracle, the 2-D oracle |
|---|---|---|---|---|
| calibrated | 0.484 / 0.289 | 0.669 px | 0.702 / 0.748, holds (0.046) | 0.717, 0.691, 0.856, 0.894 |
| without its still layer | 0.920 / 0.699 | 0.453 px | 0.850 / 0.936, fails (0.086) | 0.302, 0.562, 0.444, 1373 |
| warped | 0.330 / 0.185 | 0.673 px | 0.699 / 0.851, fails (0.152) | 1.276, 1.010, 1.138, 1.174 |

- **The claims, as pre-registered: claim 1 holds on one twin of three, and the kill line fires on all three**, so (e) is not judged on
  the restorations. The 2-D oracle's Richardson-Lucy leaves more error than the round oracle's, not 3 % less: 4.4 % more on the
  calibrated twin, 3.1 % on the warped one, and on the twin without its still layer it diverged.
- **Why the reference fails: the oracle along the equator reads 0.000 at 0.45 cycles a pixel on every twin.** The planet's power lies
  across its belts, so the frequencies along its equator hold almost none of it past 0.4, and the 2-D oracle interpolated from that read
  holds the kernel near zero there (at the 1e-4 floor). Richardson-Lucy then lifts noise without bound: 1373 on the twin without the
  still layer, where its other bands are least blurred.
- **Why (e) fails as a measurement: the polar limb is a poor edge.**
  - Along the axis the edge rises again past 0.3 (0.523 at 0.4 on the calibrated twin, 0.420 on the real capture), and the physical
    kernel fitted to it sits at its halo's bound (0.500 of 0.4 px on the calibrated twin).
  - Its flattening holds an albedo from about 64 degrees over a polar region that is darker than that, and its pixels are fewer and
    farther from the centre than the equator's.
  - On the real capture the two sectors' log ratio does not fall with frequency once it has an intercept (the axis reads 0.637, 0.300,
    0.128 at 0.1 to 0.3, the equator 0.477, 0.186, 0.112), so (e)'s jitter there is zero.
- **Read post hoc, not judged:** Richardson-Lucy with (e) beat it with (a) by 4 % on the calibrated twin and 21 % on the warped one, and
  was 86 % worse without the still layer. No sign is consistent across the three twins, which is the reason the kill line exists.
- **What it leaves:** the anisotropy part 1 found in a moon's sectors is not measurable off this capture's limb, whose polar edge is too
  poor, nor checkable against an oracle the planet's own spectrum starves along the equator. Of #1140's three probes, the defocus burst
  needs a night at the telescope and the multi-frame blind deconvolution a new estimator. Neither can run from the corpus alone today.

##### R8 follow-up 4, part 3: a multi-frame blind deconvolution as a reference

- **Pre-registered 2026-10-02, before anything was built or measured**: torchmfbd (Asensio Ramos, MIT, 0.9.2; PyTorch 2.13 on the GTX
  1070, whose sm_61 it carries) run on the twins as a REFERENCE outside the product. It estimates every kept frame's wavefront with one
  object, so it reads the kernel itself rather than an edge it was blurred over.
  - **The product's part**: the frames go in and the result comes out through tianwen's own verbs, the Python only glue (the dogfood
    rule).
    - `tianwen planetary-lucky-frames` writes the frames the stack keeps (the best 150 of 3,000 by the gradient), each registered onto
      the stack by a plain, climbed correlation and normalised to its disk, as a FITS cube in a 128 px window about the disk, beside the
      stack and the truth in the same window.
    - `tianwen planetary-score` scores a restoration against the truth: registered onto it by correlation, the band errors over bands 1
      to 4 inside 0.9 radii (R8's), and a mean PSF's ring transfer over the pupil's diffraction against the oracle.
    - A port into the product is a later decision, and only if this earns one.
  - **The configuration**: the Newtonian's 25.4 cm with its 5.8 cm obstruction, 0.497"/px at 650 nm (an overfill of 1.06, just above the
    tool's floor of 1), 44 Karhunen-Loeve modes, the object by its Wiener solution, Adam over 50 iterations, the frames in one patch.
    Recorded as a risk before anything is run: the frames are undersampled for the cutoff, and the tool's PSF has no pixel box, so its
    finest band may be biased.
  - **The read**: the tool's object convolved with the diffraction (the truth is rendered through it), and its PSFs' mean over the kept
    frames.
  - **The twins**: step 3's three (calibrated, without its still layer, warped). The real 2022-09-03 Red is read, not judged.
  - **The claims:**
    1. **The kill line.** On the calibrated twin the object leaves less band error than the stack (1.415). If not, the tool as configured
       does not restore these frames, and the claims below are not read.
    2. The object leaves no more band error than R8's gains through the true kernel on the twins with and without the still layer (0.649
       and 0.289, step 3 part 2's table).
    3. The mean PSF over the pupil's diffraction reads the oracle within 0.05 at 0.3, 0.4 and 0.45 cycles a pixel on the calibrated twin:
       the band step 3 and parts 1 and 2 could not reach.

##### R8 follow-up 4 results, part 3 (2026-10-02)

**Measured** with `planetary-lucky-frames`, `tools/planetary-mfbd/mfbd_reference.py` (torchmfbd 0.9.2 on the GTX 1070, three to seven
seconds a run) and `planetary-score`, the best 150 of each capture's first 3,000 frames in a 128 px window.

| twin | band error: the stack, the deconvolution's object, R8's gains through the true kernel | its mean PSF over the diffraction at 0.3, 0.4, 0.45 | the oracle there |
|---|---|---|---|
| calibrated | 1.415, 1.273, 0.649 | 0.926, 1.115, 1.237 | 0.280, 0.243, 0.217 |
| without its still layer | 0.510, 0.370, 0.289 | 0.985, 1.162, 1.276 | 0.734, 0.635, 0.488 |
| warped | 1.613, 1.184 | 0.285, 0.306, 0.333 | 0.115, 0.068, 0.053 |

- **The claims, as pre-registered: the kill line holds, and claims 2 and 3 fail.**
  - The object leaves less error than the stack on every twin (1.273 against 1.415 on the calibrated one), 10 to 27 % less.
  - It is far from R8's gains through the true kernel: 1.273 against 0.649, and 0.370 against 0.289.
  - Its PSFs are not the kernel. On the two twins that are not warped their mean is the diffraction limit itself, above it past 0.3,
    where the oracle reads 0.22 to 0.49.
- **Why: the frames are outside the tool's regime, as recorded before the run.**
  - Over the pre-registered 50 Adam iterations the loss moved from 0.4239 to 0.4232. The wavefronts barely left zero, so the PSFs are
    the pupil's own, and the object is the frames' mean through a Wiener filter with them.
  - An overfill of 1.06 puts the pupil's transfer past the grid and wraps it, and the model has no pixel box: hence a mean transfer
    above the diffraction's past 0.3, on every twin and on the real capture alike (0.212, 0.235, 0.256 there).
  - Only the warped twin moved the modes, and its PSFs then sit on that wrapped floor (0.29 to 0.33) past 0.3, not at the oracle's.
- **Post hoc, not pre-registered:** L-BFGS, the tool's own example's optimizer, over 20 iterations on the calibrated twin made it
  worse: PSFs sharper than the diffraction (1.525 over it at 0.4) and an object no better than the stack (1.439). The tool's model
  prefers a sharp PSF on these undersampled frames whichever way it is driven.
- **What it leaves:** no port is earned. A blind deconvolution on 8-bit frames sampled at 0.5"/px for a 25 cm aperture would need its
  own forward model, with the pixel and the undersampling in it, and nothing here says that would read the finest band either.
  - The one probe of #1140 the corpus cannot try, a defocused burst after a capture, is a bench item, #1155. #1140 closes with this part.
  - The kernel past 0.3 cycles a pixel stays unmeasured on a real capture.

## The enhanced pipeline: R1 to R8 adopted as the stack's defaults

**Issue:** #1159 (the user's goal, 2026-10-02: make `tianwen planetary-stack` and the GUI's planetary stack produce the measured-best
result end to end). It adopts #1072, #1074, #1086, #1091 and #1143 and R8's derived gains, and open question 4 (answered yes the same
day) is what lets a parameter chosen on the twin become a default.

**Where the stacking happens** (mapped 2026-10-02): `tianwen planetary-stack` builds `PlanetaryStackOptions` itself, every option
with its own default, and stacks with `LuckyImagingStacker`; the GUI's and `tianwen-fits`' SER playback, and live capture through the
node, stack with `RollingWindowStacker`, whose registration (whitened phase correlation), resampling (bilinear) and estimator (the
Laplacian) were fixed in code; and no product path used R8's derived gains or the limb channel (the GUI sharpens with
`PlanetaryDefault`'s gains on six sliders that stop at 5).

**What changes:**
- **The batch stack** (`tianwen planetary-stack`, `PlanetaryStackOptions`):
  - frames graded by the gradient (R4: +0.83 to +0.87 against the true transfer, the Laplacian +0.19), and kept at half the frames
    when the stack is sharpened (#1083) or a tenth when it is not (R4's raw optimum, 5 to 10 % of 3,000);
  - registered by plain cross-correlation, its peak climbed (R5 parts 1 and 3), against a stack of the best 1,000 frames (R5 part 3:
    on the real capture the best frame's registration error variance is larger by 0.08 and 0.38 px^2);
  - resampled by Lanczos-3 (R5 part 3: band 1's error down 0.014 to 0.024); per-point weighting as today (R5 part 1 measured it a
    little better, 0.751 against 0.760 in band 1);
  - a colour capture drizzled onto the sensor grid, if the measurement below says so (R5a found it better in band 1 and worse in bands
    2 and 3 than a demosaic resampled by Lanczos-3, #1091);
  - de-rotated when the planet's turn over the run moves the disk's centre by a pixel or more, the planet known from `--planet` or the
    capture's name (R6: a 16-minute run halves its halves' difference; Jupiter's turn moves the centre about 0.53 px a minute);
  - sharpened as the measurement below decides, from gains derived through the limb's edge (R8 follow-up 3: 0.916 against the
    stack's 1.415 and the presets' 2.1 to 2.9 on the calibrated twin) and the stack's own noise floor (R8 part 3: a white noise left
    less error than the halves'), given the telescope's pupil and the filter's wavelength; without them, `PlanetaryDefault` with the
    limb as its own channel (R8 follow-up 2, #1143);
  - `--legacy` restores every choice as it was (the Laplacian, a quarter, whitened, the best frame, bilinear, the demosaic, no
    de-rotation, `PlanetaryDefault` plain).
- **The rolling stack** (`RollingWindowOptions`: the GUI's and `tianwen-fits`' playback, the node's live run): the gradient, plain
  correlation and Lanczos-3 as options with those defaults, except that a live capture keeps bilinear unless a frame's whole fold with
  Lanczos-3 (graded, registered and folded in, 640 by 480 px) stays under 10 ms, the frame interval at 100 frames a second; and the
  GUI's sharpening by the same derivation, computed off the render thread once a master changes, the sliders kept as the manual way.

**How it is judged, set down 2026-10-02 before anything is built:**
- **The sharpening first** (`planetary-dering`, which gains the edge's derived gains beside the presets'): on the calibrated twin,
  the one without its still layer and the warped one (the first 3,000 frames, the best half by the gradient), the gains derived
  through the limb's edge plain, floored, as the limb's own channel and feathered; the pipeline takes the one with the least band
  error summed over the three twins among those whose limb undershoot stays at most 0.02 of the disk on the real capture
  (2022-09-03 Red).
- **A colour capture's drizzle** (`planetary-stack --truth` on the colour twin of 2024-12-15's Uranus-C capture, both seeds): Bayer
  drizzle to the sensor grid is the default for a colour capture if its master leaves less error summed over the four bands and the
  three colours than the demosaic does, each sharpened as the pipeline sharpens (so it is judged once the sharpening is built; until
  then the demosaic stays).
- **The pipeline against legacy**, each master sharpened as its pipeline does, the band error summed over bands 1 to 4 inside 0.9
  radii against the truth:
  - **On every twin the pipeline leaves less error than legacy, and on the calibrated twin less than half** (legacy's
    `PlanetaryDefault` as shipped left 2.872 on R8's 150-frame stack).
  - **Unsharpened, the pipeline's stack leaves less band 1 error than legacy's on every twin.**
- **On real captures**, with no truth: 2022-09-03 Red, a 2024-12-15 Uranus-C Jupiter capture (colour), the 2021-12-16 ASI462MC Saturn,
  and the 2022-10-09 Jupiter, beside AutoStakkert's own result where the corpus holds one. **The pipeline's limb undershoot is at most
  0.02 of the disk** on each (legacy's presets: 0.16 to 0.26), and the masters go side by side to the user's eye.

### The enhanced pipeline: decided and found while building (2026-10-02)

- **The rolling stack keeps today's recipe** (the user, 2026-10-02): two paths are fine for now, and the batch path may be slow if it
  gives the best stacks, but frames are many, so no step may grow faster than linearly in them. The pre-registered live criterion
  failed clearly in any case: a frame's whole fold (graded, registered, folded) took, per frame at 512 px square (85 % of 640 by 480),
  35 to 44 ms as it was, 67 ms with the gradient and the climbed plain correlation, and 102 ms with clamped Lanczos-3 as well
  (`RollingFoldBenchmarks`, Release, this x64 box; at 256 px 10.6, 15.5 and 25 ms). The gradient costs nothing (11.3 against 10.6
  ms); the climb and the kernel are the cost.
- **The climb and the translate fold were made cheaper**, the same arithmetic: the climb's phase splits by axis, so each Newton step
  takes a phasor per column and per row rather than a sine and cosine per frequency; a translation samples every pixel at one phase,
  so the fold takes Lanczos-3's weights once a frame and reads the interior's taps unchecked (pinned against the general sampler at
  every pixel). At 512 px a frame's fold with the climbed plain correlation went from 67 to 44 ms and the whole pipeline's from 102 to 57 ms against 34 ms as it was (at 256 px, 15.5 to 10.1 and 25 to 14.4 against 8.0): 1.7 times the old cost a frame, where it was three times.
- **Lanczos-3's clamp blew up on negative data.** It is PixInsight's rule for non-negative data (each tap sorted by the sign of its
  weighted value); on a sky at zero with its noise a positive lobe's weight went into the negative part and the clamped denominator
  came near zero, so a stacked reference of the banded fixture read up to 1e17 and placed frames 1.9 px off. A sample with a negative
  tap now takes the plain kernel; on non-negative data the clamp is unchanged. A real capture sits on the camera's offset, so the
  real and synthetic captures never reached it, but a dark-subtracted deep-sky frame can.
- **The sharpening is measured on the product's own master, set down 2026-10-02 before it was run.** The pre-registration named
  `planetary-dering`, which sharpens a global stack of its own; the variants are instead read by `tianwen planetary-sharpen --fix all
  --truth` on each twin's pipeline master at half the frames (the alignment-point stack `planetary-stack` makes), through
  `PlanetarySharpening`, the routine the product runs: the edge's derived gains with the stack's white noise floor, plain, floored, with
  the limb as its own channel and feathered. The rule is the one registered: the least band error summed over the three twins among the
  variants whose limb undershoot on 2022-09-03 Red's pipeline master stays at most 0.02. Without a telescope the fallback is
  `PlanetaryDefault` with the limb as its own channel put back through the stack's own blur (no pupil, so no diffraction to put it back
  through), which keeps the limb as stacked.
- **Post hoc, set down 2026-10-02 before it was measured on the twins: the gains fitted with their composite held non-negative.** The
  free fit (`PlanetaryWaveletGains.Fit`) meets a steep Wiener boost with one large gain and a negative one beside it where the edge reads
  the finest band low: 9.70 and -0.65 on the warped twin, where it still halves the error, but on the sharpening test's rendered Jupiter
  (a 1.4 px Gaussian, 1 % noise) it made the stack worse (3.18 to 4.56, a gain of -0.36). Held non-negative (`FitNonNegative`, R8
  follow-up 1) it zeroed the noise's bands there and cut the error to 1.10. Rule: the non-negative fit replaces the free one if, with the
  limb fix the registered rule chooses, its band error summed over the three twins is within 10 % of the free fit's and its limb undershoot
  on the real capture stays at most 0.02; a sharpening that can make a stack worse is not a default.
- **The sharpening, read** (`planetary-sharpen --fix all --truth` on each twin's pipeline master at half the frames, 2022-09-03 Red's
  pipeline master for the undershoot; the telescope the twins were made with, 254 mm, 650 nm; bands 1 to 4 against the truth):

  | Twin | Stack | Plain | Floored | Limb channel | Feathered | Legacy (quarter, `PlanetaryDefault`) |
  |---|---|---|---|---|---|---|
  | calibrated | 1.500 | 0.664 | 0.664 | 0.675 | 0.732 | 2.800 |
  | without its still layer | 0.559 | 0.292 | 0.292 | 0.301 | 0.289 | 3.525 |
  | warped | 1.667 | 0.820 | 0.820 | 0.827 | 0.907 | 2.631 |
  | summed | | **1.776** | **1.776** | 1.803 | 1.928 | |
  | real capture's undershoot | 0 | 0 | 0 | 0.0013 | 0 | (a deep ring) |

  - **The registered rule chooses plain or floored** (every variant keeps the real capture's undershoot under 0.02); they tie to the
    digit, and **floored is the default**, the floor taking away only what digs below the sky.
  - **The pipeline against legacy, each sharpened as its pipeline sharpens: holds on every twin, and on the calibrated twin by far more
    than half** (0.664 against 2.800; 0.292 against 3.525; 0.820 against 2.631).
  - **The non-negative fit fails its rule by far** (post hoc, set down before it ran): it switches the finest band off (a gain of -0.01 to
    0.02) and leaves every twin WORSE than its stack unsharpened. Floored, 2.071, 1.629 and 2.217 (5.917 summed against the free fit's
    1.776; the stacks 1.500, 0.559, 1.667); its best fix on each twin still 1.840, 1.540 and 1.992. The free fit stays.
  - **The free fit's gains oscillate** where the edge reads the finest band low (the warped twin 9.70 and -0.65, the real capture 13.06
    and -0.39), and its composite still lands near the truth on the twins (transfers 0.97 to 1.12).
  - **Every fix rings OUTSIDE the limb, above the sky, where neither metric reads** (#1168). Stretched with black at the sky and white at
    3 % of the disk above it, the floored master shows a bright rim, a trough the floor holds at the sky, and a fainter second bright ring
    outside it, on 2022-09-03 Red and the same, round and concentric, on the calibrated twin, which carries no ghost and whose truth shows
    only the diffraction glow. So it is the sharpening's own, lifting the finest bands 2 to 13 times across the limb's step (on that twin
    with no negative gain: 4.63, 1.97, 1.36, 0.82), not the 2022 shell (R7a's is a one-sided smear). The limb as its own channel puts many
    rings well out on the real capture, more than on the twin. The band error reads inside 0.9 radii and the undershoot below the sky
    only, so both scored it 0. Legacy's preset still digs its ring 0.16 to 0.26 of the disk BELOW the sky.
  - **The limb's edge over-reads the finest band where the seeing passes little there.** On the sharpening test's rendered Jupiter a single
    Gaussian of 1.4 px passes 0.03 at 0.3 cycles a pixel and the edge read 0.30, its own noise floor, and the derived gains made that
    stack worse. The edge reads 0.09 to 0.68 there on the twins and the real capture, and the test's core-and-halo kernel (it passes 0.17)
    is sharpened toward the truth (1.352 to 1.172). A planet with too little fine texture for the disk term's limb gains to stay out of
    the noise is the same case.
- **Whether the limb fit's north was right is incidental**: graded by the gradient, the 16-minute fixture's stack of best frames had it
  upside down and the run's quarters turned it back (31 degrees against the true 30).
- **The stacking, read on the three twins** (`planetary-stack --truth`, each twin's 3,000 frames, every master against its truth,
  the band error inside 0.9 radii; Release, the code of this PR):

  | Twin | Legacy band 1, sum of bands 1 to 4 | Pipeline unsharpened (a tenth kept) | Pipeline at half the frames |
  |---|---|---|---|
  | calibrated | 0.792, 1.706 | **0.648, 1.415** | 0.691, 1.500 |
  | without its still layer | 0.452, 0.777 | **0.278, 0.493** | 0.313, 0.559 |
  | warped | 0.868, 1.947 | **0.766, 1.614** | 0.786, 1.667 |

  - **Unsharpened, the pipeline's stack leaves less band 1 error than legacy's on every twin: holds**, by 0.10 to 0.17, and every
    band with it; even at half the frames its stack beats legacy's quarter.
  - **Sharpened by `PlanetaryDefault` as shipped** (this PR does not yet change the sharpening), the pipeline's half-the-frames stack
    and legacy's quarter end alike, both far past the truth (transfers 1.4 to 2.0 in bands 2 and 3, the limb's undershoot 0.19 to
    0.31): 2.845 against 2.800, 3.600 against 3.525, 2.853 against 2.631. Half the frames pays only once the sharpening is derived
    (#1083), which is the next part; until it lands, the default sharpened master is about as good as before, not better.
  - **The cost:** 82 to 119 s for the pipeline's unsharpened stack and 215 to 312 s at half the frames, against legacy's 45 to 78 s
    (the reference of 1,000 and Lanczos-3 a frame, all linear in the frames).

### The sharpening's ring outside the limb (#1168)

Set down 2026-10-02, before any of it was measured. Every limb fix the derived sharpening offers rings outside the limb, above the
sky: a bright rim, a trough the floor holds at the sky, a fainter second ring outside it, round, on 2022-09-03 Red and on the
calibrated twin alike (so the sharpening's own, not the 2022 shell). Neither metric the fix was chosen by reads there.

- **The readings.** On a twin, against its truth: `PlanetaryMetrics.LimbProfileError` (R3's, the azimuthal profile's RMS difference
  from the truth's over 0.8 to 1.2 radii: it sees the ring, the trough and a soft limb alike), beside the band error inside 0.9 radii.
  Without a truth: **the limb's rebound** (`PlanetaryMetrics.LimbRebound`), the most the azimuthal profile climbs back above its own
  running minimum going out from 1.0 to 1.3 radii. A planet's true profile only falls outside its limb (the glow of the diffraction
  and the blur), and so does a stack's, so a rise there is a ring; it reads 0 on a profile that never climbs.
- **The candidates**: the four fixes as they stand (plain, floored, the limb as its own channel, feathered) and one new one,
  **bounded**: floored at the sky, and outside the limb fit's outline (beyond 1.0 radii) never brighter than the stack it sharpened,
  since a sharpening only moves light inward there.
- **The rule.** The default becomes the candidate with the least `LimbProfileError` summed over the three twins, among those whose
  band error summed over the three stays within 2 % of floored's (1.776, so at most 1.812) and whose limb undershoot on 2022-09-03
  Red stays at most 0.02; floored stays if none beats it.
- **The rebound is used on a real capture only if it ranks the candidates as `LimbProfileError` does**, on each twin (Spearman at
  least 0.8, R3's rule); otherwise it is reported and not used.
- **Read** (`planetary-sharpen --fix all --truth`, each twin's pipeline master at half the frames, 2022-09-03 Red's for the real
  capture; the code of this PR, Release):

  | Fix | Limb profile error, calibrated / without its still layer / warped | Summed | Band error summed | Red: undershoot, rebound |
  |---|---|---|---|---|
  | plain, floored | 0.0115 / 0.0044 / 0.0139 | 0.0298 | 1.776 | 0, 0.0120 (plain 0.0133) |
  | limb channel | 0.0102 / 0.0041 / 0.0111 | **0.0254** | 1.803 | 0.0013, 0.0188 |
  | feathered | 0.0307 / 0.0095 / 0.0324 | 0.0726 | 1.928 | 0, 0 |
  | **bounded** | 0.0103 / 0.0043 / 0.0124 | 0.0270 | 1.777 | 0, 0.0101 |
  | the stack, unsharpened | 0.0327 / 0.0097 / 0.0344 | 0.0768 | 3.726 | 0, 0 |

  - **The rebound fails R3's rule**: it reads a ring above the sky and not a soft limb, so it ranks feathered, which has no ring and the
    softest limb, best: a Spearman against the limb profile error of -0.16, -0.73 and -0.68 on the three twins. It is reported, not used.
  - **The registered rule picks the limb channel** (the least limb profile error among the fixes within 2 % of floored's band error,
    its undershoot on Red 0.0013). **On 2022-09-03 Red it rings out to about 1.3 radii**, stretched with black at the sky and white
    at 3 % of the disk, more than floored; the rule's only real-capture gate, the undershoot, reads below the sky and cannot see it.
  - **Bounded is the default, the owner's choice against the rule (2026-10-02), labelled post hoc**: second on the twins (6 % more limb
    profile error than the limb channel, 9 % less than floored), floored's band error within 0.001 (1.777), and on Red only the stack's own glow
    outside the limb. What it leaves is the first lobe, a thin dark trough at the limb that the floor holds at the sky, below the stack's
    glow there: #1171.

### The live stack, given the batch stack's learnings

Set down 2026-10-02, before any of it was measured. The owner kept the rolling stack (live capture, the GUI's and `tianwen-fits`'
playback) on its recipe while the batch stack took the measured best, then asked for the batch stack's learnings in it wherever they
are cheap enough. The per-frame fold costs are known (`RollingFoldBenchmarks`, above: the gradient free, the climbed plain correlation
1.3 times today's fold at 512 px, with clamped Lanczos-3 1.7 times); what they do to a LIVE stack is not, since the rolling stack folds
every frame it is behind by and evicts as many, and rebuilds its window when the node's ring has dropped frames it still holds.

- **The tool**: `tianwen planetary-live`, which replays a SER into the node's own ring (`LiveCameraFrameStream`, sized as
  `PlanetaryCapture` sizes it) at the capture's own frame rate, read off its timestamps, while the node's own stacking loop (hoisted
  from `NodePlanetary` into Lib, so the probe and the node run one loop) stacks to the newest frame every 250 ms. It reports the
  masters' interval, the frames folded a second against the frames captured, the lag behind the newest frame at each master, the
  window rebuilds, and, given a synthetic capture's truth, the last master's band error inside 0.9 radii.
- **The recipes**: today's (the Laplacian, phase correlation, bilinear); the gradient alone; the gradient and the climbed plain
  correlation; and the batch stack's three (with clamped Lanczos-3).
- **The captures**: 2022-09-03 Red (800 by 600, 8 bits, 12,990 frames) and its calibrated twin with its truth, each replayed for 90 s.
- **The rules**, each recipe against the one before it:
  1. **The gradient** replaces the Laplacian if the live stack keeps its throughput (frames folded a second within 10 % of today's) and
     the twin's last master leaves no more band error than today's.
  2. **The plain correlation** joins if the twin's last master leaves less band error than with the gradient alone, and the live
     stack still keeps up: if today's recipe folds every captured frame (at least 95 % of the capture rate), the new one must too;
     otherwise it must fold at least two thirds as many frames a second as today's, and its masters come no more than 1.5 times as
     far apart.
  3. **Lanczos-3** joins on the same two conditions against the gradient and the plain correlation.
- **The derived sharpening of a live master** is the GUI's part of the pipeline (it needs the telescope the host knows), with its
  gains derived once a capture and reused, not each master.
- **Read** (`planetary-live`, the code of this PR, Release; each recipe replayed for the capture's length at its own rate):

  | Recipe | Red: folded a second (of 216 captured) | Red: frames a master holds | Twin: folded a second (of 250) | Twin's last master, bands 1 to 4 |
  |---|---|---|---|---|
  | today's | 53.6 (25 %) | 234 | 69.6 | 1.747 |
  | the gradient | 56.1 | 235 | 68.5 | 1.702 |
  | and plain correlation | 50.2 | 207 | 67.6 | 1.657 |
  | and Lanczos-3 | 21.5 | 102 | 24.1 | 1.589 |

  - **The gradient is adopted** (rule 1): the throughput within 5 % either way, the twin's master 1.702 against 1.747.
  - **The plain correlation is adopted** (rule 2): the twin's master 1.657 against 1.702; today's recipe folds a quarter of the
    frames, so the bar is two thirds of its rate, which the plain correlation clears (50.2 against 35.7 on Red, 67.6 against 46.4 on
    the twin), its masters as often (5.0 s on Red, 4.4 s on the twin, as today's).
  - **Lanczos-3 is not** (rule 3): it folds two fifths of today's rate (21.5 and 24.1), under the two-thirds bar, though it left the
    twin's master the least error. It also starved the replay itself (170 frames a second delivered of 216), as it would a capture
    loop on the same machine.
  - **Every recipe falls behind a capture this fast, today's included** (#1174): the ring of 1,024 frames drops what the stack has not
    folded, so nearly every master is a rebuild of the window (12 over 13 masters on Red), a master every 5 s, 4.7 s behind the newest
    frame, holding under half its window. The first reading of the probe counted how far the window's end moved, which a rebuild
    jumps, and so read 92 % of the capture's rate; it counts the folds since. The batch stack's own answer is the fix to try: fold only
    the frames that grade best.

### The best stack in the viewer

The owner's choices of 2026-10-02: a recorded capture gets a "Best stack" action that runs the batch pipeline, slow and best, while the
rolling stack stays the live view; the telescope comes from the profile where there is one. What shipped (#1159):

- **One routine, `PlanetaryBestStack`** (Lib), for `planetary-stack` and the viewer: the batch stack at its defaults, de-rotated by
  `DerotationFor` (a turn of a pixel or more), sharpened by `Sharpen` (derived through the limb's edge given the planet, the time and
  the telescope; the preset with the limb kept without the telescope; the preset alone without the planet or the time, each fallback
  worded), both masters written under `OutputPaths`' names. Its progress is read off the frames the stack loads against those it will.
- **`tianwen-fits`**: Shift+K or the info panel's Best stack button on a SER runs it in the background, its progress on the button
  (a second press cancels), writes both masters beside the capture and opens the sharpened one, saying how it was sharpened once it is
  on screen. The planet comes from the capture's name (`PlanetaryCaptureName`), the time from its frames.
- **The telescope**: `PupilFor(aperture, design)`, the central obstruction by design (a Newtonian 0.25 with its spider, an SCT or a
  Maksutov 0.33, a Newtonian-Cassegrain 0.3, a RASA 0.4, a refractor none). The viewer has no profile and no text input, so its panel
  steps the aperture through the common ones (60 to 508 mm, or none) and picks the design (Newtonian, SCT / Mak, refractor), remembered
  in `Viewer/planetary-telescope.json`.
- **A recording says all of this itself** (#1179), not in a sidecar (#738: SharpCap's sidecars did not survive curation):
  - **its name** is the planet the profile's mount points at (`PlanetaryCaptureName.PointedAt`, the nearest of the planets and the Moon
    within 1.5 degrees, of date or J2000) and the filter the OTA's wheel holds, then the start and the OTA
    (`Jupiter_Red_2026-10-02T12_11_08_OTA1.ser`), which `PlanetaryCaptureName` reads back;
  - **its SER header** names the camera (Instrument) and the OTA's aperture, focal ratio, design and name (Telescope,
    `PlanetaryCaptureName.TelescopeField`, `254 mm f/4.7 Newtonian, SW 250PDS`, 40 characters at most);
  - **the readers**: the viewer seeds the panel's telescope from the header when a capture names one (the panel's own otherwise), and
    `planetary-stack` takes it (`PlanetaryBestStack.PupilOf`) when no `--telescope` or `--aperture-mm` is given, and a mono capture's
    filter from its name when no `--wavelength` is.
  A third-party capture keeps what its software wrote: SharpCap's `CameraSettings.txt` names the filter wheel's slot but no telescope,
  and is not read yet.
- **The GUI has no SER playback**: its planetary stack is the live capture's rolling stack, through the node. The derived sharpening of
  that live master is the Derive button ("The live view's derived sharpening", below).

#### What the first viewer run missed (the owner's report, 2026-10-02)

The owner's first Best stack, of the red twin (`calibrated.ser`), came out blurred, with a dark ring round the limb. Under the Auto
stretch it showed a grainy sky and a square about the planet. Three causes, one fix each:

- **The planet came only from the capture's name**, and `calibrated` names none, so the sharpening was the preset's. Shown linear
  beside the derived sharpening of the same master (Jupiter, a 254 mm Newtonian, 650 nm), the preset barely sharpened and dug a ring
  below the sky. The panel now picks the planet (Auto, Jupiter, Saturn) and, for a mono capture, the filter (Auto, L, R, G, B, IR).
  Auto is what the name gives (`PlanetaryCaptureName.Planet`, `.WavelengthNm`), and a caption says what that is before the run.
  - The filter matters, though less than the planet: on the twin's master 650 nm left 0.670 of band error, 550 nm 0.771, against
    1.500 unsharpened.
- **The master opened under the deep-sky auto-stretch.** A SER opens linear on purpose, but the master it gave was a document, and a
  document from a linear view is put back on Auto. The stack's sky noise is 3e-5 (1,500 frames averaged), which Auto lifted thirty
  thousand times into grain, with the disk blown white.
  - The master now names its planet in `OBJECT` (`PlanetaryStackOptions.Planet`, for `planetary-stack` and the viewer alike).
  - A frame whose `OBJECT` names a planet or the Moon opened linear (`PlanetaryCaptureName.Named`), as a SharpCap FITS frame does too.
    Linear was still not the look the owner had been shown: it is the planetary stretch since ("The planetary stretch in the viewer").
- **The square is the twin's, not the stack's.** The plain mean of `calibrated.ser`'s 3,000 raw frames, unaligned, shows it, and a
  band across the top 140 rows: `planetary-degrade` renders the blurred planet and its halo on a finite window. It is at the level of
  1e-4, inside no metric's region, and a real capture has no such edge.

#### The planetary stretch in the viewer

The owner's goal of 2026-10-02: the viewer's Best stack should look like the comparison shown earlier, 2022-09-03 Red stacked and
sharpened by the derived gains (Jupiter, a 254 mm Newtonian, 650 nm). That comparison was rendered with `planetary-stack`'s preview
stretch (`Image.ComputePlanetaryStretchUniforms`: black at each channel's 0.5th percentile, white at the 99.9th on one common scale, a
gamma of 0.75), and the master's sky sits at 0.066 with its disk peaking at 0.29 to 0.32, so the linear view the viewer opened it in
showed a dim, flat disk on a grey sky. The data was never the difference.

- **`StretchMode.Planetary`** is that stretch as a viewer mode: a UI intent like `Auto`, resolved by the document to `Unlinked` uniforms
  from the frame's own percentiles (taken once, at open and off the render thread for a planet's frame), so it never reaches the
  shader. Only a manual white balance applies, a planet having no stars to calibrate on. It is the stretch menu's last entry, so any
  frame may be shown in it by choice.
- **`StretchMode.ForFrame` is the ONE rule for which frames open in it**: a frame whose `OBJECT` names a planet or the Moon.
  `tianwen-fits`, the Explorer thumbnail and `tianwen view` all ask it, so a master's thumbnail is its preview. A deep-sky frame
  opened after one goes back to `Auto`, and the linear toggle (T) returns to the stretch it left.
- **What it does to the real capture** is in "The best stack of a real capture, as the viewer shows it", below.

#### The best stack of a real capture, as the viewer shows it

`ViewerBestStackProbe` (on demand, `TIANWEN_BEST_STACK_PROBE`) runs the viewer's Best stack of a real capture as `tianwen-fits` runs
it: the SER dropped, the panel's planet and telescope set, Shift+K, and the master it opens drawn through the CPU mirror of the shader
with the uniforms the viewer computes. On 2022-09-03 Red (12,990 frames of 800 by 600, Jupiter, a 254 mm Newtonian, 650 nm from the
name; the code of this change, Release, with another session's tests on the machine):

- **The whole run took 248 s**, stack and derived sharpening, against the twin's 55 s for 3,000 frames: linear in the frames.
- **The gains came out 12.71, -0.27, 1.23, 0.79** against the example's 13.06, -0.39, 1.26, 0.79; the viewer's Newtonian is 25 %
  obstructed by design, `planetary-sharpen --telescope newtonian` the owner's 58 mm in 254.
- **The disk matches the example**: inside it, the two drawn with the same stretch correlate at 0.9989, an RMS difference of 0.0082 of
  white. The viewer draws its master exactly as the stretch says (the script's power-law gamma lifts the sky a shade the shader's
  curve does not).
- **Two differences remain, both the bounded fix's** (#1168, the default since the example was drawn): the ring outside the limb is
  gone, and so is the moon's sharpening. Bounded holds everything outside the limb at the stack, the moon beside Jupiter included (peak
  0.024 above the sky over 12 px, where the floored example made it 0.101 over 4 px), and it would hold Saturn's rings the same way.
  Holding only what the planet explains there is #1181, below.
- **After #1181** (the probe again, the code of that change): the disk correlates with the example's at 1.0000, an RMS difference of
  0.0004 of white, and the moon is 0.0998 over 4 px against the example's 0.1014 over 4. What is left is the ring the example shows
  outside the limb and the viewer's master does not, which is the bounded fix doing what the owner chose it for.

#### The moons under the bounded fix

#1181. The bound holds everything outside the limb at the stack, the moons with it. The rule, set in the issue before measuring, on the
three twins that carry the Galilean moons (`r8m`: calibrated, nostill, warped, 3,000 frames each, stacked by the pipeline) and on
2022-09-03 Red's viewer master: the ring stays gone (limb profile error and rebound no worse than bounded's on every twin), the moon comes
back (its peak within 10 % of floored's, its width no further from the truth's), and the disk is untouched. `planetary-sharpen` now reads
each moon (`PlanetaryMetrics.CompactSources`, `SourcePeak`) beside the limb's metrics. Three candidates, each holding outside the limb
except where its mask frees the light (the code of this change, Release):

| | Floored | Bounded | 1: above the planet's model | 2: above its own median | 3: about a local maximum |
|---|---|---|---|---|---|
| calibrated: limb profile, rebound | 0.0112, 0.0052 | 0.0102, 0.0023 | 0.0112, 0.0052 | 0.0112, 0.0051 | 0.0102, 0.0023 |
| calibrated: the moon's peak (truth 0.490) | 0.524 | 0.159 | 0.524 | 0.524 | 0.525 |
| nostill: limb profile, rebound | 0.0045, 0 | 0.0043, 0 | 0.0045, 0 | 0.0045, 0 | 0.0043, 0 |
| nostill: the moon's peak | 0.506 | 0.368 | 0.506 | 0.506 | 0.506 |
| warped: limb profile, rebound | 0.0129, 0.0058 | 0.0107, 0.0029 | 0.0129, 0.0057 | 0.0128, 0.0054 | 0.0107, 0.0029 |
| warped: the moon's peak | 0.428 | 0.112 | 0.428 | 0.428 | 0.429 |
| 2022-09-03 Red: rebound | 0.0120 | 0.0101 | 0.0120 | 0.0120 | 0.0100 |
| 2022-09-03 Red: the moon's peak | 0.550 | 0.131 | 0.550 | 0.550 | 0.551 |

- **The first two brought the ring back**: each freed what stood above the far sky's noise by five sigmas, the stack above the limb
  fit's model through the kernel (1) or above its own 11 px median (2), and a 1,500-frame stack's sky is so quiet that the halo's own
  shape crosses that line all round the limb.
- **The third passes**: only a local maximum of the stack beyond 1.05 radii that stands above its own neighbourhood by twenty sigmas
  and a hundredth of the disk is a moon, and 5 px about it are free. The planet's halo only falls away from the limb, so it holds no
  maximum. Its masters differ from bounded's in 9 to 16 pixels about the moon and are bit for bit bounded's everywhere else; the band
  score's 0.001 on the calibrated twin (0.454 against 0.453 in band 1) is the score's own limb-fit registration seeing the brighter moon,
  read after the measurement from that comparison.
- **Adopted as `Bounded` itself** (`PlanetaryDering.Bounded`), so the owner's choice keeps its name; the two failed candidates are not
  kept. Saturn's rings are not compact, so the bound still holds them: #1184.

### The live view's derived sharpening

The owner's choice of 2026-10-02 for the live view (the viewer's stacked view of a SER, K, and the GUI's planetary capture): a Derive
button works out the derived sharpening's gains for the master on show, in the background (about half a minute), and seeds the six
wavelet dials with them; every later master is then sharpened by the dials, at the cost of a wavelet pass. The telescope is the panel's,
which the GUI seeds from the profile's OTA as a capture starts (`PlanetaryCaptureController.SeedTelescope`).

- **The pieces**: `PlanetaryBestStack.DeriveGains` (the batch's derivation, the reason in words when it derives none: no telescope, no
  named Jupiter or Saturn with frame times, no limb fitted), `PlanetaryBestStack.SliderOptions` (the gains as the dials apply them) and
  `WaveletDerivation` (UI), ONE for the viewer and the GUI. Reset puts the preset back and forgets the derivation.
- **The dials apply derived gains as the derived sharpening does**: no denoise, and held at the darkest level the frame records
  (`WaveletSharpenOptions.HoldAtDarkest`, its 0.001 quantile). The floored fix holds at the sky the limb fit finds, which a wavelet pass
  on a live master has no limb fit for; the darkest recorded level stands in for it.
- **The rule, set before measuring**: the dials seeded with a twin's derived gains must leave the band error (bands 1 to 4, inside 0.9
  radii) within 0.01 of the batch's floored derived sharpening of the same master, and the limb's undershoot under 0.02, on every twin.
  `planetary-sharpen --sliders` reads both (the code of this change, Release, each twin's pipeline master):

  | Twin | Stacked | Floored derived sharpening | The dials, seeded | Undershoot (dials) |
  |---|---|---|---|---|
  | calibrated | 1.500 | 0.664 | 0.664 | 0.0000 |
  | nostill | 0.559 | 0.292 | 0.292 | 0.0000 |
  | warped | 1.667 | 0.820 | 0.820 | 0.0000 |

  It passes: the dials sharpen as the floored derived sharpening does, band for band (the warped twin's finest band 0.544 against
  0.543). They do not reproduce the batch default, `Bounded`, whose hold outside the limb needs the limb fit (#1168); a live master
  keeps the floored ring outside the limb at its rebound, 0.0052 on the calibrated twin.
- **A derived gain can pass a dial's end** (9.7 on the warped twin's finest band, -0.65 on its second; 13.1 and -0.39 on the real Red
  capture): the gain is kept as derived and only its dial rests at the end of the track (0 to 10).
- **Pinned** by `PlanetarySharpeningTests.TheLiveSlidersSeededWithTheDerivedGainsSharpenAsTheDerivedSharpeningDoes` and, through the
  host, `ViewerWaveletDeriveTests`. The latter found the e2e harness never asked the loop's `WantsFrame`, which ticks SER playback and
  the stacked view's stack, so no stacked view had ever built a master there; `ViewerE2E.Frame` asks it now.

### The batch stack on every core

The owner saw the viewer's Best stack hold one core, and it did: 1.00 core over a 3,000-frame stack of the calibrated twin (233 s,
Release, this 16-thread desktop, 2026-10-02). A sampled trace (`dotnet-trace`, `dotnet-sampled-thread-time`, 263 s traced) split it:

| Step | Time | Share |
|---|---|---|
| The fold of the kept half through the mesh, clamped Lanczos-3 (`AccumulateByMeshWeightedInto`) | 151 s | 57 % |
| Each frame's sharpness map (`FrameSharpnessMap.Build`, a 7 by 7 box re-summed at every pixel) | 45 s | 17 % |
| The stacked reference of the best 1,000 (`AccumulateTranslatedLanczos`) | 46 s | 18 % |
| Grading all 3,000 | 9 s | 4 % |
| The alignment points and the global shift (FFT) | about 10 s | 4 % |

Two changes, each giving the master bit for bit (both masters compared byte for byte against the one-core run's):

- **A fold runs in bands of output rows** (`ParallelFor.RunBands`). Each output pixel gathers from the frame into its own cell, so its
  sum sees the frames in the order it always did. The sharpness map's passes run the same way, its mean's sum still one ordered walk.
- **What is each frame's own runs a batch of frames side by side** (`PlanetaryFrameBatches`): the grade; the shift, the mesh and the
  sharpness map on an aligner and matcher twin a slot (`GlobalAligner.Twin`, `AlignmentPointMatcher.Twin`, the reference spectra shared
  and the scratch their own). The batch is then folded in the order given. A batch is a frame a core, fewer where its frames would pass
  256 MB. A de-rotated stack and a pooled one keep their walk (the de-rotator turns its reference along a run in capture order).

| | Wall | Mean cores |
|---|---|---|
| One core | 233 s | 1.00 |
| The folds in bands | 72 s | 6.1 |
| And the frames in batches | 54.6 s | 8.3 (11 once running) |

The live stack folds through the same kernels, so `planetary-live` was run again (the code of this change, Release):

| Recipe | Red: folded a second (of 216), before | after | Twin: folded a second (of 250), before | after |
|---|---|---|---|---|
| today's live default (gradient, plain) | 50.2 | 69.7 | 67.6 | 118.6 |
| and Lanczos-3 | 21.5 | 49.0 | 24.1 | 73.7 |

- **The replay was starved less**, not more: 191 a second delivered of 216 on Red (187 before), 201 of 250 on the twin (179).
- **Lanczos-3 still misses rule 3 on the twin** (73.7 against two thirds of 118.6), and a live default cannot ride on this machine's 16
  threads when a 4-core host folds a quarter as fast. #1174 (fold only the frames that grade best) stays the live stack's answer.

### The pipeline on real captures

The last of #1159's pre-registered checks ("How it is judged", above): the pipeline's limb undershoot at most 0.02 of the disk on each of
four real captures, legacy's beside it, and the masters side by side to the owner's eye with AutoStakkert's own result where the corpus
holds one.

**How it was run** (2026-10-03, Release, this PR's code). `tianwen planetary-stack` stacked each capture twice, at its defaults and with
`--legacy`, with the telescope R1 found for it: the 254 mm Newtonian, and for 2021-12-16 the Maksutov. `planetary-stack` now prints the
master's truth-free readings (`PlanetaryMasterScore.Undershoot`) whenever no truth is given.

| Capture | Frames | Pipeline: undershoot, rebound | Legacy: undershoot, rebound | Time, pipeline / legacy |
|---|---|---|---|---|
| 2022-09-03 Red (the timestamped conversion) | 12,990 | 0.0000, 0.0100 | 0.2027, 0.1410 | 310 / 90 s |
| 2024-12-15 Uranus-C, 12:36:43 (colour) | 30,000 | 0, 0, 0; up to 0.0049 | 0.066, 0.062, 0.126; up to 0.052 | 107 / 31 s |
| 2022-10-09 Jupiter, 10:42 (colour, a 200 px PIPP crop) | 15,011 | 0, 0, 0; up to 0.0010 | 0.103, 0.094, 0.067; up to 0.093 | 80 / 34 s |
| 2021-12-16 Saturn, 11:24 (colour) | 11,932 | declined (below) | | 21 / 7 s |

- **The rule holds on every Jupiter capture**: the pipeline's undershoot is 0 where legacy digs 0.07 to 0.20 of the disk below the sky.
- **By eye** (the owner's call; this is what the sheet showed):
  - On Red the pipeline matches AutoStakkert's sharpened 8 % stack (Drizzle 1.5) for detail, and legacy's deep dark ring is gone.
  - On 2022-10-09 the pipeline shows the festoons, the Red Spot and the belts' edges, which neither legacy nor AutoStakkert's P14 shows.
  - Two things remain. A dark trough at the limb (#1171). On the colour captures, a 2-pixel lattice over the disk (#1187, below).
- **Red's PIPP copy carries no frame times** (SharpCap's header date is year 1). Its pipeline master therefore falls back to the preset,
  and the output says so. The timestamped conversion is the capture R1 to R8 measured; the PIPP copy is the one AutoStakkert stacked.

**Found and fixed on the way** (each set down after its symptom, so labelled post hoc):

- **Saturn: the limb fit swallows the rings.**
  - On 2021-12-16 it fitted the globe at 28.1 px with a 9 px blur. The ephemeris and the plate scale give about 18.6 px.
  - The limb's edge then read a transfer that ROSE with frequency (0.231 at 0.1 cycles a pixel, 0.445 at 0.3), and the gains
    oscillated (-3.8, 22.3, -9.8, 7.6).
  - The bound freed a ring ansa as a moon (peak 0.95, 104 px above half), and the master came out green, with square blocks at the
    ansae. Its undershoot read 0.0000: only the eye caught it.
  - **The fix:** `PlanetaryLimbFit.Unmodelled` names a planet whose outline the fit cannot model. Saturn's sharpening then falls back to
    `PlanetaryDefault`, and the output says why. It is never de-rotated (`DerotationFor`), which would turn the rings as if they lay on
    the globe, and no truth-free reading is printed for it.
  - Lifting the gate is #1184: a limb model with the rings.
- **A tight crop has no sky for the metrics.**
  - A 200 px PIPP crop of 2022-10-09's 150 px disk leaves no pixel past the sky's 2.5 radii. The undershoot read NaN there, and the
    normalisation fell back to a sky of 0, where the real sky was 0.06 against a disk peak of 0.44.
  - **The fix:** `PlanetaryMetrics.SkyLevel` now reads the sky past 2.5 radii as before, else the farthest tenth of the pixels past
    the profile's 1.3 radii. A halo can only lift those, so the undershoot errs large, never small.
  - On a fixture cropped to 96 px the crop reads 0.0813 against the whole frame's 0.0808. Every frame that had sky past 2.5 radii reads
    exactly as before.
- **The sharpening's window was padded with zeros.**
  - `PlanetarySharpening` works in a power-of-two window about the planet: 256 px over that 200 px crop. Outside the frame it was zeros.
  - The moon finder's sky was then mostly padding, without noise, so its 20 sigma threshold fell to its 0.01 floor. The frame's own
    border passed as 16 moons a channel and was freed from the bound: coloured blocks along the border, up to 0.5 above the stack.
  - **The fix:** the window mirrors the frame about its edges. `ATightCropsSharpeningLiftsNoSkyAboveTheStackOutsideTheLimb` pins it:
    on a 130 px crop the zero padding freed 501 sky pixels, the most by 0.0054; mirrored, none.
  - Where the window fits the frame nothing moves: the twins' bounded masters read as #1185 left them (calibrated 0.777, limb profile
    error 0.0102), and Red's rebound 0.0100.

**Found, not fixed: the colour captures' lattice (#1187).** A colour stack keeps a small 2-pixel lattice from the demosaic, locked to the
sensor. The derived gains lift it with the finest bands:
- Uranus-C's band 1 gain is 17.75: its Nyquist amplitude is 11 to 19 times the stack's.
- 2022-10-09's band 2 gain is 18.3: there it is about 3 times.

A mono capture has none. Bayer drizzle to the sensor grid (#1091) leaves no demosaic to lift, so #1091's pre-registered judgement on
the colour twin, each master sharpened as the pipeline sharpens, is the measurement to run next. It ran next, and found otherwise
(below).

### A colour master's finest band

**Issue:** #1187. It also re-ran #1091's judgement.

**#1091's judgement, first.** It was run as registered on R5a's colour twin of 12:36:43 (twin 5, `uc-g4`, and its second seed): the
pipeline's master, demosaiced and with `--drizzle 1`, each sharpened as the pipeline sharpens. Summed over bands 1 to 4 and the three
colours, drizzle left 5.79 and 4.58 against the demosaic's 6.31 and 7.05, so the rule's letter held. It was not adopted:

- in both arms, every colour and both seeds, the sharpened band 1 was WORSE than the unsharpened stack's (red 2.26 against 0.94);
- the margin was band 1's alone;
- both sharpened masters show the lattice by eye, so drizzle is not #1187's remedy.

The derived band 1 gains (12 to 20) lift what lies above a colour plane's own Nyquist: a red or blue photosite every second pixel, so
its band 1 (0.25 to 0.5 cycles a pixel) holds little but noise and the CFA's residue. A luminance Nyquist reading misses that lattice,
which is chromatic and cancels in the channels' average: read it per channel.

**The candidates and the rule, set down before they ran** (2026-10-03, `PlanetarySharpenOptions.ColourFinestBand`):
- derived, as before;
- held: band 1 kept as stacked on every colour, the other gains fitted around it (`PlanetaryWaveletGains.Fit`'s `held`);
- held but green: held on red and blue only, since green's quincunx samples a little finer.

The colour default becomes the candidate with the least error summed over bands 1 to 4 and the three colours that beats derived on each
seed, demosaiced and drizzled alike, and shows no lattice by eye on Uranus-C and 2022-10-09. A mono master is untouched by construction.

**Read** (`planetary-sharpen --fix bounded --colour-finest all --truth`, the twin's four pipeline masters; the summed error):

| Master | The stack | Derived | **Held** | Held but green |
|---|---|---|---|---|
| demosaiced, seed 1 | 6.53 | 6.31 | **4.37** | 4.67 |
| demosaiced, seed 2 | 6.37 | 7.05 | **4.67** | 5.02 |
| drizzled, seed 1 | 6.59 | 5.79 | **4.49** | 4.29 |
| drizzled, seed 2 | 6.42 | 4.58 | **4.45** | 4.27 |
| all four | | 23.73 | **17.99** | 18.24 |

- **Held is the default.** Both held variants beat derived on every master. Held leaves the least over all four, and the least on the
  demosaic, the pipeline's default, on both seeds. Held but green wins only drizzled.
- **Band 1's error now falls below the stack's** (red 0.85 against 0.94; derived 2.26).
- **By eye**: on Uranus-C, derived and held but green carry the lattice (green is half the luminance) and held does not, with the belts'
  detail kept. On 2022-10-09, read per channel at Nyquist:
  - the stack has 0.0006 along x;
  - derived lifts it three times;
  - held leaves the stack's own 0.0006.

  #1187's done criterion was "within twice its stack's".
- **#1091's judgement, run again with held: the demosaic stays.** Seed 1 now favours the demosaic (4.37 against drizzle's 4.49), so
  drizzle no longer wins on both seeds. #1091 stays the owner's call, on this evidence.
- **The live view** takes the first channel's gains (`PlanetaryBestStack.DeriveGains`), now with band 1 held, so its dials and the
  batch agree on a colour capture as before.

### The trough at the limb

**Issue:** #1171.

The bounded fix leaves a dark trough just outside the limb: the floor holds the sharpening's first negative lobe at the sky, below
the glow the stack has there. Stretched hard (black at the sky, white at 3 % of the disk), it shows on 2022-09-03 Red as a black
crescent along the lower left.

**The candidates and the rule, set down before they ran** (2026-10-03, `PlanetaryLimbFix`):
- **bounded**, as today;
- **held outside**: outside the limb, the stack as it is;
- **model floor**: bounded, and outside the limb at or above the stack times its glow share, the planet's limb model through the
  pupil alone over the same model through the stack's blur (`PlanetaryDering.GlowShare`): the glow the truth keeps there;
- **blended**: bounded at the limb, blended to the stack by 1.1 radii.

All four leave moons free and hold the disk at the sky inside the limb.

The rule: the default becomes the candidate with the least limb profile error summed over the three twins, among those whose band error
summed stays within 2 % of bounded's. Bounded stays if none beats it. The winner is then seen on Red, its trough lessened and no ring
back.

A truth-free reading of the trough is `PlanetaryMetrics.LimbTrough`: the profile's deepest fall below the stack between 1.0 and 1.1
radii. It is used on Red only if it ranks the candidates as the fall below the truth does, with a Spearman of at least 0.8 on each twin.

**Read** (`planetary-sharpen --fix outside --truth`, the twins' masters #1185 scored, 2022-09-03 Red's):

| Candidate | Band error, summed | Limb profile error, summed | Trough below the truth (calibrated / without its still layer / warped) | Red: trough below the stack |
|---|---|---|---|---|
| **bounded** | **1.939** | **0.0252** | 0.0000 / 0.0020 / 0.0004 | 0.0715 |
| held outside | 2.089 | 0.0672 | 0 / 0 / 0 | 0.0367 |
| model floor | 1.940 | 0.0254 | 0 / 0.0009 / 0 | 0.0666 |
| blended | 1.955 | 0.0375 | 0 / 0 / 0 | 0.0667 |

- **Bounded stays.** It has the least limb profile error among the candidates within the band error's 2 %. Held outside is 7.7 % over
  that band, and blended's limb profile error is 49 % higher.
- **On the twins bounded has no trough against the truth** (0.0020 of the disk at most). What reads as a trough below the stack is the
  stack's own seeing glow, taken back inward where the truth has none.
- **The truth-free reading fails R3's rule** (Spearman -0.40, 1.00 and 0.80 on the three twins): it cannot tell a trough from the glow a
  sharpening rightly takes back, so it judges nothing on Red.
- **By eye on Red** (`pipeline/trough/red-trough.png` in the scratch): the crescent is one-sided, along the axis where R7a found Red's
  blur elongated (the twins' kernels are round). So it is this capture's limb misfit, not the bounded fix's.
  - Model floor turns it grey.
  - Blended leaves a thin arc.
  - Held outside removes it, at the twins' 7.7 %.
- **Model floor ties bounded on the twins** (band +0.05 %, limb profile +0.8 %) and lightens Red's crescent. Taking it would be a choice
  against the rule, as bounded's own was (#1168).
- **The owner's call (2026-10-03): chase the cause first, bounded meanwhile.**

#### The cause, and the fix

**The cause is the sharpening's side lobes outside the limb, not a misfit.** A reading of the limb sector by sector
(`tianwen planetary-limb-sectors`, `PlanetaryMetrics.SectorHalfLevelRadii` and `Harmonic`) set two hypotheses down before it ran:
- an outline off the planet's centre (H1): a first harmonic of at least 0.005 radii, the deepest trough within 45 degrees of the
  sector whose edge lies furthest inside the outline;
- a misshapen one (H2): a second harmonic dominating;
- the calibrated twin under 0.002 radii in both.

Both fell. Red reads a first harmonic of 0.0073 radii at 74 degrees and the deepest trough at 101 degrees, 135 degrees from that
sector, and the calibrated and warped twins read the same: 0.0073 and 0.0072 radii at 64 degrees, the deepest trough at 101. The twins
have a round blur and an exact outline, so the harmonic is the lighting (the lit limb is brighter, so its edge crosses half level
further out), not a misfit. Red's second harmonic, 0.0035 radii (0.17 px), is twice the twins' and far too small to matter.

Set beside the twin's truth at Red's stretch, bounded's limb is a black band and then faint bright arcs, where the truth is a sharp
disk in a faint smooth glow:
- **the black band** is the floor holding the sharpening's negative lobe at the sky;
- **the arcs** are its positive lobes, which the bound lets through up to the stack's seeing glow, far brighter than the truth's;
- **the one-sidedness** follows the lighting, the lobes scaling with the limb's brightness, on the twin as on Red.

An earlier reading here, that the crescent ran along Red's elongated blur from a misfit outline, was wrong: the twins have neither.

**The candidates that followed, each under the same rule, set down before it ran:**

| Candidate | Band error, summed | Limb profile error, summed | By eye |
|---|---|---|---|
| bounded | 1.939 | 0.0252 | the black band and the arcs |
| model glow: the stack times the model's glow share | 1.933 | 0.0189 | rings back out to 1.4 radii: the share divides by a model that falls to nothing, and carries its Airy rings |
| model outside: the planet's model through the pupil | 1.929 | 0.0159 | a clean limb; a square seam at the window's edge at a deep stretch |
| glow swapped: the stack less the model through its blur, plus the model through the pupil | 1.935 | 0.0244 | no seam, but a halo with rings: the stack's glow has wider wings than the kernel's |
| **model feathered: the model out to 1.5 radii, the stack by the window's inscribed circle** | **1.929** | **0.0159** | **a clean limb, no ring, no seam** |

- **Model feathered is the default** (`PlanetaryLimbFix.ModelFeathered`). It has the least limb profile error within the band error's
  2 %, 37 % under bounded's, and passes the eye check:
  - on the twins it looks like the truth;
  - on Red, the crescent is gone and the moon kept;
  - at a deep sky stretch there is no seam, only the spider's own spikes far out, which the stack carries;
  - on both colour captures the dark ring is gone, the limb clean at 6x (the blue fringe on 2022-10-09 is the dispersion R5a
    measured, in both).
- **The limb profile error stops at 1.2 radii**, which is why model glow and model outside could read best while ringing or seaming
  further out: the eye check out to 1.6 radii and over the whole frame is what caught both.
- **The owner had chosen bounded against the rule (#1168).** This takes it back by the rule and the eye together, for the cause the
  owner asked to be found.

## R9 A learned stage, only if the measurements say so

**Issue:** #1056 (conditional).

- **When:** only if R7 and R8 leave a measured gap to the RL oracle that a classical method cannot close.
- **How:** the deep-sky deconvolver's shape carries over. An unrolled Richardson-Lucy with the measured kernel as an input and a small learned prior between iterations (`deconvolver-training.md`, decided 2026-09-07), trained on R2's synthetic pairs.
- **Judged:** by the same gate as R7. The deploy point has to lie inside the training range in every coordinate, the kernel's width included.
- **Prior art:**
  - ASTRA-SR (arXiv 2609.26731, Ge, Cui and Liu, submitted 2026-09-22; read in full 2026-09-29, four pages) is a single-frame network, one channel, 256 px in and 512 px out.
    - **Training data:** about 20,000 Cassini ISS frames, curated from 400,000. Each is blurred on the low-resolution grid by an exposure-averaged, spatially varying PSF: a mean kernel plus 12 PCA kernels with per-pixel coefficients.
    - **The PSF:** six frozen-flow phase screens, propagated with HCIPy, with the layer strengths drawn from ESO Paranal's MASS records.
    - **Noise:** Gaussian only, 2 DN.
    - **Result:** 0.55 dB PSNR over NAFNet-class baselines on its own simulator's test set.
    - **Not stated:**
      - the telescope's aperture, wavelength or sampling, so no diffraction cutoff can be placed;
      - the exposure's sample count and the winds;
      - the source of the galaxy and nebula cases its test figure shows.
    - **Also missing:** no classical baseline (Richardson-Lucy is cited, not run); its real Jupiter and Moon examples are judged by eye; no code, weights or data are released.
    - **Its target is Cassini resampled to 2x**, so it is trained to put power above the telescope's cutoff, which R3 counts as invented.
    - **What is taken:**
      - R2's phase-screen model is the same physics, but per 4 ms frame rather than exposure-averaged, and with layer strengths fitted to the capture's own measured statistics, never to Paranal's.
      - The mean-plus-PCA kernel field is a compact form for R7's spatially varying kernel.
      - Its Fourier split is the case for R8's per-band gains: restoring a blurred image's Fourier MAGNITUDE gains 3.7 dB, restoring its phase 0.4 dB.
  - DIPLI combines a deep image prior with lucky imaging (the paper is CC BY-NC-SA and no code was released; corrected 2026-09-30, theme C of the literature, which also found that it scores real data by a Laplacian energy, which rewards over-sharpening).
  - Every learned planetary restorer found (PlaNet, AstroDiff, DIPLI, ASTRA-SR, FluxFlow) trains on turbulence alone, with no static layer or scatter, and none released code or weights (`docs/architecture/planetary-literature.md`).
  - Asensio Ramos et al. 2018 trained on MOMFBD-restored solar data.

## Rules carried over

- Pre-register each phase's predictions and kill lines in its run script before the run.
- Seed everything and report several seeds wherever a draw is involved (R2's synthesis).
- Compare only at a matched operating point: sharpenings at matched noise, stacks at matched frame count.
- A metric is validated against a truth before a conclusion rests on it, in this domain as in every new one.
- Judge at 1:1 with labelled comparisons.
- Long runs are detached and stopped through their stop file.
- One tool: the C# verbs measure and Python only plots.
- **The sources on `D:/` are never written.** Scratch is the scratch root (open question 4).
- **Licences:**
  - OPAL and WFCJ are CC BY 4.0: credit them in anything derived.
  - PDS data is public domain in practice.
  - AutoStakkert, RegiStax, WinJUPOS and PVOL results are comparisons only, never truth and never training targets.
  - PlanetarySystemStacker (GPL-3.0) and DIPLI are studied, never copied.

## Open questions for the user

1. ~~The telescope and aperture~~ **answered 2026-09-28:** a 10 inch f/4.7 Newtonian (the Saxon DeepSky collapsible, 1200 mm) with a Celestron Omni 2.5x Barlow for most sessions, and a Skymax 102 Maksutov for some. R1 tells them apart from the data.
2. **Which captures first:** agreed 2026-09-28, the Uranus-C Jupiter run of 2024-12-15 first. It was taken with the 10 inch after it came off its Dobsonian base. The order R1 onwards follows:
   - the Uranus-C Jupiter run of 2024-12-15 (the longest, timestamped, raw);
   - the ASI462MC Jupiter and Saturn of 2021-12-16 (raw);
   - the 2022-10-09 Jupiter, for its AutoStakkert and WinJUPOS references.
3. ~~The scratch root~~ **answered 2026-09-28:** `D:/Astro-Dataset/planetary`, keeping 109 GB free on `D:`. The 7z archives are unpacked one member at a time and cropped (R0). **2026-09-29:** the originals are kept, and the crops are working copies on the SSD.
4. ~~May the synthetic capture choose a parameter measured on the disk alone, while its sky's finest bands stay unmatched?~~ **Answered yes
   2026-10-02**, with the goal of adopting the measured defaults (#1072 is no longer blocked). The question as it stood: R2's kill line fired on the sky ring's bands 1 to 3 (1.4 to 1.5 times the real, beyond their sampling noise) and on nothing measured on the disk (R2 part 2, the verdict). Every parameter R3 to R8 names is measured on the disk. Until this is answered, R3 builds its metrics on the synthetic capture but chooses nothing on it. **R4 asks it first:** which estimator replaces the Laplacian, and at what keep. On the twin the gradient, fft3 and the reference gain rank frames at +0.87 to +0.99 against the Laplacian's +0.19, and on the real capture the same three agree with each other truth-free while the Laplacian agrees with none, so the estimator's choice does not rest on the twin alone; the keep does.

## Sources

- **Truth:**
  - [OPAL](https://archive.stsci.edu/hlsp/opal).
  - [WFCJ](https://archive.stsci.edu/hlsp/wfcj).
  - [Cassini ISS global maps](https://atmos.nmsu.edu/data_and_services/atmospheres_data/Cassini/sat_global_map.html).
  - [LROC WAC mosaic](https://astrogeology.usgs.gov/search/map/moon_lro_lroc_wac_global_morphology_mosaic_100m).
  - [SLDEM2015](https://pgda.gsfc.nasa.gov/products/54).
  - [SDO data rules](https://sdo.gsfc.nasa.gov/data/rules.php): too coarse for granulation at these scales, but same-time truth for sunspot geometry.
- **The literature, reviewed and verified 2026-09-30:** [`docs/architecture/planetary-literature.md`](../architecture/planetary-literature.md), with its three reviews in full (Fourier-domain lucky imaging and speckle; registration and dewarping; the blur, its inverse and learned restoration).
- **Method:**
  - [PlanetarySystemStacker](https://github.com/Rolf-Hempel/PlanetarySystemStacker).
  - [Hirsch et al. 2011, online MFBD](https://www.aanda.org/articles/aa/full_html/2011/07/aa13955-09/aa13955-09.html).
  - MOMFBD, van Noort et al. 2005.
  - Fried 1978 on the probability of a lucky exposure.
  - [PlanetMapper](https://github.com/ortk95/planetmapper) (MIT) for navigation and mapping.
  - [WinJUPOS](https://grischa-hahn.hier-im-netz.de/astro/winjupos/tutorials.htm).
  - [ASTRA-SR](https://arxiv.org/abs/2609.26731).
  - The [BlurXTerminator manual](https://www.rc-astro.com/blurxterminator-technical-manual/): sharp images convolved with synthetic PSFs, reconstruction losses only.
