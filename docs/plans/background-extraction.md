# PLAN: Background extraction (ABE / gradient removal)

> Status: **Phases 1 and 2 SHIPPED 2026-09-02** (`TianWen.Lib/Imaging/BackgroundExtraction/`; the
> section "Implementation" below is the record of what was built, what it measured and what changed
> the design on the way). Phases 3 to 5 not started, and Phase 3 stays unbuilt unless measured to add
> something. **The three Siril reference scripts were read in full on 2026-09-02** (section
> "Reference review" below); two of them changed the design, so read that section before the
> Algorithm one, which predates it.

## Goal

Add classical (non-AI) background extraction to TianWen. Fits a smooth model
of the sky background (polynomial, optionally refined with RBF interpolation)
through user-placed or auto-placed sample points, then subtracts it from the
source frame. This is the standard "automatic background extraction" /
"dynamic background extraction" feature familiar to anyone who has used
PixInsight ABE/DBE, Siril gradient removal, or GraXpert. The output is a
flat-field-corrected image ready for the downstream color-calibration +
stretch + AI-enhancement chain.

Reference implementation: SetiAstroSuite Pro's `abe.py`
([github.com/setiastro/setiastrosuitepro](https://github.com/setiastro/setiastrosuitepro),
`src/setiastro/saspro/abe.py`, GPL-3.0 per the GitHub API; 2246 lines including the Qt dialog,
the headless core `abe_run` is lines 43-575). **Read from GitHub on 2026-09-02** (section
"Reference review"); there is no local checkout on this machine (`~/source/repos/` holds only the
four org roots, so the `~/source/repos/other/setiastrosuitepro` path recorded on 2026-08-29 is
stale), a read-only copy sits at `C:\temp\tianwen-scratch\saspro-2026-09-02\`. Siril's
`AutoBGE.py` is the OLDER SAS v2 form of the same algorithm ("from Franklin Marek SAS code") and
SAS Pro has since fixed the one thing wrong with it, so where the two disagree SAS Pro is the
reference.

## Non-goals (v1)

- **AI-based gradient removal.** GraXpert-style deep-learning gradient
  estimation is a different tool with its own training distribution. The
  classical polynomial + RBF route is good enough for the majority of
  amateur-astrophotography data and avoids another model fetch + dep. Add
  later if real-world data shows the polynomial+RBF route consistently
  underperforming.
- **Interactive sample-point editing UI.** The first cut runs headless with
  auto-placed samples + scripted exclusion regions. GUI integration (the
  Qt-equivalent drag-and-drop polygon editor SAS Pro ships) is Phase 4+.
- ~~**Per-channel modelling for narrowband mosaics.** Single background model shared across
  channels in v1.~~ **Withdrawn 2026-09-02**: every reference fits each channel separately (AutoBGE
  shares sample POSITIONS and fits per-channel VALUES), and a shared model cannot remove a colour
  gradient, which is the ordinary light-pollution case. Per-channel from v1.
- **Multi-frame consistency.** ABE operates on a single integrated image; if
  multiple frames need matched backgrounds, run ABE per frame then matched
  normalisation downstream (cross-reference `stacking.md`).

## Pipeline placement

ABE belongs in the **linear-domain, pre-stretch** stage. The polynomial / RBF
model assumes the gradient is *additive* on the sky background, which is true
in linear data. After MTF stretch the same additive gradient becomes
non-additive (the stretch curve is steeper near the shadows), and a
polynomial fit on stretched data overshoots/undershoots. PixInsight DBE,
Siril, GraXpert all run pre-stretch for exactly this reason.

```
calibration -> stacking -> cosmetic correction
  -> ABE (gradient removal)           <- THIS PLAN (linear domain)
  -> color calibration (SPCC, BG neutralisation, WB)
  -> Image.MtfStretch                 <- enter non-linear/display domain (ai-enhancement.md)
  -> Darkstar (star removal)
  -> split: starless + stars-only
  -> StellarSharpener  |  NonStellarDeconvolver
  -> recombine + curves + HDR
  -> Image.MtfUnstretch (skip if exporting display PNG)
```

`SharpenPipeline.SharpenRequest.Source` (from `ai-enhancement.md`)
already assumes its input is the post-ABE + post-color-calibration image, so
no design change is needed there. ABE produces clean linear input for
downstream consumers.

## Algorithm

Two-stage fit, both lifted from SAS Pro's `abe.py`. **Written before the reference review below,
which recommends a different Stage 2 and corrects the Output section; the review wins where they
disagree.**

### Stage 1: Polynomial fit (always on)

1. **Downsample** the source to a manageable working resolution (SAS Pro
   uses `cv2.INTER_AREA` resize; we'll use Lanczos or equivalent from
   `DIR.Lib`). Keeps the polynomial fit milliseconds-fast even on
   100-megapixel inputs.
2. **Generate sample points** automatically. SAS Pro's strategy:
   - Anchor points at the four corners, midpoints of each border, and
     quartile positions interior. Roughly 20-30 default samples.
   - For each anchor, **gradient-descent locally to the dimmest spot**
     within a small patch (15 px) so the sample lands on actual sky
     background, not a star.
   - **Avoid bright regions** via a luminance threshold check; reject any
     anchor whose neighbourhood is too bright.
   - **Caller-supplied exclusion polygons** for known bright objects
     (galaxy core, nebula bright cores, dust lanes the user wants
     preserved). Empty in v1's headless mode.
3. **Fit a polynomial** of degree 1-6 (caller-configurable, default ~4)
   through the surviving samples. Each output pixel `(x, y)` gets a
   background value `B(x, y) = sum_{i+j<=degree} c_{ij} * x^i * y^j`. SAS
   Pro uses a Numba-compiled `build_poly_terms` / `evaluate_polynomial`;
   in C# we get the same hot-loop speed via `Vector<float>` SIMD.
4. **Upscale** the small background model back to full resolution via
   Lanczos interpolation (`DIR.Lib.Image.Resize` or equivalent).

### Stage 2: RBF refinement (optional)

When polynomial residuals are still significant (typical for partial-frame
gradients caused by light-pollution domes, mirror-flat mismatches, or
satellite trail-cleanup residuals), fit a **radial-basis-function**
interpolant (multiquadric kernel) on the residuals and add it to the
polynomial model:

```
B_full(x, y) = B_poly(x, y) + B_rbf(x, y)
```

SAS Pro uses `scipy.interpolate.RBFInterpolator` with multiquadric kernel
and a smoothing parameter. The C# port options:

- **Port the math directly** (the multiquadric formula and the dense
  linear solve are tractable; `~150 LOC` C# with `MathNet.Numerics` for the
  LU solve, or hand-rolled with `Vector<float>` SIMD).
- **Defer Stage 2** to a follow-up. Polynomial-only ABE handles the
  ~80% case; RBF is the polish step.

### Output

A new `Image` with the background subtracted **and the background's median (or mean) added back,
so the sky LEVEL is preserved and only the gradient's SHAPE is removed.** This section used to say
"the fit's minimum becomes the new zero baseline plus a ~1% pedestal" and attributed that to SAS
Pro; it is wrong on both counts. All four references read on 2026-09-02 restore the level
(SAS Pro and AutoBGE re-add the original median, AutoGradientRemoval the background median,
GraXpert the background mean), and so does our `OnnxBackgroundExtractor`, which is what keeps the
classical and the AI gradient paths interchangeable. The one wrinkle is SAS Pro's clip-free
finisher: after restoring the median it lifts the whole frame so the minimum is zero if anything
went negative, and compresses the ceiling about the median if anything exceeds 1.0, so its level is
"the original median unless negatives forced a lift". Never re-baseline to the fit minimum. The
fitted background model is also returned so the caller can inspect it, save it as a separate
document, or re-apply with different parameters.

## Reference review: the three Siril scripts and SAS Pro's `abe.py` (read in full 2026-09-02)

The Siril scripts were fetched from `free-astro/siril-scripts` (GitLab, `processing/`), all
`GPL-3.0-or-later`; SAS Pro's `abe.py`, `abe_preset.py` and `graxpert.py` from
`github.com/setiastro/setiastrosuitepro` (`main`, GPL-3.0). Read-only copies sit at
`C:\temp\tianwen-scratch\siril-scripts-2026-09-02\` and `...\saspro-2026-09-02\`, outside the repo
and never vendored (reimplement from the maths recorded here; AGPL section 13 would permit
combining, the preference runs the other way, see open question 5). The algorithms are written down
so the plan can be built without the scripts open.

### `AutoGradientRemoval.py` (Cyril Richard, 2026, v1.0.0): it places no sample points at all

The background is fitted on **every pixel that survives an iterative robust rejection**, so the
sample-placement problem Phase 2 was written around does not arise. Per channel, independently
(one thread each), on whatever Siril has loaded, with no internal stretch.

1. **Downsample** by block mean (`-downsample` 1/2/4/8, default 4).
2. **Model radius** from a relative `scale` 1-10 (default 5): `radius = scale / 100 * min(small
   dims)`, i.e. 5% of the smaller working dimension, so it is resolution independent.
3. **Iterate** (max 20): `residual = ch - model`; robust `(med, sigma)` = median and 1.4826 x MAD over
   the currently kept pixels; keep where `med - 4 sigma <= residual <= med + 2 sigma` (**asymmetric**:
   bright rejected at 2 sigma because structure is bright, dark at 4 because dark outliers are
   mostly noise); AND NOT structure (step 4); never fewer than `max(16, 2% of pixels)` kept (a
   percentile fallback); refit; stop when the kept fraction moves by less than 1e-4.
4. **Structure protection** (default on): pixels with `residual - med > protect_threshold` (0.05,
   in absolute [0,1] pixel units) are seeds; the seed map is low-passed with radius
   `model_radius * (0.5 + amount)` and thresholded at `(1 - amount) * 0.5` (amount default 0.5),
   which grows the seeds so a nebula's dim wings leave the fit too.
5. **Two fit models.** DEFAULT, a *multiscale smooth surface*: masked low-pass with harmonic-style
   inpainting. Fill the rejected holes with the kept-pixel mean, then 10 times {3-pass separable
   box blur of radius r (running sums, so O(N)); restore the kept pixels}, then one more blur. Each
   pass diffuses background about r into the holes, so holes up to ~10 r wide are bridged, where a
   single normalised convolution collapses toward zero inside a large hole. SIMPLIFIED
   (`-simplified -degree N`, default 2, range 1-6): a stiff tensor polynomial `x^i y^j, i + j <= N`
   by least squares over the kept pixels, **on coordinates normalised to [-1, 1]**. The doc-string
   names when to switch: a nebula that fills the frame, which the flexible surface hollows out.
6. **Final smoothing**: blur the model by `radius * smoothness` (default 1.0, range 0-3).
7. **Upsample** bilinear. **Correct** with `level = median(bg)`: subtract mode `ch - bg + level`,
   divide mode `ch / max(bg, 1e-6) * level` (vignetting or flat residue).

### `AutoBGE.py` (Knagg-Baugh, "from Franklin Marek SAS code", v2.0.2): the SAS v2 AutoDBE port

This IS the algorithm the Algorithm section above describes, credited to its SAS lineage in the
header; the core is about 300 of its 1176 lines. What it actually does, where that differs from
what this plan assumed (and see the SAS Pro subsection below for what SAS has since changed):

- **It fits AND subtracts in a STRETCHED domain, not linear.** `stretch_image` per channel,
  unlinked: subtract the channel minimum, record the median m, apply the MTF
  `((m - 1) t x) / (m (t + x - 1) - t x)` with target median t = 0.25, clip [0, 1]. Both fits and
  both subtractions happen there, and `unstretch_image` inverts using the CORRECTED image's own
  current median rather than the recorded one and adds the minimum back, so it is not an exact
  inverse and `unstretch(corrected) + unstretch(background) != original`. The "Pipeline placement"
  premise above (additive gradient in linear data) is physically right and is NOT what this port
  does. **SAS Pro's current `abe.py` calls fixing exactly this its "KEY FIX"** (below): the
  stretch is kept for sample placement and fitting only, and the correction happens in linear.
- **Sample placement** (`generate_sample_points`), on the INTER_AREA downsampled image (factor 4):
  border margin 10 px; 4 corners plus 5 evenly spaced anchors along each of the 4 edges, 24 border
  samples; interior: split into 4 quadrants, per quadrant take Rec.601 luminance, exclude the
  brightest 50% of pixels by percentile, AND the user exclusion mask, draw `npoints / 4` at random
  (default `-npoints 100`, range 10-1000; **unseeded `np.random.choice`, so a run is not
  reproducible**). Every anchor then **descends to the dimmest spot**: patch 15 x 15, value = patch
  median of luminance, step to whichever of the 8 neighbours has the lowest patch median while one
  is lower, up to 100 steps (400 full-resolution px). A point that lands inside an exclusion polygon
  is dropped. Sample VALUE = the 15 x 15 patch median in each channel: positions shared across
  channels, values per channel.
- **Stage 1**: polynomial `x^i y^j, i + j <= degree` (default **2**, range 1-10) by least squares
  in **raw small-image pixel coordinates** (conditioning degrades from degree 3 up; normalise to
  [-1, 1] as AutoGradientRemoval does). Per channel. `corrected = stretched - poly`, then a constant
  is added so the median equals the original stretched median, then clip.
- **Stage 2**: samples are **regenerated** on the poly-corrected image; legacy
  `scipy.interpolate.Rbf(function='multiquadric', epsilon=1.0, smooth=0.1)` per channel (not
  `RBFInterpolator`), evaluated over the whole small grid (dense N_samples x N_pixels, about
  124 x 560k for a 3008 px frame at factor 4). epsilon is ONE small-image pixel, so each kernel is
  nearly a cone. `corrected = after_poly - rbf`, median restored, clip.
- **Resampling**: down INTER_AREA, up INTER_LANCZOS4 (background models only).
- **Output**: total background = poly + rbf, both unstretched; the corrected image keeps the
  original median. No re-baselining, no pedestal.
- Three weight functions (`calculate_noise_weight`, `calculate_brightness_weight`,
  `calculate_spatial_weight`) are defined and never called: dead code from the port, do not carry
  them over.
- Exclusions are Siril overlay polygons rasterised to a mask, downsampled with a >= 0.5 vote; y is
  flipped because Siril's origin is bottom-left.

### SAS Pro `abe.py` (setiastro/setiastrosuitepro `main`, pushed 2026-09-01): what changed since the port

Same sampler, same two stages, and five differences that matter, all in `abe_run`
(`legacy_prestretch=True` by default):

- **The correction is applied in LINEAR space.** The stretch (identical MTF to median 0.25, but
  **no clip**, so bright stars may exceed 1.0 in the stretch domain) is used for sample placement
  and for both fits only; the total background `poly + rbf` is unstretched ONCE and subtracted from
  the original linear image, then the original linear median is restored. The source comment reads
  "KEY FIX: unstretch the background BEFORE subtracting ... preserves star colors and all linear
  photometric relationships". The inverse is still approximate (it uses the background's own median
  where the exact inverse needs the recorded one), but the error is now confined to the shape of a
  smooth surface instead of being applied to every pixel. This is the design the plan should follow
  if a pre-stretch for sampling turns out to help placement; the correction itself stays linear.
- **Seeded.** `rng` is threaded through the sampler, the dialog default seed is 42 (a negative seed
  means unseeded), and the headless preset path seeds too. The Siril port's unseeded draw is the
  old behaviour.
- **RBF is ON by default** with `scipy.interpolate.RBFInterpolator` (the deprecated `Rbf` is gone):
  multiquadric, `epsilon = 1.0`, and **`smoothing = rbf_smooth * N_samples`** because
  `RBFInterpolator`'s smoothing scales with the sample count; dialog default `rbf_smooth` 1.0
  (shown as an integer spinbox x 0.01, a documented "x100 trap"), so ten times the port's 0.1. The
  RBF grid is **hard-capped at 1 MP** (further area-downsampled, then Lanczos-upscaled) to bound the
  dense evaluation. Sample coordinates are `(y, x)` rows.
- **Degree 0 = RBF-only.** The polynomial stage is skipped and the RBF fits the raw stretched image.
  Degree range 0-6 (dialog and preset both clip at 6).
- **A clip-free finisher, `_anchor_median_linear_rescale`**, replaces the port's `np.clip`: after the
  poly stage and again after the final correction, lift the frame so the minimum is zero if anything
  is negative, then compress the ceiling about the median pivot if anything exceeds 1.0. Divide mode
  normalises the background to 1.0 at its median first and re-centres the result on the original
  median.

Smaller facts worth carrying: dialog defaults are degree **2**, samples **120**, downsample **4**
(the headless preset defaults to 6), patch 15, correction subtract; the descent runs up to 500
steps (port: 100); if the sampler yields nothing it falls back to a `sqrt(N) x sqrt(N)` grid; a
"grid" placement mode and a hand-editable "manual" mode exist beside "auto" (all three end up as an
`Nx2` array in full-image coordinates); the polynomial terms are built by Numba in **float32 on raw
small-image coordinates**, which is why the degree is capped at 6 (a 750 px coordinate to the sixth
power is ~1.8e17 against float32's seven digits; normalise coordinates, as AutoGradientRemoval
does, and the cap becomes a choice rather than a necessity); an input with a maximum above 1.0 is
divided by that maximum for the run and rescaled at the end; mono is triplicated for the run and
channel 0 taken back; the background is optionally returned as a separate linear document.

### `GraXpert-AI.py` BGE (Knagg-Baugh, v2.1.0): parity check against `OnnxBackgroundExtractor`

`BGEProcessing.extract_background_ai` "adapts code from GraXpert", so it is the closest reference
to our `OnnxBackgroundExtractor` short of GraXpert itself. Step by step against ours:

| Step | Siril script | `OnnxBackgroundExtractor` |
|---|---|---|
| Shrink | INTER_LINEAR to 240 = 256 - 2 x 8 | bilinear to 240 |
| Pad | edge, 8 px | edge, 8 px |
| Stats | median + MAD per channel, on the PADDED plate | same |
| Normalise | `(x - med) / mad * 0.04`, clip [-1, 1]; mono triplicated | same |
| Input | `gen_input_image`, NHWC | same |
| Denormalise | `bg / 0.04 * mad + med` | same |
| **User smoothing** | Gaussian `sigma = smoothing * 20` on the 256 plate BEFORE the crop; default 0.5 (GraXpert's own 0.0 judged "too low"; one of the script's CLI parsers says 1.0) | **absent** |
| Crop | 8 px | 8 px |
| Fixed smoothing | Gaussian sigma 3, kernel `int(8 sigma + 1)` = **25** | sigma 3, kernel **11** (truncates at 1.7 sigma against their 4) |
| Upsample | INTER_LINEAR | bilinear |
| Mono out | channel 0 | average of the three (deliberate, documented) |
| Subtract | `img - bg + mean(bg)`, clip [0, 1] | `img - bg + mean(bg)`, no clip |
| Divide | per channel `img / bg * mean(channel)` | absent |
| Execution provider | **CPU forced** for BGE: GPU EPs errored on some systems and CPU is "fast enough" | resolver default (DirectML on the dev box) |
| Background export | `keep_bg`: `<name>_bg.<ext>` FITS, original header + a HISTORY card, same units as the input | `--save-gradient` |

**Open question 4 is answered: the interop surface IS this table.** A GraXpert-exported background
is a full-resolution plate in the image's own units carrying the image's header, and the correction
is subtraction with the mean added back, so a background exported by GraXpert or by the Siril
script can be applied by us and vice versa with no conversion. Two parity gaps are ours to close if
byte-comparable output ever matters: the missing user-smoothing knob and the kernel truncation. The
CPU-forcing is a note for the DirectML path, not a bug report.

**SAS Pro takes the other interop route and it is worth knowing both** (`graxpert.py`,
`_build_graxpert_cmd`): it does not load the ONNX at all but shells out to the GraXpert
executable, `<exe> -cmd background-extraction <input> -cli -gpu true|false [-smoothing 0.xx]`
(denoising adds `-strength`, `-batch_size`, `-ai_version`), feeding a **plain uncompressed float32
TIFF clipped to [0, 1]** (mono 2D, RGB HxWx3, via `tifffile`) and reading back the output by exact
basename with a `fits`/`tif`/`tiff`/`png` extension. So GraXpert's CLI contract is a second, coarser
interop surface: same units, but no header round-trip and no background plate comes back, which is
why our in-process ONNX path (and the Siril script's) is the better one to keep.

**Licence, corrected the same day.** GraXpert's code is **GPL-3.0** (GitHub API, 2026-09-02) and
the script header states its AI models are **CC-BY-NC-SA-4.0**. Both `OnnxBackgroundExtractor`'s
XML doc and `tianwen-ai-models-fetch.ps1` called GraXpert "MIT-licensed"; fixed. We read the
weights from the user's own GraXpert install and never redistribute them, which the NC term
permits; shipping them inside a release asset would not be, for the reason
[ai-denoise-deconv.md](ai-denoise-deconv.md) section 2.6 gives for the Falchi atlas.

### What the review changes in this plan

1. **Sample placement is optional, not foundational.** AutoGradientRemoval shows that robust
   iterative rejection over ALL pixels plus a masked low-pass inpainting surface needs no samples,
   no RBF, no dense solve and no scipy equivalent: three separable box blurs and a mask.
   Recommended shape: Stage 1 = the stiff polynomial on the robustly-rejected pixel set (normalised
   coordinates, 2/4 sigma asymmetric rejection, structure-protection mask); Stage 2 = the inpainting
   surface as the flexible model. Keep the AutoBGE sample walk as the fallback design if the
   pixel-set fit disappoints on real data; keep RBF out unless measured to add something. This
   resolves open question 2 by removing the solver.
2. **Correct in linear and state every threshold in noise units.** AutoGradientRemoval's
   `protect_threshold = 0.05` is in absolute [0, 1] pixel units and is inert on a linear master
   whose sky sits at 0.01 and whose nebula at 0.02; the Siril AutoBGE port only behaves because it
   stretches first, and SAS Pro's "KEY FIX" moved its correction back to linear while keeping the
   stretch for placement and fitting. Ours: the correction is linear without exception; a stretch
   for SAMPLING is allowed only if it measurably helps placement; seeds at `k sigma` above the
   robust median, k a parameter with a default to be measured, never an absolute pixel value.
3. **Preserve the sky level; never re-baseline** (the Output section above, and `PreserveLevel` in
   the options record).
4. **Per-channel fits from v1** (the withdrawn non-goal above).
5. **Defaults to carry**: downsample 4 by area mean (SAS Pro dialog 4, its headless preset 6),
   model radius 5% of the smaller working dimension, degree **2** for the stiff polynomial (all three
   implementations; the "degree 4" line in open question 1 was wrong), final smoothing 1.0 radius,
   20 iterations, 1e-4 convergence, bilinear upsample. If the sample walk is ever used: 120 samples,
   patch 15, descent up to 500 steps.
6. **Determinism**: the Siril AutoBGE port's interior draw is unseeded; SAS Pro seeds it (default
   42). Anything random in ours is seeded from the image (stable hash), the same rule the dataset
   split follows.
7. **Divide mode** is cheap and both AutoGradientRemoval and GraXpert offer it: add it as an option
   for flat residue while keeping the "better flats" advice under "What ABE does NOT do".

## Architecture

### Project layout

As shipped (the pre-review sketch had `PolynomialBackgroundExtractor` / `PolyRbfBackgroundExtractor` /
`SamplePointGenerator`; the review removed the sampler and the solver, so one class carries both stages):

```
TianWen.Lib/Imaging/BackgroundExtraction/        -- zero AI dep, zero UI dep
├── IBackgroundExtractor.cs                       (Image + options -> BackgroundExtractionResult)
├── ClassicalBackgroundExtractor.cs               (IBackgroundExtractor AND IGradientCorrector: working grid, CFA split, upsample, correction)
├── RobustBackgroundFit.cs                        (internal: the per-plane fit on arrays; polynomial stage, surface stage)
├── BackgroundExtractionOptions.cs                (options record, BackgroundCorrection, ExclusionPolygon)
└── BackgroundExtractionResult.cs                 (Cleaned, Background, per-plane ChannelFitDiagnostics)
TianWen.Lib/Extensions/BackgroundExtractionServiceCollectionExtensions.cs   (AddClassicalBackgroundExtractor / AddClassicalBackgroundExtraction)
TianWen.AI.Imaging/FallbackGradientCorrector.cs  (GraXpert when its weights resolve, else the classical fit; what AddTianWenAi registers)
```

Headless-first: no Qt, no UI. The GUI/CLI surfaces (Phase 4+) call into this
core and render whatever preview they want.

### Interface contract

```csharp
public interface IBackgroundExtractor
{
    Task<BackgroundExtractionResult> ExtractAsync(Image source, BackgroundExtractionOptions options, CancellationToken cancellationToken = default);
}

public sealed record BackgroundExtractionOptions          // init-only properties; these are the reference defaults
{
    int Downsample = 4;                        // block mean; a CFA mosaic is split first and fitted at half this
    int PolynomialDegree = 2;                  // 0..6, on coordinates normalised to [-1, 1]
    bool SurfaceRefinement = false;            // the inpainted low-pass surface on the polynomial's residual
    float SurfaceScalePercent = 5;             // model radius, percent of the smaller working dimension
    int SurfaceInpaintPasses = 10; float SurfaceSmoothness = 1;
    float RejectBrightSigma = 2, RejectDarkSigma = 4;
    int MaxIterations = 20; float ConvergenceTolerance = 1e-4f, MinKeptFraction = 0.02f;
    bool ProtectStructure = true; float StructureThresholdSigma = 3, SurfaceStructureThresholdSigma = 10, StructureAmount = 0.5f;
    BackgroundCorrection Correction = Subtract;   // or Divide
    bool PreserveLevel = true;                    // add back median(model) PER PLANE; never re-baseline (see Output)
    ImmutableArray<ExclusionPolygon> Exclusions;  // full-image pixel coordinates, even-odd rule
}

public sealed record BackgroundExtractionResult(Image Cleaned, Image Background, ImmutableArray<ChannelFitDiagnostics> Planes)
{ float ResidualRms; }                         // Planes: one per FITTED plane, so four for a CFA mosaic
public sealed record ChannelFitDiagnostics(int Plane, int Iterations, bool Converged, float KeptFraction,
    float ExcludedFraction, float ResidualSigma, float ResidualRms, float Level);
```

`ClassicalBackgroundExtractor` is the one implementation and implements `IGradientCorrector` too;
`AddClassicalBackgroundExtraction()` registers it as both roles with no AI project referenced, and
`AddTianWenAi()` puts `FallbackGradientCorrector` (GraXpert when its weights resolve, else this) in
front of the pipeline role.

### Reuse from existing TianWen code

- `Image.GetChannelSpan` -- already there; row-major sample access.
- `StatisticsHelper.MedianFast` -- for the per-patch background estimation
  inside the descend-to-dim sample step.
- `DIR.Lib.Image.Resize` (or our own Lanczos) -- for downsample/upsample
  around the polynomial fit.
- `Image.MtfStretch` / `Image.MtfUnstretch` -- NOT used by ABE itself, but
  cross-listed here because callers will typically chain ABE -> color
  calibration -> MtfStretch -> AI enhancers.
- `ScanBackgroundRegion` (used by `BackgroundNeutralization`) -- directly
  reusable for the descend-to-dim sample-placement step; no need to
  reimplement dark-region scanning.
- `GetStarMaskedMedianAndMADScaledToUnit` -- the star-masking half is
  relevant to the bright-avoid sample-rejection heuristic.
- Mask morphology (dilate / erode) is on the backlog as "deferred until a
  consumer exists" (`docs/todo/imaging.md`) -- ABE's exclusion-polygon /
  bright-avoid step is plausibly the consumer that finally justifies
  building it (dilate a star mask before excluding it from sampling).

### What ABE does NOT do

- **Background neutralisation.** That's a separate per-channel multiply
  step (currently implemented in `Image.BackgroundNeutralization`,
  CLAUDE.md "Background Neutralization"). ABE removes the *spatial*
  gradient; BG-neutralisation aligns the *channel offsets*. Both run
  before stretch; they don't substitute for each other.
- **White balance.** Also separate, see `Tycho2ColorCalibration`.
- **Star reduction / starless extraction.** That's `IStarRemover` in the
  AI enhancement pipeline (`ai-enhancement.md`).
- **Vignetting correction.** Should already be handled by flat-field
  calibration upstream. If residual vignetting survives stacking it
  *will* be picked up by the polynomial fit and removed, but the right
  fix is better flats, not relying on ABE.

## Phasing

| Phase | Scope | Notes |
|-------|-------|-------|
| 1 | **DONE 2026-09-02.** `IBackgroundExtractor` + the stiff polynomial stage of `ClassicalBackgroundExtractor`: robust 2/4-sigma iteration to automatic convergence, structure protection, normalised coordinates, degree 0 to 6, block-mean working grid, per-plane level preservation, CFA mosaics per photosite colour, exclusion polygons, divide mode. Headless API + 30 synthetic tests. | The default for the unattended pipeline role; see "Implementation". |
| 2 | **DONE 2026-09-02**, as `SurfaceRefinement` on the same class: compact-structure rejection, the masked low-pass inpainting surface on the polynomial's residual, structure marked once against it, refit, final smoothing. Runs ONCE, deliberately (see "Implementation"). Caller exclusion polygons fold into the same mask. Off by default. | `SamplePointGenerator` stays unbuilt: the pixel-set fit did not disappoint on the synthetic cases; real masters are the open measurement. |
| 3 | `PolyRbfBackgroundExtractor` adds RBF multiquadric refinement. | **Only if measured to add something over Phase 2's surface**; the review removed the need for a dense solver. |
| 4 | CLI command (`tianwen abe ...`) + GUI integration (preview panel with exclude-polygon editor). | UI work; mirrors SAS Pro's interactive workflow. |
| 5 | Pipeline integration: optional ABE step in any future `LinearProcessingPipeline` orchestrator that chains stacking -> ABE -> color calibration. | Keeps each step composable as separate `IXxx` services. |

## Implementation (Phases 1 and 2, shipped 2026-09-02)

`ClassicalBackgroundExtractor` (`TianWen.Lib/Imaging/BackgroundExtraction/`) is both the headless
`IBackgroundExtractor` and an `IGradientCorrector`; `RobustBackgroundFit` is the per-plane arithmetic
it runs, on arrays, testable without an `Image`. Pinned by `ClassicalBackgroundExtractorTests` (30
tests against synthetic truth: a ramp under noise and stars, a dome, a blob, a NaN border, a
vignette, three channels with different skies, a CFA mosaic with per-colour gradients, exclusion
polygons, determinism, DI) and by the fallback tests in `OnnxBackgroundExtractorSmokeTests`.

### What it does

1. **Working grid.** Block mean by `Downsample` (4), NaN-aware (an all-NaN block is no data). A
   one-channel `SensorType.RGGB` mosaic is split into its four photosite planes first
   (`Image.SplitBayerChannels`) and each is fitted at half the factor, so the working resolution is
   unchanged; the corrected planes and the models are merged back into mosaics. A single plane
   subtracted from a mosaic removes only the average gradient and leaves each colour's own residual
   behind as a colour gradient, and the viewer hands every OSC frame to an enhancer as a mosaic, so
   this is the default path, not a special case. An odd-sized mosaic is fitted as one plane, logged.
2. **Stage 1, the stiff polynomial, iterates.** `x^i y^j, i + j <= degree` on coordinates normalised
   to [-1, 1], solved through the normal equations accumulated in double
   (`PolynomialLeastSquares.SolveNormalEquations`; no design matrix of one row per pixel ever exists),
   falling back one degree at a time when rank-deficient. Residual over the kept pixels, robust
   median and sigma (1.4826 x MAD), keep within [median - 4 sigma, median + 2 sigma] and not
   structure, never fewer than max(16, 2 percent), refit; stop when the kept fraction moves by less
   than 1e-4, cap 20. This is what PixInsight's GradientCorrection calls "automatic convergence",
   and here it is always on.
3. **Stage 2, the surface, runs once** (`SurfaceRefinement`, off by default). On the polynomial's
   residual: compact structure leaves (below), the masked low-pass inpainting surface is fitted
   (fill the holes with the kept mean, then `SurfaceInpaintPasses` x {three-pass box blur of the
   model radius, restore the kept pixels}, then one more blur), structure is marked ONCE against that
   surface (`SurfaceStructureThresholdSigma`), the surface is refitted without it and smoothed by
   `radius x SurfaceSmoothness`. The model radius is 5 percent of the smaller working dimension.
4. **Structure protection** (both stages): seeds where the residual exceeds k sigma above the median,
   low-passed with radius `r x (0.5 + amount)`, cut at `(1 - amount) x 0.5`. An isolated pixel never
   becomes structure (its blurred weight is far under the cut); a compact bright region does, dim
   wings included.
5. **Upsample and correct.** The working-grid model goes back to plane resolution bilinearly
   (`Image.BilinearResize`, pixel-centre convention, the primitive the GraXpert path uses too); the
   correction is in LINEAR, per plane: `source - background + median(background)` clamped at zero,
   or `source / max(background, 1e-6) x median(background)`. A NaN source pixel stays NaN. The level
   is per plane, so a colour gradient's removal never doubles as a background neutralisation, and the
   image's pedestal field is left alone: the ONNX path accumulates its level onto the pedestal, which
   is what forced `MasterPreviewRenderer.WithZeroPedestal` on GraXpert-flattened masters.
6. **Diagnostics per plane** (`ChannelFitDiagnostics`): iterations, converged, kept fraction (of the
   valid pixels), excluded fraction (polygons plus no-data blocks, of all pixels), residual sigma and
   RMS in image units, the level. Nothing in the fit is random, so a run is deterministic by
   construction.
7. **The product wiring.** `AddTianWenAi()` registers `FallbackGradientCorrector` as the
   `IGradientCorrector`: GraXpert's BGE when `graxpert_bge.onnx` resolves, the classical fit
   otherwise, decided per call by a file probe. Before this a machine without GraXpert had no gradient
   correction at all (`flatten` and the pipeline step failed on a missing model); now it gets the
   classical fit and a background plate for `--save-gradient`, and the log says which answered.

### Three findings, each of which changed the design

- **Iterating the surface stage feeds on itself.** The surface reproduces the kept pixels and
  low-passes them at the model radius, so its residual is a high-pass, and a smooth feature of scale
  s leaks about (sigma_blur / s)^2 of its amplitude into it. On the synthetic dome (amplitude 0.003,
  sigma 0.12 W, over 2e-4 noise) that leakage is six sigma of the BLOCK-MEAN noise at the peak; a
  2-sigma rejection carved the peak out, the harmonic hole-fill undershot it, the residual grew, and
  the hole widened every pass until the dome was gone: 7.1e-4 RMS model error, against 1.1e-4 for the
  single pass that shipped. The reference iterates its surface too and survives because its threshold
  is absolute in a stretched domain, where the noise is relatively larger. Stated in noise units on
  linear data, that loop must not be closed.
- **Stars are not structure.** On block-mean noise even a 1.5 px star's wings clear a 3-sigma seed
  threshold (its next block carries 3 percent of the peak, tens of sigma), so every bright star seeded
  a five-by-five cluster that the growth step turned into a protected disc: 69 percent kept on a
  sixty-star field. Stars are COMPACT: they fail a one-working-pixel high-pass (the residual minus its
  radius-1 three-pass blur) that a gradient, a dome or a nebula wider than a few blocks passes
  untouched. Compact pixels never seed structure, and in the surface stage they are what leaves.
- **A bright star's blur shadow is not compact either.** The first compact test flagged both sides of
  the high-pass, and a bright star's spill drives the high-pass of CLEAN blocks two and three away
  strongly negative: a third of the grid gone on a thirty-star field. Compact is the positive
  high-pass core plus its eight neighbours (which do carry the wings), nothing more: 86 percent kept
  on the same field.

### Measured (256 x 192 synthetic, sky 0.010, noise 2e-4, working grid 64 x 48)

| Case | Result |
|---|---|
| Planar ramp (0.004 across, 0.002 down) + 60 stars, degree 2 | model RMS error 4.0e-6 (noise 2e-4: the block mean and the fit average it down 50x); 7 iterations, converged; kept 0.816, which is what the truth predicts (60 stars x 9 blocks = 17.6 percent, plus 2.3 percent of noise tails); every star's excess over the true sky unchanged at its peak to 1.5e-4 |
| Ramp + Gaussian dome (0.003, sigma 0.12 W) + 30 stars | polynomial-only model RMS error 7.1e-4; with `SurfaceRefinement` 1.1e-4, kept 0.862, 14 stage-1 iterations |
| Ramp + compact blob (0.02, sigma 20 px), surface on | model error under the blob 2.6e-3 with structure protection vs 3.4e-3 without; corrected peak kept 87 percent vs 83 percent (the compact test already carves the blob's core; protection adds the wings) |
| Multiplicative vignette (1 - 0.3 r^2), divide mode | centre and corner medians agree to 1e-4; level preserved |
| Three channels, skies 0.010 / 0.014 / 0.020, slopes 0.004 / -0.003 / 0.002 | each channel's cleaned median is its own sky at frame centre to 1e-4; the offsets survive |
| RGGB mosaic, per-colour skies 0.010 / 0.015 / 0.008, slopes 0.004 / 0 / -0.003 | four planes fitted; mosaic model RMS error under 1e-4; each colour's left and right medians agree to 1.5e-4 (a one-plane fit would leave 3.5e-3) |
| 12 px NaN border | NaN pattern preserved exactly; model finite everywhere; error under 1e-4 including the extrapolated border |

### Defaults that are reasoned, not measured

`StructureThresholdSigma` 3 (polynomial stage) and `SurfaceStructureThresholdSigma` 10 (surface
stage). The reference's 0.05 is absolute in stretched units and has no meaning on a linear frame, so
both are starts, not measurements. Ten sits between the dome's leakage (six sigma) and the blob's
(eighty) on the synthetic cases; a real master with a faint extended nebula under a strong dome is
the case that decides it, and `tianwen image flatten --save-gradient` over the retained masters is
how to look (gradient-remover-training.md, G0's test). `SurfaceRefinement` is off by default for the
reason the reference gives about its own flexible model: it follows a frame-filling nebula and
hollows it.

### Not built, and why

- **Phase 3 (RBF)**: the surface does the job on the synthetic cases; RBF only if measured to add
  something (open question 2).
- **`SamplePointGenerator`**: the pixel-set fit did not disappoint; it stays the recorded fallback.
- **Phases 4 and 5** (CLI options, GUI, pipeline step): `tianwen image flatten` already runs the
  `IGradientCorrector`, which is now GraXpert-or-classical; exposing degree, surface, divide and
  polygons on the CLI is Phase 4.
- **A stretch for sampling** (SAS Pro's placement trick) was not needed: the correction is linear and
  so is the fit.

## Open questions

1. **Polynomial-degree default.** SAS Pro defaults to a UI slider, not a
   single number. ~~Empirical-default for headless mode: 4 (matches SAS Pro docs / Siril).~~
   **Corrected 2026-09-02**: degree **2** everywhere. SAS Pro's dialog default is 2
   (`QSettings "abe/degree", 2`), its headless preset default is 2, and both Siril scripts default
   to 2 (AutoBGE `-polydegree 2`, AutoGradientRemoval `-degree 2`). SAS Pro allows 0 (RBF-only) to
   6. Start at 2 for the stiff polynomial; the flexible surface, not a higher degree, is what follows
   local structure. Revisit if quality data lands.
2. **RBF library choice -- ~~correction, this was checked and the premise was wrong~~ RESOLVED
   2026-09-02 by the reference review: no solver is needed.** AutoGradientRemoval's masked low-pass
   inpainting surface does Stage 2's job with three separable box blurs and a mask, so (a) and (b)
   below only apply if RBF is ever measured to add something. If it is, SAS Pro's current recipe is
   `RBFInterpolator`, multiquadric, `epsilon = 1` small-image pixel, `smoothing = 1.0 x N_samples`
   on ~120 samples, evaluation grid capped at 1 MP (the Siril port's legacy `scipy.Rbf` with
   `smooth = 0.1` is the older form). The original text, kept for the record: `scipy.
   interpolate.RBFInterpolator` is the Python equivalent, and the claim below that we "already pull
   `MathNet.Numerics` in `Stacking`" is **false**: grepped `src/` and `Directory.Packages.props`,
   there is no `MathNet` reference anywhere in this repo. Choosing (a) is therefore a genuinely NEW
   dependency, not a zero-cost one -- re-weigh against (b) with that in mind before picking.
   (a) `MathNet.Numerics` LU solve + hand-rolled multiquadric kernel (~150 LOC, but a new package
       dependency).
   (b) Pure hand-rolled SIMD (no new dep, ~250 LOC). With the cost of (a) corrected upward, (b) is
       the more consistent choice with this codebase's general preference for no new dependency
       over a small one (see CLAUDE.md's sibling/CPM discipline) unless (a)'s LU solver is
       measurably simpler to get right.
3. **Where does the auto-stretch preview live?** ABE's preview UI in SAS
   Pro applies an autostretch to the preview thumbnail so the user can see
   gradients. In TianWen we already have the viewer pipeline -- the
   preview can reuse `StretchUniforms` for display, while the *math*
   continues to operate on linear data. No need to invent a separate
   preview stretch.
4. **GraXpert compatibility -- RESOLVED 2026-09-02**, see the parity table in the reference
   review: a GraXpert background is a full-resolution plate in the image's units with the image's
   header, applied as subtraction with the mean added back, so it round-trips with ours unchanged.
   Two parity gaps (user smoothing knob, kernel truncation) are recorded there.
5. **Sample-placement heuristics -- RESOLVED 2026-09-02**: `AutoBGE.py`'s walk is written down in
   full in the reference review, and `AutoGradientRemoval.py` showed the problem can be avoided
   altogether (robust pixel-set rejection, no samples), which is now the recommended Phase 2.
   **License rule, restated precisely**: TianWen is
   AGPL-3.0-or-later (as of 2026-08-11) and the Siril/GraXpert repos are GPL-3.0, so combining is
   legally permitted under AGPL section 13 -- reimplementing from the recorded maths is still the
   *preferred* approach (a vendored GPL-3.0 file would carry different terms than the rest of the
   tree, and the Python-to-C# port is most of the effort either way), but it is a preference now,
   not a hard legal requirement. Same framing as [narrowband-colour.md](narrowband-colour.md)'s
   ADR-2.
6. ~~SASPro's own `abe.py` license is unconfirmed~~ **RESOLVED 2026-08-29: confirmed GPL-3.0.**
   ~~The local reference checkout is ... a sibling repo at `~/source/repos/other/setiastrosuitepro`.~~
   **That path does not exist on this machine either (checked 2026-09-02)**; the licence is
   GPL-3.0 per the GitHub API for `setiastro/setiastrosuitepro`, and `abe.py` was read from that
   repo's `main` (see the review). Its own
   `README.md`: "Seti Astro Suite Pro is licensed under the GNU General Public License v3.0." Same
   licence as Siril/GraXpert, so the same framing applies: combining is lawful under TianWen's
   AGPL-3.0-or-later (section 13), reimplementing from the algorithm is still preferred (see
   question 5 above), not a hard legal requirement.

## Cross-references

- [ai-enhancement.md](ai-enhancement.md) -- `SharpenPipeline`
  expects post-ABE input. No coupling beyond that.
- [stacking.md](stacking.md) -- ABE runs *after* stacking. The
  `Normalizer` step in the stacking pipeline does per-frame intensity
  normalisation, not spatial gradient removal -- ABE is the separate,
  later step.
- [CLAUDE.md](../../CLAUDE.md) "BackgroundNeutralization" -- the existing
  per-channel offset alignment. Different concern from ABE; both run on
  linear data.
- SAS Pro reference: [`src/setiastro/saspro/abe.py`](https://github.com/setiastro/setiastrosuitepro/blob/main/src/setiastro/saspro/abe.py)
  in `setiastro/setiastrosuitepro` (GPL-3.0, see open question 6), plus `abe_preset.py` (headless
  defaults) and `graxpert.py` (its GraXpert CLI interop). Read from GitHub 2026-09-02; **no local
  checkout on this machine**; read-only copies at `C:\temp\tianwen-scratch\saspro-2026-09-02\`.
- **Siril scripts, read in full 2026-09-02** (section "Reference review"): `AutoBGE.py`,
  `AutoGradientRemoval.py`, `GraXpert-AI.py` in
  [`free-astro/siril-scripts`](https://gitlab.com/free-astro/siril-scripts/-/tree/main/processing)
  (GitLab, GPL-3.0-or-later). Read-only local copy:
  `C:\temp\tianwen-scratch\siril-scripts-2026-09-02\` (outside the repo, never vendored).
- [`pixinsight-parity.md`](pixinsight-parity.md) -- the indexed parity tracker this plan is the #2
  ranked gap under.
