# Tab strips: one description, three surfaces

**Status: PLANNED.** No code written. This is the design and the phasing for making TianWen's
navigation sidebar a first-class DIR.Lib citizen instead of hand-drawn chrome.

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

| # | Phase | DIR.Lib | Consumers | Notes |
|---|---|---|---|---|
| T1 | `TabItem<T>` + item-based `Render` overload | minor | none | Old `titles` overload delegates to it. Additive. |
| T2 | `TabStripSide`, orientation derived | minor | none | `Top` is today. Needs the accent/separator/hover geometry expressed per side. |
| T3 | `TabSizing.Uniform` | minor | none | Square tabs sized from strip thickness. |
| T4 | Affordances (`CanCloseTabs`, `CanReorderTabs`, per-item `IsEnabled` + `Tooltip`) | minor | none | Defaults keep today's behaviour. |
| T5 | TianWen sidebar adopts `TabBar` | - | tianwen | Delete `RenderSidebar`; `GuiTab` becomes the `T`. Six-place problem drops to three. |
| T6 | Strip built as a `Layout` tree | minor | none | Internal reshape; the payoff is T7. |
| T7 | Console.Lib cell painter for the strip | Console.Lib minor | tianwen | `TuiTabBar` collapses to supplying items. One strip, both surfaces. |
| T8 | Rotated text (optional, see below) | **renderer capability** | - | Only if a consumer wants long labels on a vertical strip. |

T1-T5 is the whole of what TianWen needs. T6-T7 is the prize (three implementations become one).
T8 is separable and probably never happens here.

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

1. **How does a tab carry an icon?** TianWen's are **emoji glyphs** (🏠 🔭 📅) drawn from a bundled
   emoji font via `EmojiFontPath`, not `Layout.Content.Icon` marks, which are built from rectangles
   and cannot draw an emoji. Options: `TabItem.Icon` as a `Layout.Content` (so a host passes either an
   `Icon` leaf or a `Text` run in whatever font it likes), or a narrower `Glyph` + font pair. The
   first is more flexible and matches "rows as layout trees"; the second is easier to measure and
   truncate. **Leaning to `Layout.Content`,** decided at T1.
2. **Rename `ShowNewTabButton` to `CanAddTabs`?** Consistent with `CanCloseTabs` / `CanReorderTabs`,
   but breaking, and DIR.Lib 8.0 has just shipped. Could ride the next major, or the pair can simply
   coexist with the older name documented as the odd one out.
3. **Does the sidebar's tooltip belong to the bar?** It is drawn *outside* the strip, over adjacent
   content, so a widget that clips to its own bounds cannot paint it. Either the bar returns the
   hovered item and the host draws the tooltip (simplest, and what TianWen does today), or the strip
   declares an overlay. **Leaning to the former.**
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
