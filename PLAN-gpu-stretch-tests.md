# PLAN: GPU-side Stretch Pipeline Tests via Offscreen Renderer

After landing the stretch-improvements branch (CPU/GPU mirror, WB-aware
convergence, masked-stats fixes, full pipeline parameter verification), the
remaining gap in test coverage is direct verification that the GLSL shader
in `VkFitsImagePipeline` produces the same pixel output as
`Image.RenderStretchedRgba` for the same `StretchUniforms`. The
`SdlVulkan.Renderer` package recently shipped a complete offscreen path
that makes this test feasible.

## Goal

Add a test that, for each stretch case (linked / unlinked / luma, with or
without WB / bgNeutralization / curveLut / HDR), renders the same image +
uniforms via:

1. `Image.RenderStretchedRgba(uniforms, ...)` (CPU mirror)
2. `VkFitsImagePipeline.RecordImageDraw` against an offscreen `VulkanContext`,
   then `ctx.ReadbackOffscreenRgba()`

Compares the two RGBA byte buffers within a tolerance. Catches future
divergences between the GLSL shader and the CPU mirror.

## What we have

`SdlVulkan.Renderer` (sibling repo `../SdlVulkan.Renderer`) provides:

- `VulkanContext.CreateOffscreen(VkInstance instance, uint w, uint h, ...)`
  -- builds a context that renders to a single `VkImage`, no surface, no
  swapchain, no `VK_KHR_swapchain` device extension. README's "Headless /
  offscreen rendering" section is the canonical reference.
- `VkRenderer.BeginOffscreenFrame(clearColor)` / `EndOffscreenFrame()` --
  drop-in replacements for the windowed `BeginFrame` / `EndFrame`.
- `VulkanContext.ReadbackOffscreenRgba()` -- returns `byte[]` of
  `R, G, B, A` per pixel after frame completion. Already swizzles
  `B8G8R8A8` -> `R8G8B8A8` for caller convenience.

`VkFitsImagePipeline` (in TianWen.UI.Shared) is constructed with a
`VulkanContext` and uses the context's render pass + pipeline layout,
which the offscreen path keeps compatible with the swapchain path.
`UploadChannelTexture`, `UpdateStretchUBO`, `RecordImageDraw` are all
reusable as-is.

## Test architecture

```
+-----------------------------------------------------------+
| TianWen.Lib.Tests.GpuStretch (new test class)             |
| or TianWen.UI.Shared.Tests (new test project)             |
+-----------------------------------------------------------+
| OneTimeSetup:                                             |
|   vkInitialize()                                          |
|   vkCreateInstance(empty CI) -> instance                  |
|   ctx = VulkanContext.CreateOffscreen(instance, W, H)     |
|   renderer = new VkRenderer(ctx, W, H)                    |
|   pipeline = new VkFitsImagePipeline(ctx)                 |
|                                                            |
| For each [Theory] case:                                   |
|   image = await Load(fixture)                             |
|   uniforms = AstroImageDocument.ComputeStretchUniforms... |
|                                                            |
|   // CPU side                                             |
|   var cpuRgba = new byte[W * H * 4];                      |
|   image.RenderStretchedRgba(uniforms, cpuRgba, ...);      |
|                                                            |
|   // GPU side                                             |
|   pipeline.UploadChannelTexture(image.GetChannelSpan(0)..)|
|   pipeline.UploadChannelTexture(image.GetChannelSpan(1)..)|
|   pipeline.UploadChannelTexture(image.GetChannelSpan(2)..)|
|   renderer.BeginOffscreenFrame(black);                    |
|   pipeline.UpdateStretchUBO(cmd, ... uniforms ...);       |
|   pipeline.RecordImageDraw(cmd, ctx, 0, 0, W, H, W, H);   |
|   renderer.EndOffscreenFrame();                           |
|   var gpuRgba = ctx.ReadbackOffscreenRgba();              |
|                                                            |
|   AssertByteSimilar(cpuRgba, gpuRgba, tolerance: 2);      |
+-----------------------------------------------------------+
```

## Phase plan

### Phase 1 -- GPU test fixture + smoke test (1 day)

- New file `src/TianWen.Lib.Tests/GpuStretchPipelineTests.cs` (don't add a
  new project yet; reuse the existing test project to keep CI infra simple).
- Add a `ProjectReference` to `TianWen.UI.Shared` from `TianWen.Lib.Tests.csproj`.
  This pulls in `SdlVulkan.Renderer` + `DIR.Lib` + `Vortice.ShaderCompiler`
  + `SDL3-CS`. Native libs (`libvulkan`, SDL3) are loaded lazily; Vulkan
  init throws on missing driver, which we catch to skip the test.
- Helper class `OffscreenGpuContext : IDisposable` that owns
  `(instance, ctx, renderer, pipeline)` and exposes a `RenderToBytes(...)`
  method. Created lazily in a test class fixture to amortize the ~200 ms
  Vulkan init cost across all theory cases.
- Single `[Fact]` smoke test: render the synthetic M45 fixture from
  `StretchTests_NewPipeline.GivenSyntheticStarFieldWhenSpccCalibratedThenWritesTiff`
  through both CPU and GPU paths, assert byte-mean-difference < 1.0.

### Phase 2 -- Skip-when-unavailable + CI driver install (0.5 day) [DONE]

- [x] Wrap Vulkan init in try/catch; on failure, `Assert.Skip("Vulkan unavailable")`
  with a clear diagnostic listing the underlying error. (Shipped in Phase 1
  via `IsVulkanInitFailure` in `GpuStretchPipelineTests.cs`.)
- [x] CI workflow update: add `mesa-vulkan-drivers libvulkan1 vulkan-tools` to
  the `cache-apt-pkgs-action` package list in the `test-unit` job, and bump
  `version: 1.0 -> 1.1` to invalidate the apt cache. Mesa ships `lavapipe`
  (software rasterizer) which is fast enough for our ~1280x1024 test images
  and runs without a GPU. Added a `vulkaninfo --summary` diagnostic step
  before the build so the ICD list appears in the build log.
- Document expected behaviour:
  - Local Windows: native GPU driver (NVIDIA/AMD/Intel) handles it -> hardware path.
  - Local Linux: Mesa drivers if installed; otherwise lavapipe.
  - CI: lavapipe deterministic software rasterizer.
  - macOS: MoltenVK; tests skip if not on the runner.

### Phase 3 -- Theory cases parity check (1 day) [DONE]

- [x] Ported the 8 cases from `StretchTests_NewPipeline.GivenColorFitsWhenRenderingThroughCpuPipeline...`
      into `GpuStretchPipelineTests.GpuMatchesCpuForVelaStretchCases`. Each case loads the
      Vela_SNR_Panel fixture, runs the same uniforms-building logic as the CPU theory, then
      renders through both `Image.RenderStretchedRgba` and the offscreen `VkFitsImagePipeline`
      and asserts per-byte parity. Hardware Adreno X1-85: mean=0.5, max=1, 0% outliers across
      all 8 cases (tolerances `mean < 1.5`, `max < 16`, outliers `< 1%` per the existing
      Phase 1 budget; tighten once lavapipe CI numbers land).

```csharp
[Theory]
[InlineData("linked", false, false, false, 0, 0f, "01_baseline")]
[InlineData("linked", true,  true,  true,  1, 0.8f, "07_full_pipeline")]
// ... rest from StretchTests_NewPipeline ...
public async Task GpuMatchesCpuRenderForAllStretchModes(
    string mode, bool applyWb, bool applyBgNeut, bool useConvergence,
    int curvesMode, float hdrAmount, string label)
{
    // Same setup as CPU test, then both paths, then per-pixel diff.
}
```

Tolerance choice:

- **Mean absolute difference per byte: < 1.0** -- accounts for FP rounding
  in MTF, mediump Vulkan defaults, and B8G8R8A8 round-trip.
- **Max single-pixel difference: <= 4** bytes -- isolated outliers at MTF
  knee transitions or curve LUT bin boundaries.
- **No more than 0.1% of pixels exceed tolerance** -- paranoia bound.

### Phase 4 -- Synthetic SPCC GPU verification (0.5 day)

The synthetic M45 SPCC test in `StretchTests_NewPipeline` exercises the
full SPCC pipeline (filter throughput, Tycho-2 matching, pivot1, curves,
HDR). Running it through GPU verifies the GLSL shader handles the
non-identity WB + curve LUT + HDR combination correctly.

### Phase 5 -- Wire into CI (0.5 day)

Move the GPU tests behind a `[Trait("Category", "GPU")]` filter so they
can be opt-in. Add a separate test job in the CI workflow that runs
`dotnet test --filter "Category=GPU"` -- failures here are blocking once
mesa-vulkan-drivers install is verified, but allow the existing
`test-unit` job to run untouched in parallel during the rollout.

## Risks

| Risk | Mitigation |
|---|---|
| Mesa lavapipe FP differences vs hardware GPU > tolerance | Use mean+max combined tolerance; investigate per-case if outliers found. Locally verify against real GPU once before merging. |
| Vulkan native lib not present on macOS CI runner | `Assert.Skip` on init failure; gate the GPU CI job to ubuntu-latest only. |
| TianWen.UI.Shared brings in heavy SDL3 deps even for offscreen | The README explicitly notes "no SDL window" for offscreen path; SDL3-CS is in the dependency graph but `SDL_Init` is never called. Verify by checking the SDL3 native lib stays unloaded during the test. |
| First Vulkan init takes ~200 ms (driver enumeration, ICD loading) | Class fixture so all theory cases share one context. |
| GLSL shader compilation at startup adds ~50 ms per test class | Vortice.ShaderCompiler is already loaded for the existing builds; same SPIR-V cache applies. |
| `VkFitsImagePipeline`'s render pass compatibility with offscreen ctx | Offscreen render pass uses `VkFormat.B8G8R8A8Unorm` matching the swapchain path; pipeline creation should work unchanged. Verify in Phase 1 smoke test. |

## What this catches

- GLSL bugs that differ from the CPU mirror (e.g. the `applyCurveLUT`
  off-by-one at v=1.0 we just fixed). Currently caught only by manual
  inspection.
- Future GLSL pipeline regressions when adding a new stage (saturation,
  denoise, etc.).
- Std140 alignment / packing mistakes in `UpdateStretchUBO` -- the GPU
  reads via the layout in the shader; CPU writes via byte offsets; a
  mismatch silently corrupts uniforms.
- Bayer demosaic in the GPU path (`debayerBilinear`) that isn't currently
  testable from CPU at all.

## What this does NOT catch

- Sampler / filtering differences (linear vs nearest) -- both paths use
  the same texel-accurate fetch.
- Display gamma / sRGB conversion -- not in the pipeline currently;
  output is linear.
- Frame-pacing / async issues -- offscreen path is synchronous.

## Estimated effort

~3 days. Phase 1 (smoke test + fixture) is the highest-risk chunk;
the rest is mechanical once the offscreen ctx works in our test
infrastructure.

## Out of scope (this plan)

- Histogram pipeline (`VkFitsImagePipeline.RecordHistogramDraw`) -- separate test.
- Sky map pipeline tests -- separate plan.
- WCS grid, debayer-shader, channel-view modes -- can be added later in
  the same fixture.

---

## Followups: other GPU offscreen comp test candidates

After Phase 1 caught a real production bug (GLSL `stretchMode == 2` for
Luma vs C# `StretchMode.Unlinked == 2`), it's worth surveying every GPU
pipeline that has a CPU equivalent or algorithmic check. Each candidate
below could plug into the same `OffscreenGpuContext` fixture.

### A. Bayer demosaic (`debayerBilinear` in image fragment shader)

GPU: `VkFitsImagePipeline.cs:188-226` -- the shader does an inline
bilinear demosaic when `imageSource == 2 (RawBayer)`.

CPU: `Image.DebayerAsync(DebayerAlgorithm.BilinearMono)` (existing).

Test: feed a small RGGB mosaic, render via GPU with imageSource=RawBayer,
compare to CPU bilinear debayer + `RenderStretchedRgba`. Stretch
parameters identity so demosaic is the only variable.

Value: Bayer images are common in OSC astrophotography. Confirms the GPU
demosaic produces the same colors the CPU does.

Effort: ~half a day. Same fixture, new test theory cases.

### B. Histogram pipeline (`VkFitsImagePipeline.RecordHistogramDraw`)

GPU: `VkFitsImagePipeline.cs:325-376` -- renders R/G/B histograms as
overlapping coloured bars.

CPU equivalent: synth histogram from a known image stat array; render as
RGBA bytes via `RgbaImageRenderer.FillRectangle` per bin.

Test: upload a contrived histogram (e.g. spike in middle bin), render
GPU + render CPU equivalent, compare buffers.

Value: Lower-priority -- histogram rendering rarely changes and visual
inspection in the viewer catches issues. But quick to add.

Effort: ~half a day.

### C. WCS grid overlay (`gridIntensity` in image fragment shader)

GPU: `VkFitsImagePipeline.cs:142-185` -- gnomonic-deprojected RA/Dec grid
mixed into the rendered image at fragment level.

CPU equivalent: `WCS.PixelToSky` per pixel + classify "near grid line"
the same way the shader does.

Test: small image with hand-crafted WCS, render GPU + CPU classifier,
compare grid mask.

Value: Currently the WCS-grid path has no test coverage at all. Catches
regressions in the gnomonic projection math.

Effort: 1 day -- need to mirror the `gridIntensity` GLSL into a CPU
helper which is only used by tests.

### D. `VkRenderer` primitives vs `RgbaImageRenderer`

`SdlVulkan.Renderer/VkRenderer.cs` overrides `Renderer<TSurface>` from
DIR.Lib with FillRectangle/DrawRectangle/FillEllipse/DrawEllipse/
DrawCircle/FillCircle/DrawLine. CPU implementation is `RgbaImageRenderer`
in DIR.Lib. The abstract contract is "produces the same pixel output."

Test: for each primitive, render via offscreen GPU and via RGBA-buffer
CPU renderer at the same coordinates, compare RGBA bytes (with
anti-aliasing tolerance for ellipse/line edges).

Value: HIGH. These primitives back the planner, FITS viewer chrome,
sky map labels, guider graphs, V-curves -- visual regressions here would
be widespread. DIR.Lib already has CPU benchmarks; GPU comp closes the
loop on "do they actually agree on pixels".

Effort: ~2 days. Lots of cases to enumerate; needs a per-primitive
tolerance heuristic since GPU AA differs from CPU midpoint algorithms.

This test could live in DIR.Lib's own test project or in the renderer
sibling repo (`SdlVulkan.Renderer.Tests`); the GPU offscreen helper
needs to be reusable across repos.

### E. Sky map star rendering (`VkSkyMapPipeline.cs` Star pipeline)

GPU: instanced quads with Gaussian PSF in fragment shader, stereographic
projection in vertex shader. Inputs: per-instance `(RA, Dec, magnitude)`.

CPU equivalent: not directly -- the closest thing is
`SyntheticStarFieldRenderer.Render` which uses a different projection
(gnomonic / TAN) and a different PSF formula.

Test: hard. Would need to first port the stereographic projection to a
CPU helper, then render PSFs the same way the shader does.

Value: Sky map is one of the highest-traffic GPU pipelines but also the
most complex to mirror. The bigger payoff is migrating sky-map-related
GPU code to share helpers with `SyntheticStarFieldRenderer`.

Effort: 3-5 days for a proper comparison; not worth doing until the
sky-map-gpu-overlays plan ships and the projection / PSF code is
already shared.

### F. Sky map line rendering (constellation lines, RA/Dec grid)

GPU: `VkSkyMapPipeline` Line pipeline -- great-circle lines tessellated
on CPU + drawn as line list.

CPU equivalent: deterministic line list output; could just compare the
generated vertex buffer rather than the rasterised pixels.

Test: feed a known constellation, dump the GPU vertex buffer (no
rasterisation), assert line count + endpoints. No offscreen needed.

Value: Catches line-tessellation regressions in `BuildConstellationBuffers`
without needing GPU pixel comparison. Cheaper than full offscreen test.

Effort: ~half a day. Doesn't need the offscreen fixture.

### G. Milky Way fullscreen quad (`MilkyWayFragmentSource`)

GPU: full-screen quad sampling a baked Milky Way texture, alpha-faded
by sun altitude.

CPU equivalent: trivial -- it's a textured quad with a single
multiplicative fade. The interesting part is the texture bake (already
covered by `MilkyWayTextureBaker` tests).

Test value: low. The shader is a single multiply.

Effort: skip.

### H. Overlay ellipses (galaxy markers in sky map)

GPU: `OverlayEllipsePipeline` -- rotated quad with ring shader, used
for galaxy ellipse markers.

CPU equivalent: `RgbaImageRenderer.DrawEllipse` (with rotation -- needs
an oriented variant).

Test: same as D (primitives) but specifically for the rotated-ring
shader. Could fold into D.

Value: Medium. Catches ring-thickness / rotation-matrix regressions.

Effort: ~half a day. Folded into D.

---

### Recommendation

Order by value-per-day:

1. **D (primitives)** -- highest value, broadest coverage, ~2 days.
2. **A (Bayer demosaic)** -- direct extension of Phase 1, ~half a day.
3. **F (line tessellation)** -- cheap, no GPU needed, ~half a day.
4. **B (histogram)** -- nice to have, ~half a day.
5. **C (WCS grid)** -- only if CPU mirror written for free elsewhere.
6. **E (sky map stars)** -- defer until projection code is shared.
7. **G (milky way)** -- skip.

Total ballpark for items 1-4: ~3.5 days. Each could be its own commit on
top of the GPU offscreen infrastructure shipped in Phase 1.
