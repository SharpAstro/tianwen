# The stretch pipeline: CPU/GPU mirror, link modes, white balance

The subject in full. `CLAUDE.md` keeps only the rules; this file carries the reasoning and the
measurements behind them. Companions: [`stacking-render-pipeline.md`](stacking-render-pipeline.md)
(sections 5-6, the unified solve and the per-pixel CPU order) and
[`../known-limitations.md`](../known-limitations.md) (the auto-stretch that cancelled its white
balance, and the CPU/GPU drift note).

## Two implementations, one set of uniforms

The pipeline runs in two parallel implementations that must produce visually equivalent output for the
same `StretchUniforms`:

- **GPU**: `TianWen.UI.Shared/Shaders/image.frag` (loaded by `VkFitsImagePipeline` from the baked
  `Shaders/spirv/image.frag.spv`): `stretchChannel` (per-channel) + the Luma branch that mirrors
  `StretchLumaPixelCpu`; used by the live FITS viewer.
- **CPU**: `Image.StretchChannelCpu`, `Image.StretchLumaPixelCpu`, `Image.ApplyHdr`,
  `Image.ApplyCurveLut`, `Image.ApplyBoost`, `Image.RenderStretchedRgba`; used by
  `ConsoleImageRenderer` (TUI Sixel) and tests (`StretchTests_NewPipeline`). Never uses the GPU.

Pipeline order in both: pedestal subtract -> bg neutralization -> WB -> shadow/rescale -> MTF ->
luma blend -> curves (LUT or boost) -> HDR knee -> normalize -> clamp. Per-channel for
Linked/Unlinked, luma-Y'/Y for Luma. In Luma mode the producer always populates BOTH
`StretchUniforms.LumaStretch` (scalar Luma MTF params) AND per-channel `Shadows/Midtones/Rescale`
(linked branch params) so the shader can blend between them via `LumaBlend`.

A new pipeline stage (saturation boost, denoise, ...) is wired into BOTH the GLSL shader AND the CPU
helpers. A stage that only exists in GLSL is a regression for the tests and the TUI.

**Luma mode's provenance: SetiAstro's PixInsight "Statistical Stretch" script.** Stretching one
scalar luma value and rescaling R/G/B by the stretched/raw ratio (rather than stretching each
channel independently) is a direct descendant of that script's approach, along with `LumaBlend` and
the Luma-weighting options below. The shipped `[x]` items and their provenance notes live in
`docs/todo/imaging.md`'s "Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3)" section;
see [`pixinsight-parity.md`](../plans/pixinsight-parity.md) for the indexed cross-reference (this
architecture doc previously carried none, which is the gap that doc exists to close).

## `Linked` and `Unlinked` mean what they mean in PixInsight

The difference lives ENTIRELY in the uniforms -- neither the GLSL nor `StretchChannelCpu` branches on
the mode, so `StretchSolver` is the only place the distinction exists and the only place it can
silently collapse.

- **Linked writes ONE curve into all three slots**, derived from the mean of the per-channel
  WB-applied medians and MADs (PI's and Siril's linked STF), so a white balance survives as colour.
- **Unlinked writes each channel's own auto-normalised curve**, which absorbs the auto calibration and
  neutralises the background -- that is what the mode is FOR, not a bug.

`ViewerActions.DefaultStretchMode` (= `StretchLinkModes[0]`) is the single source for every VIEWER
default and is `StretchMode.Auto`; `MasterPreviewRenderer` and `PreviewEncoder` still render **Linked
explicitly** (a baked master carries its calibration, so there is nothing to resolve per frame).

### The bug this replaced

Linked used to replicate channel 0's *stats* and scale each copy by that channel's own multiplier,
giving three curves whose anchors tracked the multipliers and divided them back out: a WB had **no
effect** on a linked render, three very different SPCC triples rendered identically, and the default
mode was Unlinked so SPCC looked like a no-op everywhere. Pinned by `StretchLinkedWhiteBalanceTests`;
measurements in [`../known-limitations.md`](../known-limitations.md). **Never re-derive a per-channel
curve in the Linked branch.**

## `StretchMode.Auto` is a UI intent, never a shader mode

It is resolved to a concrete mode before any `StretchUniforms` is built. Auto is LAST in the enum so
the shader's numeric values for None/Linked/Unlinked/Luma are unchanged, and `StretchSolver` maps a
stray Auto to Linked as a backstop.

Resolution is the pure extension `mode.ResolveAuto(isColour, calibrationActive)`
(`StretchModeExtensions`, TianWen.Lib, shared with the Explorer thumbnail renderer):

| input | resolves to | why |
|---|---|---|
| colour + a calibration being applied | Linked | the WB shows as colour |
| colour, no calibration | Unlinked | each channel's background neutralises, no cast asserted |
| mono | Linked | there is nothing to link |

The two producers resolve it with what only they know -- `AstroImageDocument` from its channels and
`autoWb is not null` (which already honours the SPCC toggle), `LiveFramePreviewSource` from channel
count with no calibration. So running SPCC on an Auto frame flips it to Linked on its own, which is the
decision the user otherwise made by hand. The StretchLink button names what Auto resolved to
("Auto (Linked)"). Pinned by `ColorCalibrationToggleTests` +
`ViewerActionsTests.DefaultStretchMode_IsAuto`. A test or renderer that needs a fixed curve passes an
explicit mode, never Auto.

## Background neutralisation is solved for a neutral POST-WB background

So its gains depend on the calibration. The gains run before the WB multiply, so neutralising the
pre-WB background and then multiplying by a non-neutral triple just re-tints it -- which rendered a
correctly-calibrated SMC master visibly blue while `NeutBg` reported `1.00/1.00/1.00`. Every
`BackgroundNeutralizationMethod` honours the `whiteBalance` argument (it was MinPivot-only, and Mean is
the default); a neutral WB reduces to the old arithmetic bit-for-bit.

**Anything caching these gains owes the WB in its cache key** -- `AstroImageDocument` keys on
`(method, WB)`. Gains print at **F4**: they are affine about 1.0 against a ~0.002 background, so the
triple that fixes a 2.66x cast is `(0.9981, 1.0003, 1.0005)` and F2 shows three 1.00s.

The `MinPivot` gain formula (`out = norm * g + (1 - g)`) ports SetiAstro Suite Pro's
highlight-protecting neutralization and is algebraically verified equivalent to SETI's own
`out = 1 - (1 - val) * g` (`BackgroundNeutralization.ComputeGains`, provenance recorded in
`docs/todo/imaging.md` and indexed in [`pixinsight-parity.md`](../plans/pixinsight-parity.md)).

## The single producer, and its coordinate space

`AstroImageDocument.ComputeStretchUniforms` is the single producer of `StretchUniforms`; it scales
per-channel stats by WB before deriving shadows/midtones/rescale so the post-WB norm and shadow are in
the same coordinate space. `ConvergeStretchFactor` takes a `whiteBalance` scalar and operates entirely
in post-WB space (median, mad, binNorm all multiplied) so the converged stretchFactor matches the
per-channel rendering.

## The SPCC / Calibrate toggle gates the RENDER, not the measurement

`ComputeStretchUniforms` and `ComputeBackgroundNeutralization` take `applyColorCalibration` (from
`state.ColorCalibrationEnabled`): false renders as if no auto triple existed while
`AstroImageDocument.ColorCalibration` keeps holding it, so toggling off shows the raw frame and
toggling on restores the exact render with no re-fit. It used to gate only the toolbar highlight and
the manual-WB stash, so "turning SPCC off" and pressing W changed nothing on screen.

Two things ride with it:

- **W is a toggle** (`SetColorCalibrationEnabled(!enabled)` then `TryStartColorCalibration`), not
  start-only, or it is a no-op on an already-calibrated document.
- **An enhance INHERITS the calibration** rather than re-fitting it: the AI enhance calls
  `AstroImageDocument.InheritColorCalibration(original)`, because the enhanced raster has its own star
  list and the auto-retrigger would re-fit SPCC on deconvolved/denoised pixels and land a different
  triple, so a calibrated frame took on a NEW cast a moment after the enhance landed (the "adds
  another colour cast" report). Only the WB triple is inherited; background neutralisation is
  re-solved per document, since an enhanced background genuinely differs (the same share-only-the-WB
  rule the stacking plates follow).

Pinned by `ColorCalibrationToggleTests`.

## Two WB facts the viewer's manual WB sliders depend on

**(1) The stat scaling only makes sense for the AUTO calibration** (`ColorCalibration`); its whole job
is to keep the background neutral. A MANUAL WB multiplier that ALSO scaled the stats would be cancelled
by a per-channel auto-normalised stretch (Unlinked / linear), so the producer takes a separate
`shaderWhiteBalance` (= auto x manual) that goes to `StretchUniforms.WhiteBalance` while only the auto
WB scales the stats. A neutral manual triple leaves `shaderWhiteBalance == whiteBalance`, so the
auto-only path is bit-identical. This split is also why the two halves must stay separate rather than
being collapsed into one number: the auto half changes what an Unlinked stretch does with the
calibration.

**The sliders show the composed EFFECTIVE triple** (`auto x manual`, via
`StretchSolver.ComposeWhiteBalance` so the panel cannot drift from the render) and a drag solves back
for the manual factor; they showed the manual triple alone until then, so a calibrated image sat at
1.00/1.00/1.00 on the one control whose job is to report the white balance. Their travel is its OWN
constant (`[0.25, 4]`), never `GrayWorldWhiteBalance`'s `[0.5, 2]` clamp -- that bounds what the
*estimator* may return, and a real photometric fit lands outside it (R = 0.463), which the shared
constant silently rounded to 0.50.

**(2) WB is applied in the `StretchMode.None` (linear) path**, in the GLSL `else` branch + the CPU
`RenderStretchedRgba` / `RenderStretchedRgba16` + `ConsoleImageRenderer` None branches. This is
load-bearing: a SER opens in linear mode (`ViewerController`), and the old None path was a pure
passthrough that ignored `WhiteBalance`, so WB (manual OR auto Calibrate/SPCC) did nothing until a
non-linear stretch was toggled on. The mono None path stays a straight passthrough (WB is meaningless
for one channel), mirroring the GLSL mono branch.

## Luma weights

They live in `StretchUniforms.LumaWeights` (Rec.709 / Rec.601 / Rec.2020 / SensorMatched via the
`LumaWeighting` enum, default Rec.709). The CPU `StretchLumaPixelCpu`, the GLSL Luma branch and
`StretchUniforms.ComputePostStretchBackground` all read from the uniform, never hardcode Rec.709
constants. `LumaWeighting.SensorMatched` resolves via `AstroImageDocument.ResolveLumaWeights` ->
`FilterCurveDatabase.TryComputeSensorLumaWeights` (integrates sensor QE x Sony CFA R/G/B over the
visible, normalises to sum 1); silently falls back to Rec.709 when the sensor model is not recognised.

Rec.709/601/2020 cover the standard broadcast/photometric weightings named directly in questions
like "can we retain more colour via Rec.2020"; `SensorMatched` goes further than either by deriving
the weights from the sensor's own QE x CFA response instead of a generic standard. Same Statistical
Stretch lineage as the rest of Luma mode -- see the provenance note above.

## Post-stretch normalize

When the caller passes `normalize: true` to `ComputeStretchUniforms`, the producer calls
`Image.PredictPostStretchMaxScale` (walks each channel histogram's top non-zero bin through the full
chain) and sets `StretchUniforms.NormalizeScale = 1/max`. CPU and GPU multiply by this scale after
curves + HDR but before the final clamp; single-pass, no GPU reduction needed. Default 1.0 = no-op.

## Test verification

`StretchTests_NewPipeline.cs` is the end-to-end test for the stretch + colour pipeline. It exercises
every input field the GPU shader cares about and writes TIFF + JPEG per case to the temp test-output
dir for visual regression. The companion `StretchTestBase.cs` adds per-channel float-value range +
AutoLevel quantum-range assertions to all four legacy stretch test files.

Pattern when extending tests: assert per-channel byte/float means stay inside `(epsilon, max-epsilon)`
to catch the channel-collapse regressions hit during the WB + shadow coordinate-space refactor.
