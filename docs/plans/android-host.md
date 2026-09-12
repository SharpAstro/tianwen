# Android Host (TianWen GUI on Android)

Status: **NOT STARTED**, but substantially de-risked since this was written (2026-07-19; refreshed
2026-09-12 -- see "What's landed since this was written" below). Goal: run the TianWen GUI, or at
minimum its sky atlas, on Android phones/tablets.

The renderer is now Android-capable: **SdlVulkan.Renderer 6.23** added a `net10.0-android` target with an
SDL Vulkan host (`Org.Libsdl.App.SDLActivity`, immersive-fullscreen default, swapchain recover-on-resume)
and `SDL3-CS.Android` (SDL Java bridge + per-ABI `libSDL3.so`). TianWen needs its own `net10.0-android`
host app that hosts the existing GUI (or the sky atlas alone) on top of it.

## Precedent: this has already been done once, on the identical stack

`../sebgod/chess`'s `Chess.Droid` (sibling org root, not a TianWen project) is a **shipped, working**
`net10.0-android` head on the same SdlVulkan.Renderer + DIR.Lib combination TianWen's GUI uses --
`SDLActivity`, `SDL3-CS.Android`, font staging via `AndroidAsset` (`MainActivity.StageFonts` ->
`FontPaths.FontsDirectory`), a dispatch-only `android` CI job (`dotnet workload install android` +
`setup-java` pinned to Temurin 17 + `InstallAndroidDependencies`) that compiles it on every push with
`UseLocalSiblings=false` so it resolves the renderer from NuGet like a clean clone would. It has working
rotation, safe-area/notch insetting on both axes, immersive fullscreen, and SDL finger-event touch input,
each documented (`Chess.Droid/docs/*.md`) with the same "what's correctness vs. what's polish" split as
TianWen's own docs. Packaging a signed APK is still a manual local step there too (no CI publish yet).
This doesn't remove any of the work below, but it means the SDL/Vulkan/Android integration risk this
plan was written to flag is **already retired org-wide** -- what's left is TianWen-shaped wiring, not
unknown territory. `TianWen.UI.Android`'s csproj, CI job and `MainActivity` should be scaffolded as a
close mirror of `Chess.Droid`'s, not designed from scratch.

## What's landed since this was written (2026-07-19)

Everything here shrinks the "host app" work below or changes which phase to start from:

- **The sky atlas is no longer GUI-only, and it moved next to its own pipeline.** `VkSkyMapTab` relocated
  from the GUI into `TianWen.UI.Shared` on 2026-09-09 ("belongs beside its pipeline, not in the GUI"),
  and `docs/plans/in-app-sky-atlas.md` P0/P4/P4c shipped 2026-09-10: the desktop GUI and `tianwen-fits`
  both now host a real in-app atlas (context ladder `none -> grid -> grid+objects -> sky`), not a link
  out to the web showcase. The host contract is **one call**, `SkyMapTab<TSurface>.Render(PlannerState,
  RectF32, ITimeProvider)`, with no GUI type in the signature -- a thin Android host can point this at a
  window with far less wiring than standing up the full GUI's tab strip / signal bus / device layer.
- **The scariest open question below -- does the full star catalog even survive a phone GPU -- already
  has a measured answer, via the web build.** `docs/plans/web-tycho2.md` is COMPLETE: the full ~2.5M-star
  Tycho-2 catalog streams into the browser atlas (region-aligned lazy fetch + IndexedDB cache). Per
  CLAUDE.md's culling section, `StarMagnitudeIndex`/`StarChunkIndex` is **one implementation shared by
  `VkSkyMapPipeline` (desktop/would-be-Android) and `WebGlSkyMapPipeline` (web)**, and it was hardened
  specifically after submitting the whole star buffer at once **TDR'd an Adreno X1-85** (a real mobile
  GPU) and dropped 944 of 1287 frames. A native Android build via SdlVkR inherits that same culling code,
  already proven against a real mobile-GPU failure mode, for free.
- **The atlas's content got materially more complete.** Comets/small bodies are now dynamic, ephemeris-
  computed catalog members (`docs/plans/comet-ephemeris.md`, `comet-integration.md`), not a gap against
  Stellarium/Stellarium Plus, which lead with exactly that.
- **A real Night colour mode shipped** (`docs/plans/colour-theme.md`): scotopic-vision-driven (zero blue,
  capped red contrast), which is a genuine differentiator for a phone app meant to be used at the
  eyepiece -- and something Stellarium Mobile also markets as a feature.
- **Remote-rig mirroring is fully done** (`docs/plans/remote-profile.md`, P1-P5, 2026-07-27):
  `TianWen.RemoteClient` + `RemoteSessionMirror` + the Home tab. This makes a *second*, much cheaper
  Android app shape available for free at the Lib level: a thin client that watches/controls a rig
  running `tianwen-server` over the existing REST/WebSocket API, with no SDL/Vulkan port and no on-device
  hardware access needed at all.
- **A non-native path is now credible too.** `TianWen.UI.Web` (the WebAssembly showcase) already carries
  the sky atlas, the full Tycho-2 catalog and comets, proven end-to-end in-browser against the same
  mobile-GPU-class hardware mentioned above. Wrapping it as a PWA / Android WebView shell is a fifth
  option that didn't really exist when this plan was written: near-zero new native code, a smaller
  feature ceiling (no local device I/O beyond the network), but reachable in a fraction of the time of a
  `TianWen.UI.Android` SDL port.

**Revised recommendation:** start the P0 shell from `TianWen.UI.Shared`'s `SkyMapTab` alone (boot straight
into the atlas, skip planner/session/device UI for v1) rather than the whole GUI -- it's now a
self-contained widget with a render path already proven mobile-GPU-safe. Device control and remote-rig
monitoring are separate, already-built capabilities that can layer on afterward rather than blocking v1.

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
  `dotnet build` (own build lane; CI needs the android workload, see SdlVkR's `dotnet.yml`).
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

## Device layer on Android (open questions, likely a reduced feature set at first)

- **ASCOM COM bridge is Windows-only** → unavailable on Android. **Alpaca (HTTP)** is the cross-platform
  path and already wired (`AddAlpaca()`), so an Android build talks to devices over Alpaca / network.
- **A third option now exists that needs no local device layer at all:** point the Android build at an
  already-running `tianwen-server` via `TianWen.RemoteClient` (see remote-profile.md above) instead of
  driving hardware directly. That sidesteps every question below -- no native SDK, no `IsAotCompatible`
  audit for a driver, no Alpaca discovery on-device -- at the cost of needing a host node on the network.
  Worth treating as the *default* device story for v1, with direct Alpaca as a P1 addition.
- **Native camera SDKs** (ZWO `ZWOptical.SDK`, QHY `QHYCCD.SDK`, Canon `FC.SDK`, LibUsb); do they ship
  android `.so`s? Most likely **not** initially; gate them out of the android build and rely on Alpaca +
  the built-in guider + fakes. Confirm which `TianWen.Lib` drivers are android-safe vs. must be excluded.
- **AOT on android**: net10.0-android uses its own AOT/interpreter story; validate startup + the existing
  `IsAotCompatible` surface.

## Phasing (revised)

1. **P0: atlas-only shell**: `TianWen.UI.Android` boots straight into `SkyMapTab` (from
   `TianWen.UI.Shared`) via SdlVkR's `SDLActivity` -- no planner, no session, no device layer. Touch
   pan/pinch/tap-to-select works (already SDL-finger-event-driven, per `android-host.md`'s original
   input note), fonts load via `AndroidAsset` (mirror `Chess.Droid.csproj`'s font-staging block
   exactly), Night mode is available. This is now a standalone widget with a render path already
   proven mobile-GPU-safe (Tycho-2 + culling), so it's a materially smaller P0 than "boot the whole
   GUI."
2. **P1: remote-rig monitor**: layer `TianWen.RemoteClient` on top of the P0 shell -- watch/control a
   `tianwen-server` node's session (telemetry, previews, planner) with no local device layer at all.
   Everything this needs at the Lib level already shipped (remote-profile.md P1-P5).
3. **P2: direct device layer audit**: compile-gate the Windows-only / no-android-RID drivers for anyone
   who wants the phone driving hardware directly rather than through a remote node; a clean android
   device set (Alpaca, built-in guider, PHD2-over-network, fakes).
4. **P3: full GUI** (original plan's scope): planner, session, live imaging -- only once P0-P2 have
   proven the shell is worth the investment.
5. **P4: packaging + polish**: APK/AAB, lifecycle, orientation, storage permissions for image output.

Alternative track, not exclusive with the above: wrap `TianWen.UI.Web` (already carrying the sky atlas +
Tycho-2 + comets, browser-proven on mobile-GPU-class hardware) as a PWA / Android WebView shell. Cheaper
than any native phase above but caps out below P2 (no local device I/O beyond the network) -- worth
prototyping in parallel if a fast "does anyone actually want this on their phone" signal is more valuable
right now than the native investment.

## Notes

- Keep the android host OUT of `TianWen.slnx`'s default build lane (like `tools/BakeShaders`) so desktop
  CI stays green without the android workload; give it a dedicated CI leg (mirror SdlVkR's dispatch-only
  android build).
- The GUI's rendering + input abstractions are already surface-agnostic (that's the whole
  `TianWen.UI.Abstractions` / `TianWen.UI.Shared` split), so the host app should be thin.
