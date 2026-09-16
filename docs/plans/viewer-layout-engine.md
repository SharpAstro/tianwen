# The chrome should not be doing its own arithmetic

Status: NOT STARTED, HIGH PRIORITY (user, 2026-09-15). Scope measured the same day; no phase started.
**P0 (the measure seam) now ships as part of [dir-lib-10.md](dir-lib-10.md) D1**, the same DIR.Lib release
that adds the input router; P1 to P4 are unchanged and are that plan's T3.
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
| `MeasureText` outside the engine | 47 call sites in 16 files on 2026-09-15 morning; **55 in 16** by the same evening's recount in [dir-lib-10.md](dir-lib-10.md) (45 / 6 / 4 across Abstractions / Shared / Gui) | `ImageRendererBase.cs` (11), `VkSkyMapTab.cs` (6), `ImageRendererBase.Toolbar.cs` (6), `.WhiteBalancePanel.cs` (5) |
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

**DONE (shipped in DIR.Lib 9.2), and ADOPTED 2026-09-16.** The seam existed but the two panels it was
added for still built a `PixelMeasureContext` by hand -- the "second statement of the font and the scale"
its own remarks warn about. Both now call `MeasureContext()`; there is no `new PixelMeasureContext` left
in tianwen.

`Layout.Engine.Measure` is public but `PixelWidgetBase.DefaultContext` is private, so a consumer that
wants "how big is this tree" has to build a `PixelMeasureContext` by hand (which is what the tone panel
does today). Add `protected Size<float> MeasureLayout(Layout.Node root, Size<float> available)` beside
`ArrangeLayout`, and a `protected PixelMeasureContext<TSurface> MeasureContext()` for the case where
measure, arrange and paint must share one context.

**This is a DIR.Lib change, so it rides the release chain** (`/release-lib DIR.Lib` then Console.Lib
and SdlVulkan.Renderer, then the pin here). Nothing else in this plan needs a sibling release.

### P1: the popovers and the info strip (MEDIUM)

**DONE 2026-09-16.** The white-balance panel and both sweeping tests were already converted by T2 -- they
read arranged regions now, not a 900x700 pixel scan. What remained was the wavelet block in
`InfoPanel.cs`, and it was the whole smell at once: a `ref float y` cursor, `MeasureText` per button
width, and `FillRect` + `DrawText` + `RegisterClickable` written separately so the drawn rect and the hit
rect could drift. It is `Content.Slider` now, and the drag machinery behind it -- `WaveletSliderHit`
(the type), `ViewerState.WaveletDragBand`, `BeginWaveletDragAt`, `WaveletTrackRect`,
`UpdateWaveletDrag`, and four dispatcher blocks across two hosts -- is deleted rather than moved.
`InfoPanel.cs`: `MeasureText` 4 -> 0, `RegisterClickable` 2 -> 0.

`ImageRendererBase.WhiteBalancePanel.cs` and `ImageRendererBase.InfoPanel.cs`, the two that already
have a tree-shaped body and a hand-summed box. The white-balance panel is the direct analogue of the
tone popover and should end up looking like it: one `BuildWhiteBalanceTree`, the box from the measure,
the sliders as keyed `Fill` leaves, `ResetLabels` / `SpccLabels` replaced by `widthSample:` on the
button nodes. **Its test then stops sweeping**, which is the acceptance test for this phase: delete the
window scan, read arranged nodes, and the suite must still be green.

### P2: the toolbar, which is where `ReservedLabelWidth` dies (MEDIUM, the visible win)

**Layout half DONE 2026-09-16.** The run is a flow the engine lays out; `WalkToolbarRows` and the
`_toolbarSlots` cache are gone, and all 16 `ViewerToolbarLayoutTests` pass UNCHANGED. It needed three
additions to `Node.Wrap`, because the bar was hand-writing three things the engine could not say:

| the bar's rule | now |
|---|---|
| only row one stops short of the help button | `FirstLineReserve` |
| a group gap that does not lead a wrapped row | `LeadingGap` (on the child) |
| past two rows the tail is DROPPED, not clipped | `MaxLines` |

**None of the three is a `Dock`, and that is the point.** A dock reserves its strip on EVERY line, so
the wrapped row narrows and buttons that fit today start being dropped --
`AnOrdinaryWindowWrapsToASecondRowInsteadOfDroppingButtons` is the pin that catches it. `MaxLines`
drops rather than clips for the same reason a dropped button must not be merely invisible: a clipped
child still registers its region and keeps taking the clicks aimed at whatever covers it.

**`ReservedLabelWidth` is still there, and the reason is a conflict this phase resolved by measuring.**
This document says deleting it follows from every button being at its widest state. The method's own
remarks say the opposite -- that "reserving room for every variant would spend width the run does not
have". Probed directly: adding **+20 px to every button** (about 300 px across the run) drops nothing at
the 1150 px ordinary window. **So the width objection is wrong and this document is right**; the run has
headroom.

What remains before it can go is therefore not width. It is a DEPENDENCY, and naming it that way
matters, because this was queued for a while as a judgement call the user had to make and it is not one:

- **The buttons are not nodes yet, so there is nowhere to put the replacement.** P2's layout half put
  the RUN's placement on the engine; each button is still hand-painted (`DrawText` + `RegisterClickable`,
  `ImageRendererBase.Toolbar.cs:535`). `Text.WidthSample` is the engine's own spelling of "measure this
  as if it held that string", and this document's own inventory lists `ReservedLabelWidth` as the
  hand-rolled version of it -- but a sample goes ON a node, and there is no node. Deleting the
  reservation before the buttons are declared removes the workaround and restores the flicker, which is
  strictly worse than either end state.
- **A widest-state sample per action, and not all of them enumerate.** `Stars` is a star count,
  `StretchParams` a parameter string, `Zoom` a percentage -- these need a representative `widthSample`
  ("99999"), not a union over a list. Zoom and Enhance, the two the reservation already covers, are the
  enumerable ones. Note this FAILS SOFT: the width is `max(measured, reserved)`, so a sample that turns
  out too small only means that button drifts again, never that a label is clipped.
- **It is still a visible change**, since every button with a varying label gets permanently wider to
  hold a state it is not in. Worth saying out loud when it lands -- but it arrives as a consequence of
  declaring the buttons, not as a separate decision to take first.

Moving the layout to the engine did NOT make the reservation unnecessary on its own: the run is still
left-packed, so a width change still shoves its neighbours, and the reservation is still what holds the
two noticed buttons steady.

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

**Re-counted 2026-09-16: the named targets are already clear.** `LiveSessionTab.Polar.cs` (7 cursor
lines when this was written), `.Panels.cs`, `.Flats.cs` and `GuiderTab.cs` no longer call `MeasureText`
or carry a `ref float y` at all -- earlier waves took them. What is actually left, ranked:

| file | `MeasureText` | `ref float y` |
|---|---|---|
| `ImageRendererBase.cs` | 11 | 5 |
| `VkSkyMapTab.cs` | 6 | 0 |
| `ImageRendererBase.Toolbar.cs` | 6 | 0 |
| `ImageRendererBase.Overlays.cs` | 5 | 0 |
| the other nine | 1-3 each | 0 |

So P3's remaining scope is the viewer's own core file and the sky-map tab, not the live-session panels.
Still lower value and still piecemeal -- and now ratcheted by P4, so it cannot quietly grow while it
waits.

`LiveSessionTab.Polar.cs` (7 cursor lines), `.Panels.cs`, `.Flats.cs`, `GuiderTab.cs`, `VkSkyMapTab.cs`
(6 measurements). These are reports rather than controls, so the drift costs less and nothing has been
reported against them; they come last and can come piecemeal.

### P4: a guard, so it does not come back (SMALL)

**DONE 2026-09-16** -- `ChromeMeasuresThroughTheEngineTests`. It could not demand zero (46 calls across
13 files), so it is a RATCHET: a per-file allowance that fails on any increase, a hard refusal for any
NEW file, and a third test that fails when an allowance is no longer earned -- debt that has been paid,
where leaving the number behind lets it be re-spent silently. Verified to bite both ways before being
trusted.

A test over the widget assemblies asserting that a file which paints chrome does not call `MeasureText`
-- allow-listed for the genuine escape hatches (an overlay label placed against a star's ellipse, the
file list's ellipsis budget, anything inside a `drawFill`). Without it the count goes back up one
convenient call at a time, which is how it got to 47.

## The unit rule this uncovered, which is the cost of adopting the engine

**A declared tree is authored in DESIGN units.** The measure context turns them into device pixels,
so a value that is ALREADY device pixels gets the scale applied a second time. The viewer's
`FontSize` is `BaseFontSize * DpiScale`, and P0/P1 handed it straight to a tree measured through
`MeasureContext()`, whose scale is `DpiScale`: the popover text rendered at
`BaseFontSize * DpiScale^2`.

Measured at 2x DPI, before the fix: the tone popover was **3.85x wider and 3.26x taller** than at 1x
instead of 2x. The two ratios differ because the gaps beside the text (`ToneGap`, `WbGap`,
`WaveletGap`) are plain constants and scaled only ONCE -- so every part of the panel was internally
consistent, it simply did not match the chrome around it. `.Pad(PanelPadding / Scale.X)` was the same
fact half-discovered: the padding had already been divided back out by hand and the fonts left alone.

**Every viewer test runs at `DpiScale = 1f`, where squaring the scale is the identity.** That is why
it shipped, and why the regression test (`ThePanelScalesLinearlyWithTheDpiScale`) renders at 1x AND
2x -- a single scale can never see it.

What this means for the phases still to come: converting a hand-laid panel is not only "move the
arithmetic onto the tree". The constants the old painter used are device pixels by the time they
reach it, and each one has to go back to its `Base*` form on the way onto a node. The exception is a
tree arranged at `DesignScale.One` -- which is what `ImageRendererBase.Toolbar.cs` does, because
every measurement in that file is already device pixels and there is no `Base*` form to return to.

A sweep for the rest of it -- every property whose body multiplies by `DpiScale`, matched against the
extents handed to declared nodes -- finds no other site. `SessionConfigStyle` already passes
`BaseFontSize`, which is the shape to copy.

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
