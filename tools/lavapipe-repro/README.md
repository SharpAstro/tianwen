# LavapipeMinRepro

Smoke test that exercises the SdlVulkan.Renderer offscreen path against
whatever Vulkan ICD the loader picks. Originally written to investigate
a CPU/GPU divergence on x86_64 lavapipe that turned out to be a
dangling-pointer bug in `VkPipelineSet.CreatePipeline` (fixed in
SdlVulkan.Renderer 3.4.471 — see TODO.md). Kept as a quick sanity tool
for the offscreen rendering path.

## What it does

A ~70-line .NET 10 console app that creates an offscreen 256x256
`B8G8R8A8_UNORM` framebuffer via `SdlVulkan.Renderer`, draws each of
seven primitives (FillRectangle, DrawRectangle, DrawLine x3, FillEllipse,
DrawEllipse), reads back the framebuffer with `vkCmdCopyImageToBuffer`,
and prints a non-zero pixel count and the centre-pixel RGBA for each draw.

`SdlVulkan.Renderer` is a thin Vortice.Vulkan wrapper published on
nuget.org -- the offscreen path bypasses SDL window/swapchain creation
entirely and issues normal `vkCreate*` / `vkCmd*` calls. The offscreen
test is platform-portable (Windows, Linux, macOS, x86_64, ARM64).

## Expected output

```
=== Lavapipe min-repro (all primitives) ===
Physical device: <something>
  FillRectangle (red)             nonzero= 18200  px(128,128)=(255,0,0,255)
  DrawRectangle (white,sw=4)      nonzero=  2752  px(128,128)=(0,0,0,255)
  DrawLine horizontal             nonzero=   180  px(128,128)=(0,0,0,255)
  DrawLine vertical               nonzero=   236  px(128,128)=(0,0,0,255)
  DrawLine diagonal               nonzero=   210  px(128,128)=(255,255,255,255)
  FillEllipse (green)             nonzero= 15380  px(128,128)=(0,255,0,255)
  DrawEllipse (blue,sw=3)         nonzero=  1272  px(128,128)=(0,0,0,255)
```

If you instead see every primitive reporting `nonzero=0` on x86_64
lavapipe, your `SdlVulkan.Renderer` is older than 3.4.471 -- bump it.

## Running

```bash
# Linux (Mesa lavapipe -- install drivers if not already present)
sudo apt-get install -y mesa-vulkan-drivers libvulkan1 vulkan-tools vulkan-validationlayers
cd tools/lavapipe-repro && dotnet run -c Release

# Windows / macOS hardware Vulkan
cd tools/lavapipe-repro && dotnet run -c Release
```
