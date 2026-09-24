# HDR in the viewer: what the button does, what the panel could do, and what the driver allows

Status: P0 DONE 2026-09-15, and the answer changed within the day (blocked on the OEM driver, OPEN on
Qualcomm's 31.0.170.0, see below); P1 DONE 2026-09-15, as a FOLD rather than a rename; P2 to P4 NOT
STARTED and now actionable on this box.
Decisions taken 2026-09-15 with the user are marked DECIDED.

## The problem, in the user's words

"The HDR functionality isn't really what I thought it would do. This is more tone-compression, which
I guess is okay, but we now have JPEG with gain maps, so maybe we don't need that? I also have an
actual HDR screen on this box (Surface Pro 11, Adreno X1-85)."

Both halves are right, and they are about two different things that share one word.

## What the viewer's HDR is today

The toolbar button labelled `HDR` (key `H`, `Shift+H` back) steps `ViewerState.HdrPresets`, five
`(Amount, Knee)` pairs from `(0, 0.8)` = Off to `(2.0, 0.7)`. The single writer is
`ViewerActions.SetHdrPresetIndex`, the status line reads `HDR: 1.0 (knee 0.80)`, and the effect is
`Image.ApplyHdr` (CPU) mirrored by `applyHdr` in `image.frag`:

```
if (v <= knee) return v;
t = (v - knee) / (1 - knee);
return knee + (1 - knee) * t / (1 + amount * t);
```

A soft knee, applied AFTER the MTF, INSIDE [0, 1]. Above the knee it trades contrast for gradient, so a
core the MTF blew to a white plate gets a little structure back. Nothing about it goes above SDR
white, and nothing about it needs an HDR panel. It is a highlight compressor and it is mislabelled.
It is also genuinely useful in SDR, so it stays; what changes is its name.

## Where the real HDR already is

`Image.RenderHdrLinearRgb` renders a display-referred LINEAR rendition, 1.0 = SDR white, taking the
per-channel signal PRE-MTF (`(norm - shadows) * rescale`, unbounded) so a core keeps its true multiple
of white and its gradient. It is gated on the CLIP: where no channel of the SDR render clipped the
output is the SDR base verbatim (gain 1), and only where it clipped does a single luminance gain roll
off toward the headroom. That rule is load-bearing (the MTF lifts shadows while the sRGB EOTF pulls
them back, so a raw ratio would push the faint field into HDR) and it is the rule any display path
must reuse.

It feeds `SharpAstro.Jpeg.JpegGainMap.Compute` for the Ultra HDR JPEG (`stack --output-format uhdr`,
`image render --output-format uhdr`, `--hdr-peak-nits`) and the cICP-PQ PNG. All of it is export-only:
the viewer's Save menu has three rows (16-bit, 8-bit, with overlays) and none of them is this.

## P0: the probe (DONE 2026-09-15: no on the OEM driver, YES on Qualcomm's 31.0.170.0)

The whole display half hangs on one question: does the Adreno X1-85's Vulkan driver offer an HDR
colour space on a Win32 surface? Two commands answer it, and both were run on the box in question.

`vulkaninfo` (the Qualcomm driver's own copy at `C:\Windows\System32\vulkaninfo.exe`; it segfaults
on exit but writes the full report first):

```
deviceName        = Qualcomm(R) Adreno(TM) X1-85 GPU
apiVersion        = 1.3.295      driverVersion = 0.855.0
Instance Extensions: count = 13   (no VK_EXT_swapchain_colorspace, no VK_EXT_hdr_metadata)
Surface type = VK_KHR_win32_surface
  R8G8B8A8_UNORM      COLOR_SPACE_SRGB_NONLINEAR_KHR
  R8G8B8A8_SRGB       COLOR_SPACE_SRGB_NONLINEAR_KHR
  B8G8R8A8_UNORM      COLOR_SPACE_SRGB_NONLINEAR_KHR
  B8G8R8A8_SRGB       COLOR_SPACE_SRGB_NONLINEAR_KHR
  R16G16B16A16_SFLOAT COLOR_SPACE_SRGB_NONLINEAR_KHR
```

`dxdiag /t`, same moment:

```
Monitor Name: Surface Panel Driver V2       Native Mode: 2880 x 1920 120 Hz
HDR Support: Supported                      Monitor Capabilities: HDR Supported (BT2020RGB Eotf2084Supported)
Advanced Color: AdvancedColorSupported AdvancedColorEnabled
Active Color Mode: DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
```

So Windows has HDR ON for the panel, the panel is BT.2020 PQ capable, and the Vulkan driver exposes
none of it: one colour space, no `VK_EXT_swapchain_colorspace`, so `EXTENDED_SRGB_LINEAR_EXT` (scRGB,
the Windows-native path) and `HDR10_ST2084_EXT` cannot even be asked for. The 16-bit float format is
there but only as sRGB-encoded, which means values above 1.0 are clipped by the compositor. The
layered `Microsoft Direct3D12 (Adreno)` Vulkan device reports the same.

**That was the OEM-channel driver, 31.0.137.0 (Surface / Windows Update, dated 1 Aug 2026), and it was
the block.** Qualcomm's own Software Center ships a different line, and its newest, 31.0.170.0
(branded "2026.08.2"), installed the same afternoon, changes the answer:

```
instance extensions: 14      VK_EXT_swapchain_colorspace: present
Adreno X1-85: Vulkan 1.4.295
Surface Panel Driver V2, SDL display HDR enabled: True
  R16G16B16A16Sfloat  ExtendedSrgbLinearEXT        <- scRGB, the Windows-native HDR path
  (the four 8-bit formats stay SrgbNonLinear; no HDR10 PQ colour space, which Windows does not need)
VERDICT: at least one display can take an HDR swapchain through SDL3 + Vulkan.   exit 0
```

**Conclusion, revised: display HDR through SDL3 + Vulkan is possible on this hardware with the
Software Center driver, on the OLED only** (the Apple Studio Display heads are SDR panels and stay
`SrgbNonLinear`). The target for P3 is therefore fixed by measurement, not by choice:
`R16G16B16A16_SFLOAT` + `EXTENDED_SRGB_LINEAR_EXT`, 1.0 = 80 nits, SDR white from SDL's
`SDR_WHITE_LEVEL` (2.00 here, i.e. 160 nits) and headroom 2.38 on this panel. A user on the OEM
driver still sees the greyed row with its reason, and the reason now names the fix: install
Qualcomm's driver. That is why the row is greyed with a reason rather than absent.

The probe is a tool in the renderer's own repo, `SdlVulkan.Renderer/tools/HdrProbe` (`dotnet run`
in that folder): it enables `VK_EXT_swapchain_colorspace` when the loader has it (without it every
driver reports sRGB only, so `vulkaninfo`'s answer and the library's answer can differ), then walks
every connected display, opens a small window there, reads SDL3's HDR properties for the display and
the window, and lists every surface format and colour space each physical device offers on that
window's surface. Exit code 0 when some display can take an HDR swapchain, 2 when none can. Both
2026-09-15 runs are recorded in its source: exit 2 on 31.0.137.0 with three displays attached (the
OLED, HDR on; two Apple Studio Display heads, HDR off), exit 0 on 31.0.170.0. It is the first thing
to run on any new box or driver, and its verdict is what P2's capability detection will compute
inside the renderer at startup.

**And the present path was then SEEN to work, the same day.** `dotnet run -- --show` makes the
swapchain the listing promised (`R16G16B16A16_SFLOAT` + `EXTENDED_SRGB_LINEAR_EXT`, 1280 x 760 on
the OLED, two images) and clears patches onto it with no shader or pipeline in between: a top row
at 0.25, 0.5, 1.0, 1.5 and 2.0 times SDR white then the panel peak, the primaries at SDR white and
at the peak, and a 64-step linear ramp. On the panel every patch right of 1.0 was brighter than
the last, and the 2.0 and peak patches were brighter than any SDR window's white, which is the
whole claim. **A screenshot cannot show this and must not be used to judge it:** the Snipping Tool
composes to an SDR bitmap, so the same window captured read 188, 255, 255, 255, 255, 255 across
the top row and the ramp saturated at step 14 of 64 (about 1.08 in scRGB), exactly the picture a
FAILED present would give. That is the rule P3 inherits: the OLED is the judge, an inspector
screenshot is not, and any automated check of the HDR path has to read back the swapchain image or
the uniforms, never a desktop capture.

**Dragging the show window onto an Apple Studio Display clipped it to the screenshot's picture.**
The swapchain did not change (still scRGB, the probe kept presenting), the DISPLAY did: an SDR
head gets the surface tone-mapped to SDR by the compositor, so everything above 1.0 collapses to
white. Fair, and the rule for P3: **whether the user is seeing HDR is a property of the display the
window is on right now, not of the swapchain**, so the indicator reads SDL's per-display HDR
property (`GetDisplayForWindow` then `DisplayHDREnabledBoolean`) every frame and follows a move,
and the paper-white scale is re-derived from the new display's window properties when a move
lands (the probe already waits for `GetDisplayForWindow` to settle for that reason). A swapchain
format tells the indicator nothing.

## P1: say what the button does (DONE 2026-09-15)

DECIDED, and then decided again on sight of the draft: not a rename but a FOLD. The user's steer was
"more of a combo menu, like White balance -- remember before we had several buttons, then one
dropdown menu with the actual options". So `Boost` and `HDR`, two buttons doing related things to the
same pixels, became one `Tone` popover on exactly the terms `Calibrate` and `SPCC` folded into the
white-balance popover, and the display HDR is a block inside it rather than a row bolted onto a list.

What shipped:

- **`ToolbarAction.Tone` replaces `ToolbarAction.CurvesBoost` and `ToolbarAction.Hdr`**, cut in one
  wave with no shim. `ImageRendererBase.TonePanel.cs` is the panel; `ToneSliderHit` / `ToneSlider`
  carry the drag, mirroring `WhiteBalanceSliderHit`.
- **Three continuous dials where there were two preset ladders**: boost (0 to 1.5), soft-clip amount
  (0 to 2.0) and knee (0.50 to 0.95). The ladders stayed on `B` and `H` and write the same fields, so a
  key press moves the sliders, exactly as the white balance's `Auto` drops a triple into its.
  **Since 2026-09-24 the soft clip has no key**: `H` is the histogram (the user's call, freeing `V`),
  and the soft-clip ladder is reached through the Tone popover's dials and the wheel over its button
  (`TryHandleToolbarWheel`), which step the same presets. `B` keeps the boost's ladder.
- **Each dial states its own precondition and registers no track when it is unmet.** The boost needs
  detected stars (the gate its button carried); the knee means nothing at zero amount; the curve mode
  reaches the pixels only through the boost. A live band over a control that cannot move is a slider
  that follows the pointer and changes nothing, which reads as broken rather than as a precondition.
- **The display-HDR block is greyed with a reason**, and the reason is about the VIEWER
  ("not yet; the viewer always presents SDR") rather than about the machine, because nothing here
  asks the GPU yet and a claim about this GPU would be the same kind of guess the old label was. P2
  replaces the constant with the renderer's answer.
- **The math is untouched.** `Image.ApplyHdr`, `image.frag`, `HdrAmount` / `HdrKnee` / `HdrPresets`
  and every stretch test are byte-for-byte what they were; `StretchTests_NewPipeline` and
  `GpuStretchPipelineTests` did not move. The problem was never the mechanism.

It is also the viewer's first overlay built as ONE arranged tree (`Layout.Builder` + the engine's own
measure) rather than a hand-advanced `y` and a hand-summed box. That was not the original intent; the
first draft did it the old way and was already wrong -- the soft-clip heading ran past the right edge of
its own panel, because the width was a union of the strings someone remembered to list. It is now the
worked example for `docs/plans/viewer-layout-engine.md`, which the user raised off this work and marked
high priority.

Three things the draft above got wrong, all found by reading the code rather than the plan:

- **The dropdown HAS had disabled-row support all along.** `DIR.Lib.DropdownItem.Disabled(label,
  value, reason)` draws the row greyed, skips it in keyboard navigation, swallows its click and
  right-aligns the reason on the same row (`PixelWidgetBase`). The plan asserted the opposite and
  budgeted for building it. It went unused in the end -- the fold made a panel the better home -- but
  the lesson stands: check the toolkit before planning a primitive for it.
- **A panel that lays itself out cannot be tested without redoing its arithmetic.** The draft assumed
  the work was the control; the cost that surfaced was the test, which had to sweep the window asking
  `HitTest` at every point to find out where anything had been put. Rebuilt as a tree, the test names
  arranged nodes instead. That is the observation the layout plan came out of.
- **A popover button needs a line in the PRESS dispatcher or it is simply dead.**
  `HandleViewerMouseDown` sends a toolbar press to `ViewerActions.HandleToolbarAction`, which has no
  arm for a button that only opens a panel, so the press did nothing at all and the button looked
  broken. White balance had a special case reading "the one button on this bar whose press has no
  cycle to fall through to"; there are two now, and the comment says to add the next one there.

Not done here, deliberately: the status line still reads `Soft clip: 1.0 (knee 0.80)` on a key press
and the info strip's snapshot still names the slot, both already renamed. The `?` panel gained a row
for `B` and one for `H`, because someone hunting for HDR finds `H` first and what it does is not what
they came for. (Superseded 2026-09-24: `H` is the histogram's row now, and `B`'s row says the soft
clip is on the Tone panel.)

## P2: capability detection (NOT STARTED; SdlVulkan.Renderer first)

The greyed row needs a fact from the renderer. `VulkanContext` creates the swapchain with
`VkColorSpaceKHR.SrgbNonLinear` hard-coded and never asks. Add, in SdlVulkan.Renderer:

- `SurfaceCapabilities` on the window: whether `VK_EXT_swapchain_colorspace` is present, and which of
  `SrgbNonLinear` / `ExtendedSrgbLinearExt` / `Hdr10St2084Ext` the surface offers with which formats
  (`vkGetPhysicalDeviceSurfaceFormatsKHR`, once at surface creation and again on a display change).
- Nothing else changes in this phase; the swapchain stays sRGB. The feature ships as a library minor
  and rides the usual release chain (`/release-lib SdlVulkan.Renderer`, then the pin here).

In TianWen: `ViewerState.DisplayHdrCapability` (`Unsupported(reason)` / `ScRgb` / `Hdr10`), filled
by the host (`tianwen-fits` `Program.cs`, policy only) and read by the dropdown. On this box it reads
`Unsupported("driver offers no HDR colour space")`, which is the honest state and the greyed row the
user asked for.

## P3: an HDR swapchain where one is offered (NOT STARTED; gated on P2 answering yes on some box)

DECIDED: opt-in. Off by default, the greyed row becomes selectable when P2 says yes, and choosing it
is what turns the swapchain over.

The rendering cost is close to nothing: twice the bytes per pixel on the final surface only (about
44 MB a frame at 2880 x 1920 instead of 22), no extra pass, and the image shader loses a clamp
rather than gaining work. The CHANGE cost is where it lives, across three layers:

1. **SdlVulkan.Renderer.** Negotiate `ExtendedSrgbLinearExt` + `R16G16B16A16_SFLOAT` with an sRGB
   fallback; recreate the swapchain when the capability changes under the app (Windows HDR toggled,
   window dragged to another monitor; `DeferDestroy` rules apply, see `viewer-gpu-lifetime.md`);
   every pipeline drawing to the swapchain gets a compatible render-pass format. Add a **paper-white
   scale** uniform the chrome draws through: in scRGB 1.0 is 80 nits and the panel peaks at 6 to 12
   times that, so an unscaled UI is dim grey while the image blows past it. The value comes from
   Windows' SDR content brightness (`DisplayConfigGetDeviceInfo` with
   `DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL`), read by the host and handed to the renderer.
2. **The chrome.** Every white in the UI is 1.0 and has to go through the scale: DIR.Lib text and
   layout, both sky-map GPU backends, the histogram, the overlays. One value, many consumers, each of
   which has to be found; `SkyMapGpuGeometry.GridLineColor` shows the shape of the work.
3. **`image.frag` and its CPU mirror.** The smallest part. An HDR branch emits linear light above 1.0
   where the SDR stretch clipped, and ONLY there, which is `RenderHdrLinearRgb`'s gate reused: the
   faint field stays identical to SDR by construction, and the panel gets the core's real gradient.
   The knee then applies only in the SDR branch (with the panel doing the highlights, the compressor
   has nothing to compress). `StretchTests_NewPipeline` grows the same branch on the CPU side; the
   mirror rule in `stretch-pipeline.md` is why.

Then the parts that are neither: an indicator in the status bar that the present is HDR (a swapchain
can be scRGB on an SDR panel and merely look washed out), the toggle persisted per user, and judging
it on the real OLED, since a screenshot cannot capture what this does. Days, not hours; the shader
itself an afternoon.

## P4: the gain-map JPEG follows the display (NOT STARTED; needs P3's shader rule, not P3's swapchain)

DECIDED (as a "maybe", firmed up here): saving a JPEG with a gain map becomes automatic when HDR
display is on, and available as an explicit row regardless.

- A new Save row, `Image as displayed, HDR (JPEG with gain map)...`, always offered: the export path
  exists today and does not need an HDR panel to produce a valid Ultra HDR file, only to view one.
- When HDR display is ON, a save whose container is JPEG writes the gain map by default (the SDR base
  is what an SDR viewer shows, the gain map is what the panel showed you), and the status line says
  so. With it off, nothing changes for the existing rows.
- One gap to close first: `RenderHdrLinearRgb` models the per-channel path only
  (`StretchMode.Linked` / `Unlinked`); `Luma` and `None` are not modelled, and the viewer offers both.
  The HDR rendition the shader branch and the gain map share has to cover all four modes, in Lib, once.

## What this does NOT do

- It does not remove the soft knee. Without an HDR present the MTF white plate is real, and the knee
  is the only in-viewer relief; the gain-map export is the HDR story until P3 exists somewhere.
- It does not chase HDR10 (PQ) separately. On Windows scRGB is the native path and the compositor
  converts; HDR10 is taken only where a driver offers it and scRGB is absent.
- It does not put HDR on the web build. WebGL2 has no HDR canvas; Chrome's is behind a flag.

## Where the pieces are

| piece | where |
|---|---|
| the knee | `Image.ApplyHdr` (`Image.Stretch.cs`), `applyHdr` in `image.frag`, presets in `ViewerState.HdrPresets`, writer `ViewerActions.SetHdrPresetIndex`, dropdown in `ImageRendererBase.Toolbar.cs` |
| the HDR rendition | `Image.RenderHdrLinearRgb` (`Image.Stretch.cs`), consumed by `MasterPreviewRenderer.RenderAsync(ultraHdrPath:)` and `JpegGainMap.Compute` |
| the swapchain | `VulkanContext` in SdlVulkan.Renderer, `imageColorSpace = SrgbNonLinear` |
| the probe | `SdlVulkan.Renderer/tools/HdrProbe` (`dotnet run`), cross-checked against `vulkaninfo` and `dxdiag /t`; results above, 2026-09-15 |
| the export flags | `stack --output-format uhdr`, `image render --output-format uhdr`, `--hdr-peak-nits`, `--png-pq-peak-nits`; `docs/todo/imaging.md` "Gain-map JPEG export" |
