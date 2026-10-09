# A Physical PSF Model per Optical Train: the Optics' Aberrations over the Field

**Status: NOT STARTED; tracked by #1296, #1297 and #1298 (milestone `physical-psf-model`).** Written
2026-10-06 on the owner's direction: "IMHO the closer we try model actual physics the better we will get". It serves the
deconvolver's synthetic blur ([deconvolver-training.md](deconvolver-training.md), H7), the star remover's injected
stars ([star-remover-training.md](star-remover-training.md), R1) and the collimation report
([camera-collimation.md](camera-collimation.md), C0), which all need the same field model and today have none.

## Why the physics

Three results so far say a synthetic degradation is worth what its realism is worth:
- **R2a's Gaussian stars** taught the remover 47.6 points less removal at 100 to 1000 sigma than stars drawn with the
  field's own measured profile (H2, apart on every seed of R2a and so far in R2b).
- **The planetary twins** matched a real capture's statistics only once the real pupil, a still turbulence layer at the
  telescope and a wide scatter were in ([planetary-restoration.md](planetary-restoration.md), R2).
- **The injected noise** had to take each master's own shape (drizzle against demosaic), not one recipe
  ([denoiser-training.md](denoiser-training.md), E16b).

And two say realism has to be the RIGHT physics, measured. R2a's at-site arm placed its stars where R0 found real ones,
which reads as more realistic, and learned less on every axis. The planetary calibration tried a static defocus first:
it matched the limb, and the frames' motion ruled it out. So every term below is fitted to the archive's own stars and
checked against stars it was not fitted on before anything trains on it.

**The archive already shows a field term the current model cannot make.** Red's FWHM falls from centre to corner on
most colour trains while green's and blue's rise. It was carried as a measurement defect for a long time and is physics:
autofocus minimises the star where the flux is, at about 500 to 550 nm, so red sits defocused, and a curved,
chromatic focal surface moves red's defocus and green's astigmatism in opposite directions across the field
(`DatasetPsfNoiseReport`, the field-radius profile per train and filter). A focal surface with a per-wavelength focus
predicts that; an elongated Moffat of random angle cannot.

**And the owner wants the field corrected, not only modelled.** The ASI585MC behind the ZS61 has a flattener whose
corners leave visibly elongated stars (Tarantula and SMC 2024-10-02, Vela SNR 2025-01-30; deconvolver-training.md,
"Real test data for the per-window kernel"), and E7.4 found two crops of one frame wanting kernels 1.3 times apart.

## What is modelled today

| where | what it draws | what it leaves out |
|---|---|---|
| Deconvolver's blur (`DatasetDegradationExporter`, blur mode) | one Moffat per tile (optionally one per channel, width from the measured channel ratio, beta from that channel's fit), elongation uniform in 1 to 1.25, angle uniform | any field dependence: `FieldRadius` is written on every row as H7's covariate and never steers the draw; coma's one-sided flare; per-channel focus |
| Star remover's injector (`InjectionPopulation`, `StarProfile`) | the master's own profile per channel, an elliptical Moffat plus a radial halo table, elongated and turned as the nearest measured stars are | anything not centrally symmetric (coma, trefoil); spikes |
| The bake's PSF report (`DatasetPsfNoiseReport`) | median FWHM and ellipticity by field radius, per train, filter and channel | direction (radial or tangential), odd harmonics; it is a report, nothing reads it back |
| Planetary twins (`Optics/Pupil`, `ShortExposurePsf`, `PhaseScreen`, `PlanetaryDegrade.DefocusNm`) | the real pupil (obstruction, spider vanes) through diffraction, Kolmogorov seeing with a still layer at the telescope, and optionally a defocus as a Zernike RMS | every other static aberration, harmless on-axis where a planet sits |

## The model

A star at field point **h** (the vector from the optical axis's centre on the sensor, normalised to the corner) in
channel c is imaged as

```
PSF(h, c) = pixel * stack * guiding * seeing * sum over lambda of w_c(lambda) |FT(P(u) exp(i 2 pi W(u; h, lambda) / lambda))|^2
```

where `*` is convolution and each term is a physical one with its own parameters:
- **P(u), the pupil:** `Optics/Pupil`, the aperture, its central obstruction and spider vanes. A Newtonian's spikes
  come from it with no model of their own (star-remover-training.md, section 8's open question).
- **W, the static wavefront**, Hopkins' expansion in the pupil radius rho, the pupil angle theta measured from h, and
  the field radius H:
  - defocus `W020 rho^2`, per WAVELENGTH through longitudinal colour (the focus autofocus left), so a broad R, G or B
    band is a range of defocus that widens its channel's PSF, never one defocus per channel;
  - spherical `W040 rho^4`;
  - coma `W131 H rho^3 cos(theta)`, pointing radially, growing with H;
  - astigmatism `W222 H^2 rho^2 cos^2(theta)` and field curvature `W220 H^2 rho^2`;
  - lateral colour `W111(lambda) H rho cos(theta)`, an image shift per wavelength growing linearly with H, which a
    broad band smears radially;
  - sensor tilt, a defocus linear across the sensor;
  - the optical axis's centre on the sensor, free, since collimation and a flattener's spacing move it. One shared
    centre is the simplest misalignment only: in a misaligned system each aberration field has its own shift, and its
    astigmatism turns field-constant and binodal (nodal aberration theory; Thompson, JOSA A 22, 1389, 2005). P1 frees a
    per-aberration shift where the residuals ask for it.

  Distortion (`W311`) is the WCS's and the registration's, never the PSF's. The physics was checked against Hopkins'
  third-order terms in the review on #1399.
- **w_c(lambda), the channel's passband:** filter curve times QE times CFA, from the curves SPCC already reads
  (`FilterCurveDatabase`, the die's QE curve), weighted by the STAR's own spectrum, `w_c(lambda) F_star(lambda)`: SPCC
  already knows each matched star's colour, and a red and a blue star through one broad band are imaged differently.
- **Atmospheric dispersion:** a shift per wavelength along the vertical, from the altitude the sub was taken at, which a
  broad band smears into an elongation toward the zenith at low altitude.
- **Seeing:** Fried's long-exposure transfer, one parameter r0 per sub (r0 scaling as lambda^(6/5) over a passband),
  with the measured Moffat kept as the labelled fallback. Pure Kolmogorov seeing predicts ONE Moffat beta, about 4.765
  (Trujillo et al., MNRAS 328, 977, 2001), so the archive's betas (2 to 12, correlated with FWHM) cannot come from seeing
  alone: whether the stack's blur, guiding and scatter account for the rest is part of P2's test, not assumed.
- **Guiding:** an anisotropic Gaussian from the session's `GUIRMSRA` / `GUIRMSDE` cards where TianWen captured it,
  free where it did not, and a PRIOR either way, never a known: the cards are the guider's error, not the main camera's
  star motion, which differential flexure and the periodic error between corrections also move.

The sum over lambda is taken on one angular grid: each wavelength's `|FT|^2` has its own scale (lambda / D) and is
resampled onto the common grid before it is summed.
- **The stack's own blur:** registration error, the warp kernel and the demosaic or drizzle, measured per master as the
  residual between the model and its stars (`docs/architecture/stacking-render-pipeline.md` on why a master is wider
  than its subs).
- **pixel:** the box over each photosite, per Bayer colour on a colour camera.

## P1: measure each train's aberrations from its own stars

Tracked by #1296. **One measurement for three consumers**: the collimation report's C0 (#823) is the same fit, so it
is built once, in Lib, and the report reads it.

- Per master and channel, every unsaturated, isolated star's shape as a **forward model**, never moments alone: the
  PSF above rendered at the star's position and fitted to its pixels (the limb fit's principle). Moments (FWHM,
  ellipticity, angle) and the azimuthal harmonics about the peak (m = 1 coma, m = 2 astigmatism, m = 3 trefoil) are
  reported beside it, as C1 reads them.
- **Fitted on SUBS, or on a master of one sensor orientation, never across one.** The aberration field is fixed to the
  sensor about the optical axis, so a master registered across a meridian flip, a camera rotation or the sessions P1
  pools averages that field taken at different sensor positions (an off-centre axis lands in two places after a flip),
  and the warp or drizzle adds its own blur. Each star carries its frame's transform into the forward model.
- **The static terms are shared by every session of a train and filter; focus, r0 and guiding are each session's.**
  The fit pools a train's sessions, so a coefficient too small to see on one night is read off many.
- **What a 2 to 3 px star cannot tell apart is given a prior, not left free** (the review on #1399):
  - defocus, spherical and seeing all broaden a star symmetrically and differ only in its wings, and focus and r0 are
    both free per session, so pooling does not separate them. Each session's focus offset comes from its own
    autofocus run (the hyperbola and the verification frame already measure it), which is a known diversity: it also
    lifts the even terms' sign ambiguity, since a pupil field and its flipped conjugate give the same image;
  - a misaligned scope's field-constant astigmatism is guiding's ellipticity field, separable only where guiding
    varies across sessions while the optics do not, which is why guiding is a prior (above);
  - what IS identifiable is the field VARIATION (curvature and tilt are a defocus that changes across the field while
    r0 does not) and the spread between channels (longitudinal colour against seeing's weak wavelength dependence).
- The output per (train, filter): the coefficients with their errors AND their correlation matrix, the sign convention,
  the axis centre, the tilt plane, and per session its focus and seeing. An error bar that excludes zero says nothing
  about whether a coefficient is entangled with another. Written as a store sidecar beside the PSF report, which
  renders it.
- **Expected, and falsifiable:** the ZS61's corners and the SH61's measure a field term past their errors; the Samyang
  135 reads near flat (H7's own prediction); red's inversion falls out as a per-channel focus offset with field
  curvature, not as a separate red term.

## P2: render, and validate against stars the fit never saw

Tracked by #1297. A renderer (`Optics`, beside `ShortExposurePsf`) that takes a train's coefficients, a field point,
a channel and a session's seeing and returns the pixel PSF, deterministic and pixel-integrated.

The test, pre-registered before the first fit is read: hold out whole sessions per train, and on their stars compare
the model's FWHM, ellipticity, angle and m = 1 to 3 harmonics with the measured ones, per field-radius bin and
channel, against two baselines on the same stars: today's injector profile (nearest stars' elongation) and today's
exporter draw. The model goes forward for a train only where it beats both there.

## P3: the training arms

Tracked by #1298.
- **The deconvolver (H7):** an exporter arm whose extra blur is the train's aberration at the tile's field point, at a
  drawn severity, against today's stationary Moffat. The blur ADDS to the master's own: wavefronts add, PSFs do not
  convolve exactly, so the extra kernel is drawn as the difference between the model at severity s and at the master's
  own fit, and how well that composes is measured on the renderer before the arm is trusted (the open question below).
- **The star remover (R1):** injected stars rendered from the model at their site, the halo's measured far wing kept
  where the model's diffraction and scatter fall short, against today's profile. Spikes come with the pupil on the
  SWQ8 Newtonian masters.
- Each arm is pre-registered in its own plan with the usual seeds and kill, and reads its gains per train: the model
  should pay most where the field term is largest.

## Phasing

| phase | scope | status |
|---|---|---|
| P1 | the forward-model star fit per (train, filter), pooled over sessions; the store sidecar; the report reads it; C0 (#823) reads it | NOT STARTED, #1296 |
| P2 | the renderer, and the held-out validation against both baselines | NOT STARTED, #1297 |
| P3 | the deconvolver arm (H7) and the injector arm, pre-registered in their plans | NOT STARTED, #1298 |

P1 first, because it alone answers which trains have a field term worth modelling, and C0 ships from it as a product
whatever P3 finds.

## Open questions

- **How an extra blur composes with a master's own.** For a seeing-dominated PSF the incoherent composition (convolve
  the extra kernel) is close; for the aberration-dominated corners of a fast train it is not. Measured on the
  renderer in P2: the model at severity s against the master's model convolved with the drawn extra kernel.
- **Whether a 2 to 3 px seeing-limited star carries enough of its aberrations to fit.** Pooling sessions, the forward
  model and the priors above are the answer proposed; P1's coefficient errors AND their correlations are the test, and
  a train where a coefficient spans zero or is entangled with another keeps today's draw.
- **Polychromatic cost.** A passband sum per star per channel is a few FFTs on a small grid; the renderer caches per
  (train, filter, channel, field cell).
