# Surface-agnostic layout engine (theme -> engine -> shared widgets -> optional DSL)

**Cross-repo plan.** The work lands mostly in `../DIR.Lib` (the shared base), with follow-on
consumption in `../Console.Lib`, `../SdlVulkan.Renderer`, TianWen, and `../../sebgod/chess`. This doc
lives in TianWen's `docs/plans/` because TianWen is the driving consumer, but Phases 1-3 are DIR.Lib
changes that also benefit chess.

## Painpoint

Tab/panel UI today is hand-laid-out with duplicated constants and no real layout engine. Concretely,
measured across the four repos:

- **No theme.** ~7 logical colours (`ContentBg`, `HeaderBg`, `HeaderText`, `BodyText`, `DimText`,
  `SeparatorColor`, `PanelBg`) are redefined **3-6x each** across 6 TianWen tabs + `VkGuiRenderer` +
  chess + DIR.Lib's own `TabBar`/`TextInputRenderer` -- ~35 duplicate `RGBAColor32` fields. Sizing
  constants too: `BaseFontSize = 14f` declared **7x**, `BasePadding` 6x (values drift 6/8/10),
  `BaseHeaderHeight` 4x, `BaseItemHeight` 3x. Every colour is a raw value passed per draw call.
- **No measure/arrange engine.** DIR.Lib has `DockLayout<T>` + `PixelLayout` (dock-only, caller
  supplies fixed sizes) and Console.Lib has a structurally identical but *separate* `TerminalLayout`.
  Beyond dock, layout is a manual `cursor += itemH` walk -- **36 increments in
  `EquipmentTab.RenderProfilePanel`, 13 in `SessionTab`**. No Stack/Flex/Grid, no proportional/stretch
  sizing, no intrinsic-size pass.
- **Hit-testing is hand-duplicated.** A widget draws at `(x,y,w,h)` *and* re-registers a clickable
  region at the same coords (`PixelWidgetBase.RegisterClickable`). Nothing binds an arranged rect to
  its click region, so the two can drift.
- **Two consumers reinvent the same widgets.** chess and TianWen each hand-roll a scrollable
  list-with-clickable-rows, a status text bar, an overlay/modal, and a centered menu. chess
  (`Chess.GUI/VkGameDisplay.cs`) and TianWen tabs both subclass DIR.Lib `PixelWidgetBase<TSurface>`
  and both use `PixelLayout`, proving the base is shared -- the gap is the widgets above it.

The original trigger was the "fully data-driven per-OTA profile panel" item (TODO.md:57): replacing
`RenderProfilePanel`'s hardcoded `RenderProfileSlot` sequence with a content-model loop is only worth
doing on top of a real layout engine, otherwise we just relocate the `cursor += itemH` math into the
content model.

## Goals / non-goals

**Goals**
- One **theme** type (palette + metrics) shared by GPU + TUI + chess. Single source of truth.
- A **measure/arrange layout engine** that works on both pixel (`float`) and character-cell (`int`)
  surfaces, with Stack/Row/Grid/Dock containers and Fixed/Auto/Star (proportional) sizing.
- **Auto-binding** of arranged rects to clickable regions (draw-position and hit-region can't drift).
- **Shared widgets** in DIR.Lib so chess and TianWen stop reinventing them.
- A **declarative `LayoutNode` tree** as the public contract -- so an optional DSL can target it later
  with zero engine rework.

**Non-goals**
- Replacing the Vulkan or terminal *paint* primitives (`Renderer<TSurface>` / `ITerminalViewport`
  stay). The engine produces rects; per-surface painters draw.
- A full retained-mode/reactive framework (no virtual DOM diffing). Immediate-mode paint stays; the
  engine adds *layout*, not a new render loop.
- Animations, flexbox-complete spec compliance, RTL. Out of scope for v1.

## Dependency picture

```
              DIR.Lib  (base: Renderer<TSurface>, PixelWidgetBase, DockLayout<T>, RGBAColor32)
              /   |   \\
   Console.Lib  SdlVulkan.Renderer  (concrete surfaces: terminal cells / Vulkan)
        |          |
   TianWen TUI   TianWen GPU + chess GPU/console   (consumers)
```

Everything shared goes in **DIR.Lib** (the *type*); each app supplies its own theme *instance* and
content. Note: the theme MUST live in DIR.Lib, not TianWen.UI.Abstractions -- chess shares only
DIR.Lib, and `RGBAColor32` already lives there and is already what Console.Lib's `VtStyle` consumes,
so one theme type serves all three surfaces with no new cross-dependency.

## The load-bearing decision: declarative-tree-first

Build the engine around a **data-only `LayoutNode` record tree** that the engine measures and
arranges -- NOT an imperative `Dock(); cursor += itemH` API. If the tree is the public shape, then
(a) the data-driven OTA panel is just "build a tree from the content model", and (b) the optional DSL
(Phase 4) is just an alternate front-end whose LALR.CC visitor emits the same records. If the engine
stays imperative, no DSL can ever target it and the panel refactor stays cursor math. This decision
costs nothing now and gates everything later.

Sketch (in DIR.Lib, surface-agnostic):

```csharp
abstract record LayoutNode { public StyleRef? Style { get; init; } }

// Containers
record Stack(ImmutableArray<LayoutNode> Children, Axis Axis = Axis.Vertical, float Gap = 0) : LayoutNode;
record DockNode(ImmutableArray<(DockSide Side, LayoutNode Child, Sizing Size)> Docked, LayoutNode Fill) : LayoutNode;
record Grid(int Columns, ImmutableArray<LayoutNode> Cells, ...) : LayoutNode;
record Overlay(LayoutNode Base, LayoutNode Top) : LayoutNode;

// Leaf: a paintable + hit-testable piece. Content is surface-neutral (a label, a button spec,
// a text-input state, or an app "draw callback" escape hatch for charts / the sky map).
record Leaf(ILayoutContent Content, Sizing Width = default, Sizing Height = default) : LayoutNode;

// Sizing model = the flex story
readonly record struct Sizing(SizeKind Kind, float Value);   // Fixed(n) | Auto | Star(weight)
```

Two passes, generic over the coordinate numeric `T` (`float` pixels / `int` cells) reusing the
existing `DockLayout<T>` generic-math approach:

1. **Measure(node, constraint, IMeasureContext) -> DesiredSize.** `IMeasureContext` carries the
   per-surface **text width oracle** (the only surface-specific input to layout): pixels ->
   `Renderer<TSurface>.MeasureText`; terminal -> char count (`text.Length`, with a wide-char hook
   later). Also carries theme metrics so `Auto` rows size to content + padding.
2. **Arrange(node, finalRect) -> ArrangedTree.** Walks top-down assigning a rect to every node;
   `Star` children split leftover space by weight. Output is a flat `(LayoutNode, Rect)` list.

Then a **per-surface painter** walks the arranged tree:
- Pixel painter: `Renderer<TSurface>` draws + `RegisterClickable(arrangedRect, hit)` -- auto-bound, so
  draw-position == hit-region by construction.
- Cell painter: `ITerminalViewport` writes; clickable maps via the existing px<->cell bridge
  (`TermCell`).

Measurement is the only thing that differs per surface (glyph metrics vs char count); arrange and the
tree are fully shared.

## Phases

| Phase | Scope | Repo(s) | Gate |
|------|-------|---------|------|
| **1** ✅ | `UiTheme` (palette + metrics) record; migrate consumers off duplicated constants | DIR.Lib (type) + TianWen + chess (instances) | constants deduped; pixel output unchanged — **DONE for the core chrome roles** |
| **2** 🔧 | Layout engine: `LayoutNode` tree, Measure/Arrange, Stack/Row/Dock/Grid, Fixed/Auto/Star, width-oracle, auto-clickable; unify `TerminalLayout` onto `DockLayout<int>` | DIR.Lib + Console.Lib | one TianWen tab (Equipment profile panel) ported, GPU+TUI parity — **engine core + 15 tests DONE; painters + port pending** |
| **3** | Extract shared widgets: `PixelTextBar`, `PixelScrollableList`, overlay/modal, surface-agnostic `PixelMenuWidget` | DIR.Lib (+ chess + TianWen consume) | chess + TianWen both drop their hand-rolled copies |
| **4** | **(gated)** Layout DSL on LALR.CC: build-baked grammar, runtime-parsed `.layout` -> visitor -> `LayoutNode` tree; hot-reload in dev | LALR.CC grammar + DIR.Lib loader | one screen authored in the DSL renders on both surfaces |

### Phase 1 -- `UiTheme`

- New `DIR.Lib/.../UiTheme.cs`: `record UiTheme(UiPalette Palette, UiMetrics Metrics)`.
  - `UiPalette` -- the ~7 shared `RGBAColor32` roles (`ContentBg`, `PanelBg`, `HeaderBg`, `HeaderText`,
    `BodyText`, `DimText`, `Separator`) + interactive roles (`SlotNormal`, `SlotActive`, `ButtonBg`,
    `ButtonText`, `Accent`). One semantic name per role, not per-tab.
  - `UiMetrics` -- `BaseFontSize`, `Padding`, `HeaderHeight`, `ItemHeight`, `ButtonHeight`, `ArrowWidth`.
    Pixel base values; DPI scaling stays the caller's `dpiScale` multiply (unchanged).
- `RGBAColor32` already lives in DIR.Lib and is consumed by Console.Lib's `VtStyle` -- so the SAME
  palette drives ANSI/truecolor (TUI) and Vulkan (GPU); the SGR nearest-colour mapping
  (`VtStyle.NearestSgrColor`) already exists.
- Migrate: replace the ~35 `private static readonly RGBAColor32` fields in TianWen tabs +
  `VkGuiRenderer` and the 7x `BaseFontSize` with `Theme.Palette.X` / `Theme.Metrics.X`. DIR.Lib's own
  `TabBar`/`TextInputRenderer` take an optional theme (default = today's hardcoded values, so no
  behavioural change). chess supplies its own `UiTheme` instance for its chrome (it keeps its
  board-specific colours, which are domain, not chrome).
- **Smallest, highest-ROI step; no new abstractions, surface-agnostic already.** Pixel output is
  byte-identical when the theme instance carries today's values -- assert via existing rendering tests.

**Status (2026-06-17, branch `fix/guider-slewing-calibration`, not yet committed/released):**

- **1a colours -- DONE.** `UiTheme`/`UiPalette`/`UiMetrics` records added to DIR.Lib
  (`src/DIR.Lib/UiTheme.cs`); `GuiTheme` instance added to `TianWen.UI.Abstractions`
  (`GuiTheme.cs`). The 8 genuinely-shared chrome colour roles (`ContentBg`, `PanelBg`, `HeaderBg`,
  `HeaderText`, `BodyText`, `DimText`, `Separator`, `Selection`) are single-sourced across
  `SessionTab`, `GuiderTab`, `EquipmentTab`, `LiveSessionTab`, `NotificationsTab`, `PlannerTab`,
  and `VkGuiRenderer`.
- **1b metrics -- DONE.** `UiMetrics` (`BaseFontSize 14`, `Padding 8`, `HeaderHeight 28`,
  `ItemHeight 24`, `ButtonHeight 28`) single-sourced. Only the constants whose value already
  equalled the theme value were flipped from `const float` to `static readonly float = GuiTheme.Metrics.X`;
  the divergent ones (Session `ItemHeight 26`, Guider `HeaderHeight 32`, Planner `ItemHeight 22` /
  `Padding 6`, LiveSession `Padding 6`, Notifications `FontSize 13` / `Padding 10`, SkyMap `12`,
  ImageRenderer `18`) stay local -- so pixel output is byte-identical by construction. Full-solution
  `dotnet build` green (0 warnings, 0 errors).
- **Byte-identity rule, confirmed empirically.** Domain colours that merely *coincide* with a chrome
  value are deliberately left local -- e.g. `GuiderTab.TargetRingColor` is `0x333344` (== `Separator`)
  but is the guide-graph concentric ring, not chrome; coupling it to `Separator` would wrongly retheme
  the graph. Same for LiveSession `RaColor` `0x4488ff` (RA trace blue), which coincides with TabBar's
  accent but is a domain axis colour.

**Deferred into Phase 2 (was originally listed here):**

- **Palette enrichment with second-tier roles.** A real-but-modest tier of duplicated chrome shades
  remains -- `RowAltBg` (`0x1a1a24`, Session+LiveSession+Notifications), `ControlBg`/`ButtonBg`
  (`0x2a2a3a`, Session+LiveSession jog/step/progress), `Faint` (`0x555566`, scrollbar-thumb / tick /
  empty), `Accent` (`0x4488ff`). These are 2-3x dupes each; folding them in means *growing* the palette
  with named interactive roles, which is the same role-set Phase 2's engine needs -- so they land with
  Phase 2 rather than as a speculative Phase-1 palette that has no engine consuming it yet.
- **DIR.Lib `TabBar` / `TextInputRenderer` optional theme.** These widgets carry control-state colours
  (`ActiveBg`/`InactiveBg`/`ActiveAccent`/`ActiveText`/`InactiveText`/`CloseColor`;
  `FieldBg`/`FieldBorder`/`Cursor`/`Placeholder`/`Selection`) that need the enriched interactive-role
  palette above to map cleanly, and no TianWen caller overrides them today. Making them theme-aware is
  best done in Phase 2/3 alongside the role expansion and the shared-widget extraction, not as a
  half-mapping against the 8 core roles now.

### Phase 2 -- the engine

- New in DIR.Lib: the `LayoutNode` records, `IMeasureContext` (with the width-oracle delegate),
  `Measure`/`Arrange`, the `Sizing` model, and the pixel painter (`ArrangedTree` ->
  `Renderer<TSurface>` + auto `RegisterClickable`).
- **Unify the two dock engines.** Console.Lib's `TerminalLayout` is a structural duplicate of
  `DockLayout<T>`; reimplement it as `DockLayout<int>` (cells) so dock logic exists once. Pixel stays
  `DockLayout<float>` / `PixelLayout`.
- Cell painter in Console.Lib walks the same `ArrangedTree` via `ITerminalViewport` (cells), using the
  char-count oracle.
- **First consumer: `EquipmentTab.RenderProfilePanel`.** Replace the 36 `cursor += itemH` increments
  with a `Stack` of rows (device slots + their attachment sub-nodes from the content model). This is
  also where the data-driven OTA panel (TODO.md:57) lands: `EquipmentContent` emits the `LayoutNode`
  tree (or the panel-item list that builds it) and both the GPU painter and the TUI painter consume
  it -- one source, no `cursor` math, adding a Rotator/Dome slot becomes one tree node.
- Keep the rest of the tabs on the old path until ported; the engine coexists with manual layout.

**Phase 2 is shipped as four increments so each is independently verifiable:**

- **2A -- engine core (DONE, 2026-06-17, branch `fix/guider-slewing-calibration`, not committed/released).**
  New in DIR.Lib: `LayoutNode.cs` (the surface-neutral record tree -- `Stack`/`Dock`/`Grid`/`Overlay`/`Leaf`
  + `LayoutContent` `Text`/`Box`/`Fill` + `Sizing` Fixed/Auto/Star + `Size<T>` + `ArrangedNode<T>`) and
  `LayoutEngine.cs` (`IMeasureContext<T>` width oracle + `Measure<T>`/`Arrange<T>`, generic over
  `T : INumber<T>` reusing `Rect<T>`/`DockLayout<T>`). Two passes: Measure -> intrinsic size; Arrange ->
  flat **pre-order** `ImmutableArray<ArrangedNode<T>>` (parent before children, Overlay base before top, so
  a list-order painter z-stacks correctly). Star/grid splits use cumulative-target rounding so parts sum
  *exactly* to the total -- exact for float pixels, deterministic-remainder for int cells (no cell lost).
  AOT-clean (records + generic math, no reflection). **15 `LayoutEngineTests` pass** (Fixed/Auto/Star,
  weighted stars, gap, padding inset, cross-axis Fixed/Star/Auto, nested axis switch, Dock strip+fill, Grid
  tiling, Overlay z-order, root-first emission, int-cell exact tiling); DIR.Lib build warning-free.
- **2B -- pixel painter (DONE, 2026-06-17).** The 2A model gained surface-neutral *presentation*
  (`LayoutNode.Background` RGBAColor32?, `LayoutContent.Text` Color/HAlign/VAlign, `Box` fill Color,
  `LayoutContent.OnClick`) -- `RGBAColor32` serves both Vulkan and the TUI's `VtStyle`, so it stays
  surface-neutral. New `PixelMeasureContext<TSurface>` wraps `Renderer.MeasureText` + DPI scaling as the
  width oracle (arranged rects come back in device px; text draws at `fontSize * dpiScale`). Three new
  `protected` methods on `PixelWidgetBase<TSurface>`: `ArrangeLayout` (build ctx + arrange), `PaintLayout`
  (fill `Background` parent-first for z-order -> draw `Text`/`Box`/`Fill` -> auto-`RegisterClickable(rect, Hit)`),
  and `RenderLayout` (= arrange + paint). `Fill` leaves dispatch to an app `Action<Fill, RectF32>` callback
  (charts / sky map). 3 `LayoutPainterTests` (region binds to arranged rect, OnClick dispatches inside the
  rect / misses outside, non-clickable leaves register nothing) + the 15 engine tests all pass against the
  CPU `RgbaImageRenderer`; TianWen.UI.Gui builds clean against the local DIR.Lib.
- **2C -- cell painter + dock unification (DONE, 2026-06-17).** Console.Lib `CellLayout` walks the SAME
  arranged tree as the pixel painter but writes character cells: `Background`/filled `Box` -> runs of spaces
  with a bg SGR (via `VtStyle`), `Text` -> glyphs foreground-only (so the painted bg shows through) aligned
  within the leaf rect, `Fill` -> app callback. `CellLayout.HitTest(col,row)` maps a cell back to a leaf's
  `Hit` (+ invokes `OnClick`) -- same arranged-rect-is-hit-region guarantee as the pixel side.
  `CellMeasureContext` is the char-count oracle (width = `text.Length`, 1 row tall; design-unit scalars round
  to cells -- wide-char width is a documented follow-up). **Dock unification:** `TerminalLayout.ComputeGeometries`
  now builds a `DockLayout<int>` and reads `Fill()` for the clamp, so the four-way edge arithmetic lives once
  in `DockLayout<T>`; `TerminalLayout` keeps only the terminal-specific "strip never exceeds remaining cells"
  clamp + viewport wiring. Public surface unchanged (`Panel` + existing tests untouched). 6 new
  `CellLayoutTests` (dock geometry, oversized-strip clamp, hit-test map, OnClick dispatch, bg+text paint,
  center-align) + existing `TerminalViewportTests` all pass; TianWen.Cli builds clean against the local chain.
- **2D -- first consumer (STARTED, 2026-06-17).** Two pieces done:
  - **Model refinement:** `Hit`/`OnClick` moved from `LayoutContent` (leaf-only) onto `LayoutNode` (any
    node), so a whole slot row / panel is clickable, not just a leaf. Both painters updated (GPU
    `PaintLayout` registers `node.Hit`; TUI `CellLayout.HitTest` reads `node.Hit`); inner nodes register
    later so they still win the hit. Existing painter tests green.
  - **Data-driven panel tree:** `EquipmentPanelLayout.Build` bridges the existing `EquipmentContent` models
    (`DeviceSlotRow` / `OtaSummaryRow`) into ONE `LayoutNode` tree -- profile header -> profile slot rows ->
    per-OTA sections (loop over the OTA set, no hardcoded count) each with header / properties / 4 sub-slot
    rows / optional filter sub-table. Each slot row is a clickable `Stack` carrying `SlotHit<AssignTarget>`
    + an `onSlotClick` handler; active slot gets `SlotActive` bg. `EquipmentPanelStyle.Default` holds the
    equipment-specific colours (slot states, OTA header). 7 `EquipmentPanelLayoutTests` (per-OTA loop,
    add-OTA adds a section, 4 sub-slots per OTA, slot Hit carries the target, active highlight, onClick
    wiring, filter rows). **This is the data-driven per-OTA panel (TODO.md:57) as a tested, surface-neutral
    tree -- consumed by BOTH painters.**
  - **PENDING:** wire `EquipmentPanelLayout` into the live `EquipmentTab.RenderProfilePanel` (replace the
    imperative cursor walk for the slot/OTA skeleton; keep site editor / telemetry / dropdowns as `Fill`
    escape-hatch callbacks) + the TUI path, then verify GPU/TUI visual parity via run-gui. Same treatment
    for the **FitsViewer** (`ImageRendererBase` -> its own `UiTheme` instance at 18px + `LayoutNode` panels).
    These wiring steps touch live production UI and need visual verification, so they are done with the GUI
    running, not blind.

### Phase 3 -- shared widgets

Extract the patterns both consumers hand-roll into DIR.Lib, built on Phase 2:

- `PixelTextBar` -- fill + left/right aligned text (chess `RenderStatusBar`, TianWen footers,
  Console.Lib already has `TextBar`).
- `PixelScrollableList` -- row-based, per-row `RegisterClickable`, scroll offset, viewport-row count,
  scrollbar (chess `RenderHistoryPanel`, TianWen `PlannerTargetList`; Console.Lib already has
  `ScrollableList<T>` -- mirror its API so the TUI/GPU lists converge).
- `RenderOverlay(rect, dim, content)` -- modal/popup (chess promotion popup + `VkMenuWidget`, TianWen
  keymap help + dropdowns).
- `PixelMenuWidget` -- move `VkMenuWidget` down from `SdlVulkan.Renderer` to a surface-agnostic base in
  DIR.Lib so `Chess.Lib` (DIR.Lib-only) can use the same menu path as the startup screen without a
  GPU-package dependency (called out as a tension in chess `MIGRATION-VK.md`).

### Phase 4 -- LALR.CC layout DSL (gated on Phase 2 + verbosity proving out)

Only if the C# `LayoutNode` builder proves verbose across many screens. LALR.CC makes this unusually
cheap and AOT-clean:

- A small grammar (`layout.lalr.yaml`) -- containers, attributes, style-token refs, data-binding
  holes -- run through `LALR.CC.SourceGenerators` at **build time** to emit an AOT-clean parser +
  typed visitor (`IsAotCompatible=true`, no reflection). The grammar is *baked*; only the `.layout`
  *content* is parsed at runtime by the AOT-safe `Parser`.
- A visitor builds the `LayoutNode` tree from a parsed `.layout` file -- the same target the C# builder
  produces, so the engine is unchanged. (Mirrors LALR.CC's LaTeX example: one grammar, multiple
  visitors -- here one layout grammar could even drive both a pixel arrange and a cell arrange.)
- **Hot-reload in dev** (file-watch -> reparse -> rebuild tree -> redraw), embedded/baked string in
  Release. AOT-clean throughout; the only `PublishAot=false` case in LALR.CC is *runtime grammar
  loading* (Tui via YamlDotNet), which a fixed layout grammar does not hit.
- Sketch:
  ```
  Stack(gap: $pad) {
    Row { Label("Mount") width:auto; DeviceSlot(bind: mount) width:* }
    SiteEditor(bind: site)
    foreach ota in otas { OtaPanel(bind: ota) }
  }
  ```
- **Honest caveat:** the DSL earns its keep through authoring ergonomics + hot-reload, NOT reduced
  implementation effort. The hard semantics (data binding, theming, responsive sizing) are the real
  work and the DSL relocates rather than shrinks them. It is a maintenance surface (grammar, visitor,
  error messages, editor support). dotcc (C99/Zig frontend on LALR.CC) proves the toolchain scales, so
  the *parser* cost is near zero -- but commit to Phase 4 only once Phases 1-3 exist and the tree
  builder is genuinely the bottleneck.

## Consumers in scope (all must participate -- none left behind)

The refactor is not "the GUI tabs only". Every `PixelWidgetBase<TSurface>` consumer adopts the theme
(Phase 1) and, where it hand-lays-out, the engine (Phase 2/3):

- **TianWen GUI tabs** (`TianWen.UI.Abstractions/*Tab.cs` + `VkGuiRenderer`) -- Phase 1 done.
- **TianWen FitsViewer** (`TianWen.UI.FitsViewer`, the standalone `tianwen-fits` AOT binary) --
  **REQUIRED, not optional.** `ImageRendererBase.cs` carries its own duplicated constant set
  (`BaseFontSize=18`, `BaseInfoPanelWidth=300`, `BaseToolbarHeight=40`, histogram/button metrics) and
  hand-rolls its info panel / toolbar / file-list / histogram layout. It must (a) adopt a `UiTheme`
  instance for chrome colours + metrics (its 18px font is a deliberate viewer-scale divergence -> its
  own metrics instance, NOT GuiTheme's 14px), and (b) port its panels to `LayoutNode` trees in 2D/3.
  Versioned in lockstep with the rest of TianWen (6.0.0). Do not ship the layout refactor while the
  FitsViewer still hand-codes constants.
- **TianWen TUI** (`TianWen.Cli`, via Console.Lib cell painter) -- Phase 2C cell painter ready.
- **chess** (`Chess.GUI`/`Chess.Lib`) -- the second-consumer canary.

## Migration strategy

- Incremental, never a big-bang rewrite. Phase 1 ships standalone (pure dedup). Phase 2's engine
  coexists with manual layout; port one tab at a time, asserting GPU/TUI parity per tab.
- chess is the **second-consumer canary** -- porting its history panel + status bar to the Phase-3
  shared widgets validates that the engine is genuinely surface- and app-agnostic, not TianWen-shaped.
- The **FitsViewer is a co-equal first-party consumer**, not a follow-up: its theme adoption rides with
  the GUI's, and its panel ports are scheduled in 2D/3 alongside the tabs.
- Each phase is independently shippable and independently useful.

## Cross-repo release sequencing

Phases 1-3 bump DIR.Lib. Per `/release-lib` + the no-push-before-NuGet rule: bump + push DIR.Lib ->
wait for NuGet -> bump Console.Lib + SdlVulkan.Renderer to the new DIR.Lib -> wait for NuGet -> bump
TianWen's `Directory.Packages.props` (and chess's) to all three. With `UseLocalSiblings=true` the local
dev loop uses ProjectReference and skips the wait; the dance is only for CI / other machines.

## Verification

- Phase 1: existing rendering/snapshot tests stay green with a today's-values theme instance (proves
  no behavioural change); grep shows the duplicated constants gone.
- Phase 2: unit tests for Measure/Arrange (Fixed/Auto/Star splits, nested Stack/Dock, intrinsic sizing
  via a stub oracle); the ported Equipment panel renders identically on GPU and matches the TUI item
  list; hit-regions land on the arranged rects.
- Phase 3: chess + TianWen both compile against the shared widgets with their hand-rolled copies
  deleted; chess GUI + console still render.
- Phase 4: a sample `.layout` renders the same tree on pixel + cell surfaces; AOT publish of
  `TianWen.UI.Gui` stays warning-free (the AOT-clean-parser claim is the gate).

## Out of scope / deferred

- Reactive/retained-mode rebinding, animation, RTL, full flexbox spec.
- Wide-char (East-Asian-Width) terminal measurement -- the cell oracle starts as char-count (today's
  behaviour); the wide-char hook is a follow-up (Console.Lib `TextArea` already documents the gap).
- The DSL (Phase 4) is explicitly gated, not a commitment.

## Related TODO items this subsumes or unblocks

- TODO.md:57 (data-driven per-OTA profile panel) -- becomes a `LayoutNode` tree from the content model.
- `docs/todo/infra.md`: "RollingGraphWidget extracted to DIR.Lib", "Abstract redraw flag propagation",
  "Replace IReadOnlyList<T> with ReadOnlySpan<T>".
- `docs/todo/inbox.md`: "Move RGBAColor32Extensions to DIR.Lib" (Phase 1 neighbour).
