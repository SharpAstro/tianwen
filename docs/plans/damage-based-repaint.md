# Damage-based repaint: a mouse move should not repaint the window

## The measurement that started it

Four readings on an Adreno X1-85, 4096x4096 mono frame, maximised, from Task Manager's GPU figure:

| state | GPU |
|---|---|
| no redraw at all (zoomed out, pointer over the file list) | **0%** |
| redraw, cached image layer HIT -- blit + chrome | **8%** |
| redraw, cached image layer MISS -- stretch shader + chrome | **16%** |
| before/after divider drag -- two renditions + chrome | **34%** |

The cached image layer (`ImageRendererBase.CachedLayer`) removed exactly the shader half, 16 -> 8.
The remaining 8% is the price of **repainting the whole window**: a full-pane textured blit plus
toolbar, file-list rows, info panel, status bar, histogram and overlays, all re-emitted from
scratch, in order to change one number in the status bar.

There is no damage concept anywhere in the stack: `OnRender` paints everything or nothing, which is
exactly why the only two observable states are 0% and 8%. That is what this plan fixes, and it
supersedes caching as the answer to "why does a mouse move cost 8%" -- if a readout change damages
only the status bar, the image pane is never touched and no cache is involved. The image layer keeps
its value for PAN, where the pane genuinely is damaged.

## Premises checked before designing (each one could have invalidated the approach)

- **MSAA is off.** `VulkanDevice.Create`'s `msaaSamples` defaults to `Count1` and no TianWen host
  passes anything else, so the swapchain has a single colour attachment and `loadOp = Load` can
  preserve the previous frame directly. Under MSAA it could not: the multisample attachment is
  transient (`storeOp = DontCare`) and cannot be reloaded from the resolved image, which would have
  forced a persistent offscreen target plus a blit-back -- a different and much larger design. **If
  MSAA is ever enabled, this plan needs revisiting, not just re-testing.**
- **`ArrangedNode<T>` already carries what a diff needs**: `(Node Node, Rect<T> Bounds)`, and `Node`
  is a record. The arranged tree is the GPU-side counterpart of Console.Lib's `CellBuffer`, which
  already paints by diffing (a clock tick emits ONE cell). Same idea, one surface over.
- **But `ArrangedNode` equality is useless for damage.** `Node.OnClick` is an
  `Action<InputModifier>?`, records compare delegates by REFERENCE, and trees are rebuilt per frame
  with fresh lambdas -- so every clickable node compares unequal every frame and a naive diff reports
  the entire UI damaged, always. Damage needs a **visual signature** that includes only what paints.
  Excluding handlers is correct rather than a shortcut: two nodes differing only in which lambda they
  would invoke are pixel-identical.
- **Hover is resolved at PAINT time**, not declared. `Node.HoverBackground` is chosen against
  `PixelWidgetBase.Pointer` when the node is painted, so a hovered and an unhovered node have
  IDENTICAL declared properties. A signature over declared properties alone reports no damage on a
  hover transition and **highlights silently stop working**. The signature must carry the RESOLVED
  background.
- **A `Content.Fill` leaf is opaque to the tree.** Its pixels come from a painter callback (the image
  pane, the histogram, the sky map), so the diff cannot see them change. Fill leaves must either be
  treated as always-damaged or self-report.
- **A `Content.TextInput` leaf holds `TextInputState` by REFERENCE.** Typing and caret movement change
  nothing the diff can see. Its signature must extract text, caret and focus.
- **Theme changes need no special term.** `Content.Text.Color` and `Node.Background` are both declared
  on the tree, so a palette switch alters the nodes and damages naturally. This is the one place the
  usual `GuiTheme.PaletteGeneration` rule does NOT have to be restated -- but only because the tree is
  declarative; anything painting straight from `GuiTheme.Palette` (tab chrome, control internals)
  bypasses the tree and is outside this mechanism.

## The damage rule

Damage is the **symmetric difference** of the two frames' signature sets, taking the bounds of every
entry present in one and not the other. Stated that way it is order-independent and gets three cases
right that an index-by-index walk does not:

- a node that MOVED contributes both its old and new bounds (they are different entries);
- a node that APPEARED contributes its new bounds;
- a node that VANISHED contributes its old bounds -- **which is the tooltip case**. A dismissed
  tooltip changes nothing in the current tree, so its damage is entirely its old rect, and a diff that
  only walked the current frame would leave it painted on screen forever. Same for dropdowns and the
  split divider.

Two consequences worth stating because they are the point:

- **Moving inside a button produces a byte-identical tree, so damage is empty and nothing repaints.**
  Crossing the boundary flips two nodes' resolved background, so damage is those two rects. "Repaint
  on transition, not on motion" is not a special case anyone writes -- it is what the diff says.
- **The redraw GATE becomes derivable.** Empty damage means do not render. Today `CheckNeedsRedraw` is
  a hand-maintained predicate, and hand-maintained predicates are what went wrong: the cursor readout
  flagged a full repaint for pixels that were not visible (fixed separately, below).

Full damage is forced when: the surface resized, the palette generation moved, a Fill leaf reports a
change, or the target swapchain image has never been painted.

## Phasing

| # | where | what |
|---|---|---|
| D1 | DIR.Lib | `Layout.PaintSignature` over an `ArrangedNode` (bounds + resolved background + corner radius + content, content-specific for TextInput, opaque for Fill); `LayoutDamage.Compute(previous, current)` -> merged rects. Pure logic, offline-testable. |
| D2 | DIR.Lib | Retain the arranged tree in production. Today capture is gated on `LayoutInspection.Enabled` for "zero overhead in production"; damage makes it load-bearing. One list of structs per widget per frame -- cheap, no longer free. **Decision taken: accept the cost, 8% GPU is worth more than a per-frame list.** |
| D3 | SdlVulkan.Renderer | A preserve-and-scissor frame: render pass variant with `loadOp = Load`, damage-rect scissors, and **per-swapchain-image** damage accumulation. With 2-3 images each holds a frame from 2-3 frames ago, so what must be repainted into image N is the union of damage since image N was last presented -- not the current frame's damage. Getting this wrong leaves stale pixels that only appear at certain frame counts. |
| D4 | TianWen | Fill leaves self-report (image pane, histogram, sky map). A readout change damages the status bar only. `CheckNeedsRedraw` derives from damage. |

D1+D2 are a DIR.Lib minor, which per the org convention forces a lockstep Console.Lib AND
SdlVulkan.Renderer release before TianWen can re-pin. D3 is a second SdlVulkan.Renderer release. So
this is two cascades, not one -- worth batching D1-D3 into a single DIR.Lib + renderer pair if the
damage API can be settled before the frame work starts.

## The device loss: a document swap mid-frame destroyed views the frame had bound (found 2026-08-27, fixed)

The Store viewer (6.3.1352) died at 12:19 while the user stepped through the files of a stack run:
two `nvlddmkm 153` errors within half a second of two document loads 1.3 s apart, then
`LiveKernelEvent 141` (the GPU watchdog) and the process gone with no .NET exception. Windows keeps
no process list for a past instant, so the earlier `153`s this month were read from what the event
logs held around them: the three on 08-09 had `TianWen.UI.Benchmarks.exe` running GPU benchmarks
(and the 22:12 watchdog killed the very process the inspector's `batch` was driving: its socket
reported "connection forcibly closed by the remote host" the same second); 08-23 and 08-24 fired
within two seconds of `Kernel-Power 42`, the system entering sleep; 08-12 had nothing. No `4101`
"driver recovered" event exists at all: this driver goes straight to the watchdog.

**Reproduced at HEAD under the validation layer**, `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1`, by
loading three files of different geometry. The layer named it seconds before the loss:

    vkCmdBindDescriptorSets(): was called in VkCommandBuffer 0x14eed70ca20 which is now in an invalid
    state (instead of recording state) because the following objects bound to the command buffer were
    invalidated: VkImageView 0xab00000000ab was destroyed
    ... (vkCmdSetScissor, vkCmdEndRenderPass, vkEndCommandBuffer, then again for VkImageView 0xa8)
    [VulkanContext] VK_ERROR_DEVICE_LOST from vkWaitForFences

and the driver logged the identical pair of `153`s at 14:33:15.3 / 15.8 against a load at 14:33:15.25,
then `WATCHDOG-20260827-1433.dmp`.

**Mechanism.** The standalone frame is `BeginFrame` -> `OnPreRenderPass` (`PrepareFrame` +
`PrepareCachedImageLayer`, which records the layer pass sampling the channel views) -> `OnRender` ->
`EndFrame`. Every host uploaded the new document's textures from its render callback, and the
standalone even performed the SWAP there (`tracker.ProcessCompletions`, `HandleFileRequest`,
`TryApplyPendingEnhance`). A new geometry makes `UploadStagedChannel` destroy and recreate the channel
textures. `DestroyChannelTexture` drains the fences of PRIOR frames (its remarks are explicit about
that), but nothing can un-record what THIS frame's command buffer already holds: the layer pass had
bound the old views, so the frame was submitted referencing destroyed views, the GPU faulted, the
watchdog fired. Only a change of geometry or format destroys anything, which is why stepping between
files of the same size never showed it, and why the run folder (3348x3089 comet layer, 3065x3037 star
layer and composite, 2956x2983 autocrops, one-channel rejection maps) was the perfect trigger.

**Fix.** The swap steps run in `loop.OnBeforeFrame`, before `BeginFrame`, and a swap requests full
damage there (a mouse move in the same tick could otherwise narrow a frame that shows a new document).
The upload itself moved into `PrepareFrame`, the one point every host passes before recording anything
that samples, so the four host-side `UploadDocumentTextures` calls are gone (`Program.cs`,
`GuiderTab`, `LiveSessionTab`, `VkPlanetaryTab`) and the layout in the same `PrepareFrame` sees the new
document's size at once instead of a frame late. Under the layer, five swaps across three geometries
then produced zero messages and no loss. `ANewDocumentIsUploadedBeforeTheLayerPassThatSamplesIt` pins
the order on the fake backend (it fails with the upload taken out of `PrepareFrame`) and asserts that
the layer built that frame is the one drawn, where the old order built it from the previous document,
invalidated it and drew directly.

**Upstreamed (SdlVulkan.Renderer 7.28), so the class cannot recur in any consumer.** The
ordering fix above is necessary but rests on call order, which is exactly what went wrong. The
renderer now owns the lifetime: `VulkanContext.DeferDestroy(view, image, memory, buffer,
descriptorSet)` (and an `Action` overload) stamps the handles with the current frame ordinal and
destroys them right after the fence wait that proves that frame and every frame in flight retired,
with `RecoverFromGpuError` and `Dispose` flushing the queue after their own drains. `VkTexture.Dispose`
defers, so disposing a texture in the frame that drew it is legal for every consumer.
`VkFitsImagePipeline` hands every channel, before and histogram texture to it and drops the
`TryWaitAllFramesIdle` drain that used to stall the render thread per document swap. The second half:
a set a pending frame holds may not be written, and the drain was what made the in-place
`vkUpdateDescriptorSets` legal, so the image and before sampler sets are now one per frame in flight,
each rewritten at draw time when the views' stamp has moved (`EnsureSamplerSet`). Pinned in the
renderer by `DeferredDestroyTests`: the schedule on the plain fixture (pending until the ordinal has
advanced by `MaxFramesInFlight`, then destroyed at that `BeginFrame`), and texture disposal in the
frame that drew it under the validation layer with zero errors. Adoption guide for other consumers:
`SdlVulkan.Renderer/docs/deferred-destroy-adoption.md`. Not covered, and noted there: host-written
per-frame UBOs are the same class on the host side and want a region per frame in flight.

**The other finding of the validation round**, independent of the crash: the damage pass's `loadOp
LOAD` reads the attachment, and the shared external dependency admitted COLOR_ATTACHMENT_WRITE only,
so the read was not ordered after the pass's own PresentSrc -> ColorAttachmentOptimal transition
(READ_AFTER_WRITE, once per swapchain image on every partial frame). Fixed in SdlVulkan.Renderer's
`FillSubpassDependencies` by admitting the read for every pass. Widening the LOAD pass alone was tried
first and made it incompatible with the framebuffers and pipelines built against the clearing pass
(VUID-VkRenderPassBeginInfo-renderPass-00904 and vkCmdDraw-renderPass-02684 on every partial frame):
dependencies are not among the things render-pass compatibility exempts, which load/store ops and
layouts are.

## The cached layer squashed the image after the pane shrank (found 2026-08-27, fixed)

The user widened the file list in the Store build and the picture compressed horizontally instead of
re-fitting. Reproduced at HEAD with the inspector: dragging the divider from 450 to 1000 px drew a
2956 x 2983 image 921 px wide in a 1421 px pane, at the right zoom (0.4807, the fit for 1421), and
dragging it back to 450 restored true aspect. Neither damage narrowing nor the layout was involved (the
resize branch asks for a full repaint, and the arranged rects were right); it was the cached image
layer's UVs.

`VulkanContext.CachedLayer` allocates its targets ONCE at the first requested size and answers any
smaller request out of the same texture (a mid-frame reallocation would stall the render thread; that
is documented and deliberate). `BeginCachedLayerPass(w, h)` then renders into the top-left `w x h`
of a texture that stays at the first size. The base normalised the blit's UVs by the REQUESTED layer
size, which is right exactly as long as request and capacity coincide, i.e. until the first time the
pane shrinks. Then `u = px / request` samples `capacity / request` more texels than the pane holds:
at a 2957 px capacity and a 2132 px request the content drew 0.721x wide and, because the margin
offset scales the same way, 99 px left of the pane and clipped there, which is the 925 px measured
(the arithmetic closes to 4 px). Growing the pane instead is refused by `EnsureCachedLayerTargets`
and falls back to the direct render, so a widening never showed it; only a shrink did, and only with
the cache opted in, which the standalone viewer is.

Fix: the seam reports the capacity it holds (`TryEnsureCachedLayerTargets(w, h, out capW, out capH)`;
the Vulkan subclass remembers the request that first succeeded, since the renderer does not expose
it, and forgets it when `OnResize` releases the targets), the blit's UVs are normalised by that
capacity, the slot state carries it so a re-allocation can never be mistaken for a reusable slot, and
a backend that answers yes with less capacity than the request is refused into the direct render.
`TheBlitSamplesInTextureSpaceWhenTheTargetIsLargerThanTheLayer` pins it (fails with the UV divisor
reverted) and `ABackendWhoseCapacityIsBelowTheRequestIsNotSampled` the refusal. The rule: a UV is a
texture coordinate, so whatever divides it must be the texture's size, and a fixed-capacity target's
size is not the size you asked for this frame.

## Already landed, independent of the above

**The chrome-hover repaint is fixed** (`ViewerActions.UpdateCursorFromScreenPosition`). The readout
gated on the IMAGE's mathematical extent, which when zoomed in runs underneath the toolbar, file list
and info panel -- all drawn OVER it. So hovering chrome reported a pixel that is not visible there,
the caller saw the readout change (`return state.CursorImagePosition != prevPos`), and repainted the
whole window per motion event: 8% GPU sliding the pointer down the file list, and 0% once zoomed out
far enough that the image no longer reached it. That asymmetry is what made it findable, and also what
hid it -- the waste APPEARS as you zoom in, so it reads as a rendering cost rather than a hit test
against the wrong rectangle. Now gated on the pane rect, so it is 0% at any zoom.

## The frames were cheap and the schedule was not (found 2026-11-25, fixed)

The atlas stuttered on a laptop over Remote Desktop, and its hover highlight was called terrible. The trace
(`tools/start-app.ps1 gui -Trace`) said the frames were not the cost: a hover frame took 1.7 ms of processor and
0.6 ms of GPU, a pan frame 2.5 and 3. What was wrong was WHEN they were drawn. A hover drew two frames 14 ms
apart and then none for 65, on repeat, 13 updates a second under a pointer delivering 40 moves; a pan ran at
display rate for a few frames and then stood for 50 to 90 ms. Five causes, each found from the per-frame lines
and the five-second `Frames in 5 s` line rather than by asking how it felt.

**Three rate limiters beat against each other.** SdlVulkan.Renderer's loop armed a frame for a pointer move at
most thirty times a second and DROPPED the arm of a move inside that window, a limit from before frame pacing
existed. `GuiFrameScheduler` drew what a dropped move had changed on a timer of its own, a thirtieth of a second
after the check that found it. And the atlas's hover resolved the pointer once per PAINTED frame, discarding the
moves in between. After a trailing frame the next move armed at once (the 14 ms pair); the move after that was
dropped, waited for a check, then 33 ms, then another check (the 65 ms hole). The loop arms every move a handler
takes now and pacing alone sets the rate (SdlVulkan.Renderer 7.110); a stated change with no frame armed is asked
for at once; and the hover keeps where the pointer IS and resolves it again right before each frame
(`IPointerMoveChange.SettlePointer`, asked from the redraw check). A pointer that stopped inside the old window
left the highlight on an object it was no longer over until it moved again, which is the same fix.

**A frame the check armed was drawn in full for the check's own yes.** A frame is commonly held for pacing past
the next check, which then finds a frame owed. Owed to nothing but the check's earlier answer, it read as a cause
of the loop's (an expose, a resize), which cannot be narrowed: under a moving pointer nearly every clock tick was
a full frame. The loop now says when a frame is owed to the check and pointer moves alone
(`RedrawPendingForCheckAndPointerMovesAlone`), and such a frame is narrowed to what the scheduler's reasons hold.

**The settling frame followed every full frame.** Three of the four redraw flags were plain booleans, and the
host's clear after a frame took whatever a thread-pool callback had raised while that frame was being drawn. A
full frame a second later caught it, lost or not: on an idle atlas that was every second full frame. Each flag is
a `RedrawFlag` now. Its clear keeps a raise from another thread and a frame asked for from inside the draw, and
SAYS when it dropped a plain raise the draw itself made, which is the one thing a settling frame is owed to.
The same inspection found two animations with no frames of their own: the layer palette's fade and the roll
easing back to level asked for each next frame with exactly the raise that clear takes, so both moved a step
whenever something else drew, a step a second on a still map. They are standing reasons
(`SkyMapState.NeedsContinuousFrames`).

**The window was asked sixty times a second whether anything had changed.** 322 redraw checks in five idle
seconds, each a wake of the render thread with the loop's hooks behind it, for an answer that was "no". The
scheduler now says when the clock could next owe a frame (`GuiFrameScheduler.NextTimedNeed`), the host passes
that to the loop capped at a quarter of a second (`SdlWindowView.IdleCheckInterval`), and a redraw flag raised on
another thread wakes it (`RedrawFlag.WakeHostOnRaise`, `SdlEventLoop.Wake`). Input is an event and pulls the
check in at once, and for a second after input the loop keeps the refresh's pace, which is what a tooltip's delay
and a double click need. 25 checks in five seconds.

**The sky was redrawn for a drift nobody could see.** The planet and comet positions were recomputed every
second whatever the zoom, 70 percent of what an idle atlas spent drawing; they now stand for as long as the
fastest body takes to cross a quarter of a pixel (`SkyMapState.EphemerisCacheTolerance`), a second again while a
body's own numbers are on show. And the whole map was redrawn for half a pixel of sidereal drift, every 4 s in
the opening view; it is one pixel now (`GuiTimeCadence.SkyDriftPixels`). The owner's rule for both: everything
that needs updating should update eventually, but not excessively so.

Measured on that laptop, the Sky Map idle in its opening view, per five seconds:

| | before | after |
|---|---|---|
| redraw checks | 322, about 20 ms | 25, about 2.5 ms |
| settling frames | 1 to 2 | 0 |
| full frames | 5 | 1 to 3 |
| processor in the frames | about 90 ms | about 20 ms |
| the render thread | about 86 ms | 25 ms, the frames and the checks and nothing else |
| the process | 2.34 % of a core | 0.90 % |

Three things about measuring it, each of which cost an hour. A sampling profile parks a thread at its native
calls, so it bills the clock reads and the syscalls for time nothing spent there, and adds a `PollGCWorker` share
that is its own overhead: trust the top of the tree, measure a leaf with a stopwatch. Idle is not idle for the
first minute or two, while the runtime's background JIT works: readings swung between 16 and 330 ms per five
seconds. And a build or a test run starves the render thread on a laptop, which the log shows as 150 to 500 ms
frames that are nobody's fault.

What a full frame still costs after it: the next two or three frames are full too, one per swapchain image that
missed it. About 2.6 ms of GPU each, a few a minute at idle. The renderer's retained frame would trade that for a
copy of the whole surface every frame, a pan's included, so it stays for software rasterisers.

## A pan's paints under 8 ms and 150 KB (found 2026-11-25, fixed)

The goal set after the schedule was fixed: a minute of panning and hovering with the Objects layer on, no paint
over 8 ms, at most one collection a second, under 150 KB allocated a frame. Before, the render thread allocated
about 500 KB a frame, a generation-0 collection came three or four times a second with a 7 to 11 ms pause each, and
the slow paints had five more causes. The trace's five-second line now carries the three figures (collections,
paints median, 90th percentile and longest, KB a frame), and a paint over 8 ms or 150 KB writes a line saying which
layer spent it. Each fix below was found from those lines.

- **Kept layouts.** The status bar, sidebar, navigation strip, layer palette, info strip and the object's panel
  were built every frame. Each keeps its arranged tree by a key of everything its build reads (`KeptLayout<TKey>`)
  and paints from it, so hover and hit regions are still registered every frame. The clock is drawn into its kept
  box. The object's panel alone was 40 percent of all garbage. DIR.Lib 11.100 keeps the tab strip the same way.
- **Collections between frames.** `BetweenFramesCollector` asks for a generation-0 collection after a presented
  frame once 4 MB has been allocated, so the pause lands between frames. No runtime setting moves the budget
  except the `DOTNET_GCgen0size` variable.
- **Efficiency cores.** The laptop's i7-1255U has two performance cores and eight efficiency cores, and Windows put
  the render thread of a window not in the foreground on an efficiency core: every layer took twice as long. The
  render loop asks Windows for full speed on that thread (SdlVulkan.Renderer 7.110, `RenderThreadQos`).
- **Cold glyphs on the measure path.** The draw path rasterises a glyph it has not seen off the render thread, but
  a measurement does it in place: the `&` of a picture credit cost 138 ms. The GUI warms the printable Latin range
  and the usual marks in the background three seconds after start (`GlyphWarmUp`).
- **Ephemerides computed ahead.** The planet and comet sweep, 10 ms when the tolerance ran out, is computed on a
  task at three quarters of the tolerance for an instant half a tolerance ahead.
- **The Objects layer.** An object off screen is dropped on one dot product before projection, and the rest are
  projected from their unit vectors. A gather keeps each object's candidate, label strings included
  (`OverlayGatherScratch`), and the items and label hits of objects still in view carry over an install by
  catalogue index. Crosses are one batch of rectangles. The grid's edge labels are traced from tables of sines.
- **Library allocations.** DIR.Lib's fallback measure and its glyph lookup closure; SdlVulkan's line and rectangle
  vertices; a device key rebuilt per lookup (now cached per `Uri`); SOFA's leap-second tables, which a Debug build
  allocated on every call.

A scripted minute (pans and hovers through the inspector at about 33 frames a second, Debug, 2900 by 1700 at 1.5,
an object's panel open):

| | before | after |
|---|---|---|
| KB a frame | about 500 | 17 to 26 |
| collections | 3 or 4 a second, inside frames, 7 to 11 ms each | 1 to 3 per five seconds, all between frames |
| paints over 8 ms in the minute | 21 with only the efficiency cores left to fix | 0 |
| longest paint | | 7.9 ms |
| largest paint | | 101 KB |

Two probes print the same figures without a window, for a change to be read in seconds: `GuiFrameAllocationProbe`
(CPU surface, by part) and `SkyMapPanCostProbe` (GPU, by layer). Both run only with `TIANWEN_FRAME_PROBE=1`.

## A clock tick paints the clock (found 2026-11-26, fixed)

An idle window draws a frame a second for its clock, and since "The frames were cheap and the schedule was not" the
renderer confines that frame to the clock's rectangle. The processor still painted the whole window for it: every tab,
the sidebar and the bar, built or taken from their kept trees and emitted in full, for the GPU to drop everything
outside the scissor. A frame owed to the clock's tick alone (`GuiFrameScheduler.FrameIsClockAlone`) now paints the
bar's fill over the clock's pixels and the time drawn into its box (`GuiWindowChrome.TryRepaintClockAlone`), which is
every pixel a full frame paints there.

What it rests on, each asked rather than assumed:

- **Nothing else paints in the clock's pixels.** The bar's fill is opaque, and no other node of the bar has a
  background, a border or content where the clock is (`NothingElsePaintsIn`, asked of the bar as built, so a later
  change to its layout turns the path off instead of leaving a stale pixel). What the window paints over the bar
  after the chrome, the night calendar and a tooltip, makes the frame a full one.
- **The region is the renderer's, not the frame's damage.** A swapchain image holds the frame of two or three frames
  ago, so it is repainted in everything damaged since it was last painted (`VulkanContext.LastFrameRegion`): a hover's
  rectangle a frame before is in it, and then the frame is painted whole.
- **The bar is the one on screen.** Its kept key must still hold: the day turning, a status message, the planned
  night, Connect All or a session builds the bar again, and that is a full frame.
- **No region moves.** Such a frame registers nothing and leaves `Ui.FrameId` where it was, so every region the last
  full frame registered still answers, which is right: that frame is what is on screen everywhere but the clock. It
  needed no change to DIR.Lib's regions, which is what this was expected to need.

`GuiWindowChromeTests.TheClockRepaintedAloneIsWhatAFullFrameWouldPaint` paints a full frame and then the clock alone a
second later on a CPU surface that keeps its pixels, as a loaded swapchain image does, and holds every pixel to a full
frame of that second, at two widths and at 1, 1.15 and 1.5. Without the fill under the time it fails at the clock's
first changed pixel.

Measured on the laptop, the Sky Map idle in its opening view, about a hundred clock frames each, against the same
build with the path switched off:

| a clock frame | paint median | 90th percentile | mean |
|---|---|---|---|
| painting the window | 0.70 ms | 1.30 ms | 0.80 ms |
| painting the clock alone | 0.20 ms | 0.50 ms | 0.27 ms |

Less than the 3 to 4 ms a second this was expected to save: by the time it was done, a partial frame already drew the
atlas's sky as it was (`SkyMapTab.HoldViewingTime`) and the renderer dropped text outside the region before shaping it,
so the window's paint under a clock frame had come down to under a millisecond. What an idle window spends now is in
its full frames, one to four every five seconds at 1.5 to 2.2 ms each: a time-driven frame for the sky's drift, a
redraw flag the app raises about every 25 s on an idle window, not yet traced to what raises it, and the two clock frames after either,
which land on swapchain images that missed it and are painted whole.
