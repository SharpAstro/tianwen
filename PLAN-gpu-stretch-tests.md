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

### Phase 2 -- Skip-when-unavailable + CI driver install (0.5 day)

- Wrap Vulkan init in try/catch; on failure, `Assert.Skip("Vulkan unavailable")`
  with a clear diagnostic listing the underlying error.
- CI workflow update: add `mesa-vulkan-drivers libvulkan1 vulkan-tools` to
  the `cache-apt-pkgs-action` package list in the test-unit job. Mesa
  ships `lavapipe` (software rasterizer) which is fast enough for our
  ~1280x1024 test images and runs without a GPU. Validation: `vulkaninfo
  --summary` should list at least one ICD.
- Document expected behaviour:
  - Local Windows: native GPU driver (NVIDIA/AMD/Intel) handles it -> hardware path.
  - Local Linux: Mesa drivers if installed; otherwise lavapipe.
  - CI: lavapipe deterministic software rasterizer.
  - macOS: MoltenVK; tests skip if not on the runner.

### Phase 3 -- Theory cases parity check (1 day)

Port the 8 cases from `StretchTests_NewPipeline.GivenColorFitsWhenRenderingThroughCpuPipeline...`
to a `[Theory]` that runs CPU and GPU side-by-side, asserting per-pixel
equivalence within tolerance.

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

## Out of scope

- Histogram pipeline (`VkHistogramPipeline` rendering) -- separate test.
- Sky map pipeline tests -- separate plan.
- WCS grid, debayer-shader, channel-view modes -- can be added later in
  the same fixture.
