# LavapipeMinRepro

Standalone reproducer for the Mesa lavapipe x86_64 256-bit AVX2 JIT codegen
bug (see `../../TODO.md` "Mesa lavapipe CPU/GPU divergence" entry and
`../../lavapipe-bug-report-draft.md`).

## What it does

A ~70-line .NET 10 console app that creates an offscreen 256x256
`B8G8R8A8_UNORM` framebuffer via `SdlVulkan.Renderer` 3.4.0, draws each of
seven primitives (FillRectangle, DrawRectangle, DrawLine x3, FillEllipse,
DrawEllipse), reads back the framebuffer with `vkCmdCopyImageToBuffer`,
and prints a non-zero pixel count and the centre-pixel RGBA for each draw.

`SdlVulkan.Renderer` is a thin Vortice.Vulkan wrapper published on
nuget.org -- the offscreen path bypasses SDL window/swapchain creation
entirely and issues normal `vkCreate*` / `vkCmd*` calls. The offscreen
test is platform-portable (Windows, Linux, macOS, x86_64, ARM64).

## Expected output on a working host (ARM64, hardware Vulkan, etc.)

```
=== Lavapipe min-repro (all primitives) ===
Physical device: <something>
  FillRectangle (red)            nonzero=  3600  px(128,128)=(0,0,0,255)
  DrawRectangle (white,sw=4)     nonzero=   240  px(128,128)=(0,0,0,255)
  DrawLine horizontal            nonzero=   181  px(128,128)=(0,0,0,255)
  ...
```

Non-zero pixel counts are positive and roughly match the drawn shape.

## Observed output on Mesa lavapipe x86_64 (LLVM 20.1.x, 256 bits)

```
=== Lavapipe min-repro (all primitives) ===
Physical device: llvmpipe (LLVM 20.1.x, 256 bits)
  FillRectangle (red)            nonzero=     0  px(128,128)=(0,0,0,255)
  DrawRectangle (white,sw=4)     nonzero=     0  px(128,128)=(0,0,0,255)
  ...
```

Every primitive reports `nonzero=0` -- the readback is fully clear-colour.

## Running

```bash
# Linux x86_64 (default Mesa lavapipe, Ubuntu 24.04)
sudo apt-get install -y mesa-vulkan-drivers libvulkan1 vulkan-tools vulkan-validationlayers
cd tools/lavapipe-repro && dotnet run -c Release

# Linux x86_64 with kisak-mesa PPA (Mesa 26.0.6)
sudo add-apt-repository -y ppa:kisak/kisak-mesa
sudo apt-get update && sudo apt-get install -y mesa-vulkan-drivers
cd tools/lavapipe-repro && dotnet run -c Release

# Linux ARM64 (currently works -- 128-bit NEON JIT)
cd tools/lavapipe-repro && dotnet run -c Release

# Windows x64 / ARM64 hardware Vulkan
cd tools/lavapipe-repro && dotnet run -c Release
```
