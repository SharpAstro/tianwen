# Android Host (TianWen GUI on Android)

Status: **NOT STARTED** (prerequisites in progress). Goal: run the TianWen GUI on Android phones/tablets.

The renderer is now Android-capable: **SdlVulkan.Renderer 6.23** added a `net10.0-android` target with an
SDL Vulkan host (`Org.Libsdl.App.SDLActivity`, immersive-fullscreen default, swapchain recover-on-resume)
and `SDL3-CS.Android` (SDL Java bridge + per-ABI `libSDL3.so`). TianWen needs its own `net10.0-android`
host app that hosts the existing GUI on top of it.

## Prerequisites (this is where the "adopt 6.23" work lands)

- [x] **Pin SdlVulkan.Renderer 6.23** (`Directory.Packages.props`). 6.23 multi-targets
  `net10.0;net10.0-android`; a net10.0 consumer restores the net10.0 asset with no Android workload, so
  desktop/CI is unaffected. *(done: `deps/sdlvulkan-6.23-android`)*
- [x] **Drop runtime shaderc → pre-bake shaders to SPIR-V.** 6.23 stopped transiting
  `Vortice.ShaderCompiler` (it pre-bakes its own shaders); TianWen.UI.Shared compiled GLSL at runtime
  via that transitive dep. Runtime `shaderc` (`Silk.NET.Shaderc.Native`) ships **no android RID**, so it
  is unloadable on Android anyway. Shaders now live as GLSL files under
  `src/TianWen.UI.Shared/Shaders/*.vert|frag`, pre-baked to committed `Shaders/spirv/*.spv` by
  `tools/BakeShaders`, embedded + loaded at runtime (`LoadShaderModule`). *(done, GPU-render-verified;
  see CLAUDE.md "Sky Map / FITS Viewer GLSL")*
- [ ] Audit any remaining native-lib / Windows-only dependency reachable from the GUI for an android RID.

## The host app (the real work)

- [ ] **New `TianWen.UI.Android` project** (`net10.0-android`, OutputType Exe / Android app) that
  references `TianWen.UI.Gui` (or a shared GUI-composition library) and hosts it via SdlVkR 6.23's
  `SDLActivity` entry point. Mirror SdlVkR's own `Android/*.cs` host wiring. Keep it out of the desktop
  `dotnet build` (own build lane; CI needs the android workload — see SdlVkR's `dotnet.yml`).
- [ ] **Asset/font loading on Android.** The GUI stages a UI font into a file path
  (`ManagedFontRasterizer`); Android assets are packaged, not a plain FS. Route font + any embedded
  data through the Android asset manager / `MauiAsset`-style packaging.
- [ ] **Input.** Touch is already modelled (`InputEvent.Pinch`/`PinchEnd` + drag-pan in `SkyMapTab`,
  the SDL `FingerMotion` path). Verify SdlVkR 6.23's android host forwards SDL finger events into the
  existing pipeline; no new gesture code should be needed.
- [ ] **Lifecycle.** Handle activity pause/resume (surface loss). SdlVkR 6.23 already does swapchain
  recover-on-resume; confirm the GUI's render loop cooperates.
- [ ] Packaging + signing (APK/AAB), min SDK (SdlVkR sets `SupportedOSPlatformVersion 24.0`), per-ABI
  natives (arm64-v8a primary).

## Device layer on Android (open questions — likely a reduced feature set at first)

- **ASCOM COM bridge is Windows-only** → unavailable on Android. **Alpaca (HTTP)** is the cross-platform
  path and already wired (`AddAlpaca()`), so an Android build talks to devices over Alpaca / network.
- **Native camera SDKs** (ZWO `ZWOptical.SDK`, QHY `QHYCCD.SDK`, Canon `FC.SDK`, LibUsb) — do they ship
  android `.so`s? Most likely **not** initially; gate them out of the android build and rely on Alpaca +
  the built-in guider + fakes. Confirm which `TianWen.Lib` drivers are android-safe vs. must be excluded.
- **AOT on android**: net10.0-android uses its own AOT/interpreter story; validate startup + the existing
  `IsAotCompatible` surface.

## Phasing (suggested)

1. **P0 — shell**: `TianWen.UI.Android` boots the GUI on-device via SdlVkR's SDLActivity, renders the
   sky atlas + planner, touch pan/pinch works, fonts load. Devices = fakes + Alpaca only.
2. **P1 — device layer audit**: compile-gate the Windows-only / no-android-RID drivers; a clean
   android device set (Alpaca, built-in guider, PHD2-over-network, fakes).
3. **P2 — packaging + polish**: APK/AAB, lifecycle, orientation, storage permissions for image output.

## Notes

- Keep the android host OUT of `TianWen.slnx`'s default build lane (like `tools/BakeShaders`) so desktop
  CI stays green without the android workload; give it a dedicated CI leg (mirror SdlVkR's dispatch-only
  android build).
- The GUI's rendering + input abstractions are already surface-agnostic (that's the whole
  `TianWen.UI.Abstractions` / `TianWen.UI.Shared` split), so the host app should be thin.
