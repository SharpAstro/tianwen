# GPU device loss: the 2026-09-22 wedge, what the desktop must check, and in-process recovery

Status: **NOT STARTED** (the recovery); **OPEN** (the wedge's trigger). High priority: `TODO.md`.

## What happened (win-arm64, Adreno X1-85, 2026-09-22 20:35 local)

The GUI's sky atlas, Horizon mode, Objects layer on, the Large Magellanic Cloud selected (panel open)
and hovered (the new shaped wash drawn over it), field zoomed from 4 to 19 degrees in one step and
back to 16. The GPU then took longer than 500 ms on a frame and never took another:

| stderr line (`src/gui-stderr.log`) | Meaning |
|---|---|
| `skymap.stars drawn=20178 effMag=9.7 fov=19` then `drawn=14976 fov=16` | ordinary frames, CPU side healthy (`frame.slow` never fired) |
| `GPU fence late (window 2); retrying without teardown (500ms since last clean frame)` | the in-flight fence did not signal within the poll budget |
| `vk.submit result=ErrorInitializationFailed frame=0 img=1` | the next submit is rejected |
| `GPU fence recovered after 2141ms; no teardown needed` | the stuck fence did signal in the end |
| `vkQueueSubmit rejected ...` repeated, every frame, for minutes | the device takes no more work |

No exception anywhere. The process stayed alive: the session, the input and the tab state all
kept running (a tab click still retitled the window), but nothing new was ever presented, so the
window showed its last frame until it was closed. Windows logged no display-driver reset. The
application log (`%LOCALAPPDATA%/TianWen/Logs/20260922/GUI_20260922T20_25_37.log`) holds nothing
about it.

The FITS viewer, running the same renderer and the same wash for an hour beside it, did not wedge.

## Two defects, one fixed

1. **The renderer counted a rejected submit as a clean frame.** `VulkanContext.SubmitFrame` absorbs
   `ErrorInitializationFailed` as one dropped frame (right for the two-frame transient measured in
   August 2026), threw nothing, and the event loop reset its storm accounting on every one of them.
   No recovery was ever attempted. **Fixed in SdlVulkan.Renderer 7.46** ("a streak of rejected
   submits is a device that is not taking work"): `LastFrameSubmitted` keeps a dropped frame out of
   the clean-frame accounting, and `RejectedSubmitStreakLimit` (three) consecutive rejections throw
   the driver's result, which runs the mid-frame recovery (sync and swapchain rebuilt), the backoff,
   and after two recoveries within a second the host's `OnRenderDegraded` (the GUI switches to
   Notifications, resets the atlas zoom, records a warning).
2. **A device that stays dead cannot be brought back in-process.** This is the open item. It is not
   impossible: the same `VkDevice` cannot recover from `DEVICE_LOST`, but a new one can be created,
   and everything device-owned rebuilt. What it takes:
   - SdlVulkan.Renderer: destroy and recreate the device behind `VulkanContext` (pipelines, the
     per-frame sync, the vertex ring, the glyph atlases, descriptor pools, the swapchain), on the
     sacrificial recovery task so a driver blocking inside teardown (the June 2026 zombie) is
     abandonable rather than fatal.
   - TianWen: one "GPU resources invalidated" event, after which the viewer re-uploads its
     document textures (`docs/architecture/viewer-gpu-lifetime.md` has the rules every upload must
     follow), the sky map its star, Milky Way and overlay buffers, the GUI its planner chart
     texture, and the font atlases refill lazily.
   - A host that cannot rebuild keeps running headless with a degraded surface and a notification,
     never a frozen window, because a running session with cooling cameras is worth more than the
     picture of it.

## What the desktop found, and what the laptop then measured (2026-09-23)

The desktop ran the repro below with the Khronos layer live on a GTX 1070, at the same fields and
star counts, and **it never wedged, before or after, with zero validation messages and zero sync
hazards**. What the review found instead was a class of defect the layer does not report and a
conforming driver need not show:

- **Eight cull sites wrote `gl_Position = vec4(0, 0, 0, 0)`.** That is an undefined vertex, not a
  degenerate one. `w = 0` makes the perspective divide `0/0`, and the clip test `-w <= x <= w`
  degenerates to `0 <= 0 <= 0`, so on the way to being NaN the vertex reads as INSIDE the view
  volume. A desktop rasteriser drops the primitive anyway; a tiling binner deriving a tile range
  from NaN need not, since every comparison against NaN is false. **Three of the Vulkan sites are in
  the star shader and one of those is the HORIZON clip**, so Horizon mode sends every below-horizon
  star down this path, thousands of primitives per frame. That is the one thing the atlas does which
  the viewer's sky backdrop never does, and it is the asymmetry the account above could not explain.
  `skymap_line.vert` already used `vec4(2, 2, 0, 1)`; the rule existed and the other shaders had
  drifted off it.
- The overlay shaders took `atan(north2d.y, north2d.x)` unguarded, which is undefined at (0, 0) and
  reachable two ways near the antipode. That one puts a VISIBLE instance with no finite position
  into the stream rather than a culled one.
- `image.frag` took `asin` of an argument only mathematically within [-1, 1].

**Then the laptop measured whether any of it is observable on the Adreno**, through a new offscreen
GPU test (`SkyMapHorizonCullGpuTests`): the sky map pipeline built from the real catalogue, drawn
headless at 512x512 centred on the nadir, read back and counted.

| Shaders | Star pixels below the horizon | Star pixels at the zenith | Result |
|---|---|---|---|
| Fixed (`vec4(2, 2, 0, 1)`) | 0 | thousands | passes |
| Pre-fix (`vec4(0, 0, 0, 0)`), rebaked | 0 | thousands | passes |

**So the undefined vertices are not observable this way on the Adreno either.** The cull works in
both arms; the defect is real but its effect, if it has one, is not a mis-drawn star in a single
offscreen frame. That rules out the simplest story (culled stars leaking into the picture) and
leaves the binner's behaviour under a real swapchain and sustained load, which no test in this repo
reaches. **Whether the fix closes the wedge is still unknown**, and the only evidence that can
settle it is the atlas running for a long session on the Adreno without wedging.

What IS pinned, on every host and with no GPU at all, is that the source keeps saying the safe
thing: `ShaderContractTests` reads every `.vert` and `.frag` the app ships plus the WebGL twin's
inline GLSL and asserts a culled vertex leaves the clip volume with a positive w, `asin` and `acos`
take a clamped argument, and the overlay's north angle is guarded. Reverting the four shader
sources to the commit before the fix turns 6 of its 31 cases red, one per defect per backend.

## Prior art: what Drawboard's PDF viewer does, and why it is not obviously right for us

`drawboard/pdf-viewer` runs the same renderer (a Drawboard fork of SdlVulkan.Renderer, carried as a
git submodule) on the same Adreno X1-85, and it has already answered the question this plan is about.
Its answer is **not** to recover the device. It is to replace the process.

`MultiWindowApp`'s `OnGpuWedged` snapshots the open tabs and the navigation history, writes them to
an INI manifest, spawns a successor process, and calls `Environment.Exit(70)` **with no Vulkan
teardown at all**. The reasoning is recorded there and is sound: an orderly shutdown runs
`vkDeviceWaitIdle` and `vkFreeMemory`, both of which block for ever on a hung device (which is the
same driver behaviour our own sacrificial-recovery task exists for), so the teardown is skipped
rather than attempted, and a fresh process gets a fresh driver instance, which is the one recovery
layer a hung driver cannot defeat.

Three details worth copying if we ever go that way, and one reason we probably cannot:

- **A recovery storm is treated as terminal, not as a load to shed.** Their `OnRenderDegraded`
  immediately calls `OnGpuWedged`, so the third quick recovery inside a second is a respawn. Ours
  switches the GUI to Notifications and posts a warning instead.
- **The boot loop is bounded three ways**: a generation counter capped at three, a five minute
  "healthy uptime" rule that resets it, and a two minute freshness window on the manifest, so a
  stale hand-off from yesterday cannot restore anything.
- **The successor is marked by an environment variable** so the single-instance gate does not hand
  the session back to the process that is dying.
- **The reason it does not transfer**: a PDF viewer's whole state is which documents are open and
  where the reader was looking, and that is a few hundred bytes. Ours is a running session with a
  mount tracking, cameras at setpoint, a guider calibrated and a filter wheel mid-sequence, held
  through device leases that a new process would have to re-acquire from drivers that still believe
  they are owned. A respawn ends the night. That is the argument FOR in-process recreation here and
  against simply taking their approach, and it is why this plan still says what it says.

Also worth knowing: **their submodule predates the 7.46 streak fix**, so on that build a device
rejecting every submit still reads as healthy for ever and their `OnGpuWedged` can only be reached
through the fence-stuck path or an explicit `DEVICE_LOST`. If the wedge is ever reproduced there,
that is why it will look different.

## What the desktop must check (it has the Vulkan validation layer; this machine does not)

Same repo root, same branches. The branches involved and their heads on 2026-09-22:

| Repo | Branch | Head |
|---|---|---|
| DIR.Lib | `feat/affine-ellipse-pixel-stroke` (PR #94, green) | affine ellipse with pixel stroke, 10.4 |
| SdlVulkan.Renderer | `feat/ellipse-quad-and-instanced` (PR #109, local commits unpushed) | SDF ellipse shaders, 7.46, plus the submit-streak fix |
| WebGl.Renderer | `feat/sdf-ellipse` (unpushed) | 1.36 |
| tianwen | `feat/gpu-overlays-sdf-ellipse` (unpushed) | overlays, gestures, hover wash, resolver |

Local builds compile against sibling SOURCE (`UseLocalSiblings`), so checking out those four
branches side by side is the whole setup.

1. Confirm the layer is there before anything else: `pwsh -NoProfile -File tools/start-app.ps1 gui -Validation -SyncValidation`,
   then `validation_report` through the inspector must answer `layerAvailable: true` and
   `active: true`. On this laptop it answers `layerAvailable: false`, which is why the trigger is
   still unnamed.
2. Reproduce, in this order, reading `validation_report` after each step (a hazard count that rises
   names the step): Sky Map tab; `H` to Horizon mode; `O` for Objects; pan to the Large Magellanic
   Cloud (RA 5.4h, Dec -69.8); click it so the panel opens and the selection ring draws; hover it
   so the wash draws; zoom with the wheel from about 4 degrees to about 19 in quick notches and
   back to 12. Watch `render_liveness` with `watchSeconds` set while doing it, and `frame_stats`
   before and after.
3. If it wedges: `render_liveness` says BLOCKED or the log shows the rejected submits; capture
   `src/gui-stderr.log`, the `validation_report` output and a `dotnet-stack` of the render thread
   (the liveness tool prints the exact command). With the 7.46 fix the log should now also show
   `consecutive vkQueueSubmit rejections ... escalating to mid-frame recovery`, a recovery, and the
   GUI's own warning notification.
4. If it does not wedge with the layer on, run the same steps without it (the layer serialises
   enough to hide a race), and then try the FITS viewer with the sky backdrop on the same object.

Candidates the layer can confirm or clear, in the order of suspicion:

- A synchronisation hazard between the atlas's overlay instance stream and the wash quad, both of
  which draw ellipses in one frame through different pipelines (`skymap_overlay` versus the
  renderer's `EllipsePipeline`), where the wash is new in this build.
- The new pixel-distance ellipse shaders (`ellipse.frag`, `ellipseinst.frag`, `skymap_overlay.frag`):
  derivatives (`dFdx`, `dFdy`) taken on a quad whose local coordinate spans millions of units for a
  degenerate axis (bounded positions, but the fragment stage still evaluates them), and `discard`
  ordering relative to those derivatives.
- Vertex-ring pressure: `frame_stats` reports `vertexRingPeakBytes` against a 4 MB capacity and
  `vertexRingOverflowFrames`; a frame that overflows drops draws silently, it should not wedge, but
  the count says whether that frame was unusual.
- The overlay marker quads at deep zoom: the LMC's own marker is an 18,000 px quad at a one degree
  field; clipped by the rasteriser, but it is the largest primitive the atlas ever submits.

Ruled out on this machine: infinite or NaN vertex positions from a zero-length axis (both the
generic instanced shader and the atlas overlay shader keep `ext * |axis| <= |axis| + pad`); a CPU
hang (the event loop pumped throughout); a Windows TDR (none logged).

## Where the pieces are

- `SdlVulkan.Renderer/src/SdlVulkan.Renderer/VulkanContext.cs`: `SubmitFrame`, `RejectedSubmitStreakLimit`, `LastFrameSubmitted`, `RecoverFromGpuError`.
- `SdlVulkan.Renderer/src/SdlVulkan.Renderer/SdlEventLoop.cs`: the render-view catch, the recovery storm accounting, `OnRenderDegraded`, `OnGpuWedged`.
- `tianwen/src/TianWen.UI.Gui/Program.cs`: the GUI's `OnRenderDegraded` handler.
- `tianwen/src/TianWen.UI.Shared/Shaders/skymap_overlay.*`: the atlas's marker pipeline.
- `tianwen/src/TianWen.UI.Abstractions/SkyMapTab.Hover.cs`: the wash.
- `tianwen/src/TianWen.Lib.Tests/ShaderContractTests.cs`: the source rules, and what they deliberately do not cover.
- `tianwen/src/TianWen.Lib.Tests/SkyMapHorizonCullGpuTests.cs`: the offscreen sky-map render, and `VkSkyMapGpuFixture` beside the other offscreen fixtures.
- Memory of the June 2026 wedge: `reference_gpu_wedge_from_inspector_readback` (an unbounded wait on a hung device; unrelated shaders).
