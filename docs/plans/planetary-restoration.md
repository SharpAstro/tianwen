# Planetary restoration by measurement

**Status: PARTIAL: R0 done** (written 2026-09-28, the user's request; R0 2026-09-29: the survey, the FITS video conversion and the tracked lossless crop). Milestone `planetary-restoration`: R0 #1048, R1 #1049, R2 #1050, R3 #1051, R4 #1052, R5 #1053, R6 #815, R7 #1054, R8 #1055, R9 #1056 (conditional).

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
  - **The decider is T1, and it is R2's first step** (#1050): render an OPAL map at 2022-09-03's geometry (belts and dark poles included), blur it with a Moffat PSF, and fit it with this same code. The rendered geometry is known exactly, so the fit's own error is measured without an outline set by eye. Until that passes (centre within 0.2 px, radius within 0.5 %, the plan's numbers), the fit's centre is trusted to about a pixel and its radius to about 2 %. R2 may render and degrade with that, but no later phase leans on the geometry below it.
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
  - Kill line: a drizzle that wins where the frames' cutoff stays under the input grid's Nyquist is fabricating detail, the RL oracle's rule, and the metric is checked before anything is concluded.

## R6 De-rotation

**Issue:** #815 (planetary-stacking.md's phases 10 and 11, measured here).

- **What:** the phases #815 plans, on R1's geometry.
  - 6a: each frame reprojected to the capture's mid epoch before stacking.
  - 6b: finished stacks and colour channels to a common epoch.
  - Each reprojection runs through the oblate spheroid in longitude, never a flat image rotation.
- **Measured:**
  - Two stacks from the two halves of one long run (the 16-minute, 441,558-frame Jupiter run), derotated to one epoch, must agree better than the same pair without derotation.
  - WinJUPOS's own derotated composites of the 2022 captures are the external comparison.
  - What remains after derotation is physics, not error: zonal winds against System III, and the Great Red Spot's drift. It is measured and reported, never fitted away.
- **The user's "measure the bands":** belts are the check on the geometry (R1). The rotation itself comes from the ephemeris, since belts move with their own winds and would bias a rotation fitted to them.
- **Pre-registered:**
  - R1's belt check, moved here with the projection it needs: the belt-edge latitudes on the projected map of the 2022 Jupiter stacks agree with OPAL's within 1 degree, in planetographic latitude.
  - Derotation cuts the half-to-half belt difference RMS by at least half on the 16-minute run.
  - The residual drift profile matches the known zonal wind profile's shape.

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
  - DIPLI combines a deep image prior with lucky imaging (code CC BY-NC-SA, so learn only).
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

## Sources

- **Truth:**
  - [OPAL](https://archive.stsci.edu/hlsp/opal).
  - [WFCJ](https://archive.stsci.edu/hlsp/wfcj).
  - [Cassini ISS global maps](https://atmos.nmsu.edu/data_and_services/atmospheres_data/Cassini/sat_global_map.html).
  - [LROC WAC mosaic](https://astrogeology.usgs.gov/search/map/moon_lro_lroc_wac_global_morphology_mosaic_100m).
  - [SLDEM2015](https://pgda.gsfc.nasa.gov/products/54).
  - [SDO data rules](https://sdo.gsfc.nasa.gov/data/rules.php): too coarse for granulation at these scales, but same-time truth for sunspot geometry.
- **Method:**
  - [PlanetarySystemStacker](https://github.com/Rolf-Hempel/PlanetarySystemStacker).
  - [Hirsch et al. 2011, online MFBD](https://www.aanda.org/articles/aa/full_html/2011/07/aa13955-09/aa13955-09.html).
  - MOMFBD, van Noort et al. 2005.
  - Fried 1978 on the probability of a lucky exposure.
  - [PlanetMapper](https://github.com/ortk95/planetmapper) (MIT) for navigation and mapping.
  - [WinJUPOS](https://grischa-hahn.hier-im-netz.de/astro/winjupos/tutorials.htm).
  - [ASTRA-SR](https://arxiv.org/abs/2609.26731).
  - The [BlurXTerminator manual](https://www.rc-astro.com/blurxterminator-technical-manual/): sharp images convolved with synthetic PSFs, reconstruction losses only.
