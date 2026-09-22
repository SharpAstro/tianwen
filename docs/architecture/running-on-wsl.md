# Running the GUI under WSL, on the Adreno, with the validation layer

Set up and measured on the win-arm64 laptop (Snapdragon X Elite, Adreno X1-85), 2026-09-23:
WSL 2 Ubuntu 24.04 aarch64, Mesa `dzn` 26.x built from source, .NET SDK 10.0.401.

## Why bother, and what it cannot tell you

Two things the Windows host cannot do:

- **The Khronos validation layer runs here.** On Windows this box answers `layerAvailable: false`
  to `validation_report` even under `-Validation -SyncValidation`, so a GPU-side fault cannot be
  named there at all. The guest has `VkLayer_khronos_validation.json` installed.
- **Real pointer motion.** The SDL inspector has click, drag, scroll and keys but **no bare
  mouse-move verb**, and a drag presses a button, which is a pan rather than a hover. Under WSLg,
  `xdotool mousemove` produces a genuine X11 motion event that XWayland forwards and SDL turns into
  an ordinary `SDL_EVENT_MOUSE_MOTION`. That is the one gesture the atlas's hover path needs and
  the one no synthetic-input route on Windows has ever reached.

**What it cannot do is reproduce the 2026-09-22 wedge.** The guest does not have Qualcomm's native
Vulkan driver. It reaches the same silicon through Mesa's `dzn`, which translates Vulkan to D3D12,
so the whole Vulkan implementation is different from the one that returned `ErrorInitializationFailed`
from `vkQueueSubmit`. Treat this as a second, independent implementation to validate our own usage
against, never as the driver under investigation, and never as an oracle: `dzn` reports
`conformanceVersion 0.0.0.0` and says so at every launch. See `docs/plans/gpu-device-recovery.md`.

The driver build itself (Mesa from source, the ICD symlink, the arm64 differences) is written up
once, in `drawboard/pdf-viewer`'s `docs/running-on-wsl.md`. Do not duplicate it here; this file is
only what TIANWEN needs on top.

## What TianWen needs on top, and why each one

Each of these cost time to find, and each fails in a way that does not name itself.

| Need | Why it is not optional |
|---|---|
| **.NET SDK matching the Windows one** (10.0.401) | An older SDK's Roslyn silently DROPS the source generator. Ubuntu's `dotnet` is 10.0.110, whose compiler is 5.0, and `TianWen.Lib.SourceGenerators` references 5.9. You get **one** `CS9057` warning and then **146 errors** about partial members that never mention the generator. Install with `dotnet-install.sh --version 10.0.401 --install-dir "$HOME/.dotnet"` and put it first on `PATH` with `DOTNET_ROOT` set. Do **not** "fix" it by pinning `Microsoft.CodeAnalysis.*` down in `Directory.Packages.props`. |
| **`pwsh`** | `TianWen.Lib.csproj`'s `ExpandTycho2` target shells out to `tools/expand-tycho2.ps1`. Without it the build dies with `MSB3073 ... exited with code 127`. `dotnet tool install --global PowerShell` is enough (7.6.6 here), no Microsoft apt repo needed. |
| ~~`LD_PRELOAD=/lib/aarch64-linux-gnu/libudev.so.1`~~, **fixed at the source in ZWOptical.SDK 4.4** | `libEFW1.7.so` and `libEAFFocuser1.6.so` call fifteen udev functions each and declare no dependency on libudev, so the first call into either one killed the PROCESS with `undefined symbol: udev_new` before a frame was drawn. The SDK now loads libudev with `RTLD_GLOBAL` from those two classes' static constructors, which is the only way the symbols reach a library the runtime loads afterwards. **Until this repo's pin moves from `4.3.*` to `4.4`, a GUI build still needs the preload.** `tianwen-fits` never did: it references no vendor drivers. |
| **`libicu`**, or `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` | A stock WSL image ships none and the app `FailFast`s inside logger construction, before its first frame. Present on this box already. |
| **`xdotool`** | The input driver, above. Needs an X11 or XWayland session, which WSLg provides. |
| **`vulkan-validationlayers`** | The whole point. Turn it on with `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1`. |

## Which app to run

`tianwen-fits` is the cheaper target: no vendor natives, so no preload, and it still builds the same
`VkSkyMapTab` over the same `VkSkyMapPipeline` and the same shaders for its sky backdrop, hover wash
included. Use it for a validation smoke.

**It is the wrong target for the wedge**, and that is not a detail. The viewer ran the same renderer
and the same wash for an hour beside the wedged atlas on 2026-09-22 and did not wedge. The asymmetry
the desktop identified is the HORIZON CULL: in Horizon mode the star shader sends every below-horizon
star down the cull path, thousands of primitives a frame, and the viewer's backdrop never does that.
So a wedge hunt runs `tianwen-gui`, on the Sky Map tab, in Horizon mode, with the preload.

## Build on ext4, and let the siblings be absent

Two rules, for different reasons.

**Copy the tree into the guest's own filesystem** rather than building over `/mnt/c`. A .NET build
is a storm of small files and the 9p mount is slow at exactly that; worse, Windows and Linux builds
sharing one `obj/` will confuse each other's asset files. `rsync -a --exclude bin/ --exclude obj/
--exclude .git/ --exclude artifacts/` from the Windows checkout is enough, about 2.6 GB.

**Do not copy the sibling repos.** `UseLocalSiblings` is all-or-nothing and self-enables only when
EVERY sibling working copy is present, so with none of them there it answers false and the build
restores the published packages. That is usually what you want here: the guest then compiles
against exactly what CI and a user's install would, and the copy cannot drift from the Windows
working tree while you are testing. Confirm with:

```sh
dotnet msbuild TianWen.Lib/TianWen.Lib.csproj -getProperty:UseLocalSiblings   # answers true, or nothing
```

If you DO need a local sibling change under validation, copy every sibling in the list, not one.

## Running it

```sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd ~/tw/src && dotnet build TianWen.UI.Gui/TianWen.UI.Gui.csproj -c Release

cd ~/tw/src/TianWen.UI.Gui/bin/Release/net10.0
DISPLAY=:0 \
LD_PRELOAD=/lib/aarch64-linux-gnu/libudev.so.1 \
VK_DRIVER_FILES=$HOME/mesa-dzn/share/vulkan/icd.d/dzn_icd.aarch64.json \
SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1 \
./tianwen-gui
```

The `LD_PRELOAD` line goes away once the `ZWOptical.SDK` pin is 4.4 or later; see the table above.

`VK_DRIVER_FILES` is pinned deliberately: leave it off and `llvmpipe` is enumerable beside `dzn`, and
a software run proves nothing about the GPU. The launch is correct when the log carries

```
[VulkanDevice] selected Microsoft Direct3D12 (Qualcomm(R) Adreno(TM) X1-85 GPU)
               (IntegratedGpu, driver 109051912, Vulkan 1.2.335), queue family 0, from 1 enumerated.
```

**A Release build still validates**, because the gate is `DEBUG || SDLVK_VALIDATION=1`. What a
Release build does NOT have is the inspector, so validation findings are read from the log (the
renderer's 4xx events) rather than from `validation_report`.

## Two things that will waste your time

**WSLg goes stale, and the symptom is a window that never appears.** The process runs, the log looks
healthy, and nothing is drawn. `wsl --shutdown` and start the distro again; this has now happened
twice. It also stops every other distro, Docker Desktop's included, so do it deliberately.

**Do not read a dzn run's exit code.** Qualcomm's shader compiler, which the guest gets through WSL's
driver mirroring, unloads with a use-after-free in its own `fini()`, so a process that shut down
perfectly can still end in `exit 134` (`corrupted double-linked list`) or `exit 139`, after `main`
has returned and after every Vulkan object is destroyed. The log is complete; the exit code is not
ours. Established in detail, with the cores, in `drawboard/pdf-viewer`'s `docs/running-on-wsl.md`,
and reported as microsoft/wslg#1513.
