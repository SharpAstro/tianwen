# DRAFT — Mesa upstream bug report

**File at:** https://gitlab.freedesktop.org/mesa/mesa/-/issues/new
**Suggested label set:** `lavapipe`, `regression?` (let triagers decide), `vulkan`
**This file is not committed** — delete after filing, or move out of the working tree.

---

## Title (copy-pasteable)

> lavapipe: offscreen render-to-image-then-readback produces fully clear-color framebuffer on x86_64 AVX2 256-bit JIT, works on ARM64 NEON 128-bit JIT

---

## System information

| | x86_64 (failing) | ARM64 (working) |
|---|---|---|
| Distribution | Ubuntu 24.04.4 LTS | Ubuntu 24.04.4 LTS |
| CPU | Intel/AMD x86_64 (GitHub Actions hosted) | Cobalt 100 (GitHub Actions hosted) |
| GPU | software (lavapipe) | software (lavapipe) |
| Mesa | **25.2.8** (Ubuntu) **and** **26.0.6** (ppa:kisak/kisak-mesa) — both reproduce | 25.2.8 (Ubuntu) — works |
| LLVM | 20.1.2 (Mesa 25) / 20.1.8 (Mesa 26) | 20.1.2 |
| llvmpipe device name | `llvmpipe (LLVM 20.1.x, 256 bits)` | `llvmpipe (LLVM 20.1.2, 128 bits)` |
| Vulkan Instance | 1.3.275 | 1.3.275 |
| API version | 1.4.335 | 1.4.335 |
| Driver ID | DRIVER_ID_MESA_LLVMPIPE | DRIVER_ID_MESA_LLVMPIPE |
| Kernel | Linux 6.x (GitHub-hosted runner) | Linux 6.x (GitHub-hosted runner) |

(The `256 bits` / `128 bits` in `deviceName` is the lavapipe report of the LLVM JIT codegen
width — AVX2 vs NEON. That difference is the only signal-distinguishing factor.)

---

## Describe the issue

A straightforward offscreen render-to-image pipeline produces a fully clear-color framebuffer
on x86_64 lavapipe (256-bit AVX2 JIT) — every fragment-shader output is dropped, regardless
of whether the shader does texture sampling, vertex-fed colour, or a constant push-constant
colour. The same code, same SPIR-V, and the same Mesa version produces the expected output
on ARM64 lavapipe (128-bit NEON JIT).

The pipeline pattern is:

1. Create an offscreen `VkImage` (`VK_FORMAT_B8G8R8A8_UNORM`, `COLOR_ATTACHMENT | TRANSFER_SRC`,
   `OPTIMAL` tiling, single sample).
2. Begin a render pass that clears to a specified colour.
3. Issue one or more `vkCmdDraw*` calls with simple graphics pipelines (trivial vertex shader,
   either a colour-pass fragment shader or a sampled-texture fragment shader).
4. End the render pass.
5. `vkCmdCopyImageToBuffer` from the image to a host-visible buffer.
6. Memory barrier + `vkQueueWaitIdle` + map + read.

On x86_64 lavapipe the readback contains only the clear-colour for every pixel — the draws
appear to have produced no output. There are **no validation messages** emitted by
`VK_LAYER_KHRONOS_validation` (1.3.275) at any stage.

On ARM64 lavapipe the readback contains the expected drawn output and pixel-matches a
software reference renderer to within rounding tolerance.

This is reproducible with a ~70-line .NET 10 console application — attached as
`LavapipeMinRepro.zip` (see *Attachments*). It depends only on `SdlVulkan.Renderer`
3.4.0 (a thin Vortice.Vulkan wrapper published on nuget.org); the offscreen path issues
normal `vkCreate*` / `vkCmd*` calls and bypasses SDL window/swapchain creation entirely.

---

## Steps to reproduce

```bash
# Ubuntu 24.04 x86_64 host
sudo apt-get install -y mesa-vulkan-drivers libvulkan1 vulkan-tools vulkan-validationlayers
# (optional, also reproduces with kisak-mesa-PPA Mesa 26.0.6 / LLVM 20.1.8)

# Build + run the attached standalone reproducer:
dotnet run --project LavapipeMinRepro -c Release

# Or run against latest Mesa via kisak PPA:
sudo add-apt-repository -y ppa:kisak/kisak-mesa
sudo apt-get update && sudo apt-get install -y mesa-vulkan-drivers
dotnet run --project LavapipeMinRepro -c Release
```

The reproducer prints the per-pixel mean of the readback buffer for each of seven test
primitives (FillRectangle, DrawRectangle, DrawLine ×3, FillEllipse, DrawEllipse). On
x86_64 every reading is the clear colour; on ARM64 every reading matches expected.

`vulkaninfo --summary` output for the failing host is in *Attachments*
(`vulkaninfo-x86_64-mesa-26.0.6.txt`).

---

## Expected behaviour

The readback buffer matches a software reference rasterisation of each primitive (within a
~±1-byte per-channel rounding tolerance). This is the observed behaviour on ARM64 lavapipe
with the same Mesa version, the same LLVM major, the same SPIR-V, and the same .NET binary.

## Actual behaviour

The readback buffer contains the clear colour for every pixel, indicating the draw calls
produced no fragments — or fragments were produced but their outputs were discarded /
written to the wrong location. No `VK_LAYER_KHRONOS_validation` messages are emitted.

CPU reference (drawn by an unrelated software rasteriser) vs GPU readback for the failing
`DrawRectangle_ThickStroke` case:

- CPU per-channel mean = `[10.71, 10.71, 10.71, 255]`
- GPU per-channel mean = `[0, 0, 0, 255]`
- GPU max per channel = `[0, 0, 0, 255]` — every drawn pixel is missing

Both TIFFs are attached as `DrawRectangle_ThickStroke.cpu.tiff` and
`DrawRectangle_ThickStroke.gpu.tiff` for visual inspection.

---

## Architecture comparison (the smoking gun)

Same source, same build, same xUnit test, the only difference is the runner architecture:

| Runner | Mesa | LLVM | lavapipe width | Test outcome |
|---|---|---|---|---|
| GitHub Actions `ubuntu-latest` (x86_64) | 25.2.8 | 20.1.2 | 256 bits | **all GPU tests skip-or-fail** |
| GitHub Actions `ubuntu-latest` + kisak-mesa PPA (x86_64) | 26.0.6 | 20.1.8 | 256 bits | **all GPU tests fail** (when skips bypassed via env var) |
| GitHub Actions `ubuntu-24.04-arm` (ARM64) | 25.2.8 | 20.1.2 | 128 bits | **all GPU tests pass** (`GpuStretchPipelineTests`, `VkRendererPrimitiveTests`, `VkHistogramPipelineTests`) |
| Local Windows 11 ARM64 (hardware Vulkan, Qualcomm Adreno) | n/a | n/a | n/a | passes |

The only varying axis between rows 1/2 (failing) and row 3 (passing) is the host
architecture and corresponding LLVM JIT backend width. We believe this is enough to rule
out an application-side bug or a spec violation:

- Same SPIR-V (deterministic compiler output).
- Same Mesa version on rows 1 vs 3 — only the JIT target differs.
- No validation messages (LunarG SDK 1.3.275 validation layer attached programmatically).
- The bug persists across a major Mesa release (25.2.8 → 26.0.6) without changing
  signature.

---

## Reproducer

`LavapipeMinRepro` is a standalone ~70-line C# .NET 10 console application that
exercises the seven primitives. The archive contains:

- `Program.cs` — full reproducer (top-level statements, ~70 lines including diagnostics)
- `LavapipeMinRepro.csproj` — .NET 10 project file, single `PackageReference` to
  `SdlVulkan.Renderer` 3.4.0 (which transitively pulls in Vortice.Vulkan 3.2.1 + DIR.Lib)
- `README.md` — what it does, expected output, build/run commands

`SdlVulkan.Renderer` is a thin published Vortice.Vulkan wrapper. When used in
offscreen mode (`VulkanContext.CreateOffscreen` + `VkRenderer.BeginOffscreenFrame`),
it issues normal `vkCreateInstance` / `vkCreateImage` / `vkCmdDraw*` /
`vkCmdCopyImageToBuffer` calls — no SDL window, no swapchain, no surface. The compiled
SPIR-V for each primitive shader is embedded in the SdlVulkan.Renderer assembly and is
identical across architectures.

The reproducer:

1. `vkCreateInstance` (no validation layer in the min-repro itself, but identical
   behaviour with the layer enabled — see *What we ruled out application-side*).
2. `VulkanContext.CreateOffscreen(instance, 256, 256)` — picks the first physical
   device (lavapipe on the failing hosts), creates device, queue, command pool,
   256×256 `B8G8R8A8_UNORM` offscreen image + framebuffer + render pass.
3. For each test primitive: `BeginOffscreenFrame` (clears to black) → call the renderer's
   `FillRectangle` / `DrawRectangle` / `DrawLine` / `FillEllipse` / `DrawEllipse` →
   `EndOffscreenFrame` (`vkCmdCopyImageToBuffer` + queue wait + map readback buffer).
4. Print the non-zero pixel count + centre-pixel RGBA.

Cross-platform — runs on Windows, Linux, macOS, ARM64, x86_64. The renderer's
offscreen path does not call into SDL.

---

## Workaround

None that we can apply at the application layer. We currently `Assert.Skip` GPU
verification tests when `deviceName` contains `llvmpipe` **and** architecture is
`x86_64`, which is a CI band-aid rather than a fix. End users on x86_64 systems running
software Vulkan would see broken rendering.

---

## What we ruled out application-side

Before filing this we did the following sanity checks, all from the .NET application
side, just to make sure we weren't shipping a spec violation that happened to land on
defined-by-NEON / undefined-by-AVX behaviour:

- Enabled `VK_LAYER_KHRONOS_validation` (LunarG SDK 1.3.275) programmatically — **no
  messages**, on either architecture.
- Audited every layout transition for `srcStageMask`/`dstStageMask`/`srcAccessMask`/
  `dstAccessMask` correctness; fixed one image-usage flag (TransferSrc missing on a
  texture we copy-back from) and that fix had no effect on the lavapipe-x86_64 result.
- Compared SPIR-V hex of the offscreen render pipeline between architectures — identical.
- Reduced the failing primitive shaders to a constant-colour fragment shader with no
  uniforms, no descriptors, no textures — still produces clear colour on x86_64 lavapipe.

We're not 100% certain the bug is a Mesa bug versus a strict-interpretation-of-an-implementation-
defined-corner of the Vulkan spec; if the latter, the closest candidate is the implicit
external subpass dependency wording (which the validation layer accepts), and we're happy
to tighten that if it turns out to be the cause. But the architectural split (same SPIR-V,
same Mesa, only LLVM JIT target differs) makes us think it's a lavapipe JIT codegen issue.

---

## Attachments to include in the gitlab issue

1. `LavapipeMinRepro.zip` — the standalone reproducer (Program.cs + .csproj)
2. `vulkaninfo-x86_64-mesa-26.0.6.txt` — failing host vulkaninfo summary
3. `vulkaninfo-arm64-mesa-25.2.8.txt` — passing host vulkaninfo summary
4. `DrawRectangle_ThickStroke.cpu.tiff` — CPU reference render
5. `DrawRectangle_ThickStroke.gpu.tiff` — broken GPU render (all clear colour)
6. CI run links (public — SharpAstro/tianwen):
   - failing on x86_64 Mesa 26.0.6: https://github.com/SharpAstro/tianwen/actions/runs/25649005562
   - passing on ARM64 Mesa 25.2.8: https://github.com/SharpAstro/tianwen/actions/runs/25649006209

---

## Notes for the filer (you, not the upstream maintainers)

- Strip the `## Notes for the filer` section before pasting into gitlab.
- Confirm the architecture comparison table dates are accurate (2026-05-11 today).
- Min-repro source lives at `tools/lavapipe-repro/` in the repo. Zip is also produced
  at the repo root as `LavapipeMinRepro.zip` (gitignored). Paste `Program.cs` inline in
  a `<details>` block (it's < 3 KB) for triage convenience.
- The CI run URLs are stable as long as the workflows aren't purged; GitHub keeps them
  for 90 days by default. Consider also linking the PR (`#5 stretch-improvements`) so
  triagers can see the discussion that led here.
- Mesa triage tends to want a `RADV_DEBUG` / `LP_DEBUG` capture for lavapipe bugs;
  consider re-running the reproducer with `LP_DEBUG=tgsi,llvm,gallium_trace` and
  attaching the trace if requested in triage.
