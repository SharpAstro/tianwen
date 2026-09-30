# Planetary restoration by measurement

**Status: PARTIAL: R0 to R5a done** (written 2026-09-28, the user's request; R0 2026-09-29: the survey, the FITS video conversion and the tracked lossless crop; R1 2026-09-29: the ephemeris, the limb fit, which telescope; R2 2026-09-29: rendered truth, T1 passed, and a synthetic capture that matches the real one on the disk, its kill line firing on the sky's finest bands; R3 2026-09-30: the metrics, the limb's undershoot validated, the halves' agreement not, pending R7's blur; R4 2026-09-30: per frame, the Laplacian ranks an 8-bit capture's frames near chance and the mid bands rank them well, on the twin and truth-free on the real capture, the choice waiting on open question 4 and the per-point half on #1071; R5 part 1 2026-09-30: phase correlation places 8-bit frames and points three times worse than a plain one, every stack better plain (#1074), and the real capture's warp visible only plain; part 2 2026-09-30: the dewarp cannot follow this capture's warp, 0.6 px over 10 px, the mesh the stack applies recovering 0 to 3 % of it, and the kill line fires; part 3 2026-09-30: the bilinear kernel is the stack's blur, sinc^2 in transfer, and Lanczos-3 lifts band 1 10 to 12 % (#1086), a correlation's peak is climbed rather than fitted by a parabola, a stacked reference rescues phase correlation, and the three-cornered hat compares registrations with no truth where the twin clears the triple, which AutoStakkert's track and our limb fit, both reading the outline, never do; the literature behind what comes next is `docs/architecture/planetary-literature.md`, its follow-ups #1081 to #1085; R5a 2026-09-30: a colour twin of 2024-12-15's Uranus-C capture, calibrated per colour, on which Bayer drizzle to the sensor grid beats the demosaic above a plane's Nyquist and nothing past the sensor grid pays, the adoption #1091 and a smaller drop #1092; on the way, a camera's corrupted readout frame kept out of every grade, and an alignment point's patch cut at the exact global shift; R6 part 1 2026-09-30: the spheroid's projection both ways and a de-rotation that carries the albedo, finished stacks carried to one epoch leaving 0.46 of the difference at 11 minutes, north decided by the stacks' agreement). Milestone `planetary-restoration`: R0 #1048, R1 #1049, R2 #1050, R3 #1051, R4 #1052, R5 #1053, R6 #815, R7 #1054, R8 #1055, R9 #1056 (conditional).

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

**Issue:** #1071.

`PlanetaryDegrade` applies one PSF to a whole frame, and the calibrated twin has no warp. On it, a point's quality is its frame's up to noise, and no per-point estimator can be ranked. The alignment-point stack weights each pixel by `FrameSharpnessMap`, a smoothed Sobel energy (the family that ranks frames well), and each frame by the Laplacian's score (which does not). Measuring the per-point half needs:
- the free air at an altitude, so points a few arcseconds apart look through different parts of it, calibrated against the real capture's warp (R2: 0.787 px RMS a point, its correlation length at most 9 px);
- the quality's spread over the frames and its coherence from one to the next matched to the real capture's, both read by the reference gain.

#### R4 keeps are scored after restoration

**Issue:** #1083 (the literature: `docs/architecture/planetary-literature.md`, theme A).

R4's keeps minimise the error of a raw stack, and a raw stack's band 1 error is mostly the blur R8 is there to remove. Decomposed from R4's own numbers (theme A section 1.4, derived), band 1's error^2 at a 5 % keep is about 0.34 transfer deficit, 0.06 a residual that does not average down, and 0.015 frame noise. So the keep is chosen where it is used:
- R4's selections, per-band matched weights (each frame by its transfer) and a Fourier burst accumulation exponent sweep (Delbracio and Sapiro 2015), each scored after an oracle per-frequency Wiener, then after R8's gains;
- the oracle ceiling of per-frequency selection (Garrel, Guyon and Baudoz 2012; Mackay 2013) from the twin's noise-free frames, warp on and off, before anything is built.
- **Pre-registered:** band 1's best keep moves from 5 to 10 % to half the frames or more; the ceiling of per-frequency selection is under 15 % in band 1's transfer on this twin, whose frames vary little (D/r0 about 3), where the literature's large gains came from D/r0 of 7 to 30.

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
  - The residual drift profile matches the known zonal wind profile's shape.

### R6 results, part 1: finished stacks carried to one epoch (6b)

**Measured** (2026-09-30) on 2024-12-15's Uranus-C session: single-file stacks (the first 3,000 frames, keep 5 %, plain correlation, Lanczos-3), each at its file's middle, compared inside 0.9 radii of the later one's disk, each on its own disk level. Part 2 (each frame to the capture's epoch, 6a) and part 3 (belt latitudes against OPAL, and the drift against the zonal winds) are still #815's.

- **The geometry both ways** (`PlanetaryProjection`): a pixel to the planetographic latitude, west longitude and lighting it sees, and a latitude and longitude back to a pixel. `PlanetaryRender` casts its rays through it, the same arithmetic, so every render is unchanged.
- **A de-rotation carries the ALBEDO** (`PlanetaryDerotation`): each output pixel's latitude and longitude at the target instant, found where they lay at the source instant and sampled there by Lanczos-3, divided by Minnaert's lighting where it was and multiplied by the lighting where it goes, with the limb fit's k. On a map rendered ten minutes apart, 0.0706 RMS falls to 0.00044; carried as brightness, 46 % of it went.
- **A pixel is de-rotated only from a source inside 0.9 radii** (`Derotation.Covered`). Nearer the limb a stack is its seeing-blurred edge, not Minnaert's law, and over 31 minutes the side the rotation turns into view read its sources out there: relit samples reached 195 times the stack's peak and the de-rotation doubled the difference (1.83 of none) before the limit.
- **North is decided by the agreement, and the limb fit's was right on every pair** (`tianwen planetary-derotate`); turned over, the planet turns backwards and every pair gets worse (1.26 to 1.55 of none).
- **The pairs**, over the pixels the de-rotation covers:

  | Pair | Apart | Rotation | Not de-rotated | De-rotated | Of none |
  |---|---|---|---|---|---|
  | 13:02:24 and 13:07:47 | 5.38 min | 3.25 deg | 0.01108 | 0.00805 | 0.727 |
  | 12:56:44 and 13:02:24 | 5.67 min | 3.43 deg | 0.01236 | 0.00836 | 0.676 |
  | 12:56:44 and 13:07:47 | 11.05 min | 6.68 deg | 0.01753 | 0.00812 | **0.463** |
  | 12:36:43 and 13:07:47 | 31.06 min | 18.78 deg | 0.02853 | 0.01932 | 0.677 |

- **The pre-registered claims:**
  - **The difference without de-rotation grows with the gap**: it does, 0.011 to 0.029.
  - **De-rotated, what is left is the same at every gap**: 0.0081 to 0.0084 for the three pairs that share their camera's settings. The 31-minute pair's 0.019 is not the rotation: its earlier capture was taken at an analogue gain of 215 against 263 (4.8 dB less, its stack peaking at 0.41 of full scale against 0.71) and 2.9 degrees warmer, so the two stacks differ in what the camera made of them.
  - **The 11-minute pair meets the plan's half**: 0.463. **The 31-minute pair beats it clearly: it does not** (0.677), for the camera's reason above; a pair 31 minutes apart at one gain would say more, and this session has none.

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

## R8 Wavelet gains from the measured blur and noise

**Issue:** #1055 (the wavelet half of #817).

- **First principles:** sharpening is an inverse filter. The gain that minimises error at a band with transfer `H` and signal-to-noise `S/N` is Wiener's, `H / (H^2 + N/S)`, applied per a-trous band. `H` comes from R7's kernel, and `N` per band from the split-half difference (T2).
- **Measured:**
  - The derived gains against every preset (`PlanetaryDefault`, `Bandpass`, `Combo`) at matched noise, on T1 fidelity and R3's ringing gate.
  - On the real captures, the split-half metrics against AutoStakkert's and RegiStax's results as comparisons.
  - This is the AUTO mode's wavelet half (#817), measured rather than chosen by eye.
- **Pre-registered:**
  - The derived gains beat every preset at matched noise on per-band fidelity and pass the ringing gate.
  - Kill line: if a preset wins, the Wiener model is missing a term, most likely the resampling blur or the noise's colour; find which.

### R8 how far a real per-band gain can reach

**Issue:** #1085 (the literature: theme C, sections C and D).

Before the gains are derived, the ceilings they are judged against:
- **ASTRA-SR's oracle swap, per band:** the stack's Fourier magnitude replaced by the truth's with its phase kept, and the other way round. A long exposure's transfer is real and positive, which is why restoring the magnitude gains most, but the twin's still layer is one static screen whose transfer has a phase: whether a real, isotropic per-band gain can reach the oracle is this measurement.
- **A multi-frame Wiener with the twin's true per-frame PSFs**, the bound on anything multi-frame blind deconvolution or Fourier-domain lucky imaging could add.
- **Three regularised inverses at a matched band 3 transfer, scored on the limb:** per-band Wiener with Conan et al. 1998's power-law object spectrum (the gain formula above with a prior the capture can fit), Richardson-Lucy on offset-subtracted electrons, and an L1-L2 edge-preserving prior (MISTRAL, Mugnier et al. 2004), each with the measured core-plus-scatter kernel and against a single Gaussian.
- **Pre-registered:** the multi-frame Wiener oracle gains under 10 % on the calibrated twin and over 20 % with the still layer off; Richardson-Lucy and L1-L2 leave at most a third and a half of Wiener's limb undershoot; a single Gaussian rings 1.5 times more. No published quantitative ringing metric for planetary sharpening was found (Lewis 2020 measures the Mars edge-rind's width, not its depth), so R3's limb undershoot stays the penalty.

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
4. **May the synthetic capture choose a parameter measured on the disk alone, while its sky's finest bands stay unmatched?** R2's kill line fired on the sky ring's bands 1 to 3 (1.4 to 1.5 times the real, beyond their sampling noise) and on nothing measured on the disk (R2 part 2, the verdict). Every parameter R3 to R8 names is measured on the disk. Until this is answered, R3 builds its metrics on the synthetic capture but chooses nothing on it. **R4 asks it first:** which estimator replaces the Laplacian, and at what keep. On the twin the gradient, fft3 and the reference gain rank frames at +0.87 to +0.99 against the Laplacian's +0.19, and on the real capture the same three agree with each other truth-free while the Laplacian agrees with none, so the estimator's choice does not rest on the twin alone; the keep does.

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
