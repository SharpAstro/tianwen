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
  `Image.ApplyCurveLut`, `Image.ApplyBoost`, `Image.ApplyFinish`, `Image.RenderStretchedRgba`; used by
  `ConsoleImageRenderer` (TUI Sixel) and tests (`StretchTests_NewPipeline`). Never uses the GPU.

Pipeline order in both: pedestal subtract -> bg neutralization -> WB -> shadow/rescale -> MTF ->
luma blend -> curves (LUT or boost) -> HDR knee -> finish -> normalize -> clamp. Per-channel for
Linked/Unlinked, luma-Y'/Y for Luma.

**The finish is the Affinity masked contrast boost and saturation, as ONE stage of the stretch**
(`StretchUniforms.FinishContrast` 0 to 1, `FinishSaturation` 1 = off; `Image.ApplyFinish` and the shader's
`applyFinish`, held together by `GpuMatchesCpuForTheMaskedFinish`). Its mask (`Image.FinishMask`) is read per pixel
from the STRETCHED luminance against the post-stretch background (the boost's `curvesMidpoint`): zero on the sky,
full a quarter of the way to white, easing out over the last fifth below white, so the sky's noise and the star cores
are left alone. Contrast is a smoothstep S-curve on the luminance between the background and white, the colour kept;
saturation then moves each channel away from the new luminance. The viewer's Tone popover (Contrast, Saturation),
the CLI's `image render --contrast-boost --saturation` and the stacking preview (`stack --contrast-boost
--saturation`, `MaskedBoostOptions`) all drive this stage, so no host has a masked boost of its own: the separate pass
that boosted the finished 16-bit buffer through a blurred percentile mask (`Image.MaskedBoost`) was removed with it.

**The viewer neutralises the background by default, min pivot after the white balance, as the headless render
always has.** A white balance levels the stars, not the sky behind them; shown Linked with the sky left coloured, one
channel's background sits below the shared shadow point and clips once a denoise has shrunk the noise (the enhanced
L-Quad Sculptor master, its SPCC triple in the pixels, rendered as a flat cyan field). And a balance already IN the
pixels (`AstroImageDocument.IsColourInPixels`) counts as an active calibration for Auto, so the frame renders Linked,
as the headless render resolves it; Unlinked, each channel's own curve undid the balance.

**One colour chain, every step always run, an identity where it has nothing to do** (the owner, 2026-10-22: "a more
uniform pipeline, not a mess of if else"):

- **The balance has ONE source, the document's calibration.** A file that STATES an SPCC fit (its `WB*` cards, written
  by the stacker that fitted it) carries it from the moment it opens (`AstroImageDocument.AdoptStatedBalanceAsync`,
  popover "SPCC, from the file"), exactly as a fit made in the viewer does; a fit through a line-selective filter is
  carried as the display multiplier it is. The calibration is ON by default (`ViewerState.ColorCalibrationEnabled`), so
  such a master opens as the CLI renders it, and the enhance bakes in what is shown (`EnhanceActions.EnhanceWhiteBalance`):
  the calibration on display, when it is photometric, and nothing when it is switched off.
- **The enhance's background stage is the gradient AND the level.** The gradient correction flattens each channel about
  its OWN level, by design; right after it the sky is levelled IN THE PIXELS on every colour frame
  (`Image.WithBackgroundNeutralised`: each channel raised by an offset to the brightest channel's sky, so the colour above
  the sky is kept and no sample drops). A sky already level takes a zero offset.
- **Every stretch mode neutralises, then balances, then curves**, Luma included: its branch skipped the neutralisation,
  and its curve was solved on a luminance measured above its own pedestal while the pixels measured it above the
  per-channel pedestals (`StretchSolver`'s Luma branch now solves in the pixel path's coordinates).
- **Luma keeps a pixel's colour ABOVE THE BLACK POINT** (the curve's shadow), not above the pedestal: measured above the
  pedestal, the sky every pixel of a galaxy shares diluted its ratios to grey. On the enhanced L-Quad Sculptor master the
  core reads R 0.0018 / G 0.0014 / B 0.0011 over its levelled sky, an orange Linked crushes to white near the top of its
  curve and Luma now shows.
- **An auto stretch is sized by the data's real noise, not by what a denoiser left.** Every enhance stamps how many times
  quieter it left the frame (`ImageMeta.NoiseReduction`, FITS `NOISERED`, the mean noise in over the noise out,
  compounding over earlier enhances), and every solve multiplies the frame's measured MAD back by it
  (`StretchSolver.ComputeStretchUniforms`' `noiseScale`: the viewer, a reopened file, the headless render, thumbnails).
  The preset (`(0.2, 2.8)`) and the user's choice of it are untouched; only the noise the black point is sized by
  changes. Without it the enhanced Sculptor master solved to midtones of 0.000127 (MAD 6e-6, a fifth of its pre-denoise
  noise) and every pixel above the sky went toward white, its colour noise with it. A frame no enhance touched carries
  no card and stretches exactly as before.
- **An enhanced frame keeps its star split for the render** (`AstroImageDocument.StarSplit`, #1771): the enhance's
  starless plate is the picture every display lever acts on, and its stars plate is kept LINEAR and stretched as it is
  drawn by the CLI's colour asinh (`Image.AsinhStretch`, `Image.AsinhLumaScale`: every channel times the one factor its
  Rec.709 luminance gives, no black point, unclipped) at the Tone popover's Stars dial (`ViewerState.StarStretch`, the
  asinh beta on a log track from 1 to 1000, carried in `DisplayRendition`; default `StarSplit.StarCurve`, 300, the CLI's
  `--dual-stretch --star-stretch-mode asinh --asinh-beta 300`), then screened over the stretched result after the
  normalise (`ScreenedStars.Display`, `Image.ScreenStars`, `a + s(1 - a)`, each channel clamped; the shader samples the
  plate from descriptor set 2, a 1x1 placeholder when a frame has none, with `finishParams.z` the beta, 0 for no split,
  and `.w` one over asinh of it). The beta is a uniform, so the dial costs no re-upload and the plate is held once. **A
  per-channel curve is the wrong one for stars**: StarStretch (the CLI's default) runs each channel into the top of its
  curve on its own, which whitened every core; the asinh keeps a star's colour to its core, chosen by eye on the enhanced
  Sculptor master against StarStretch 2 and 3.5 and betas 100 and 1000 (#1773). The stars take no OTHER display lever,
  which is the point: one curve over the recombined frame lifted every star's wings with the faint signal, the blue rings
  round the Sculptor's bright stars (#1771, #1772). The document's own image stays the recombined master
  (statistics, star detection, what Save writes as FITS); a saved PNG composes as the screen does. Held by
  `StarSplitRenderTests` and `GpuMatchesCpuForTheStarScreen`. In Luma mode the producer always populates BOTH
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
default and is `StretchMode.Auto`, and every renderer resolves it before building uniforms, headless
included: `MasterPreviewRenderer` from the same four inputs as the viewer, while the node's
`PreviewEncoder` and the live pane's `LiveFramePreviewSource` resolve from channel count alone, since a
live frame carries no calibration (colour Unlinked, mono Linked). Both headless renderers used to render
Linked explicitly; the master's literal clipped red on narrowband cards, and the encoder's kept a raw
colour sky's imbalance as a cast (P0b item 15 of `docs/plans/hardware-in-the-server.md`).

### The bug this replaced

Linked used to replicate channel 0's *stats* and scale each copy by that channel's own multiplier,
giving three curves whose anchors tracked the multipliers and divided them back out: a WB had **no
effect** on a linked render, three very different SPCC triples rendered identically, and the default
mode was Unlinked so SPCC looked like a no-op everywhere. Pinned by `StretchLinkedWhiteBalanceTests`;
measurements in [`../known-limitations.md`](../known-limitations.md). **Never re-derive a per-channel
curve in the Linked branch.**

## `StretchMode.Auto` is a UI intent, never a shader mode

(`StretchMode.Planetary` is the second: the high-key planetary stretch `planetary stack`'s preview is drawn with, resolved by the
document to `Unlinked` uniforms from the frame's own percentiles (`Image.ComputePlanetaryStretchUniforms`), and the mode a frame
whose `OBJECT` names a planet opens in, by ONE rule, `StretchMode.ForFrame`, for the viewer, the thumbnail and `tianwen view`.
`docs/plans/planetary-restoration.md`, "The planetary stretch in the viewer".)

It is resolved to a concrete mode before any `StretchUniforms` is built. Auto is LAST in the enum so
the shader's numeric values for None/Linked/Unlinked/Luma are unchanged, and `StretchSolver` maps a
stray Auto to Linked as a backstop.

Resolution is the pure extension `mode.ResolveAuto(isColour, calibrationActive,
colourIsNotPhotometric, channelsAlreadyAgree)` (`StretchModeExtensions`, TianWen.Lib, shared with the
Explorer thumbnail renderer). It grew from two inputs to four once a literal `StretchMode.Linked` in
`MasterPreviewRenderer` (the dataset bake and `tianwen image render`) exposed the gap: the
line-selective veto (`FilterCurveDatabase.IsLineSelective`, a 38 nm cut measured over all 183 shipped
curves) applied to the interactive viewer alone and to nothing the bake drew, so a 3 nm L-Ultimate
master's SPCC (`1.136, 1.000, 1.860`, a fit against a continuum that never reached the sensor) rendered
Linked: the one shared shadow point came off the mean of three unequal medians, and red -- weakest
after that triple -- clipped to zero once the enhance had shrunk the MAD (five of 139 gallery cards,
every one of them that filter). The white balance is not refused and not changed; what Unlinked stops
is ASSERTING a fit of nothing as colour.

| input | resolves to | why |
|---|---|---|
| colour + a calibration being applied + the filter is photometric | Linked | the WB shows as colour |
| colour + a calibration being applied + the filter is line-selective (narrowband) | Unlinked | no continuum reached the sensor; a shared curve clips the weakest channel |
| colour, no calibration | Unlinked | each channel's background neutralises, no cast asserted |
| mono | Linked | there is nothing to link |

The fourth input, whether the frame's backgrounds already agree, is `StretchSolver.ChannelsAgree` (the
0.15 percent rule), which the viewer's `ChannelsAlreadyAgree` delegates to; it used to live on the
document alone. Through `RenderAsync` it rarely decides, because with no catalog that path still
solves a sky-background white balance and a calibration is then active, so the pin is on the predicate
rather than the outcome.

The two producers resolve it with what only they know -- `AstroImageDocument` from its channels and
`autoWb is not null` (which already honours the SPCC toggle), `LiveFramePreviewSource` from channel
count with no calibration. So running SPCC on an Auto frame flips it to Linked on its own, which is the
decision the user otherwise made by hand. The StretchLink button names what Auto resolved to
("Auto (Linked)"), and while the stretch is off it names the stretch STF would turn on
(`ViewerState.StretchModeOn`, #1440): Planetary on a SER, never a mode in effect nowhere. Pinned by `ColorCalibrationToggleTests`, `ViewerActionsTests.DefaultStretchMode_IsAuto`
and `NarrowbandStretchModeTests.TheHeadlessRendererHonoursTheSameLineSelectiveVeto` (whose broadband row
is the control). A test or renderer that needs a fixed curve passes an explicit mode, never Auto.

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

## The statistics a stretch is built on must resolve the frame's own noise

Every curve here starts from a channel's median and MAD (`Image.GetPedestralMedianAndMADScaledToUnit`,
`Image.GetStats`), read off a histogram. A unit-scaled frame is binned at 1/65535, which is far finer than a
normal sky needs, and not fine enough for one sky: an **enhanced narrowband master**. The eta Carinae 2026-02-20
pier A master, after the RC-Astro enhance, had its sky at about 1e-4 of full scale with a noise MAD of 0.2 to 0.5
of a bin (medians 5.08 / 11.42 / 6.53 bins, MADs 0.52 / 0.33 / 0.20, measured exactly on its pixels). The coarse
walk took each median as a whole bin and floored every MAD at half a bin, so the three channels of the Unlinked
stretch (a line-selective frame's) were curved on numbers the rounding chose. That card rendered green (sky
52 / 59 / 44) while its raw view and its pier B sibling, whose rounding fell differently, did not.

So a **float** frame whose coarse MAD is under `Image.RefineBelowMadBins` (8) is read again on a zoomed histogram,
the same `Traverse` kernel with the scale multiplied by 256 and the pedestal moved to a window 64 coarse bins either
side of the coarse median (`Image.RefineStretchStats`). The median is taken against the COARSE count, since the zoom
drops what lies above its window and piles what lies below into its first bin; the window is eight times the widest
MAD refined, so the MAD's walk never reaches either edge. The MAD floor is then half a FINE bin. After it, all four
eta Carinae renders (both piers, raw and enhanced) read a sky of 51 / 51 / 51.

Two cases keep the coarse walk, both deliberately:

- **An integer source** (`BitDepth` integral, a 16-bit frame scaled to unit included). Its samples sit on a lattice
  no finer than a coarse bin, so a zoom only reads the lattice back, and it would move a MAD the coarse walk
  interpolated (0.667 bins on a 40/30/30 three-level sky) to the lattice's 1. No frame gains from that.
- **A float frame that is one value** (a constant plane): the zoom finds it in a single fine bin, a MAD under one
  fine bin, and the coarse statistics and their half-bin floor stand.

Pinned by `FaintFrameStretchStatsTests`, whose faint-sky rows fail with the refinement switched off.

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
