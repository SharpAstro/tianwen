# PLAN: AI enhancement — next steps after first real-world validation

> Status: **NOT STARTED**. Captures the work surfaced by the first
> end-to-end runs against real masters on branch `ai-enhancement`. Companion
> to [`ai-enhancement.md`](ai-enhancement.md) (Phases 0-6 shipped
> there + see "Shipped since the original plan" below).

## Goal

Three buckets of follow-on work, in order of immediate user-visible value:

1. **Frank-parity Star Stretch port** -- match the three-step pipeline
   inside `pixinsight-updates/src/scripts/star_stretch_v2.1.js` so a TianWen
   user can reproduce what PixInsight users do with Frank's script.
2. **Per-plate stretch control ("B")** -- different stretch functions /
   parameters for the stars-only plate vs the starless plate, applied
   between split and recombine. Aesthetic control + missing knob for
   star-friendly final-output rendering.
3. **Remaining work from the original plan** -- denoise enhancer,
   classical fallbacks, EnhancementWorkflow + `tianwen image enhance`,
   `tianwen stack --enhance`, runtime model self-bootstrap, NPU INT8 quant.
   Cross-referenced below.

## Shipped since the original plan

Branch `ai-enhancement` carries quality + perf wins that landed after
Phases 0-6 of the original plan and aren't in `summary.md` yet:

| Commit | Topic |
|---|---|
| `937555a` | NAFNet 16-px chunk padding + `ArrayPoolHelper` for input tensors (fixes broadcast error on real-world image sizes, cuts ~200 MB GC churn) |
| `2b67a6f` | `tianwen image render` verb + `--png` flag on sharpen/remove-stars (PNG goes through the same `MasterPreviewRenderer` as the stack output) |
| `be5bbcf` | `Image.SubtractiveChromaticNoise` + `Image.Lerp` (via `TensorPrimitives.Lerp`); `SharpenRequest.{StarsScnrMode, StellarBlend, DeconvBlend}` |
| `a4ba20e` | win-arm64 switched from QNN -> DirectML (Adreno GPU); 2.4x speedup on the Skull master (6m42s -> 2m45s) |
| `a9ca142` | Auto-detect pre-stretched input in `ChunkedNafnetRunner` (skips MtfStretch round-trip when median(ch0 - min) >= 0.125) |

## 1. Frank-parity Star Stretch

Reference: `pixinsight-updates/SetiAstroScripts*.zip` ->
`src/scripts/star_stretch_v2.1.js`. The script's `applyPixelMath` +
`applyColorSaturation` + `applySCNR` are run in sequence on the stars-only
plate (the user has already split stars in PixInsight). Three primitives;
we have two:

### 1.1 MTF stretch with `amount` slider (have it)

```javascript
P.expression = "((3^amount)*$T) / ((3^amount - 1)*$T + 1)"
```

Algebraically equivalent to PixInsight STF / `Image.MidtonesTransferFunction`
with `target_median = 1 / (3^amount + 1)`:

| amount | target_median |
|---|---|
| 0 | 0.5 (identity) |
| 1 | 0.25 (current default) |
| 2 | 0.1 |
| 3 | ~0.036 |

We have `Image.MtfStretch(targetMedian, ...)`. Optionally expose an
`amount`-front-end (`MtfStretch(byAmount: 1.5)`) for callers thinking in
Frank's UI terms. Trivial.

### 1.2 `ColorSaturation` -- HSV-space saturation with Akima spline curve (port target)

```javascript
P = new ColorSaturation;
P.HS = saturationLevel;            // 1D curve over hue
P.HSt = ColorSaturation.prototype.AkimaSubsplines;
P.hueShift = 0.000;
```

What it does (PixInsight docs): per pixel, convert RGB -> HSV, look up a
saturation multiplier from the H -> S curve (interpolated by Akima
subsplines), multiply S, convert back. The curve lets callers boost some
hues more than others (e.g. push reds in stars without saturating greens).

What we need:

- `Image.AdjustSaturation(IReadOnlyList<(float Hue, float Saturation)> curve)`
  per-pixel HSV math. ~50 LOC.
- `AkimaSpline` (1D) interpolator. Either use the existing
  `FritschCarlsonSpline` (see CLAUDE.md "Stretch Pipeline") which is
  similar (monotonic cubic Hermite) and probably acceptable, or port the
  Akima variant from scratch (~80 LOC; the algorithm is well-documented).
  Start with FritschCarlsonSpline-as-substitute since we already have it;
  port Akima later only if visual diffs appear vs PixInsight.
- A `StarsColorSaturation` field on `SharpenRequest` (similar shape to
  `StarsScnrMode`): if non-null, runs between sharpen and SCNR on the
  stars-only plate, NEVER on the starless plate.

Total: ~150-200 LOC + tests + per-pixel HSV-RGB primitives if not already
in `Image.Arithmetic.cs` (probably need to add them).

### 1.3 SCNR Green AverageNeutral with `preserveLightness=true` (have it, missing one flag)

```javascript
P = new SCNR;
P.amount = 1.00;
P.protectionMethod = SCNR.prototype.AverageNeutral;
P.colorToRemove = SCNR.prototype.Green;
P.preserveLightness = true;        // <-- this
```

We have `ScnrMode.Average` + amount. **Missing**: the
`preserveLightness=true` mode. PixInsight's lightness-preserving SCNR
adjusts R/B alongside G so the overall pixel lightness (L from HSL or
CIELAB L*) stays constant. Without it, neutralising green also dims the
star; with it, the star keeps its overall brightness and only the hue
shifts.

Concretely: after `Gnew = G - amount * max(0, G - m)`, scale `(R, G, B)`
by `(L_before / L_after)` per pixel. ~30 LOC addition to
`Image.SubtractiveChromaticNoise` + new bool param.

### 1.4 Composite: `StarStretch` workflow command

Once 1.2 + 1.3 land, the Frank-parity flow is a tiny orchestration:

```csharp
public sealed record StarStretchOptions(
    double MtfAmount = 1.0,                // 0..8, default 1.0 (target=0.25)
    HueSaturationCurve? Saturation = null, // null = skip
    ScnrMode ScnrMode = ScnrMode.Average,
    bool ScnrPreserveLightness = true,
    float ScnrAmount = 1.0f);
```

Applied to the stars-only plate (any of `StarsOnly`, `SharpenedStars`, or
a user-supplied star plate). Lives in `TianWen.Lib/Imaging/Enhancement/`,
no AI dep. CLI surface: a `tianwen image starstretch <input> [options]`
verb -- or just exposed via `SharpenRequest`.

## 2. Per-plate stretch ("B")

Today the orchestrator returns `Starless`, `StarsOnly`, `SharpenedStars`,
`DeconvolvedStarless` as separate `Image` instances (all in linear units)
and recombines via additive or screen. There's no per-plate stretching
between the AI passes and the recombine.

### 2.1 Why we want it

- **Stars** typically benefit from an aggressive MTF (amount=1-3) +
  saturation + SCNR.
- **Nebula** typically benefits from a gentler MTF (amount=0.5-1.0) and
  zero SCNR.
- PixInsight workflow: stretch the two plates *separately* with different
  curves, then composite.

### 2.2 Two implementation options

| Option | Where | Lifetime impact on output |
|---|---|---|
| **A. Inside `SharpenPipeline`** -- per-plate stretch fields on `SharpenRequest`; recombine sums *stretched* plates. | `TianWen.Lib/Imaging/Enhancement/SharpenPipeline.cs` | Output FITS is in *display* units (not linear). Caller renders without further stretching. |
| **B. As a separate orchestrator step** -- `SharpenPipeline` keeps its linear-output contract; a new `PlateStretchStep` runs after `ProcessAsync` returns. | `EnhancementWorkflow` runner (see Section 3) | Output still linear from `SharpenPipeline`; the workflow runner does the per-plate stretching before write-out. |

B is cleaner and aligns with the SAS Pro "pipeline-as-list-of-commands"
shape we wanted to adopt anyway. **Decision: B**.

### 2.3 The `StretchTransform` abstraction

Generic over stretch method so both per-plate and final display can pick
freely:

```csharp
public abstract class StretchTransform
{
    public abstract Image Apply(Image input);
    public abstract Image Inverse(Image stretched);
}

public sealed class MtfStretchTransform(double targetMedian) : StretchTransform;        // wraps Image.MtfStretch
public sealed class AsinhStretchTransform(float stretchFactor) : StretchTransform;      // ~80 LOC; PI-built-in port
public sealed class GammaStretchTransform(float gamma) : StretchTransform;              // ~30 LOC trivial
public sealed class GhsStretchTransform(GhsParameters p) : StretchTransform;            // ~250 LOC ported from SAS Pro ghs_dialog_pro.py
```

Asinh is **PixInsight's built-in `ArcsinhStretch`** (not a Frank script)
-- worth adding because the PI community expects it for star-friendly
display stretching. GHS is the modern PI choice for nebulae (Cranfield).
Both invertible; safe to apply + reverse.

### 2.4 Where StretchTransform plugs in

1. **Per-plate stretch step in the `EnhancementWorkflow`**: `Sharpen` ->
   `PlateStretch(stars=<transform>, starless=<transform>)` ->
   `Recombine`.
2. **Display-side stretch in `MasterPreviewRenderer`**: today it's
   hard-coded to MTF via `StretchUniforms`; refactor to accept a
   `StretchTransform` parameter (default = current MTF behaviour) so the
   `image render` verb can take `--stretch asinh|mtf|ghs`.

## 3. Other deferred items (cross-refs)

Existing `ai-enhancement.md` Section "Phasing" lists Phases 7-10
already; restating the priority order with current state:

| Item | Status | Reference |
|---|---|---|
| `IDenoiseEnhancer` (`deep_denoise_*_AI4.onnx` wrapper) | NOT STARTED -- highest-impact missing AI piece. Slots after deconv on the *starless* branch (preserves star plate). Resolves the "deconv amplifies noise" speckle observed on first real runs. | Original Phase 8 (was "denoise") |
| `EnhancementWorkflow` + `IEnhancementCommand` + `tianwen image enhance` + `tianwen stack --enhance` | NOT STARTED -- task #21 in current session. SAS Pro pipeline-as-list-of-(command, options) shape. | Original Phase 7 |
| Runtime self-bootstrap of model files (in-app first-launch download + `tianwen models fetch` sub-command) | NOT STARTED. Critical for shipped binaries. | Original Phase 7 / TODO "Deployment" |
| Classical (non-AI) fallbacks (`AddTianWenClassicalEnhancers`) | NOT STARTED. Lucy-Richardson deconv, unsharp-mask, bilateral/NLM denoise. No fallback for star removal. | Original Phase 9 / TODO |
| `IUpscaleEnhancer` (`superres_{2,3,4}x.onnx`) | NOT STARTED. | TODO "image upscale" |
| `IWalkingNoiseEnhancer` (`deep_denoise_*_AI4_1w.onnx`) | NOT STARTED. Could be a `--walking` flag on denoise. | TODO "image denoise-walking" |
| `ITrailRemover` (`satelliteRemovalAI4.onnx`) | NOT STARTED. Belongs in stacking pre-rejection per `stacking.md` but standalone verb useful too. | TODO "image remove-trails" |
| `IAberrationCorrector` (`riccardoalberghi/abberation_models`) | NOT STARTED. Separate model repo; needs its own fetcher branch. | TODO "image correct-aberration" |
| Hexagon NPU INT8 quant (win-arm64) | NOT STARTED. Current DirectML-on-Adreno gives the GPU win for FP32. INT8 quant moves matmuls to the NPU. Swap `Microsoft.ML.OnnxRuntime.DirectML` -> `Microsoft.ML.OnnxRuntime.QNN` when ready. | Original Phase 10 / TODO |
| Per-chunk SEP-based PSF re-measurement | NOT STARTED. Current `HfdPsfEstimator` returns a whole-image scalar. | Original PLAN "Open questions" |
| GUI menu entry for `SharpenPipeline` | NOT STARTED. | TODO |

## Open questions

1. **AsinhStretchTransform parameterisation.** PixInsight's `ArcsinhStretch`
   takes `Stretch Factor` and `Black Point`; some forks also expose a
   `Highlight Protection` slider. Match PixInsight 1:1 (most users will
   set values copied from their workflow) or simplify? **Vote: match PI
   1:1 to start; simplify later if it proves over-knobbed.**
2. **Per-step MTF target on AI input?** Explored in session; concluded
   low-ROI because Frank trained all three AI4 models against
   `target_median = 0.25`. If someone wants this in future, add a
   `(double?)overrideTarget` per-call parameter on `ChunkedNafnetRunner.Run`.
   Out of scope here.
3. **Lightness preservation in SCNR -- HSL or CIELAB L\*?** PixInsight
   uses HSL by default (faster, slightly less perceptually uniform).
   Start with HSL; revisit if visual diffs vs PI are noticeable.

## Cross-references

- [`ai-enhancement.md`](ai-enhancement.md) -- original plan;
  Phases 0-6 shipped.
- [`background-extraction.md`](background-extraction.md) -- ABE /
  flatten; sits *before* the AI enhancement chain in the post-stack flow.
- [`stacking.md`](stacking.md) -- satellite removal belongs there
  as a pre-rejection filter; cross-link when `ITrailRemover` lands.
- [`CLAUDE.md`](../../CLAUDE.md) "Stretch Pipeline: CPU/GPU Mirror" -- existing
  display-stretch primitives that `MasterPreviewRenderer` already uses;
  `StretchTransform.MtfStretchTransform` wraps the same math.
- [`TODO.md`](../../TODO.md) "AI Enhancement" section -- per-verb TODOs that
  this plan groups under Sections 1-3.

## Reference files

- Frank's PixInsight Star Stretch v2.1:
  `pixinsight-updates/SetiAstroScripts05.12.2026.zip` ->
  `src/scripts/star_stretch_v2.1.js`. The relevant 3 functions are
  `applyPixelMath` (line 94), `applyColorSaturation` (line 103),
  `applySCNR` (line 116).
- Frank's SAS Pro star stretch (Python rewrite, same MTF math):
  `setiastrosuitepro/src/setiastro/saspro/star_stretch.py` and
  `legacy/numba_utils.py:2222 applyPixelMath_numba`.
- Frank's CosmicClarity input stretch (also MTF):
  `cosmicclarity/setiastrocosmicclarity_darkstar.py:517 stretch_image()`
  and `cosmicclarity/SetiAstroCosmicClarity.py:555 stretch_image()`.
