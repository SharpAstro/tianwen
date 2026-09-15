# The chrome should not be doing its own arithmetic

Status: NOT STARTED, HIGH PRIORITY (user, 2026-09-15). Scope measured the same day; no phase started.
The tone popover (`ImageRendererBase.TonePanel.cs`, shipped 8.1) is the worked example of the target
shape and the only viewer overlay already in it.

## The problem, in the user's words

> "that thing where we widen all buttons to avoid flicker is a bit of a hack but i do see it helps
> keeping mouse clicks stable so we can keep it. I just don't like its actually just a work-around"

> "there's still a lot of box calc going on for my tests"

> "its not just cursor += h, but box width calc, non-DIR.Lib etc side text measurements, unions of all
> possible values, etc, all should be handled by the engine"

> "no need to fix this all now but we should have a plan for this, high prio"

## What the engine already does

`DIR.Lib.Layout` measures and places a tree of `Layout.Node`, and `PixelWidgetBase` binds each node's
`Hit` to the rect the engine arranged for it, so **draw position and hit region cannot drift**. It
already owns every part of what the chrome is doing by hand:

| the chrome does | the engine offers |
|---|---|
| `y += rowH + gap` down a panel | `VStack(...).WithGap(g)` |
| summing a box height from the same constants the body draws with | `Layout.Engine.Measure(tree, available, ctx)` |
| `MeasureText(label, size)` to place the next thing | intrinsic `Auto` sizing, `Star(weight, min, max)` |
| a union of every label a control can show | `widthSample:` on the node |
| `RegisterClickable(x, y, w, h, hit, onClick)` beside a `DrawText` at the same numbers | `.Clickable(hit, onClick, cursor)` on the node |
| ellipsising against a budget worked out at the call site | `TextTrim` on the run |

## The four smells, counted 2026-09-15

Across `TianWen.UI.Abstractions`, `TianWen.UI.Shared` and `TianWen.UI.Gui`:

| smell | count | where the worst of it is |
|---|---|---|
| `MeasureText` outside the engine | 47 call sites in 16 files | `ImageRendererBase.cs` (11), `VkSkyMapTab.cs` (6), `ImageRendererBase.Toolbar.cs` (6), `.WhiteBalancePanel.cs` (5) |
| hand-advanced cursor (`y +=`, `ref float y`) | 31 lines in 8 files | `ImageRendererBase.cs` (9), `LiveSessionTab.Polar.cs` (7), `.InfoPanel.cs` (5) |
| width union over every possible value | 3 | `ReservedLabelWidth` (Zoom, Enhance), `ResetLabels`, `SpccLabels` |
| a test that SWEEPS the surface to find regions | 2 files | `ViewerWhiteBalancePopoverTests`, `ViewerInfoPanelCollapseTests` |

Adoption for contrast: 22 of 165 files in `TianWen.UI.Abstractions` call `RenderLayout` /
`ArrangeLayout` / `PaintLayout` at all. The Equipment tab, the planner frame and the Home board are on
the engine; the viewer chrome, the live-session panels and the sky-map tab are not.

## Why it is worth doing, measured rather than argued

Two bugs, both of the same shape, both found by eye rather than by a test:

- **The toolbar drift.** The bar is one left-packed run, so a button's x is the sum of every width
  before it. A wheel zoom walked the Zoom label across "Fit", a ratio and a percentage, and **Crop,
  Overlays, Stars and Enhance each moved by exactly 26.4 px**, measured from two inspector snapshots
  one zoom apart. `ReservedLabelWidth` freezes the inputs to that sum for the two buttons someone
  noticed; every other stateful label still moves the run, just less often.
- **A line out of its own box.** The tone popover's first draft sized itself from a hand-kept list of
  the strings it might show, and the soft-clip heading was not on the list, so the heading ran past the
  right edge of the panel drawing it. Rebuilt as a tree, the box IS the measure and the case cannot
  arise. Pinned by `ViewerTonePopoverTests.EveryArrangedNodeFitsInsideThePanelAndThePanelFitsOnScreen`.

And the cost the user actually named: **a hand-laid-out panel forces its test to redo the arithmetic**.
`ViewerWhiteBalancePopoverTests` sweeps 900 x 700 pixels asking `HitTest` at every point, because there
is no other way to learn where the panel put anything -- and its own remarks record a day lost to that
scan guessing a column. The tone tests ask `ToneLayoutForTest` instead and name nodes.

## Phasing

Each phase is independently landable and independently useful; none is a big-bang rewrite.

### P0: a measure seam on the widget base (SMALL, unblocks the rest)

`Layout.Engine.Measure` is public but `PixelWidgetBase.DefaultContext` is private, so a consumer that
wants "how big is this tree" has to build a `PixelMeasureContext` by hand (which is what the tone panel
does today). Add `protected Size<float> MeasureLayout(Layout.Node root, Size<float> available)` beside
`ArrangeLayout`, and a `protected PixelMeasureContext<TSurface> MeasureContext()` for the case where
measure, arrange and paint must share one context.

**This is a DIR.Lib change, so it rides the release chain** (`/release-lib DIR.Lib` then Console.Lib
and SdlVulkan.Renderer, then the pin here). Nothing else in this plan needs a sibling release.

### P1: the popovers and the info strip (MEDIUM)

`ImageRendererBase.WhiteBalancePanel.cs` and `ImageRendererBase.InfoPanel.cs`, the two that already
have a tree-shaped body and a hand-summed box. The white-balance panel is the direct analogue of the
tone popover and should end up looking like it: one `BuildWhiteBalanceTree`, the box from the measure,
the sliders as keyed `Fill` leaves, `ResetLabels` / `SpccLabels` replaced by `widthSample:` on the
button nodes. **Its test then stops sweeping**, which is the acceptance test for this phase: delete the
window scan, read arranged nodes, and the suite must still be green.

### P2: the toolbar, which is where `ReservedLabelWidth` dies (MEDIUM, the visible win)

Lay the bar out as a `WrapH` of buttons (it already wraps by hand on a narrow window) with each button
a node carrying its own `widthSample` -- its widest state, stated once, on the node, rather than
computed in a switch. Two consequences worth stating in advance:

- **`ReservedLabelWidth` is then unnecessary rather than merely narrow.** The reservation exists to
  stop a width change propagating; if every button's width is already its widest state, nothing
  propagates and the workaround has nothing to do. Delete it in the same commit; leaving it would be
  two mechanisms for one rule.
- **`ViewerToolbarLayoutTests` is the pin and must stay green unchanged.** It already asserts that the
  help button does not move when a neighbour relabels -- the exact property this phase makes
  structural. If it needs editing to pass, that is a signal the layout changed meaning, not a chore.

### P3: the live-session panels and the sky-map tab (LARGER, lower value)

`LiveSessionTab.Polar.cs` (7 cursor lines), `.Panels.cs`, `.Flats.cs`, `GuiderTab.cs`, `VkSkyMapTab.cs`
(6 measurements). These are reports rather than controls, so the drift costs less and nothing has been
reported against them; they come last and can come piecemeal.

### P4: a guard, so it does not come back (SMALL)

A test over the widget assemblies asserting that a file which paints chrome does not call `MeasureText`
-- allow-listed for the genuine escape hatches (an overlay label placed against a star's ellipse, the
file list's ellipsis budget, anything inside a `drawFill`). Without it the count goes back up one
convenient call at a time, which is how it got to 47.

## What this does NOT do

- **It does not touch the picture.** Every number in the image pipeline, the stretch and the overlays
  is unaffected; this is about where chrome rectangles come from.
- **It does not make the engine draw the sliders, the sky map or the chart.** Those stay `Fill` leaves
  the engine places and the widget paints, which is what the escape hatch is for.
- **It does not chase the 47 call sites for their own sake.** A measurement inside a `drawFill`, or one
  placing a label against a rotated ellipse on the image, is not chrome arithmetic and is fine.
- **It is not a visual redesign.** Every phase should be pixel-boring; the tone popover was the one
  place a layout change was also a design change, and that shipped with 8.1.

## Where the pieces are

| piece | where |
|---|---|
| the engine | `DIR.Lib.Layout` (`Builder`, `Node`, `Engine.Measure` / `Engine.Arrange`) |
| the widget seam | `DIR.Lib.PixelWidgetBase` (`ArrangeLayout`, `PaintLayout`, `RenderLayout`) |
| the worked example | `TianWen.UI.Abstractions/ImageRendererBase.TonePanel.cs` + `ViewerTonePopoverTests` |
| the workaround to delete | `ImageRendererBase.Toolbar.cs`, `ReservedLabelWidth` |
| the panels to convert | `ImageRendererBase.WhiteBalancePanel.cs`, `.InfoPanel.cs`, `LiveSessionTab.*` |
| the rules | `CLAUDE.md`, "Layout DSL"; the five traps are listed there and all still apply |
