# PLAN: Background extraction (ABE / gradient removal)

> Status: **NOT STARTED**. Design captured for future work; no code yet.

## Goal

Add classical (non-AI) background extraction to TianWen. Fits a smooth model
of the sky background (polynomial, optionally refined with RBF interpolation)
through user-placed or auto-placed sample points, then subtracts it from the
source frame. This is the standard "automatic background extraction" /
"dynamic background extraction" feature familiar to anyone who has used
PixInsight ABE/DBE, Siril gradient removal, or GraXpert. The output is a
flat-field-corrected image ready for the downstream color-calibration +
stretch + AI-enhancement chain.

Reference implementation: SetiAstroSuite Pro's
`abe.py` (`~/source/repos/other/setiastrosuitepro/src/setiastro/saspro/abe.py` -- a sibling
checkout of this org's repos, not inside `tianwen/`, ~2050
LOC including the Qt UI). The core algorithm is ~400 lines; the rest is
preview/exclude-polygon UI.

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
- **Per-channel modelling for narrowband mosaics.** Single background model
  shared across channels in v1; revisit if HOO / SHO data shows channel-
  specific gradient mismatches.
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

Two-stage fit, both lifted from SAS Pro's `abe.py`:

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

A new `Image` with the background subtracted and a small positive pedestal
added back to keep the result non-negative (matches SAS Pro's behaviour: the
polynomial fit's minimum becomes the new zero baseline, plus ~1% pedestal so
faint nebula detail isn't clipped). The fitted background model is also
returned so the caller can inspect, save as a separate document, or
re-apply with different parameters.

## Architecture

### Project layout

```
TianWen.Lib/Imaging/BackgroundExtraction/        -- zero AI dep, zero UI dep
├── IBackgroundExtractor.cs                       (Image -> (cleanedImage, backgroundModel))
├── PolynomialBackgroundExtractor.cs              (Stage 1: poly fit + auto samples)
├── PolyRbfBackgroundExtractor.cs                 (Stage 1 + Stage 2: poly + RBF refine)
├── SamplePointGenerator.cs                       (auto-place + descend-to-dim + bright-avoid)
├── BackgroundExtractionOptions.cs                (degree, sample density, exclusion polygons)
└── BackgroundExtractionResult.cs                 (cleanedImage, backgroundImage, residualRms)
```

Headless-first: no Qt, no UI. The GUI/CLI surfaces (Phase 4+) call into this
core and render whatever preview they want.

### Interface contract

```csharp
public interface IBackgroundExtractor
{
    Task<BackgroundExtractionResult> ExtractAsync(
        Image source,
        BackgroundExtractionOptions options,
        CancellationToken ct = default);
}

public sealed record BackgroundExtractionOptions(
    int PolynomialDegree = 4,                       // 1-6; degree 4 is SAS Pro default
    int SampleCount = 24,                            // anchor samples before bright-rejection
    bool UseRbfRefinement = false,                   // Stage 2 toggle
    double RbfSmoothing = 0.0,                       // multiquadric smoothing factor
    ImmutableArray<ExclusionPolygon> Exclusions = default,
    float PedestalFraction = 0.01f);                 // tiny positive offset added post-subtract

public sealed record BackgroundExtractionResult(
    Image Cleaned,                                   // source - background + pedestal
    Image Background,                                // the fitted gradient (for inspection)
    float ResidualRms);                              // diagnostic
```

`PolynomialBackgroundExtractor` does Stage 1 only; `PolyRbfBackgroundExtractor`
extends it with Stage 2. DI picks one by config.

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
| 1 | `IBackgroundExtractor` interface + `PolynomialBackgroundExtractor` (Stage 1 only). Headless API + tests against synthetic gradients. | Smallest useful chunk; covers 80% of real-world need. |
| 2 | `SamplePointGenerator` with descend-to-dim + bright-avoid + caller exclusion polygons. | Auto-sample quality matters a lot for ABE output quality. |
| 3 | `PolyRbfBackgroundExtractor` adds Stage 2 (RBF multiquadric refinement). | Polish step; can defer until Stage 1 lands. |
| 4 | CLI command (`tianwen abe ...`) + GUI integration (preview panel with exclude-polygon editor). | UI work; mirrors SAS Pro's interactive workflow. |
| 5 | Pipeline integration: optional ABE step in any future `LinearProcessingPipeline` orchestrator that chains stacking -> ABE -> color calibration. | Keeps each step composable as separate `IXxx` services. |

## Open questions

1. **Polynomial-degree default.** SAS Pro defaults to a UI slider, not a
   single number. Empirical-default for headless mode: 4 (matches SAS Pro
   docs / Siril). Revisit if quality data lands.
2. **RBF library choice -- correction, this was checked and the premise was wrong.** `scipy.
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
4. **GraXpert compatibility -- now has a concrete reference path, not just a "probably yes."**
   `docs/todo/imaging.md`'s Siril-scripts note (2026-08-18, expanded since) located
   `processing/GraXpert-AI.py` in the `free-astro/siril-scripts` GitLab repo -- Siril driving
   GraXpert, which answers this question from a working implementation rather than only from docs.
   Still separable and lower priority than Phases 1-3; doesn't block them.
5. **Sample-placement heuristics are now sourced, not SAS-Pro-only.** The same Siril-scripts note
   also located `processing/AutoBGE.py` (automatic background extraction -- the sample-placement
   half, which is exactly what Phase 2 needs and what this doc previously left open) and
   `processing/AutoGradientRemoval.py`. **License rule, restated precisely**: TianWen is
   AGPL-3.0-or-later (as of 2026-08-11) and the Siril/GraXpert repos are GPL-3.0, so combining is
   legally permitted under AGPL section 13 -- reimplementing from the recorded maths is still the
   *preferred* approach (a vendored GPL-3.0 file would carry different terms than the rest of the
   tree, and the Python-to-C# port is most of the effort either way), but it is a preference now,
   not a hard legal requirement. Same framing as [narrowband-colour.md](narrowband-colour.md)'s
   ADR-2.
6. ~~SASPro's own `abe.py` license is unconfirmed~~ **RESOLVED 2026-08-29: confirmed GPL-3.0.**
   The local reference checkout is not at the path this doc previously cited
   (`other/setiastrosuitepro` relative to the tianwen repo, which does not exist) but as a sibling
   repo at `~/source/repos/other/setiastrosuitepro` -- fixed in Cross-references below. Its own
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
- SAS Pro reference: `~/source/repos/other/setiastrosuitepro/src/setiastro/saspro/abe.py` (a
  sibling checkout next to this org's repos, not inside `tianwen/`; GPL-3.0, confirmed -- see
  open question 6).
- [`docs/todo/imaging.md`](../todo/imaging.md)'s Siril-scripts note -- `AutoBGE.py`,
  `AutoGradientRemoval.py`, `GraXpert-AI.py` in `free-astro/siril-scripts` (GitLab), resolving open
  questions 4 and 5 above.
- [`pixinsight-parity.md`](pixinsight-parity.md) -- the indexed parity tracker this plan is the #2
  ranked gap under.
