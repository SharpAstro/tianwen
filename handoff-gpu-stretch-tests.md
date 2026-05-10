# Handoff: GPU Stretch Tests + Stretch Pipeline Rescue

Closing-out summary for the `stretch-improvements` branch. Pick up from
here next session.

## Branch state

- **Branch**: `stretch-improvements`
- **PR**: [#5](https://github.com/SharpAstro/tianwen/pull/5) — open, mergeable
- **Last commit**: `18bb1ed` (Survey other GPU offscreen comp test candidates)
- **CI**: in-progress at handoff time; previous run on `29be180` was green
  except for an unrelated flake in `SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging`
  (functional suite). The earlier `0653a76` run was fully green.

## What landed (~16 commits this session)

### Core fixes (production code)

1. **`ConvergeStretchFactor` bisection direction inverted** (`ca9ea3c`)
   — too-bright reduced factor, too-dim raised it; for typical median<0.5
   astro images the relationship is the other way round. Affected anyone
   using `UseIterativeConvergence`.

2. **`GetStarMaskedMedianAndMADScaledToUnit` two bugs** (`537d932`)
   - Returned median in raw pixel-value space while the unmasked twin
     returns pedestal-subtracted -- inconsistent, drove shadows ~28x too
     high under the converged path.
   - MAD floor `invMax * 0.5f` collapsed to **0.5** after
     `ScaleFloatValuesToUnitInPlace` set `MaxValue=1`, pinning every
     masked MAD at half the dynamic range. Replaced with `0.5/65535`
     (half a 16-bit bin) so it stays correct regardless of normalisation
     state.

3. **`FritschCarlsonSpline.ComputeKnots33` capacity bug** (`73696f7`)
   — `MoveToImmutable` threw at runtime; the GUI's Shift+B (toggle curve
   mode) would crash. Length now cleanly 33 floats (no padding).

4. **`ApplyCurveLut` divisor mismatch** (`73696f7`) — CPU used
   `lut.Length - 1` but GLSL hardcoded `v * 32.0`. With pad removed both
   paths agree at divisor 32.

5. **`ComputeStretchUniforms` WB-vs-shadow coordinate-space mismatch**
   (`48b9846`) — GPU shader applies WB after pedestal subtract, then
   subtracts shadow. Shadows were derived from pre-WB stats; WB-reduced
   channels (B with wb<1) clamped to zero on bg pixels. Fix: scale
   per-channel median+mad by WB before deriving shadows/midtones/rescale.

6. **`ConvergeStretchFactor` WB-aware** (`3686ba4`) — added a
   `whiteBalance` scalar parameter that scales median/mad/binNorm so the
   bisection runs in post-WB space and the converged factor matches the
   per-channel rendering.

7. **GLSL `applyCurveLUT` off-by-one at v=1.0** (`267fae6`) — `clamp(i,
   0, 31)` ran after `frac` was computed; v=1.0 returned knot 32 instead
   of knot 33. Replaced with `min(int(idx), 31)` so frac=1 cleanly
   selects the high anchor.

8. **GLSL `stretchMode` integer mapping bug** (`29be180`) — `Unlinked`
   (the viewer's default!) was triggering the Luma shader path on GPU
   while the CPU mirror correctly went per-channel. Caught by the GPU
   smoke test on its first run. Fixed: shader now treats modes 1+2 as
   per-channel and 3 as Luma, matching `(int)StretchMode.*`.

### New CPU helpers (`73696f7`, mirror the GLSL)

- `Image.StretchChannelCpu(raw, channel, in StretchUniforms)` — single
  source of truth scalar stretch math.
- `Image.StretchLumaPixelCpu(r, g, b, in StretchUniforms)` — Rec.709
  luma stretch.
- `Image.ApplyHdr(v, amount, knee)` — soft-knee HDR compression.
- `Image.RenderStretchedRgba(in StretchUniforms, Span<byte> rgba32, ...)`
  — full image -> RGBA buffer renderer used by `ConsoleImageRenderer` (TUI
  Sixel) and tests.

### Test infrastructure

- **`StretchTests_NewPipeline.cs`** — 9 cases: parametric Theory (8
  cases covering linked / luma × WB × bgNeut × convergence × curveLut ×
  HDR) + a dedicated SPCC `[Fact]` that synthesises an M45 starfield with
  IMX533 + Sony CFA throughputs and verifies the full SPCC pipeline.
  Asserts every `StretchUniforms` field plus per-channel rendered byte
  means. Writes TIFF + JPEG per case for visual regression.

- **`StretchTestBase.cs`** — extended with per-channel float-range +
  AutoLevel quantum-range assertions; touches the 4 legacy stretch test
  files (linked / unlinked / mono / luma) without per-class duplication.

- **`GpuStretchPipelineTests.cs`** — Phase 1 of the GPU offscreen comp
  test plan. Uses `VulkanContext.CreateOffscreen` +
  `ReadbackOffscreenRgba` from `SdlVulkan.Renderer`. Local result:
  **mean diff 0.607 bytes, max 1 byte, 0% outliers >4** -- essentially
  perfect parity. Skip-on-Vulkan-unavailable so CI without a driver
  stays green.

### Data + CI

- **SASP `.gs.gz` files now tracked in git** (`0653a76`) — the
  `FilterCurveDatabase` data (`filter_curves.gs.gz`, `sensor_qe.gs.gz`,
  `pickles_sed.gs.gz`, total 3 MB) is exempt from the wildcard
  `.gitignore` so CI's checkout has data to load. Without this fix, 60+
  `FilterCurveDatabaseTests` failed every CI run after the SPCC commit.

### Documentation

- **`CLAUDE.md`** — new "Stretch Pipeline: CPU/GPU Mirror" section
  documenting the dual-implementation contract and where new stages must
  be wired. "Test Verification" pointer naming `StretchTests_NewPipeline`
  as the exemplar.
- **`TODO.md`** — `Stretch / Image Processing` section records every fix
  on this branch (8 distinct items, with dates).
- **`PLAN-gpu-stretch-tests.md`** — full GPU offscreen comp test plan
  (Phase 1 done, Phases 2-5 pending) plus survey of 8 other GPU comp
  test candidates ranked by value-per-day.

## What's still open

### CI

- **Phase 2 of `PLAN-gpu-stretch-tests.md`** — install
  `mesa-vulkan-drivers libvulkan1 vulkan-tools` in the `test-unit` CI
  job so the GPU smoke test runs on Linux runners against lavapipe (the
  software rasterizer), instead of always skipping. Also ensure
  Vortice.ShaderCompiler is happy on lavapipe.

- **Flaky meridian-flip test** —
  `SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging`
  intermittently fails with `TotalFramesWritten=0`. Not introduced on
  this branch but worth adding to the "Flaky CI Tests" section in
  `TODO.md` so it gets fixed alongside the existing entries.

### GPU tests followups (per `PLAN-gpu-stretch-tests.md` § Followups)

Surveyed 8 additional GPU offscreen comp targets. Recommended order:

1. **D. `VkRenderer` primitives vs `RgbaImageRenderer`** — highest value
   (~2 days), backs every drawing operation in the app.
2. **A. Bayer demosaic** — direct extension of Phase 1 (~½ day).
3. **F. Sky map line tessellation** — vertex-buffer comparison, no GPU
   pixel rasterisation needed (~½ day).
4. **B. Histogram pipeline** — colored bar plot comp (~½ day).
5. **C. WCS grid overlay** — only if CPU mirror written for free (~1 day).
6. **E. Sky map star rendering** — defer until projection code shared.
7. **G. Milky Way fullscreen quad** — skip.
8. **H. Overlay ellipses** — folded into D.

### Stretch pipeline followups (per `TODO.md`)

- **Per-channel convergence** — `ConvergeStretchFactor` runs once on
  luma stats; for Linked/Unlinked the converged factor is approximate
  per channel because the WB scalar is the Rec.709-weighted average not
  the per-channel WB. Fix needs the factor to become a triple. Bigger
  refactor.

- **Normalize after stretch** — `x / max(x)` to fill full [0, 1] range.
  Marked `[ ]` in TODO.md, hasn't been touched.

- **Luma blend** — smoothly blend between linked and luma-only results.

- **rec601/rec2020 luminance options** in luma stretch.

## Pickup checklist for next session

1. Look at the latest CI run on `stretch-improvements` (probably
   complete by then) and confirm green or chase any new flake.

2. Review PR #5 if you want to merge -- the branch is now feature
   complete from a stretch-improvements standpoint.

3. If continuing the GPU comp test work, start with **Phase 2** (CI
   driver install) so the existing smoke test runs in CI -- without
   that, the test ships dormant.

4. Then **Followup D** (`VkRenderer` primitives vs `RgbaImageRenderer`)
   is the highest-value next test.

## Key files to know

- `src/TianWen.Lib/Imaging/Image.Stretch.cs` -- CPU stretch helpers (single
  source of truth for scalar math).
- `src/TianWen.Lib/Imaging/Image.Histogram.cs:235` --
  `GetStarMaskedMedianAndMADScaledToUnit` (recently fixed).
- `src/TianWen.UI.Abstractions/AstroImageDocument.cs:327` --
  `ComputeStretchUniforms` (entry point, scales stats by WB,
  bakes uniforms).
- `src/TianWen.UI.Shared/VkFitsImagePipeline.cs` -- GLSL fragment shader
  + UBO upload (`UpdateStretchUBO`).
- `src/TianWen.Lib.Tests/StretchTests_NewPipeline.cs` -- end-to-end
  pipeline test exemplar.
- `src/TianWen.Lib.Tests/GpuStretchPipelineTests.cs` -- GPU offscreen
  smoke test, template for future GPU comp tests.

## Memory entries written

- `feedback_cpu_gpu_stretch_mirror.md` — CPU mirror is intentional;
  scalar math one source of truth, CPU loop mirrors GLSL loop.
- (No new memory entries this session beyond the above.)

## Quick stats

- 16 commits on this branch
- ~370 lines of test code in `StretchTests_NewPipeline.cs`
- ~280 lines in `GpuStretchPipelineTests.cs`
- 4 production-code bugs fixed (bisection, masked-stats space, MAD
  floor, GLSL stretchMode)
- 2 GLSL bugs fixed (`applyCurveLUT` v=1.0, `stretchMode` mapping)
- 1 GUI-crashing bug fixed (`ComputeKnots33` capacity)
- 1 doc + survey of 8 future GPU comp test candidates
- 142+ tests passing locally; 9/9 GPU/CPU parity tests green
