# Tab strips: one description, three surfaces

**Status: T1-T7 SHIPPED** (DIR.Lib `feat/tab-strip-items` + tianwen `feat/tab-strip-sidebar`, both
unpushed; DIR.Lib 8.3). Only T8 remains, and it is the phase the plan expected never to happen. This
is the design and the phasing for making TianWen's navigation sidebar a first-class DIR.Lib citizen
instead of hand-drawn chrome.

**Three implementations are one.** `TabStripTree.Build` describes the strip as a `Layout.Node` tree;
`TabBar` paints it through `PaintLayout` and `TuiTabBar` through `CellLayout`, and TianWen's sidebar is
a configured `TabBar` (`RenderSidebar` deleted). What differs between surfaces is `TabStripMetrics`
(pixels vs cells) and two policies -- `TabStripOverflow` and `TabLabelDecoration`. Everything else is
shared.

## What exists today

Three implementations of "a strip of tabs, one active, click to switch":

| | where | lines | surface | layout | model |
|---|---|---|---|---|---|
| `TabBar<TSurface>` | DIR.Lib | 308 | pixel | imperative (`x += w`) | `IReadOnlyList<string>` + `activeIndex` |
| `VkGuiRenderer.RenderSidebar` | TianWen.UI.Gui | ~72 | pixel | imperative (`btnY = startY + i * size`) | `GuiAppState.TabOrder` + `TabChrome` dict |
| `TuiTabBar` | TianWen.Cli | 140 | cell | **`Layout` tree** (`Arrange` -> `ArrangedNode<int>`) | `GuiAppState` |

They share no code. The sidebar re-derives hover from geometry, re-derives button rects from an
index, and its click regions are registered separately from its drawing; `TuiTabBar` already does the
right thing through `CellLayout`, and `TabBar` does the right thing through `PaintLayout`-style
registration since 8.0. So the odd one out is TianWen's GUI sidebar, and the useful observation is
that **the terminal already proves a tab strip is expressible as a layout tree.**

Cost of the split, concretely: adding a GUI tab touches six places (the `GuiTab` enum,
`GuiAppState.TabOrder`, `TabChrome`, the `GuiEventHandlerBase` Ctrl+letter map, two `VkGuiRenderer`
switches, and `GuiTabNavigationTests`), and a change to the strip lands on one surface, not both.

## Why not a second widget

The obvious shape is a new `VerticalTabBar`. This repo should not take it, and the reason is
evidence rather than taste: **every hand-maintained mirror in this family has eventually diverged in
a way nothing caught.**

- The sky-map overlay cache key existed twice (CPU + GPU) and **both** copies carried the same
  quantization bug, fixed only when a browser trace forced someone to read one of them.
- `SdlVulkan.Renderer` silently turned an override into a hide when DIR.Lib grew a `DrawTriangles` of
  the same shape; `WebGl.Renderer` hit the same class one release later with `PushClip`.

A `VerticalTabBar` would be a third copy of tab measurement, hover, active-plate and hit
registration. The fold-into-one instinct is right.

## Recommendation

**One description, three painters.** Express the strip as a `Layout` tree built by DIR.Lib, and let
each surface paint that tree the way it already paints every other tree:

- `TabBar<TSurface>` keeps its widget identity (it owns the interactive state a tree cannot model:
  drag-to-reorder via `SlotAt`, tear-out, `Pointer` hover, the 7.32 frame stamp) but **builds and
  paints a tree** instead of `FillRect`-ing by hand.
- Console.Lib paints the same tree through `CellLayout`, which is what lets `TuiTabBar` collapse to
  "supply items".
- TianWen's sidebar becomes a configured `TabBar`, and `RenderSidebar` is deleted.

This is where DIR.Lib has been heading anyway: 7.18 made an icon a leaf named by meaning, 7.28 made a
text field a declaration, Console.Lib 4.10 made list rows trees, and the Home board already paints
ONE tree on both the GPU tab and the terminal.

## The axes

The user-facing shape is four independent properties, not a pile of flags.

### 1. `TabStripSide { Top, Bottom, Left, Right }` (default `Top`)

Orientation **derives** from the side rather than being a second property, so the two cannot be set
inconsistently. It also decides the accent edge, the separator edge and which way a tooltip opens.
`Top` reproduces today's rendering exactly.

### 2. `TabSizing { Content, Uniform }` (default `Content`)

`Content` is today: `Clamp(measuredText + Pad * 2 + closeBox, MinTabW, MaxTabW)`. `Uniform` makes
every tab a square of the strip's thickness, which is what a nav rail is. TianWen needs this and the
user's sketch did not name it, but without it a vertical strip of icons still sizes itself from label
text it is not drawing.

### 3. Affordances, positive logic, defaults preserve today

`CanCloseTabs` (default `true`), `CanReorderTabs` (default `true`), and the existing
`ShowNewTabButton`. Whether that last one is renamed to `CanAddTabs` for consistency is an open
question below. Per item: `IsEnabled`.

### 4. `TabContentAlign` and the label/icon model

See "Items, not titles".

## Items, not titles

`Render(..., IReadOnlyList<string> titles, int activeIndex)` cannot express a disabled tab, a
per-tab tooltip, or an icon. Replace it with `TabItem<T>`, mirroring **`DropdownItem<T>` from
DIR.Lib 7.29** exactly, whose own release note makes the argument: an entry generic over what it
MEANS hands the selection back rather than an index the caller maps through a switch that has to
agree with the label order. TianWen's two `VkGuiRenderer` switches are that switch.

Sketch:

```csharp
public readonly record struct TabItem<T>(T Value, string Label)
{
    public Layout.Content? Icon { get; init; }   // see the open question
    public bool IsEnabled { get; init; } = true;
    public string? Tooltip { get; init; }
}
```

`TabClick` then carries `T`, so `appState.ActiveTab = click.Value` and the switches go.

## Phasing

Each phase is independently shippable and leaves every existing consumer byte-identical until it
opts in.

| # | Phase | DIR.Lib | Consumers | Status |
|---|---|---|---|---|
| T1 | `TabItem<T>` + item-based `Render` overload | 8.2 | none | **DONE.** Old `titles` overload delegates to it; pinned by comparing painted surfaces. |
| T2 | `TabStripSide`, orientation derived | 8.3 | none | **DONE.** Painter reworked onto a flow/cross axis pair; one body serves four sides. |
| T3 | `TabSizing.Uniform` | 8.3 | none | **DONE**, with T2 — see below. |
| T4 | Affordances (`CanCloseTabs`, `CanReorderTabs`, per-item `IsEnabled` + `Tooltip`) | 8.3 | none | **DONE.** `IsEnabled`/`Tooltip` landed in T1 as fields of the record. |
| T5 | TianWen sidebar adopts `TabBar` | - | tianwen | **DONE.** `RenderSidebar` deleted; forced `CompositeWidget` + `HoverBackground` + `IconSize`. |
| T6 | Strip built as a `Layout` tree | 8.3 | none | **DONE.** 69 geometry tests unchanged; the `+` stays imperative (see below). |
| T7 | Console.Lib cell painter for the strip | none needed | tianwen | **DONE**, and Console.Lib needed NO change -- `CellLayout` already paints any tree. |
| T8 | Rotated text (optional, see below) | **renderer capability** | - | Only if a consumer wants long labels on a vertical strip. |

T8 remains and is still not wanted: a vertical strip draws upright content, which is what its only
consumer needs.

**T7 cost Console.Lib nothing.** The plan budgeted a "Console.Lib cell painter for the strip"; none was
needed, because `CellLayout` already paints an arbitrary `Layout.Node` tree -- the Home board had
proved that. The phase was really "stop `TuiTabBar` building its own tree", which is a TianWen change.

**Two things the shared strip needed that neither surface alone would have asked for.**
`FillsAvailable`, because the terminal embeds its strip beside a status text while the GPU strip IS the
bar -- and a Star-sized strip beside any other Star sibling splits the row with it, compressing every
fixed-width tab (9 cells arranged into 5). And the `+` button stays imperative in `TabBar`: it belongs
to a tab BAR rather than a tab strip, and its mark is two rectangles, so a tree form would mean adding
`IconKind.Plus` and owing a cell drawing for a control no cell surface has.

**`TuiTabBar` got LONGER** (140 -> 170 lines). The duplicated logic went; explicit documented
configuration replaced it. The measure that matters is that tab layout has one implementation, not the
line count -- and the honest version of "collapses to supplying items" is "supplies items plus a
config block".

**T2 and T3 shipped together, deliberately.** They are not independent the way the table implied: a
vertical strip sizing by content sets a tab's HEIGHT from the WIDTH of its label, and on an icon-only
rail from a label it does not draw. T2 alone would therefore have shipped a vertical mode whose only
sizing rule is meaningless, so `Uniform` is not a follow-up refinement but the thing that makes the
side axis usable. **T4's per-item half also moved earlier**, into T1, because `IsEnabled` and
`Tooltip` are fields of `TabItem<T>` and shipping the record with two inert properties would have been
worse than honouring them on arrival.

**One source break, in a minor.** `Render`'s `contentLeft`/`viewportW` became `contentStart`/
`viewportEnd` and `SlotAt`'s `x` became `flow`, since on a vertical strip the old names name the wrong
axis. Named-argument callers break; positional ones do not, and nothing in the org passed them by
name. Taken rather than kept for compatibility because the parameters are the API's own description of
which axis it means.

## Rotated text is not a flag

The user's sketch asked for a vertical strip whose text direction is vertical by default. That is the
one part of the request that is **not** a configuration change:

```csharp
public abstract void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
    RGBAColor32 fontColor, in RectInt layout,
    TextAlign horizAlignment = TextAlign.Center, TextAlign vertAlignment = TextAlign.Near);
```

There is no angle. Rotated text means a new renderer capability implemented across `VkRenderer`,
`WebGlRenderer` and `RgbaImageRenderer`, meaningless on a cell surface, and interacting with the SDF
atlas and the fallback chain. So:

- **Default for a vertical strip is upright content**, not rotated. That is also what TianWen wants,
  since its tabs are emoji and a rotated emoji is simply wrong.
- Rotation is T8, gated on a consumer that actually needs long labels vertically, and it is a
  `Renderer` feature first and a `TabBar` property second.

Note this inverts the user's stated default, deliberately: upright is both cheaper and correct for
the only known consumer.

## Open questions

1. ~~**How does a tab carry an icon?**~~ **RESOLVED at T1, and neither option was needed.**
   `TabItem.Icon` is a plain `string`. The premise — that a symbol character is unreliable on a pixel
   surface — is true but already solved one layer down: `PixelWidgetBase.DrawText` splits a run by
   coverage through `FontFallback` and routes supplementary-plane codepoints to `EmojiFontPath` even
   without one, so 🏠 🔭 📅 simply draw. A `Layout.Content` would have added an API surface to reach
   machinery the widget already runs, and the `Icon` half of it could not draw an emoji at all
   (`IconKind` names a caret or a grid, not a telescope). Width is a **fixed box**, never measured: a
   pictograph's advance varies by face, so measuring would make tab width depend on which fallback
   happened to resolve.
2. **Rename `ShowNewTabButton` to `CanAddTabs`?** **Declined at T4.** It keeps its name and is
   documented as the odd one out. Renaming a shipped property costs a consumer more than the
   inconsistency does, and the pair added beside it (`CanCloseTabs`, `CanReorderTabs`) are both
   positive-logic, so the convention is established for anything added next.
3. ~~**Does the sidebar's tooltip belong to the bar?**~~ **RESOLVED as leaned: the host draws it.**
   `TabBar.HoveredIndex` reports the tab under the pointer, resolved while the tabs are laid out so
   the host pays no hit test for it, and `TabItem.Tooltip` carries the text. The bar cannot paint it:
   a tooltip lands outside the strip, over whatever is adjacent, and the bar clips to its own bounds.
   Declaring an overlay would have moved a z-order and placement decision into a widget that cannot
   see what it would cover.
4. **Does TianWen's status bar stay out?** `TuiTabBar` renders a status line alongside the tabs. That
   is a TianWen composition, not a tab-strip feature; it should stay in the host.

## What this does not fix

`GuiAppState.TabOrder`, the `GuiTab` enum and the Ctrl+letter map stay TianWen's. The six-place cost
of adding a tab drops to about three; it does not go to one, and pretending otherwise would be the
wrong reason to do this work.

## Related

- [`docs/plans/layout-driven-ui.md`](layout-driven-ui.md) - the migration this is a late instance of.
- [`docs/plans/controls-upstreaming.md`](controls-upstreaming.md) - the U-plan that moved sliders,
  text inputs and the key router into DIR.Lib. This is the same shape: a control TianWen wrote by
  hand that turns out to be generic.
