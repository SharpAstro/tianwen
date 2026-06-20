# Layout engine + the `Layout.Builder` DSL

TianWen's GUI/TUI panels are built from a **surface-agnostic declarative layout engine** that lives in
DIR.Lib (`DIR.Lib.Layout`). You describe a tree of immutable records; the engine measures + arranges it
into rects; a per-surface painter walks the arranged tree to **draw and bind clicks from the same rect**
(draw == hit by construction). The `Layout.Builder` DSL is the ergonomic front-end for authoring that tree.

This doc is the "how to build a panel" reference. The engine itself is in
`../../../DIR.Lib/src/DIR.Lib/Layout/`.

## Pipeline

```
Layout.Builder.VStack(...)        // 1. author a tree (records)
   -> Layout.Node tree
Layout.Engine.Arrange(root, rect, ctx)   // 2. measure + arrange (two-pass)
   -> ImmutableArray<Layout.ArrangedNode>  (pre-order: parent before children)
PixelWidgetBase.PaintLayout(arranged)     // 3. paint + auto-bind clicks
   -> FillRect(Background) + DrawText/Box/Fill + RegisterClickable(Hit) per node
```

Step 3 is the load-bearing guarantee: a node's **background fill, its drawn content, and its click region
all come from the one arranged rect** — there is no second "hit rect" arithmetic that can drift from the
drawn position. Inner nodes register later, so a button inside a clickable row still wins the hit.

## The namespace + the `Layout` alias

The engine + DSL live in `DIR.Lib.Layout`, with the redundant prefix dropped: `Layout.Node`,
`Layout.Engine`, `Layout.Content`, `Layout.Axis`, `Layout.Sizing`, `Layout.Size<T>`, `Layout.DockChild`,
`Layout.DockSide`, `Layout.ArrangedNode`, `Layout.IMeasureContext`, `Layout.Builder`.

**Consumer convention** — keep `using DIR.Lib;` and add a per-project alias:

```csharp
global using Layout = DIR.Lib.Layout;   // GlobalUsings.cs, or a csproj <Using Include="DIR.Lib.Layout" Alias="Layout" />
```

Then write the qualified form everywhere: `Layout.Builder.VStack(...)`, `Layout.Node`, `Layout.Sizing`.

- **Do not** `using DIR.Lib.Layout;` directly — that drops the collision-prone barewords (`Node`,
  `Content`, `Size<T>`, `Builder`) into the file's scope.
- A `using DIR.Lib;` alone does **not** make the nested `Layout` namespace referenceable (a using-directive
  imports types, not nested namespaces) — that's why the alias is required. (Inside DIR.Lib's own code the
  alias is unnecessary: `Layout.X` resolves via enclosing-namespace lookup.)
- A consumer that already owns a `Layout` type can't alias it. (periodic-table-viewer renamed its
  `PeriodicTable.Layout` element-grid helper to `ElementGrid` to free the name.)

## Authoring: `Layout.Builder` + fluent `Layout.Node` modifiers

**Build a tree, never `cursor += h`.** The DSL is pure sugar — `Layout.Builder.Text("x")` is exactly
`new Layout.Node.Leaf(new Layout.Content.Text("x"))`. Two halves:

### Factories (`Layout.Builder`)

| Factory | Produces |
|---------|----------|
| `Text(value, fontSize=14, color=white, hAlign=Near, vAlign=Center)` | text leaf (styling is intrinsic, set here) |
| `Box(w, h, color=transparent)` | fixed box / swatch / separator |
| `Fill(minW=0, minH=0, key=null)` | app-drawn escape hatch (chart, image, text input); route multiple via `key` |
| `Spacer()` | transparent `Box(0,0)` |
| `VStack(...)` / `HStack(...)` | stacked children (`params ReadOnlySpan<Node>`; pass `arr.AsSpan()` for a dynamic list) |
| `Grid(cols, ...)` | uniform N-column grid |
| `Overlay(layer, top)` | modal / dropdown / popup |
| `Split(first, second, axis, firstExtent, dividerThickness, dividerHit, dividerColor)` | two resizable panes + a draggable divider |
| `Dock(fill, Right(child, w), Top(child, h), ...)` | edge-pinned strips + a fill remainder |

### Fluent modifiers (instance methods on `Layout.Node`)

Chrome that would otherwise be an object-initializer block. They are **instance methods** (we own `Node`),
each a pure `this with { ... }` transform, so chaining needs no `using`:

| Modifier | Sets |
|----------|------|
| `.W(Sizing)` / `.H(Sizing)` | explicit sizing |
| `.WFixed(u)` / `.WStar(w=1)` / `.WAuto()` (+ `H*`) | single-axis shorthand |
| `.RowH(u)` | `Width=Star, Height=Fixed(u)` — the dominant full-width row |
| `.ColW(u)` | `Width=Fixed(u), Height=Star` — fixed column that stretches vertically |
| `.Stretch()` | `Star` on both axes — fill the cell |
| `.Bg(color)` | background fill |
| `.Pad(u)` | inner padding |
| `.Clickable(hit, onClick=null)` | bind `Hit` (+ optional handler) to the whole rect |
| `.WithGap(g)` / `.WithGaps(r, c)` | stack gap / grid gaps (no-op on the wrong node kind) |

> `.WithGap` (not `.Gap`) because `Node.Stack` already exposes a `Gap` property — a method + property of the
> same name collide. This is also why the modifiers are instance methods rather than extension methods on a
> type we don't own: extensions would need their declaring namespace imported, which reintroduces the
> bareword-collision problem the alias avoids.

### Before / after

```csharp
// before -- object-initializer, chrome as a block, double-wrapped leaves
var pad   = new Layout.Node.Leaf(new Layout.Content.Box(0f,0f)) { Width = Layout.Sizing.Fixed(8f), Height = Layout.Sizing.Star() };
var label = new Layout.Node.Leaf(new Layout.Content.Text(slot.Label, 14f) { Color = dim }) { Width = Layout.Sizing.Star(0.35f), Height = Layout.Sizing.Star() };
return new Layout.Node.Stack([pad, label, ...], Layout.Axis.Horizontal)
{
    Height = Layout.Sizing.Fixed(28f), Width = Layout.Sizing.Star(),
    Background = active ? activeBg : normalBg,
    Hit = new HitResult.SlotHit<AssignTarget>(slot.Slot), OnClick = onClick,
};

// after -- DSL
return Layout.Builder.HStack(
        Layout.Builder.Spacer().ColW(8f),
        Layout.Builder.Text(slot.Label, 14f, dim).WStar(0.35f).HStar(),
        /* ... */)
    .RowH(28f)
    .Bg(active ? activeBg : normalBg)
    .Clickable(new HitResult.SlotHit<AssignTarget>(slot.Slot), onClick);
```

**Conditional background:** the fluent `.Bg(color)` always sets a value, so for a nullable/conditional
background build the base node then `if (cond) n = n.Bg(color);` — never `.Bg(default)` (that paints
transparent rather than leaving `Background` null).

**Interactive sub-widgets** (text inputs, telemetry graphs, the sky map, custom charts) stay as imperative
helpers: emit a `Layout.Builder.Fill(key: "...")` leaf and draw into its arranged rect via `PaintLayout`'s
`drawFill` callback, keyed when a tree has several.

## Where the builders live

- **Generic shared rows** -> `FormRowLayout` (StepperControl, InsetPillButton, ToggleHeaderRow,
  LabeledInputRow, StepperRow).
- **Equipment-panel structure** -> `EquipmentPanelLayout` (data-driven OTA flow, SlotRow, FilterTable).
- **Per-tab trees** -> the tab files (`EquipmentTab`, `SessionTab`, `LiveSessionTab`, `PlannerTab`,
  `SkyMapTab.Search`, `NotificationsTab`, `SessionConfigLayout`, `ImageRendererBase`).

All of them author via `Layout.Builder`. New panels should too — never reach for `new Layout.Node.X { }`
object-initializers or imperative `cursor += h` placement.

## Testing

`Layout.Engine.Arrange` is headless (a stub `Layout.IMeasureContext` supplies glyph metrics), so panel
geometry is unit-testable with no renderer. `EquipmentPanelLayoutTests` / `SessionConfigLayoutTests` assert
exact arranged rects; the DIR.Lib `LayoutBuilderTests` prove a DSL-built tree arranges identically to the
hand-built records. For tabs without geometry tests, verify via the SDL inspector (`tw_inspect.py regions`)
that elements are present with the right footprint — see the UI-refactor verification bar.

## Shipped

DIR.Lib 6.0 / Console.Lib 3.3 / SdlVulkan.Renderer 6.7 (lockstep). All TianWen UI builders author via the
DSL; `DIR.Lib.Layout` is the single home for the engine + Builder.
