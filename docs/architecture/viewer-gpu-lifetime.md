# Viewer GPU resource lifetime: uploads, destroys, resizes, cached layers

Why the viewer's Vulkan resources are managed the way they are, and the incidents that decided it.
`CLAUDE.md` keeps the rules; this file keeps the evidence. Companion:
[`../plans/damage-based-repaint.md`](../plans/damage-based-repaint.md) (the damage model and the
cached-layer geometry), and SdlVulkan.Renderer's own `docs/deferred-destroy-adoption.md`.

## A document may change only BETWEEN frames, and its textures upload in `PrepareFrame`

Before anything in the frame samples them. A swap recreates the channel textures, and the recreate
destroys the old views after draining PRIOR frames; it cannot un-record what THIS frame's command
buffer already holds.

The upload used to run from each host's render callback (`Program.cs` `OnRender`, `GuiderTab`,
`LiveSessionTab`, `VkPlanetaryTab`), AFTER the cached-layer pre-pass had bound the old views: the frame
went to the GPU with a dangling view and the GPU faulted. That is the `nvlddmkm 153` pair plus
`LiveKernelEvent 141` watchdog that killed the Store viewer on 2026-08-27 (two loads of differently
sized files 1.3 s apart), reproduced at HEAD under `SDLVK_VALIDATION=1` as
"`vkCmdBindDescriptorSets(): ... invalid state ... VkImageView was destroyed`" followed by
`VK_ERROR_DEVICE_LOST`, with the same driver signature to the second.

The standalone host also SWAPPED the document inside `OnRender` (completions, file request, enhance
result); those steps now run in `loop.OnBeforeFrame`, before `BeginFrame`, and a swap forces full damage
there. **Never call `UploadDocumentTextures` from a render callback again; `PrepareFrame` is the one
path.** Pinned by `ANewDocumentIsUploadedBeforeTheLayerPassThatSamplesIt`.

Two more things that validation round found:

- The earlier `nvlddmkm 153`s that month were TianWen GPU benchmarks (08-09) and the sleep transition
  (08-23/24), not the driver at rest.
- The damage pass's `loadOp LOAD` needed COLOR_ATTACHMENT_READ on the **SHARED** external dependency
  (SdlVulkan.Renderer `FillSubpassDependencies`), shared because dependencies are not exempt from
  render-pass compatibility -- widening only the LOAD pass tripped VUID 00904/02684 on every partial
  frame.

**Run the viewer under `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` and read `validation_report` after
driving it** whenever GPU resource lifetime is touched; that is what turned a watchdog dump into a
named line of code. (And `validation_report` with zero messages is evidence only when `active` is true.)

## Since SdlVulkan.Renderer 7.28 the class is closed structurally, not by call order

`VkFitsImagePipeline` hands every channel, before and histogram texture to
`VulkanContext.DeferDestroy`, which destroys it once every frame that could reference it has retired
(no drain, so no render-thread stall per document swap). Its sampler descriptor sets are **one per
frame in flight**, rewritten at draw time when the views' stamp moved (`EnsureSamplerSet`), because a
set a pending frame holds may not be written. `VkTexture.Dispose` defers too.

**Never destroy a Vulkan object a frame may have bound directly, and never write a single shared
descriptor set from an upload path.**

## A window resize is a distinct GPU-lifetime path from a document swap

Drive maximize/restore, not just file loads. The swapchain-teardown race a resize exposes was invisible
to the viewer's file-load validation and surfaced only under GUI resize testing: `RecreateSwapchain`
destroyed the swapchain and its per-image present semaphores while `vkQueuePresentKHR` was still in
flight, because the bounded fence drain (`TryDrainDevice`) only ever waited on the graphics-SUBMIT
fences and present is gated by no fence. 10 messages over 5 recreations
(VUID-vkDestroySwapchainKHR-swapchain-01282 / VUID-vkDestroySemaphore-semaphore-05149); benign on
desktop NVIDIA, a rejected `vkQueueSubmit` on Adreno.

Fixed in **SdlVulkan.Renderer 7.29** (`FlushPresentQueueAfterDrain`: a `vkQueueWaitIdle` after a
*successful* drain, skipped on a wedged-GPU drain timeout so the no-hang-on-resize property
`TryDrainDevice` exists for still holds).

## The cached image layer samples in TEXTURE space

And a fixed-capacity target is not the size you asked for this frame. `VulkanContext.CachedLayer`
allocates once and answers any smaller request out of the same texture, so after the pane shrinks the
layer occupies the top-left of something larger; UVs divided by the requested layer size then sample
`capacity / request` more texels than the pane holds, and the image draws squashed by that factor and
shifted by the margin's share (0.721x and 99 px on a 2957/2132 px pair).

That was "the picture compresses when I widen the file list", reproduced at HEAD by dragging the divider
with the inspector and gone at the width where the capacity was allocated. The seam reports the capacity
(`TryEnsureCachedLayerTargets(w, h, out capW, out capH)`), the base divides by it and keys the slot on
it. Pinned by `TheBlitSamplesInTextureSpaceWhenTheTargetIsLargerThanTheLayer`; write-up in
[`../plans/damage-based-repaint.md`](../plans/damage-based-repaint.md).
