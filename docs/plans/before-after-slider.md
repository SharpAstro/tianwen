# Before/After Split Slider (viewer A/B comparison)

**Status: P0-P2a DONE, P3 PARTIAL** (design captured and implemented 2026-08-18; idea: user, after
asking what the viewer's Enhance button actually does and finding it swaps the image with no way back).

A draggable vertical divider across the image: left of it the "before" rendition, right of it the
"after", with the same pan/zoom so features line up. The request was "for all modes really", and
the constraint the user set is the one that shapes the whole design: *"only though if that doesn't
cost like crapton of memory"*, alongside the standing preference for avoiding CPU copies.

## The name covers two features with completely different costs

| | what differs | extra pixels | examples |
|---|---|---|---|
| **Uniform A/B** | the `StretchUniforms` only | **none** | stretch mode, WB (auto + manual), bg-neutralisation, curves, HDR knee, luma weighting, debayer algorithm, normalize |
| **Pixel A/B** | the sampled textures | one texture set | AI enhance, wavelet re-sharpen, RAW vs STACK |

Uniform A/B is free -- the same three textures sampled twice with two uniform blocks. It covers
most of what "all modes" means and should ship whatever is decided about the expensive half.

Pixel A/B needs a second set of channel textures, and that is the part with a memory story.

## Mechanism: two scissored draws, not a shader branch

`VkDynamicState.Scissor` is **already** a dynamic state on both viewer pipelines, and
`RecordImageDraw` already binds set 0 (the `StretchUBO`) and set 1 (the three `sampler2D`s) as
separate descriptor sets before pushing the projection and drawing. So a split is:

```
scissor = contentRect INTERSECT left-of-divider ; bind (uboA, samplersA) ; draw quad
scissor = contentRect INTERSECT right-of-divider; bind (uboB, samplersB) ; draw quad
restore scissor
```

**This needs no GLSL change and therefore no SPIR-V re-bake**, which is worth protecting: the
shaders are pre-baked, committed, and a forgotten re-bake is caught only by warning TWSH0001. One
mechanism serves both features -- uniform A/B binds the *same* sampler set for both halves, pixel
A/B binds the same UBO for both. They compose: comparing a different stretch of a different plate
is the same two draws.

Cost in Vulkan objects: the descriptor pool grows from `maxSets = 4` to 6, uniform-buffer
descriptors 2 to 3, combined-image-sampler descriptors 6 to 9. One extra ~416-byte UBO slot. Nothing
that shows up in a memory graph.

**Two things the implementation settled that this section had guessed at:**

- **The clip is DIR.Lib's, never a raw scissor, and that put the split in the base class.** `PushClip`
  reaches `vkCmdSetScissor` on the current command buffer immediately (`VkRenderer.ApplyClip`), and the
  clip stack already owns intersect-with-parent plus the restore. So `VkFitsImagePipeline` touches no
  scissor at all and the whole split lives in `ImageRendererBase` -- surface-agnostic, and therefore
  testable on the offline `RgbaImageRenderer` rather than only through a GPU readback.
- **Two UBO slots are mandatory, not a tidiness choice.** The two draws are recorded into ONE command
  buffer and the GPU reads a UBO at EXECUTE time, so write-A / draw / write-B / draw would hand both
  halves the values written last. The failure is silent and looks exactly like the comparison doing
  nothing. The stretch UBO therefore holds two slots strided to `minUniformBufferOffsetAlignment`.

## The "before" is a texture set, never a document

`AstroImageDocument` **retains** its `UnstretchedImage` for its whole lifetime (`IPreviewSource.
GetChannelData` reads from it), so a displayed 3840x2160x3 frame already costs about
**199 MB standing**: 99.5 MB of CPU floats plus 99.5 MB of `R32_SFLOAT` textures. Keeping a whole
second *document* alive as the "before" would therefore cost another 199 MB, and buy nothing: the
before half needs no statistics, histogram, WCS, star list, file path or stretch solve. Those all
belong to the displayed (after) document, which is what every panel and overlay reads.

So the before is three `VkImage`/`VkDeviceMemory`/`VkImageView` handles plus the dimensions. One
texture set: **99.5 MB, no CPU copy at all.**

## The finding that inverts the cost comparison

`UploadChannelTexture` reuses the existing texture when the dimensions match and copies the new
pixels over it. An enhance therefore **overwrites the before pixels in place** -- meaning that at
the moment `TryApplyPendingEnhance` runs, the before pixels are *already resident on the GPU*.

Keeping them is not a copy. It is declining to overwrite: move the three handles aside into a
before-slot and let the enhanced upload allocate a fresh set. **No readback, no copy, no CPU
touch, no extra upload, no extra latency.** The only cost is the deferred free.

That reverses the comparison put to the user earlier in this conversation:

| | cost to capture the before | held while shown | peak | latency on press |
|---|---|---|---|---|
| **Keep the textures** | one allocation, zero data movement | +99.5 MB (GPU) | +99.5 MB | instant |
| **Reload on press** | disk read + full decode + upload | +199 MB (CPU doc + GPU) | ~+300 MB transient | ~0.6-1.0 s, off-thread |

Keeping is cheaper on every axis except one: it holds 99.5 MB from the enhance until the before is
dropped, even if the user never opens the split. Which is what the cache policy below is for.

## Policy: a cache with a fallback, not a two-way mode

The user asked for a two-way mode ("on memory pressure use reload-on-press, otherwise keep").
The same behaviour falls out of a better-shaped thing, so build that instead:

**Reload-on-press has to exist regardless** -- it is what happens when keeping failed, or when the
before was evicted, or when the pixels were never resident. Once it exists, keeping is a *cache in
front of it*. That removes the parts of a mode that would bite: no user-visible switch, no
threshold to tune wrong, and no state where the UI promised instant and then stalled.

Deciding, in priority order:

1. **Observe, do not predict.** Try the allocation; `vkAllocateMemory(...).CheckResult()` throws on
   `OutOfDeviceMemory` / `OutOfHostMemory`. Catch at the one allocation site, log it, and fall back
   to today's in-place overwrite. On a shared-memory Adreno, where VRAM *is* RAM, asking is the only
   truthful answer -- no heuristic beats it.
2. **A pre-check so we do not *cause* the pressure.** `GC.GetGCMemoryInfo()` exposes
   `MemoryLoadBytes`, `TotalAvailableMemoryBytes` and `HighMemoryLoadThresholdBytes`.
   **The obvious spelling of this is wrong twice, and only testing showed it.** `GetGCMemoryInfo`
   returns ZEROES until the first collection, so a freshly-started process reads no load against a
   real threshold and concludes there is room whatever the truth -- "no reading" must be handled as
   its own case, not as "plenty of room". And comparing the CURRENT load against the threshold
   answers a question nobody asked: it refuses a 100 MB cache on a box with 1.4 GiB of headroom. Ask
   whether THIS retention would cross the line (`load + bytes >= threshold`). The check is also
   `protected virtual`, because otherwise it reads machine-global state that moves underneath it --
   which made two tests 100 ms apart disagree, and would make the same user action behave differently
   minute to minute with nothing on screen to explain it.
3. **Evict later, which is the thing a mode structurally cannot do.** Pressure usually arrives
   *after* the enhance -- another file opened, a stack started. A cache can be dropped at that
   point and the press path still works.

Eviction triggers: the split is dismissed, the next pixel-changing operation runs, or the high-load
threshold is crossed. Each one falls back to reload-on-press, so eviction is always safe. Opening
another document is NOT one of these -- it is invalidation, below.

**The fallback needs a file on disk.** A live source -- SER playback frame, camera preview, live
stack -- has no on-disk before, so for those it is keep-or-nothing. That is acceptable because
they are overwhelmingly the *uniform* A/B case, which costs nothing either way.

**The weak-reference trick that works for documents is unavailable here.** `DocumentCache` keeps
`WeakReference<AstroImageDocument>` and lets the GC arbitrate -- the same cache-with-reload shape
this plan describes, already shipped, and worth imitating in spirit. It cannot be imitated
literally: a `VkImage` is not GC-managed memory, nothing reclaims it under pressure, and it is
absent from `GC.GetGCMemoryInfo`'s `MemoryLoadBytes`. So a held before-set makes the runtime
*under*-estimate how tight things are, and the document cache goes on keeping stale documents alive
at exactly the moment GPU memory is scarce. Pair the hold with `GC.AddMemoryPressure(bytes)` and the
free with `RemoveMemoryPressure`, so the one number both policies read accounts for the allocation
neither of them can see.

## Opening another document is invalidation, not pressure

Two different reasons for the before to go away, and conflating them is what produces a stale
comparison:

| | still the before of what is displayed? | recovery | the split |
|---|---|---|---|
| **Evicted under pressure** | yes | reload-on-press restores it | stays available |
| **Invalidated by a document change** | no | nothing to restore | must turn OFF |

The second is a correctness case before it is a memory one. A before-set from image A drawn beside
image B is not a comparison, it is two unrelated pictures with a line between them -- and where the
dimensions differ, a wrongly-scaled one.

**Do not enumerate the paths that change what is displayed.** They are: file open, file-list click,
sequence arrow, drag-drop, SER open, live frame arrival, the RAW-vs-STACK toggle, and the enhance
apply itself. `ViewerController` already sets `state.ShowStacked = false` in two of them and gets
away with it; a third flag copied to those same two places is exactly how a stale before survives
into a frame.

Instead, **the before-slot carries the identity of the document it is the before OF**, and the draw
checks it: mismatch means free the set and drop `SplitFraction` to null. The stale case stops being
something to remember and becomes something that cannot be represented.

**The pair dies together.** Losing either half turns the split off, which also settles the round
trip (open B, come back to A): A may well still be alive behind `DocumentCache`'s weak reference,
but its before was freed and its *after* was never persisted either -- reopening A yields
A-from-disk, which IS the before. So there is nothing to compare and the split is correctly off,
rather than half-restored against a plate that no longer exists.

**The free is deferred to the render thread, never done where the swap is noticed.** The load
completes on a thread-pool thread (`ViewerController`'s `Task.Run`), and a `VkImage` may still be
referenced by an in-flight command buffer. There is a precedent to follow exactly rather than
reinvent: `StashForDispose` queues replaced sources and `ReleaseCompletedTasks` disposes them
post-frame on the UI thread behind a `StillInUse` guard. The before-set joins that drain.

And it is a pressure case too -- the worst one in the app. At the moment of adoption the process can
be holding the outgoing document's CPU floats (the weak-ref cache may not have let go), the incoming
document's, the live texture set and a before set: about 400 MB for a 3840x2160x3 frame. If the
dimensions differ, `UploadChannelTexture` destroys and recreates rather than overwriting, so the new
set is allocated while the old is still live. Dropping the before at adoption takes 99.5 MB off
precisely the peak that matters.

## Phasing

Tracked by #833.

| Phase | What | Status |
|-------|------|--------|
| P0 | **DONE. Split-draw mechanism.** `VkFitsImagePipeline.RecordSplitImageDraw(...)`: grow the descriptor pool, allocate a second UBO set + a second sampler set, set/restore scissor around two draws. `ViewerState.SplitFraction` (0-1, `null` = off). | DONE |
| P1 | **DONE. Uniform A/B.** "Pin current view" snapshots the live `StretchUniforms` into the B slot; the divider then compares pinned-vs-live across every uniform-driven control. Zero pixel cost, so no policy needed. Divider drag + keyboard toggle in `ImageRendererBase` (the existing `PixelWidgetBase` track-slider drag model, registered as its own hit band with a resize cursor). | DONE |
| P2 | **DONE. Enhance A/B by handle transfer.** `TryApplyPendingEnhance` moves the current texture handles into the before-slot instead of letting the upload overwrite them, and the enhanced image uploads into a fresh set. The cheap keep. | DONE |
| P3 | **PARTIAL. The cache policy.** Reload-on-press as the fallback path (off the render thread, adopted via the existing `Task<T>` poll hand-off), the `GC.GetGCMemoryInfo` pre-check, `GC.AddMemoryPressure` while held, the catch-and-fall-back on allocation failure, and the eviction triggers. **Reload-on-press is the one piece NOT built** -- so today a dropped cache means the comparison is unavailable rather than restorable, which is safe but not a cache in the full sense. Everything else shipped. | PARTIAL |
| P2a | **DONE. Invalidation, which lands with P2 and not after it.** The before-slot's document-identity token, the draw-time check that drops `SplitFraction`, and the free routed through `ReleaseCompletedTasks`. Without it P2 can draw two unrelated images side by side, and a document open whose dimensions differ leaks the before set on every open. | DONE |
| P4 | **Halve it (optional).** `R16_SFLOAT` for the before textures: ~50 MB instead of 99.5 MB. It is a comparison view, not a master, so precision loss is tolerable in principle -- but an aggressive stretch has enormous gain in the shadows, so this needs a look for banding before it is adopted. Measure, do not assume. | NOT STARTED |
| P5 | **More consumers (deferred).** The GUI's viewer tab and the Live Session preview host the same renderer, so they inherit P1 for free once the divider is in `ImageRendererBase`. RAW-vs-STACK (`ShowStacked`, `K`) is a natural pixel-A/B consumer and currently costs a full re-upload per toggle. | NOT STARTED |

## The split is a control, not state plus handlers

The first implementation put five fields on `ViewerState` (`SplitFraction`, `IsDraggingSplit`,
`SplitCompare`, `PinnedRendition`, `PinRenditionRequested`) and a press / move / release branch in the
press dispatcher. It did not work, and the way it failed is the argument for the shape that replaced it.

**The viewer has TWO press dispatchers.** The embedded host routes presses through `HandleInput`;
`tianwen-fits`'s `Program.cs` has its own, because opening a toolbar dropdown and running a DI-backed
action are host concerns. Everything after that branch was a verbatim copy. The split's press branch went
into one of them, so the divider drew, stated a resize cursor, and could not be dragged in the standalone
viewer -- with nothing to connect the two copies and nothing to notice.

`SplitCompareController` owns the position, the drag, the mode, the pinned rendition and the retained
pixels' generation. Consequences worth keeping:

- **The press needs no branch anywhere.** The divider registers its region with an `onClick` that arms
  the control's own drag, from the same rect it just painted -- so "draw == hit" becomes "draw == drag".
- **Motion and release are ONE routed line**, in the one method both hosts already forward to.
- **`ViewerState` gains nothing.** The only field left is `SourceGeneration`, which is about the source
  rather than the split.
- **It is testable without a host**: press the region through `HitTestAndDispatch`, then drive
  `HandleInput` -- which is exactly what `TheDividerArmsItsOwnDragSoNoHostNeedsAPressBranch` does.

The four older drag flags beside it (`IsResizingFileList`, `WhiteBalanceDragChannel`, `WaveletDragBand`,
`IsScrubbing`) are the same shape and are the remaining conversions; the file-list divider additionally
needs a `dividerOnClick` on `Layout.Builder.Split`, since DIR.Lib paints that one.

## The button says when it cannot compare (#1883)

A planetary view has no pre-enhance pixels, no crop and no pin, so A | B used to be a press that drew nothing.
`SplitCompareController.AvailabilityOf(mode, hasBeforePixels, hasCrop)` is the ONE availability rule per mode
(`BeforePixels` needs retained pixels, `CropExtent` a crop, `PinnedSettings` a pin or a pending pin) and answers
available or a plain-language reason. `ResolveDividerX` (is a divider drawn) and `ButtonState` (what a press
would do: `ModeOnPress` picks by `Toggle`'s own precedence) both read it, so the divider and the button cannot
disagree. When nothing differs, the button is drawn in the toolbar's disabled look with the reason as its
declared tooltip, and stays pressable, since a press still pins; the reason says to move a dial after pinning,
enhance or crop. Pinned by `ViewerSplitTests` at DPI 1 and 1.5. The planetary modes themselves (sharpened vs
unsharpened, raw vs stack, Live vs Full) are the rest of #1883.

## A planetary stack compares the sharpening: Unsharpened | Sharpened (#1883)

On a planetary stack view, Live or Full (a `LiveStackPreviewSource`, which is what implements `IUnsharpenedPreview`), the sharpening is the
enhance, and the question A | B answers is what it did. `SplitCompare.Unsharpened` is the mode `Toggle` picks there, ahead of retained
before-pixels and pinned settings, whenever sharpening is on (`SplitCompareController.StackCompares`). Its left half is the master as
stacked, its right the sharpened one on show, **both under the live display settings**, so the Enhance dials move only the right half's
pixels. Labels: "Unsharpened" and "Sharpened".

- **It reuses the before slots, and only those.** The unsharpened master goes into them through `TryUploadBeforeImageChannel`
  (`VkFitsImagePipeline.TryUploadBeforeChannel`), from `PrepareFrame` and nowhere else. A same-size master is overwritten in place as a
  live upload is; a different size gets a new texture and the old one goes through `VulkanContext.DeferDestroy`. A set the pipeline
  did not upload (an enhance's retained pixels) is released first, never written over (its format may be 8-bit).
- **It is uploaded once per master, never per dial move.** `IUnsharpenedPreview.UnsharpenedGeneration` moves only when the stack
  publishes a new master; `SetSharpen` re-sharpens off the cached raw master and leaves the unsharpened document, and its generation,
  alone. The widget keys its upload on (source, generation, channel view), so a Live to Full switch, which may count generations
  alike, still uploads.
- **It is built only while wanted.** `WantUnsharpened(true)` is said each frame the split is on in this mode; with it off, or the
  view off the stack, the stack lets its copy go and the before slots are released. Its scale is the sharpened master's divisor
  (`ScaleToFullScaleInPlace(display.UnitScaleDivisor)`), so the halves differ by the sharpening and not by a peak the sharpening moved.
- **It says when it compares nothing** (the section above): sharpening off is a dim button with the reason as its tooltip, and the
  view leaving the stack (Raw) turns the split off, as a stale before is dropped.
- **Known limit:** the unsharpened master is the stack as it came, before the colour alignment and the limb drawing that belong to the
  sharpened view (`PlanetaryLiveLimb`), so a colour master's halves can differ by those as well as by the wavelets.

## A planetary stack compares what stacking bought: Raw | Stack (#1883)

`SplitCompare.RawVsStack`: the left half is the capture's frame at the playhead, the right the stack, both under the live display
settings. It needs the stack's frames in hand, so only a stack over a capture's own SER offers it (`IRawFramePreview.CanShowRaw`: a
`StackedMasters`, never a node's masters nor a master opened as a file, whose dim option says so). A press picks it by itself only
where the sharpening is off and there is nothing else to compare; with sharpening on, Unsharpened stays the default.

- **The disk is placed by the stack's own registration, never a centre of mass.** `PlanetaryRawFrame.LoadPlacedAsync` loads the frame,
  demosaics it as a master is (`PlanetaryMaster.MergeAndDemosaicAsync`, so a split-CFA frame lands on the master's full grid), and
  registers it onto the stack's master with `GlobalAligner` (`FromReference` over the master, `Estimate` of the frame): the call
  `RollingWindowStacker` makes for every frame it folds, tile size and correlation choice included. The frame is sampled at
  `(x + Dx, y + Dy)`, so it is DRAWN `(-Dx, -Dy)` from the stack's origin (`IRawFramePreview.RawOffset`). A frame whose size is not
  the master's is not placed (no common grid), and the option dims with the frames-not-in-hand reason.
- **One draw path.** The comparison quad is the live quad with its origin moved by `SplitCompareController.ComparisonOffset`
  times the scale; the left half is then bounded to the live quad as the crop case is. Same size, same scale, only the origin moves.
- **Built once per (frame, stack), only while wanted.** `LiveStackPreviewSource.WantRaw` loads off the render thread in a task of its
  own (apart from the stack task, which it never preempts), keyed on (playhead, stacks published); a frame that cannot be placed is
  asked again only when the playhead or the stack moves. `RawGeneration` moves per published frame, and the widget re-uploads into the
  before slots on a new generation only. The frame is put on the scale the display master is shown on (`Built.DisplayDivisor`).
  Disposal waits for this task as it does for the stack's, since both read the stream.
- **The choice** is a `Layout.Builder.ButtonGroup` under the labels, declared only while the split is on over a stack view
  (`RenderPlanetaryChoice`): "Sharpening" (Unsharpened | Sharpened) and "Raw | Stack". An option that cannot compare is dimmed with its
  `AvailabilityOf` reason, the rule the divider and the A | B button read. The choice is the controller's `Mode`, per viewer, never saved.
- **Known limits:** the raw frame is demosaiced on the CPU as a master is (MHC), where the viewer's Raw view runs the GPU demosaic of
  the chosen algorithm, so the two can differ in fine colour; and the stack on show lags the playhead by one stack, so while the
  playhead plays the frame is registered against the master that is on show, not the one being built.

## Invariants (set now, before code exists)

- **The before is a texture set, not a document.** Never keep a second `AstroImageDocument` alive
  for comparison; it doubles the cost and every panel already reads the displayed one.
- **No shader edits.** If a step appears to need one, the design has drifted off the scissored-draw
  mechanism -- reconsider before re-baking SPIR-V.
- **Intersect the scissor, never replace it.** The image draw shares a command buffer with chrome
  that may already be clipping; the split rect is `existing INTERSECT half`, and the previous
  scissor is restored after the second draw.
- **The split is display state only.** It never writes a file and never changes what save/export
  emits. Same rule as the masked finishing boost being a render stage.
- **Never block the render thread on a reload.** The press path goes through the established
  `Task<T>` poll hand-off (`TryApplyPendingEnhance` / `TryApplyPendingStarBuild`), not an await on
  the loop and not a lock.
- **Eviction is always safe.** Dropping the before must degrade to reload-on-press, never to a
  broken or half-drawn view. Anything that cannot survive losing the before does not belong here.
- **The split's state lives on its control, never on `ViewerState`.** A flag there buys a branch in
  every press dispatcher, and there are two.
- **A before belongs to exactly one document, and the draw proves it.** Identity is checked where
  the comparison is drawn, not maintained by clearing a flag on each path that swaps the image.
- **GPU objects are freed on the render thread's post-frame drain**, never at the point the swap was
  noticed -- that point is a thread-pool continuation, and the handles may still be referenced by an
  in-flight command buffer.
- **Peak matters more than steady state.** On a UMA box the GPU allocation competes with the same
  pool as the CPU decode, so a design that transiently doubles is worse than one that holds a
  flat extra -- which is the whole argument for handle transfer over reload.

## Open questions (decide at the phase, not now)

- **Does P4's half-float band?** Deep shadows under a hard MTF are where it would show. Render the
  same frame both ways and diff, rather than reasoning about it.
- **What is "before" for the uniform A/B: pinned snapshot or defaults?** Leaning pinned ("pin, then
  fiddle") because comparing against defaults answers a question nobody asks twice.
- **Does the divider carry labels?** A "before"/"after" caption is useful the first time and noise
  afterwards. Possibly fade after the first drag.
- **Does `ShowStacked` become a slider or stay a toggle?** It is the one existing pixel pair, and
  folding it in would remove a re-upload per press -- but it changes a shipped keybinding's
  behaviour, so it is a P5 decision, not a P2 one.

## Related

- [rc-astro-enhancers.md](rc-astro-enhancers.md) -- the enhance path whose one-way swap prompted this.
- [multi-source-previewer.md](multi-source-previewer.md) -- the viewer this lands in, and the
  `IPreviewSource` model the live sources use.
- [gpu-stretch-tests.md](gpu-stretch-tests.md) -- the GPU readback harness P0's test should join.
- [layout-driven-ui.md](layout-driven-ui.md) -- the divider is an interactive control, so it keeps
  its own arranged rect under that plan's DoD.

## A capture's two stacks compare: Live | Full (#1883)

`SplitCompare.LiveVsFull`: the Live rolling stack against the Full stack (the Auto view's, #1877's names). Whichever is on show is the right
half, the other the left, so from the Live view the labels read "Full | Live" and from the Full view "Live | Full". Both are sharpened by
the same routine with the dials on show (`SetSharpen`, handed to the other layer by `ViewerController.TickPeers`; never a second
sharpening path), under the live display settings.

### The three planetary comparisons and how each is chosen

| Comparison | Left | Right | Offered when | Chosen |
|---|---|---|---|---|
| Sharpening (`Unsharpened`) | master as stacked | sharpened master | a stack is on show with sharpening on | the default of a press |
| Raw \| Stack (`RawVsStack`) | the frame at the playhead | the stack | the stack is over a capture's own frames | the row, or a press where nothing else compares |
| Live \| Full (`LiveVsFull`) | the other stack | the stack on show | the other stack exists | the row only |

The row is a `Layout.Builder.ButtonGroup` under the half labels (`RenderPlanetaryChoice`), declared only while the split is on over a stack
view. An option that cannot compare is dimmed with its `AvailabilityOf` reason, the one rule the divider and the A | B button read; Live | Full
says to build the Full stack (Auto, Shift+K) or open the capture where its Full master sits beside it.

- **"Exists" means built this session, or beside the SER.** The Auto view's layer counts once built; otherwise
  `PlanetaryAuto.OutputPaths(dir, name).Master` beside the SER counts, probed once per capture off the render thread (never a `File.Exists`
  per frame). Choosing the option with only the file in hand reads that master as a layer of its own (`FixedMaster`, off the render thread,
  held beside the Auto view's and dropped with the capture); the divider waits for it as it waits for any second image.
- **The disks coincide by the same placement rule as Raw | Stack**: `PlanetaryRawFrame.RegisterOnto` (`GlobalAligner` of the other
  master's disk onto this one's, the registration a stack gives a frame), never a centre of mass. It reads the two linear masters, which
  need not be the same size (a Full master is cropped to where 0.95 of its frames reached), so the left quad is drawn at its own size, its
  origin moved by `ComparisonOffset`, and bounded to the live quad.
- **Built once per change.** `LiveStackPreviewSource.WantPeer` registers off the render thread, keyed on (this stack's stacks published,
  the other's); `PeerGeneration` moves when the other layer publishes (a new stack, or a re-sharpen as a dial moves) and the widget
  re-uploads into the before slots on a new generation only. Nothing is placed, held or driven while the comparison is not chosen.
- **Known limits:** the two masters are each shown on their own display scale, so a Live and a Full master that differ in peak level are
  not matched in brightness beyond what the shared stretch does; the Full master read from its file is the linear master, so its sharpening
  uses the dials on show and not the derivation its run made.
