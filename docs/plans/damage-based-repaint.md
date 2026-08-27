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
