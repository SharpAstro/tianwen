# DIR.Lib 10: a control is declared once, and the engine behaves

Status: PLANNED, HIGH PRIORITY (user, 2026-09-15). Reviewed the same day against DIR.Lib 9.1 and every
tianwen host (GUI, `tianwen-fits`, web, TUI). Nothing started. This plan ABSORBS
[viewer-layout-engine.md](viewer-layout-engine.md) P0 (the measure seam) and
[automatic-text-input.md](automatic-text-input.md) P3b (declarative focus), both of which turn out to be
corners of the same missing piece.

## The ask, in the user's words

> "lets do a review of all the DIR.Lib stuff we use here, and adhere to the goal of having it like a proper
> engine. the usage site of a text box should not have to write in that double click or Ctrl-A while inside
> the control selects text and stuff. Ideally even declaring that the textbox is selectable via Ctrl-F via a
> property/fluid build step should be enough. Think of this as DIR.Lib 10"

The bar is WinForms: drop a `TextBox` on a form and it selects on double-click, cycles on Tab, takes the
clipboard, and a `Shortcut` property on a menu item is the whole of a keyboard binding. Nothing at the
usage site knows those behaviours exist.

## The one-sentence finding

**The engine has most of the BEHAVIOUR and none of the ROUTING.** DIR.Lib 9.1 can place a caret under a
pointer, select the word under a second click and the field under a third, cycle Tab in paint order and
blur a field that scrolled away. Not one tianwen host calls the pointer half, because the engine hands a
host a `HitResult` and stops: what to DO with a `TextInputHit`, a `LinkHit`, a press on a backdrop, a key
while a popover is up, is written again in every host's own dispatcher. That is where the double-click and
the Ctrl+A live today, and it is why there is no place to put a `.Shortcut(...)` even if a node declared
one.

## What the review found, host by host

### 1. The pointer rule shipped in 9.1 and nobody wired it

`TextInputInteraction.HandlePointer(field, caretIndex, clicks, extend, ctx)` is the rule (one click
places, two select the word, three the field, Shift or a drag extends), `PixelWidgetBase.CaretIndexAt(hit,
pointerX)` is the pixel host's half, and every `TextInputHit` the layout painter registers carries the
geometry it needs. tianwen pins `DIR.Lib 9.0.*` and contains zero references to any of the three. Instead:

| host | file | what it does on a press over a field |
|---|---|---|
| GUI | `TianWen.UI.Abstractions/GuiEventHandlerBase.cs:158` | `ActivateTextInput(clickedInput); if (clicks >= 2 && Text.Length > 0) SelectAll();` |
| web | `TianWen.UI.Web/Pages/Planner.razor:787` | the same four lines, copied, in `ActivateTextInput(input, clicks)` |
| TUI | `TianWen.Cli/Tui/TuiEquipmentTab.cs:1189` | keyboard only; `HandleInlineEditInput` calls `input.HandleKey(textKey)` DIRECTLY, never `TextInputInteraction`, so the inline filter-name editor has no Tab, no clipboard, no `OnTextChanged`, and fires `OnCommit` as `_ = input.OnCommit?.Invoke(...)` instead of through the tracker; the field is `new`ed outside any tree (`:1255`), so `CellLayout.TextInputs` cannot see it. The SAME file routes its site editor correctly (`HandleSiteEditInput`, `:1329`), whose own comment calls this shape "a fourth hand-rolled copy" |

So on every surface a click focuses a field and puts the caret at the END, a double click selects the
whole field rather than the word, and a drag selects nothing. The engine already disagrees with all
three.

### 2. Focus is moved by hand in four places, past the owner built to prevent it

`TextInputFocus` exists so that "the transition is expressible exactly one way" (its own doc). Four sites
still reach around it:

| site | what it does |
|---|---|
| `SkyMapSearchActions.OpenSearch` (`:48`) | `search.SearchInput.Activate(); search.SearchInput.SelectAll();` then the CALLER posts `ActivateTextInputSignal` |
| `SessionTab.cs:264` | `State.ExposureInput.Activate($"{seconds}"); SelectAll(); PostSignal(new ActivateTextInputSignal(...))` |
| `TuiEquipmentTab.cs:59` | a private `_activeInlineInput` pointer BESIDE `appState.TextInputFocus`, the two never told about each other |
| `TextInputFocus.Focus(input, initialText)` itself | documents "selecting the whole field's worth of value the way opening an editor on an existing value should" and puts the caret at the end without selecting -- which is WHY the two sites above call `SelectAll` themselves |

`TextInputState.Activate` / `Deactivate` are public, so the owner is a convention, not a boundary. There are
three spellings of "give this field the keyboard" in tianwen: `input.Activate()`, `PostSignal(new
ActivateTextInputSignal(input))` (six sites) and `TextInputFocus.Focus(input)` (the TUI site editor).

### 3. A shortcut is a `switch` in the host, per key

There is no way for a node to say "Ctrl+F focuses me". The sky-map search is `F3`, and the GUI's key
router special-cases it by name (`GuiEventHandlerBase.cs:261`, "F3 is a global shortcut ... Let it fall
through to the active tab even when a text input is focused"); the tab shortcuts are a second hand-written
`Ctrl+letter` map beneath it; the planner's bare `F` is a rating-filter cycle in `PlannerTab.HandleInput`.
The web host re-implements the router (`Planner.razor:918`, `:2651`) and the TUI has a third. Three
copies of "which key reaches which thing while what is focused", none of them the engine's.

The same is true one level down. `ListCursor` (DIR.Lib 8.20, the keyboard's position in a list the tree
already declares) has ZERO references in tianwen; the Up/Down-with-bounds-check-and-EnsureVisible block is
written by hand three times against shared state (`PlannerTab.cs:649`, `TuiPlannerTab.cs:333`,
`TuiSessionTab.cs:250`), and "Escape retires the most recent thing" is a per-widget precedence walk in the
viewer (`ImageRendererBase.Input.cs:285`), the Equipment tab (`DismissActiveState`, five states), the Live
Session tab (prompt, then abort confirm, then running) and each TUI tab's `Mode` enum.

### 4. A popover costs five obligations, and forgetting one is silent

The white-balance and tone popovers each: paint with the dropdowns; register a full-window backdrop FIRST;
set `Ui.KeyboardClaimant` so Escape closes; appear in `ViewerState.OverlayOwnsPointer` so hover resolves in
z-order; clear their slider bands when closed. And a SIXTH: a line in `HandleViewerMouseDown` naming the
action (`is ToolbarAction.WhiteBalance or ToolbarAction.Tone`), because a toolbar press otherwise falls
through to `HandleToolbarAction`, which has no arm for a button that opens a panel. The Tone button
shipped dead for exactly that reason before the line was added, and `CLAUDE.md` now carries a warning
about it. A warning in a doc is what an engine writes when it has no node to put the rule on.

Counted: four hand-rolled dropdown or popover sites (`OpenToolbarDropdown`'s 200-line switch, the two
popovers with their full-window `ButtonHit("...Backdrop")` regions, and `SkyMapTab.Search.DrawSearchModal`,
a whole modal assembled from primitives with no claimant at all); two near-identical ten-line
`IKeyboardClaimant` classes whose whole body is "Escape closes me"; `OverlayOwnsPointer` consulted by hand at
four paint sites; and THREE independent tooltip painters (`ImageRendererBase.Toolbar.RenderHoverTooltip`,
`VkGuiRenderer.DrawTooltip`, `VkPlannerTab.DrawWeatherTooltip`), none with a hover delay, over a
declaration (`TabItem.Tooltip`, `DropdownItem.Tooltip`) the engine owns and refuses to paint.

### 5. A drag is a flag plus three branches, written twice

`ViewerState` carries five drag flags (`WhiteBalanceDragChannel`, `ToneDragSlider`, `WaveletDragBand`,
`IsScrubbing`, `IsResizingFileList`), each with a press, a move and a release branch. `Layout.Node.OnClick`
is `Action<InputModifier>` and carries NO POSITION, so a slider cannot arm its own drag from the node it was
painted on and the viewer opts out of the region model entirely: `ISelfDispatchingInputWidget` is an EMPTY
marker interface whose only purpose is to make `GuiEventHandlerBase` route the raw press to the viewer's
own dispatcher. The before/after split divider was added to one dispatcher and did nothing in the other
(`widgets-and-controls.md`, rule 1).

Six parallel drag implementations, each a state field plus a cached track rect plus begin/update/release:

| drag state | track cache | begin / update |
|---|---|---|
| `ViewerState.ToneDragSlider` | `_toneTrackRects[3]` | `TonePanel.cs:335` / `:306` |
| `ViewerState.WhiteBalanceDragChannel` | `_wbTrackRects[3]` | `WhiteBalancePanel.cs:347` / `:359` |
| `ViewerState.WaveletDragBand` | `_waveletTrackRects[]` | `InfoPanel.cs:205` / `:228` |
| `ViewerState.IsDraggingSplit` | | `SplitCompareController` (the reference shape) |
| `PlannerState.DraggingSliderIndex` | `PlannerSliderInteraction.GetHitBands` | `:95` / `:142` |
| `SkyMapState.IsDragging` | | `SkyMapTab.cs:1553` / `:1361` |

And the wheel: 17 `InputEvent.Scroll` handlers in 11 files, each deciding "is the pointer over my rect" for
itself before handing the delta to its `ListScrollController`. The engine knows which arranged rect is under
the pointer; nothing asks it.

Hit regions generally: 25 `RegisterClickable` calls in 13 files and four app-side `HitResult` subclasses.
The recurring shape is `FillRect(x, y, w, h)`, `DrawText(label, x + gap, ...)`, `RegisterClickable(x, y, w,
h, ...)` as three separate derivations of one rect (`WhiteBalancePanel.cs:164`, `:205`, `:251`;
`InfoPanel.cs:142`, `:149`; the comet label at `SkyMapTab.cs:1081` registers the same four literals it drew
with six lines earlier), which is exactly the drift `.Clickable` on a node exists to make impossible.

### 6. The chrome measures itself

Recounted today (the figure in [viewer-layout-engine.md](viewer-layout-engine.md) was 47; the grep below is
the one to repeat): 55 `MeasureText(` call sites in 16 widget files (45 in `UI.Abstractions`, 6 in
`UI.Shared`, 4 in `UI.Gui`), 42 hand-advanced cursors (`ref float y`, `y +=`) in 14, three width unions, two
tests that sweep 900 x 700 pixels because a hand-laid-out panel gives them no other way to find anything.
The engine's `Measure` is public but the widget base has no seam for it, so even the tone popover builds a
`PixelMeasureContext` by hand.

The line between engine and hand is clean and worth stating: `RenderLayout` is called 91 times in 19 files,
and every `*Tab.cs` (Equipment, Session, Planner, Live Session, Guider, Notifications, Home, the sky-map
search) is on the tree; **every `ImageRendererBase.*` partial except `Layout.cs`, `TonePanel.cs` and
`SelectionPanel.cs` draws its chrome by hand** (`Toolbar.cs` is 2,254 lines of it). Seven containers live in
app code that an engine would own: `DrawTable` (measures its own column stops; the engine's `Grid` splits
columns evenly and has no per-column `Auto`), `DrawCollapsibleHeading`, the file list, the toolbar's own
wrapping flow (`WalkToolbarRows`, which is `Layout.Node.Wrap` written out), the transport bar, the search
modal and the planner's suggestion dropdown (a THIRD popup mechanism beside `DropdownMenuState` and the
popovers). `TextLineAdvance` derives a line height from measuring `"Mgjq"` and every `DrawTextLine` adds it.

The TUI is the counter-example: zero `MeasureText`, six `IRowLayout` row types, `ScrollableList` owns every
scroll and drag, `TuiTabBase` states the rule ("The tree owns placement; widgets own behaviour"). Its two
lapses are the inline editor above and the same hand-written Up/Down blocks. Console.Lib's own
`TextInputBar` / `TextArea` / `GapBuffer` / `Clipboard` are referenced by nothing in tianwen: a second text
stack, unused, beside the DIR.Lib one.

### 7. Inventory: who owns what today

For scale: `Layout.Node` carries exactly `Width`, `Height`, `Padding`, `PaddingY`, `CrossAlign`, `Background`,
`HoverBackground`, `FocusBackground`, `CornerRadius`, `Hit`, `OnClick`, `Cursor` and `CollapseThreshold`; `Content`
is `Text`, `Box`, `Icon`, `TextInput`, `Fill`; `Layout.Engine` is two public methods (`Measure`, `Arrange`).
`PixelWidgetBase.HandleInput` is `virtual => false`, and nothing inside DIR.Lib calls `TextInputInteraction.HandleKey`
or `HandlePointer` except their tests. Every one of the popover's behaviours DOES exist for the dropdown, inside
`RenderDropdownMenu` (backdrop, Escape, disabled rows swallowed with a `NotAllowed` cursor), as a protected method
with ten parameters and the obligation "must be called last in the render pass". The engine knows how; it has no
node to know it ON.

| behaviour | engine (DIR.Lib 9.1) | host / consumer |
|---|---|---|
| draw a field, register its hit + I-beam, Tab order | yes (`Builder.TextInput`) | |
| typing, Backspace, Home/End, Enter, Escape, Ctrl+A/C/V | yes (`TextInputInteraction.HandleKey`) | hosts must CALL it with a `KeyContext` they assemble |
| click places caret, double-click word, triple field, drag extends | yes (`HandlePointer`, 9.1) | NOT CALLED; hosts hand-roll `clicks >= 2 -> SelectAll` |
| which field has the keyboard | yes (`TextInputFocus`) | bypassed at 4 sites; `Activate` is public |
| a field asks for focus when its dialog opens | | posted by hand (`ActivateTextInputSignal`, 6 sites) |
| a key binding on a control | | host `switch` per key, 3 copies |
| a global key that beats a focused field | | `if (key == F3) return false;` |
| popover: backdrop, Escape, outside-press, hover z-order, open-from-button | partly (`DropdownMenuState`, `IKeyboardClaimant`) | 5 obligations + a dispatcher line per popover |
| slider drag | paint only (`DrawTrackSlider`) | 5 flags x 3 branches x 2 dispatchers |
| a press with a POSITION reaching the node that was hit | | `ISelfDispatchingInputWidget` bypass |
| hover background, pointer cursor, list cursor | yes (`HoverBackground`, `Cursor`, `ListCursor`) | |
| disabled row with a reason | dropdown only (`DropdownItem.Disabled`) | greyed by hand elsewhere |
| box = measurement of content | `Engine.Measure` public | no widget seam; hand sums |
| selectable read-only text (Ctrl+A / Ctrl+C / drag on a label) | region only (`SelectableTextRegion`, DOM host acts) | nothing on the raster hosts |
| Ctrl+Left / Ctrl+Right word motion, Ctrl+Backspace, Ctrl+X | | none anywhere: `TextInputKey` has no word keys and no Cut |
| undo | **the README claims it** ("cursor, selection, undo") | there is no undo stack, no `Ctrl+Z` mapping |
| a long value scrolls inside its field | | `TextInputRenderer` draws from the left edge and lets the rect clip (noted in `CellLayout.cs`, left alone "rather than half-fixed on one surface") |
| drag state (which button is down, with which modifiers) | | `MouseUp` carries no modifiers or click count, `MouseMove` no button; every drag remembers them from the press (`TapOrDragGesture` says so) |
| nested keyboard claimants | one-deep slot (`Ui.KeyboardClaimant`) | a modal over a popover cannot both claim; last painter wins, nothing restores |
| a focus ring | colour role only (`UiPalette.Focus`) | nothing in DIR.Lib draws one; `.BgFocus` is list-scoped |
| list navigation on the terminal | `ListCursor` + `ListScrollController` on pixels | `ScrollableList<T>` in Console.Lib is a second, independent implementation of both |

## What DIR.Lib already has that tianwen does not use

The text box was the example; this is the general case. Counted 2026-09-15 over the six UI projects
(`*.cs` + `*.razor`, `obj`/`bin` excluded). Two facts frame it: all 693 `Layout.Builder.*` calls are in
`UI.Abstractions` (582), `Cli/Tui` (61) and `UI.Gui` (50), and `UI.Shared`, `UI.FitsViewer` and `UI.Web`
contain no layout-tree code at all; and inside `UI.Abstractions` the hole is the viewer's chrome partials
(`Toolbar.cs` 2,254 lines, `FileList.cs`, `WhiteBalancePanel.cs`, `InfoPanel.cs`, `Transport.cs`,
`Histogram.cs`, `ContextMenu.cs`), which have ZERO `Builder` calls between them: `Layout.cs` arranges the
five panes and hands each to an imperative painter through a `Fill` key.

### The layout tree: shipped, unused, re-implemented

| the engine has | tianwen uses | tianwen writes instead |
|---|---|---|
| `.WithGap(g)` | 38 | 73 `Spacer().WFixed(pad)` nodes counted by grep, **of which only 4 were uniform between-sibling gaps** when the sweep looked at each: the rest are start/end padding, non-uniform gaps, or root fills into an explicit rect where `.Bg` is the honest spelling. A count from a grep is a ceiling, not a yield |
| `HoverBackground` / `.BgHover` (8.1) | **0** | 5 hover tests at the call site (`FileList.cs:250` `rowRect.Contains(mouseX, mouseY)`, `Toolbar.cs:456`, `Histogram.cs:83`, `VkPlannerTab.cs:296`) plus the repaint bookkeeping the feature removes (`_lastHoveredToolbarButton`, `_lastHoveredFileListRow`, `hoverRepaint`, about 50 lines of `Input.cs`) |
| `FocusBackground` / `.BgFocus` + `ListCursor` (8.20) | **0** | 9 `isSelected ? SelectedBg : RowBg` ternaries and about five `SelectedIndex` fields, on rows that ALREADY register `ListItemHit` (14 sites), so they are navigable-shaped and nobody asked |
| `IconKind.Plus` / `Minus` / `CaretUp` / `CaretDown` | ~~0 / 0 / 0 / 1~~ **DONE 2026-09-16** | Was "19 marks as text runs". Every step, jog and pan mark now resolves through `FormRowLayout.StepMark`, which maps the glyph to an `IconKind`; the call sites still pass a glyph STRING, which is why a grep for the literals still finds them and why this row read as outstanding. Two deliberate exceptions remain, both correct: the coarse-jog guillemets in `VkPlanetaryTab` fall through to a text run because `IconKind` has no member for them, and the TUI's bracketed pair are cell-surface characters, where a character IS the mark |
| `IconKind.List` | 0 | `DrawFileListMark`, whose comment re-derives the kind's own rationale ("a face without the codepoint draws .notdef") |
| `IconKind.Search` + `TextInput.LeadingIcon` (8.11) | 0 | two search fields, neither marked |
| `MatchIconsToText` (8.10) | never fires | all four `Icon` calls state a size; `LiveSessionTab.Strips.cs:69` derives it from the sibling label by hand, the exact duplication 8.10 removed |
| `Text.Trim` (`TextTrim.End` / `Middle`) | **0** on any node | three imperative `TextFit.TrimToWidth` / `ForWidth` calls (`FileList.cs:141`, `:267`, `Toolbar.cs:2053`) |
| `Text.WidthSample` | 5, all in `TonePanel.cs` | `ReservedLabelWidth`, `ReservedButtonWidth` (6 call sites), `DeviceList.cs:238` measuring `"Discovering..."`, `SessionTab.cs:623` sizing a stepper cell to its widest value |
| `Node.Anchored` (8.12) | **0** | `OverlayPlacement.cs` (86 lines: `ClampX`, `ClampY`, `Place(anchor, ...)`) is its doc comment re-implemented, 6 call sites, plus `VkGuiRenderer.DrawTooltip`'s private copy |
| `Node.Overlay` | **0** direct | every popover, menu and tooltip is a separate post-pass with `OverlayOwnsPointer` as the hand-kept z-order answer and `RenderHoverTooltip` "called LAST in the frame" |
| `Node.Wrap` (`WrapH`) | 1 | `WalkToolbarRows` (50 lines: `if (x + gap + w > limit && row + 1 < maxRows) { row++; x = pad; }`) and `DrawWrappedTextLine` |
| `Node.Dock` | 11 | the older imperative `PixelLayout.Dock(PixelDockStyle.Right, w)` cursor, 10 calls in `SessionTab.cs` and `PlannerTab.cs`; `Layout.cs:167` carving the transport strip out of the image `Fill` by hand |
| `Node.Grid` (+ `AutoRows`) | 1 | `DrawTable` ("the stops are measured, not assumed") |
| `Node.Split` + `DividerHit` | 1 (the file list, the model use) | `SplitCompareController` (330 lines) + `RenderSplitDivider`: a second divider drag with its own track, fraction, clamp and hit rect |
| `.CollapseBelow` | 2 | `AltitudeChartRenderer.VerticalLayout` returning `TitleVisible = false` "when the chart is too short"; `HomeBoardLayout.cs:366` switching to a table by hand |
| `.CrossCenter()` / `.Align` | 4 / 0 | 110 `Builder.Text` calls passing BOTH aligns positionally plus `.HStar()`; in imperative chrome `(h - fontSize) / 2f` in `Transport.cs:52`, `ImageRendererBase.cs:1631` and all nine `Draw*Mark` helpers |
| `.Pad(across, down)` / `PaddingY` (7.24) | 0 / 0 (`.PadX` 2) | every fixed-height bar pads symmetrically or by arithmetic (`Transport.cs:44`) |
| `.Radius` | 5, all Home board | none by hand either: `FillRoundedRect` has 0 callers, so every other panel is square |
| `Layout.Engine.Measure` | **1** (`TonePanel.cs`) | 55 `MeasureText` sites; `WhiteBalancePanel.cs:83` sums "what the row needs in its widest state" from three reserved widths |
| `LayoutDamage.Compute` / `Coalesce` (8.8) | **0** | `ImageRendererBase.Damage.cs` (86 lines) is a hand-kept replacement with 3 narrowing sites, consumed ONLY by `tianwen-fits`; **the GUI repaints whole frames** |
| `Builder.Box` (swatch / rule) | 5 | 41 `Spacer().Bg(colour)` nodes by grep; 10 were sized in-tree rules and became `Box`, about 16 are tree ROOTS painted into an explicit rect (a wash, correctly `.Bg`), the rest padding cells that carry a colour |
| `Builder.Progress` | 2 | `LiveSessionTab.Polar.RenderErrorBar` as three `FillRect`s; `InfoRowItem.cs:135` `new string('█', filled)` |
| `DesignScale` | 9, six of them `DesignScale.One` | six trees authored in device pixels that opt out of engine scaling |

Zero-use features judged **unneeded rather than unknown**: `WrapV` / `.WithLineGap` (no vertical-flow
surface), `.WClamp` / `.HClamp` (reachable through `.WStar(w, min, max)`, which is used), `.Align` other
than centre, `.WithCursor` (the `cursor:` argument on `.Clickable` covers the four real cases),
`ArrangedNode.Depth` (the inspector's, not a consumer's). Everything else in the table is unknown, not
unneeded: each has a hand-written twin in the same codebase.

### The widget base and the controllers: the same picture

| the engine has | tianwen uses | tianwen writes instead |
|---|---|---|
| `RenderButton` | 2 (`DeviceList.cs`) | **8 `FillRect` + `DrawText` + `RegisterClickable` triples** (`Histogram.cs:97`, `InfoPanel.cs:144`, `:152`, `Transport.cs:63`, `:77`, `WhiteBalancePanel.cs:166`, `:208`, `:263`) and the toolbar's own fill/draw/register loop over every button (`Toolbar.cs:460`) |
| `MeasureButtonWidth` | 0 | `WhiteBalancePanel.cs:162` `MeasureText(autoLabel, FontSize) + gap * 2f`, the method's body verbatim |
| `MeasureValueColumnWidth` (widest of a set) | 1 | `ReservedButtonWidth` and `ReservedLabelWidth`, the same loop written twice |
| `TextFit.TrimToWidth` | 3 | `EquipmentTab.TruncateToWidth` (26 lines, a binary-search ellipsis fit), 3 call sites |
| `RenderDropdownMenu` + `DropdownMenuState` | 4 instances | **two hand-built result lists**: `PlannerTab.RenderSuggestionDropdown` and `SkyMapTab.BuildSearchResults`, neither with a state object, a scroll, a keyboard claim or a disabled row |
| `MenuLayout` / `MenuModel` / `PixelMenuWidget` | **0** | the viewer's `?` panel (`HelpPage`, `BuildHelpLines`, `PumpHelpPanel`) |
| `TapOrDragGesture` | 1 (the sky map's tap-or-drag verdict) | five press/drag state machines: `IsScrubbing` + `_scrubTrackRect`, `DraggingSliderIndex`, `SkyMapState.IsDragging`, `SplitCompareController.IsDragging`, `VkPlanetaryTab._pipDragging` |
| `PanZoomController` | 2 | the sky map's own `HandleDragStart` / `HandleDrag` / `HandleZoom` / `HandleZoomByFactor` and a runaway-zoom detector; the spherical centre must stay bespoke, the anchor/clamp/step arithmetic need not |
| `ListCursor` / `HandleListKey` | **0** | three Up/Down/Enter loops (`PlannerTab.cs:649`, `SessionTab.cs:175`, `ImageRendererBase.Input.cs:502`), each with its own ensure-visible pairing |
| `HitTestCursor` as the ONLY cursor source | 3 | `tianwen-fits` `Program.cs:808` re-derives one as a host predicate: `HitTest(mx, my) is ResizeHandleHit ? CursorKind.ResizeEW : Default`, the thing the rule forbids |
| `GetRegisteredRegions()` / arranged nodes as the answer to "where did I paint that" | | **six hand-kept rect caches** written every frame and read on the next press: `_wbTrackRects`, `_toneTrackRects`, `_waveletTrackRects`, `_toolbarButtonBounds`, `_toolbarBoxes`, `SessionTab._exposureValueRegions` |
| `WindowUiSettings.Focus` (per window, shared by `ShareUiContext`) | **0** | a SECOND instance of the same type app-wide, `GuiAppState.TextInputFocus`, which every path uses; the per-widget one is dead weight in every widget |
| `BackgroundTaskTracker.Run` | 39 | **21 fire-and-forget sites**, 15 of them in `Planner.razor`, which constructs a tracker on line 137 and uses it five times; 3 untracked with a tracker in scope (`PlanetaryCaptureController.cs:539`, `TuiEquipmentTab.cs:717`, `StackSubCommand.cs:599`); `async void` is zero |
| `DropdownItem.Tooltip` / `TabItem.Tooltip` (declared, deliberately not painted) | 0 / 1 | three tooltip painters (see above) |
| `FontFallbackResolver.CanRender` | 0 | nothing checks coverage before choosing a mark, against `CLAUDE.md`'s own rule that a new mark is picked by what the codepoint is |
| `RgbaImageRenderer` + arranged nodes in tests | 40 files | **4 pixel-sweep tests** still loop x/y calling `HitTest` (`SessionTabTests.cs:187`, `ViewerWhiteBalancePopoverTests.cs:152`, `:272`, `ViewerInfoPanelCollapseTests.cs:129`) |

Clean, and worth saying so: `ListScrollController` (7 instances, no raw scroll offset anywhere),
`SearchInteraction<T>` (both searches are subclasses with every virtual overridden), `TabBar` /
`TabStripTree`, `PushClip` (12, zero hand-built `RectInt`), `SignalBus` + the generated
`SignalDirectory`, `UiTheme` (tianwen DERIVES its palettes and pushes them back into
`TextInputRenderer.Colors`), `KeyDown.Repeat` and `KeyUp` (consumed; `RepeatsAsAStep` is a policy
allow-list over the library's fact, not a re-derived filter), `MouseDown.ClickCount` (no hand-timed
double click anywhere), the SDL event pump (zero hand-written `SDL_EVENT_*` switches), the cursor
mapping, `IActivatableWindow`, and the TUI's `IRowLayout` rows (`RowPen` wraps, it does not
re-implement). On the terminal only two things are duplicated: the inline editor and a second sparkline
(`TuiLiveSessionTab.SparkChars`) beside `AsciiAltitudeChart`. Console.Lib's `TextInputBar`, `TextArea`,
`GapBuffer`, `Clipboard`, `TextTable` and `TreeView` are unused, and rightly: the TUI took the DIR.Lib
text stack instead. Zero-use and unneeded: `ContentTransform`, the Markdown and MathLayout namespaces,
`TabBar`'s new-tab affordances, `ListScrollController`'s tuning knobs, `WrapsAround`.

### The payoff, ranked by sites deleted

| # | hand-rolled | declares instead | sites |
|---|---|---|---|
| 1 | spacer-as-gap | `.WithGap` | 4 nodes converted (73 matched; see the table) |
| 2 | the viewer's imperative chrome inside the five `Fill` panes | subtrees under the arrangement `Layout.cs` already makes | about 55 draw/hit sites (25 `RegisterClickable`, 28 cursor advances) plus four helpers (`DrawTextLine`, `DrawSectionHeading`, `DrawTable`, `DrawWrappedTextLine`) |
| 3 | marks as text runs | `IconKind.Plus/Minus/CaretUp/CaretDown` | ~~19~~ 0 (done) |
| 4 | `isSelected ?` row backgrounds + selection-index plumbing | `.BgFocus` + `ListCursor` | 9 ternaries, about 5 fields |
| 5 | call-site hover + repaint bookkeeping | `.BgHover` (+ `LayoutDamage`) | 5 sites, about 50 lines |
| 6 | `ImageRendererBase.Damage.cs` | `LayoutDamage` | 86 lines, 4 sites, and narrowing reaches the GUI |
| 7 | `OverlayPlacement` | `Anchored` + `Overlay` | 86 lines, 6 sites, one private copy |
| 8 | `ReservedLabelWidth` / `ReservedButtonWidth` | `WidthSample` | 2 helpers, 8 sites |
| 9 | `WalkToolbarRows` | `Wrap` | 50 lines, 4 fields |
| 10 | `PixelLayout` docking cursors | `Dock` | 10 calls, 2 files |
| 11 | fire-and-forget async in the web host | `_tracker.Run` | 15 sites, one file |
| 12 | button triples | `RenderButton` (until T3 makes them nodes) | 8 triples plus the toolbar loop |
| 13 | six per-frame rect caches | the regions / arranged nodes the paint already registered | 6 fields |
| 14 | two hand-built result lists | `RenderDropdownMenu` + `DropdownMenuState` | about 90 lines, and they gain scrolling |
| 15 | `TruncateToWidth` | `TextFit.TrimToWidth` | 26 lines, 3 sites |
| 16 | the pixel-sweep tests | arranged-node assertions (the tone test is the model) | 4 tests |

**Every row in that table is a tianwen-only change on the pin we have**, except that rows 3, 4 and 13
finish properly only once `OnPress` and `Content.Slider` exist (D1); until then the drag half stays.
None of it waits for D1 to START, and none of it is the text box. That is the finding the user predicted: the principle is violated more
widely than the example, and most of the fix is adoption, not engine work.

## Resume here (state as of 2026-09-15, late evening)

**SUPERSEDED 2026-09-15: everything below LANDED, plus waves 2 and 3, the release chain, T1 and T2.**
Kept as the record of what was outstanding, not as a to-do list. Where it stands now:

- **Published:** DIR.Lib **9.2.3051**, Console.Lib **4.35.1841**, SdlVulkan.Renderer **7.38.3151**.
  tianwen `main` pins all three. **DIR.Lib 9.3.3111 published 2026-09-16** (PR #84, the dropdown node).
  The rest of that release chain is NOT done: Console.Lib / SdlVulkan.Renderer / WebGl.Renderer still have
  to be rebuilt against 9.3 before tianwen's pin moves from `9.2.*` to `9.3.*`, and they move together --
  which the 9.1 `MouseUp`/`MouseMove` lesson makes mandatory rather than optional.
- **Landed in tianwen:** #267, #269, #270, #271, #273, then **T1** (#275, the router) and **T2** (#276,
  popovers and sliders). Gone as types: `OverlayPlacement`, `ISelfDispatchingInputWidget`,
  `TextFieldPointerInteraction`, `ICaretPlacingWidget` and its six implementations, two bespoke keyboard
  claimants, two slider hit types, two drag flags, the Ctrl+letter map and the F3 special case.
- **Still open:** T3 (the viewer chrome onto trees, `.BgHover`, `LayoutDamage`) -- which now also carries
  what the dropdown item wrongly claimed, `OpenToolbarDropdown`'s switch and `PumpHelpPanel`; **D2 / 10.0**
  (the cuts, including `RenderDropdownMenu` once the migration below lands); C1; and the two engine seams
  T1 worked around (a `TabItem` that can carry neither a chord nor a select handler; two sibling top-level
  widgets that cannot share one `WindowUiSettings`). The only genuinely independent piece left of the
  dropdown item is the menu migration, which is a branch away and adds lines rather than removing them.
- **The dropdown is its own item, and it is ADDITIVE.** It was listed under D2 above until 2026-09-16,
  which reads as "wait for the major" and is wrong: a `Builder` node over `PopoverState` breaks nothing,
  so it can go out in a 9.x whenever someone has the afternoon. It is still the biggest remaining
  hand-written chrome and it DELETES code on the tianwen side, including the close-then-reopen-next-frame
  dance in `PumpHelpPanel`. Landing it as a second mechanism beside `Popover` is the way to get this
  wrong; it is the same shape 9.2 already solved for panels.
- **The ENGINE half of the dropdown landed 2026-09-16 in DIR.Lib 9.3.3111** (PR SharpAstro/DIR.Lib#84).
  `Layout.Builder.Dropdown(anchor, state)` composes from what already existed -- `Popover` for the backdrop
  and the Escape claim, `.Disabled(reason)` for a refused row, `.BgHover`, `.WithScroll` -- and
  `DropdownMenuState` now HOLDS a `PopoverState` with `IsOpen` reading and writing through it, so the
  declared menu and the rendered one cannot disagree. `RenderDropdownMenu` and the anchor fields STAY, so
  nothing has to move until the tianwen side is done.
  The chain is walked as of 2026-09-16 -- DIR.Lib `9.3.3111`, Console.Lib `4.36.1861`,
  SdlVulkan.Renderer `7.40.3221`, WebGl.Renderer `1.30.481`, tianwen `8.2.17821`, pins following -- so
  nothing is blocked on a package any more.

  **But this item was mis-scoped, and the correction matters more than the unblocking.** It reads as one
  independently deliverable piece. It is not. Taking its three named parts in turn:

  1. **The two hand-built result lists were ALREADY done.** Sweep B converted
     `PlannerTab.RenderSuggestionDropdown` and `SkyMapTab.BuildSearchResults` to arranged trees with
     `ListCursor` + `.BgFocus` -- "the dropdown is ONE arranged tree ... draw==hit suggestion rows" is in
     `PlannerTab` now. That work is on `main`. They should also **stay their own mechanism**: a `Popover`
     brings a full-window backdrop that dismisses on any outside press, which is right for a menu opened
     from a button and wrong for a suggestion list under a text field, where a click elsewhere in the
     panel must not be swallowed by a scrim. "A THIRD popup mechanism" is not automatically a defect.
  2. **`OpenToolbarDropdown`'s switch and `PumpHelpPanel` are T3 work, not this item's.** The switch opens
     with `TryGetPaintedToolbarRect(action, out var bounds)` -- it reads geometry back out of hand-painted
     chrome. `ImageRendererBase.Toolbar.cs` is 2,309 lines with 17 hand-paint calls and 2 layout-tree
     references. What deletes the switch is a toolbar BUTTON that can carry
     `.Clickable(hit, _ => state.Popover.Toggle())`, and a hand-drawn button has no node to carry it. The
     183 lines are menu CONTENT and per-action behaviour; declaring the menu relocates them next to the
     button, it does not remove them. Same for `PumpHelpPanel`, whose anchor is `_helpAnchor` off the same
     painted rect.
  3. **What IS available now is the menu migration, and it ADDS lines.** All four `DropdownMenuState`
     instances (filter name, profile, live-session mode, and the viewer toolbar -- which is also the help
     panel, same state with different content) move from `RenderDropdownMenu` to
     `Layout.Builder.Dropdown`. Branch `refactor/dropdown-as-declaration`, pushed, +80/-37. Each site now
     states its own anchor rect and `maxHeight` because `RenderDropdownMenu` computed both internally and
     a `Builder` call has no viewport to derive them from. **`maxHeight` is not cosmetic:** it is the
     clamp to the space between anchor and bottom edge, and it is what engages the scroll on a long menu.

  So the item's real payoff is deferred to **D2**: with zero consumers left, `RenderDropdownMenu` -- 127
  lines, a ten-parameter signature and a "must be called LAST in the render pass" obligation -- becomes
  cuttable. Trading +43 lines here for that cut is a good trade; it is not the deletion this entry
  claimed.
  **Two things that half learned, and a T3 session will hit both:**
  1. **`.Disabled(reason)` STRIPS a handler; it does not create a region.** A row declared disabled without
     a `Hit` registers nothing, so its press falls through to the backdrop and DISMISSES the menu -- the
     click looks like it did nothing, which is the dead-end the disabled state exists to remove. Every row
     declares its hit whether or not it is enabled.
  2. **Painting a popover takes the window's single keyboard-claimant slot.** So anything inside it that
     implements `IKeyboardClaimant` is never asked, and a declared menu opened and dismissed but could not
     be navigated. `PopoverState.ContentKeys` (new in 9.3) is how content reaches the keys the popover does
     not handle; Escape stays the popover's. Any T3 chrome put inside a popover has the same problem.

**Three bugs were found by the consumer adopting a feature, not by the tests written for it**, and all
three are the same shape: a rule gated on an opt-in the host never set, failing silently. `PointerOwner`
never released on a host leaving `FrameId` at 0; the Tab ring spanning every tab ever visited, same
cause; and two `IconKind` members with no cell glyph, because adding a kind is a TWO-REPO change.
**Adding an `IconKind` upstream means adding its `CellLayout` glyph in Console.Lib in the same wave.**

### Picking this up on another box

The campaign ran on the desktop (win-x64) and may go back to the Surface (win-arm64). **This file is the
state.** A session task list does not travel and memory does not sync between the two boxes, so anything
that is not written here is not handed over. The plan was itself unfindable on the desktop once for
exactly that reason.

**Pull every sibling before building anything.** `UseLocalSiblings` is all-or-nothing and self-enabling,
so tianwen compiles against whatever sibling SOURCE is checked out and the `PackageReference` version is
never exercised. A pre-9.2 `DIR.Lib` checkout does not fail as a version mismatch: T1's and T2's code
does not compile at all (no `InputRouter`, no `Popover` node, no `Content.Slider`), which reads as tianwen
being broken rather than as a stale sibling. The floor is DIR.Lib **9.2.3051**, Console.Lib **4.35.1841**,
SdlVulkan.Renderer **7.38.3151**; `dotnet build -c Release -p:UseLocalSiblings=false` is how to check what
CI would restore, and pulling the siblings is the fix, never turning the switch off.

Everything below is the pre-landing record.

### tianwen, four draft PRs, all green on the full unit suite run ALONE on the box

| branch | base | what | suite |
|---|---|---|---|
| `docs/dir-lib-10-plan` (#268) | `feat/tone-popover` (#267) | this plan, both audits, the D1 spec, this section; also corrects CLAUDE.md and the web host on where the SDL key map lives | docs |
| `feat/text-field-pointer` (#269) | `main` | T0: `TextFieldPointerInteraction`, both pixel hosts on `HandlePointer`, TUI inline editor on `TextInputInteraction`, `OpenSearch` through the focus owner, pins DIR.Lib `9.1.*` + Console.Lib `4.34.*` | 6114 / 0 |
| `refactor/layout-gaps-and-boxes` (#270) | `main` | sweep A: 4 gaps, 10 rules to `Box`, `TruncateToWidth` deleted | 6110 / 0 |
| `refactor/list-cursor-and-dock` (#271) | `main` | sweep B: two lists on `ListCursor` + `.BgFocus`, `PixelLayout` gone | 6113 / 0 |

Merge order: #267 first (the plan branch retargets to `main` on its own when GitHub drops the merged base;
if it does not, retarget by hand). The three code branches are independent of each other and of #267.
They will rebase cleanly onto each other except possibly `Directory.Packages.props` (T0 moves two pins).

### DIR.Lib, one integration branch

`feat/dir-lib-9.2` off `main` (b07c547), draft PR SharpAstro/DIR.Lib#82, carries wave 1a (`feat/dir-lib-9.2-wave1a`) and wave 1b
(`feat/dir-lib-9.2-wave1b`) BOTH MERGED and green: 1235 tests, up from 1102 on main. Its draft PR body lists what each wave shipped with exact signatures.
`VersionMajorMinor` is still 9.1 on purpose: the release is cut from this branch when wave 3 is green.
`CHANGELOG.md` already has the `## 9.2` section, one block per wave.

### What is NOT done, in order

1. **Wave 2** (slider, popover) and **wave 3** (the `InputRouter`), specified above under "D1 as
   specified for implementation". Each is one opus agent in a DIR.Lib worktree branched off
   `feat/dir-lib-9.2`, reading the spec from this file (`git -C <tianwen> show <branch>:docs/plans/dir-lib-10.md`).
   Wave 3 depends on wave 2; nothing else does.
2. **The 9.2 release chain**: bump `VersionMajorMinor` to 9.2 on `feat/dir-lib-9.2`, merge, wait for NuGet,
   then Console.Lib 4.35 / SdlVulkan.Renderer / WebGl.Renderer rebuilt against it (the `MouseUp` / `MouseMove`
   records make the chain mandatory, see the 9.1 lesson), then tianwen's pins move together. `/release-lib`.
3. **T1** (tianwen on the router; delete the three routers, the tab-shortcut switch, F3; `.WithShortcut(F, Ctrl)`
   on the search box; `SessionTab`'s exposure edit and `CloseSearch` through the focus owner; the three
   hand-written Up/Down blocks the sweep could NOT convert, once `ListCursor.Open(..., count)` exists),
   **T2** (popovers and sliders as nodes; delete `OverlayOwnsPointer`, the five drag flags,
   `ISelfDispatchingInputWidget`, `OpenToolbarDropdown`'s switch), **T3** (the viewer chrome onto trees,
   `.BgHover`, `LayoutDamage` in the GUI), **D2** (the 10.0 cuts, Console.Lib major), **C1**.
4. **T0b leftovers** that need no engine change and no agent has taken: `OverlayPlacement` to `Anchored`, the 15 web-host
   fire-and-forget calls onto the tracker, the two hand-built result lists onto `RenderDropdownMenu`, the
   six per-frame rect caches, the four pixel-sweep tests.

### Rules the next session must not relearn

- **One full test suite at a time on this machine, whatever the worktree.** Three agents were each told
  "never two suites at once" and ran three; the user called it "a really stupid move". Agents run narrow
  filters freely and the full suite only on an explicit "go" from the coordinator, one at a time.
- A grep count is a ceiling, not a yield (73 spacers matched, 4 were gaps).
- An optional parameter on a record's primary constructor is a binary break (the 9.1 `TextInputHit`
  lesson); `UseLocalSiblings` hides it; a `.claude/worktrees/` checkout takes the package path and shows it.
- The user wants sub-agents for grunt work and the judgement calls kept as "stop and report" in the brief.

## The DIR.Lib 10 shape

One principle: **a node DECLARES, the engine BEHAVES, a host BINDS the platform once.** Concretely, six
pieces. The first is the one the other five hang off.

### A. `InputRouter`: the dispatcher every host has been writing

A DIR.Lib class that owns the frame's regions and answers `Handle(InputEvent)`. Given what the last paint
registered (regions, text inputs, popover, drag captures), it does what today's three routers do, in one
order, once:

- **Pointer down:** an open popover's backdrop first (close it, consume); then the hit. A `TextInputHit`
  goes to `HandlePointer` with the caret index from the host's `CaretIndexAt` and the platform's click
  count. A `LinkHit` raises the router's `OpenUrl` event. A node with an `OnPress` gets the press WITH its
  position and may return a `DragCapture`. Else `OnClick`. Then: a press that is not on a field blurs the
  focused one.
- **Pointer move:** the active capture, else hover (the router sets `Pointer` and asks for a redraw only
  when a `HoverBackground` node's state changed), else the widget.
- **Pointer up:** releases the capture, which gets the release coordinates.
- **Wheel:** the innermost painted node under the pointer that declares `.Scroll(controller)` gets the delta;
  17 "is the pointer over my rect" checks become none.
- **Key down:** the popover (Escape closes, arrows walk its rows), then any painted node's declared
  `Shortcut` whose modifier class beats the focused field (see C), then the focused field through
  `TextInputInteraction.HandleKey`, then `ListCursor`, then the widget's `HandleInput`.
- **After paint:** `BlurIfUnpainted`, the caret rect for the IME, and the once-per-open `FocusOnOpen` (see
  D). This is the hook P3b was waiting for: the router sees the painted set every frame, so "first painted
  node that asked, once, since it was last not painted" is a two-line rule.

Hosts keep what is genuinely theirs: SDL `StartTextInput` on `FocusChanged`, the clipboard delegates, the
tab-switch policy. `GuiEventHandlerBase` shrinks to that. `Planner.razor`'s copy and the TUI's inline path
are deleted, because a terminal host is a host like the others: `CellLayout` already registers the same
`TextInputHit`.

### B. A press carries where it landed

`Layout.Node.OnPress : Func<PointerPress, DragCapture?>?` beside `OnClick`, where `PointerPress` is
`(X, Y, Button, Modifiers, Clicks)` in surface units and `DragCapture` is `(Move(x, y), Release(x, y))`.
A slider arms its drag from the node it painted, so "draw == hit" becomes "draw == drag" by construction
and `ISelfDispatchingInputWidget` has nothing left to mark. The capture also carries the button and modifiers
from the press, which is what every consumer drag stores by hand today because `MouseUp` and `MouseMove` do not. `SliderHit(int)` goes: a slider is a
`Content.Slider(SliderState)` leaf (E) whose drag the engine owns.

### C. A shortcut is a property of the node

`.Shortcut(InputKey key, InputModifier mods = None)` on `Layout.Node`. The router matches it against the
PAINTED tree, so a shortcut on a hidden panel is inert without anyone saying so, the same rule
`BlurIfUnpainted` and the keyboard claimant already follow. What a match does depends on the node:
a `TextInput` leaf is focused (and seeded-and-selected, D), a `.Clickable` node is clicked with the
modifiers, a `Popover` is opened.

The precedence question is the one the F3 special case answers by hand today, so it is stated once: **a
shortcut with Ctrl, Alt or a function key beats a focused field; a bare letter or Shift+letter does not.**
That is what makes `Ctrl+F` reach the search box while you are typing in another field, and what keeps a
bare `F` a letter while a field has the keyboard. The tab map (`Ctrl+H/E/P/...`) becomes nine `.Shortcut`
declarations on the sidebar's tab nodes and the hand-written map goes.

### D. Focus is a boundary, and it selects

`TextInputState.Activate` / `Deactivate` become internal; only `TextInputFocus` transitions a field (the
cut the 8.x design described and did not enforce). `Focus(input, initialText)` selects the seeded text, as
its doc already promises, so the two `SelectAll()` call sites and the TUI's private pointer have nothing
left to do. `Builder.TextInput(state, ..., focusOnOpen: true)` is P3b, implemented in the router's
after-paint hook.

### E. Two content kinds the chrome keeps painting by hand

- **`Content.Slider(SliderState)`**: `DrawTrackSlider` as a leaf, with `SliderState { Value, Min, Max,
  Enabled, OnChanged }`. The engine paints it, registers it, arms and tracks its drag (B) and steps it on
  Left/Right when it has the list cursor. The tone popover's three `Fill` leaves become three of these
  and `DrawToneFill` is deleted.
- **`Content.Text` with `.Selectable()`**: on a raster host the router runs a `TextSelection` controller
  over `SelectableTextRegion`s (drag selects, double-click a word, Ctrl+A the run, Ctrl+C copies), which
  is the user's "Ctrl-A while inside the control selects text" for a READ-ONLY label. The DOM host keeps
  its native span. The planner's detail lines and the viewer's info strip are the first consumers.

Two smaller ones the container count asks for: `Grid` columns sized `Auto` (measured to the widest cell)
as well as evenly, so `DrawTable` is a `Grid` with `TextAlign.Far` cells and no column arithmetic; and
`.Scroll(ListScrollController)` on a node, which is what lets the router deliver the wheel (A) and lets a
list state its viewport once instead of calling `SetExtent` beside its paint.

### F. `Popover` is a node, and `Disabled` is on every node

`Layout.Builder.Popover(anchor: RectF32, content: Node, state: PopoverState)` paints on the overlay layer
with a full-window backdrop under it, owns pointer z-order (so `ViewerState.OverlayOwnsPointer` is
deleted), claims Escape (so `IKeyboardClaimant` is retired in favour of the router asking the popover
directly), closes on an outside press and raises `Closed` so a consumer can clear what it must.
`DropdownMenuState` becomes a popover whose content is a list. Opening one from a toolbar button is
`.Clickable(hit, _ => state.Toggle())` on the button node, so the dispatcher line in
`HandleViewerMouseDown` and the `OpenToolbarDropdown` switch both go.

`.Tooltip(text)` on any node: the router owns the hover delay and the one painter, placed by
`OverlayPlacement`-style clamping inside the window, on the overlay layer so a clipped widget can show one
(the reason `TabBar` refuses to). Three tianwen painters become none. `.Disabled(reason)` on any node:
painted with the dim colour role, registers a hit that swallows the press with a `NotAllowed` cursor, and
the reason is its tooltip. `DropdownItem.Disabled` becomes the row case of the general rule.

### G. The measure seam

`protected Size<float> MeasureLayout(Node root, Size<float> available)` and
`protected PixelMeasureContext<TSurface> MeasureContext()` on `PixelWidgetBase`, unchanged from
viewer-layout-engine P0, which this plan now owns.

## D1 as specified for implementation (DIR.Lib 9.2, additive)

Decided 2026-09-15 evening with the user: no 9.2 for the compatibility constructor alone ("maybe moot"),
the whole refactoring goes; Console.Lib takes a major in the 10.0 chain. This section is the contract the
implementation agents build to. Every name is final unless an agent reports a collision with an existing
member; nothing here removes or renames a public member (that is 10.0).

### Wave 1a: declarations on the tree, and the seams beside them

Files: `Layout/Node.cs`, `Layout/Node.Fluent.cs`, `Layout/Builder.cs`, `Layout/Content.cs`, `Layout/Engine.cs`
(Grid only), `ClickableRegion.cs`, `ClickableRegionTracker.cs`, `ListCursor.cs`, `PixelWidgetBase.cs`
(`PaintLayout`, `RegisterClickable`, the new seams), `InputEvent.cs`.

- **`readonly record struct KeyChord(InputKey Key, InputModifier Modifiers = InputModifier.None)`** with
  `bool BeatsFocusedField => (Modifiers & (Ctrl | Alt)) != 0 || Key is F1..F24`. The precedence rule is a
  property of the chord, stated once, so the router and a test read the same fact.
- **`Node.Shortcut : KeyChord?`**, fluent **`.WithShortcut(InputKey key, InputModifier mods = None)`** (a method cannot
  share its property's name; `WithCursor` / `WithGap` met the same wall). Inert in
  arrange and paint; consumed by the router (wave 3) from the captured layout, PAINTED nodes only.
- **`readonly record struct PointerPress(float X, float Y, MouseButton Button, InputModifier Modifiers, int Clicks)`**
  and **`readonly record struct PointerMove(float X, float Y, MouseButton Button, InputModifier Modifiers)`**:
  the move carries what `InputEvent.MouseMove` does not, copied from the press by whoever captured.
- **`sealed class DragCapture(Action<PointerMove> onMove, Action<PointerMove> onRelease)`** with
  `Move(in PointerMove)` / `Release(in PointerMove)`. A capture is what a press handler RETURNS to say "the
  gesture is mine until the button comes up".
- **`Node.OnPress : Func<PointerPress, DragCapture?>?`**, fluent **`.Pressable(hit, onPress, cursor?)`**
  (sibling of `.Clickable`; a node may have both, press wins when it returns a capture, otherwise the click
  fires on release as today). `ClickableRegion` gains `OnPress`; `RegisterClickable` gains an optional
  `onPress:`; `PaintLayout` binds it from the node like `OnClick`.
- **`Node.OnActivate : Action<InputModifier>?`**, fluent **`.Activatable(action)`**: what Enter does on the
  row when it differs from a click. `ActivateListCursor` invokes `OnActivate ?? OnClick`. Answers the planner's
  "Enter pins, click selects".
- **`Node.Tooltip : string?`**, fluent **`.WithTooltip(text)`**. `ClickableRegion` gains `Tooltip`;
  `PaintLayout` registers a region for a node that has a tooltip but no hit (a `HitResult.ChromeHit` with
  no `OnClick`, so it stays inert to presses). Painting the tooltip is the router's (wave 3).
- **`Node.DisabledReason : string?`** and **`bool IsDisabled => DisabledReason is not null`**, fluent
  **`.Disabled(string reason)`** (and `.Disabled(bool when, string reason)` for the common conditional).
  `PaintLayout`: text and icon colours are halved toward the node's effective background (the same rule
  `RenderDropdownMenu` uses for a disabled row, hoisted into one `PixelWidgetBase.DimTowards(color,
  background)` helper both call); the region is registered with NO `OnClick` / `OnPress`, `CursorKind.NotAllowed`,
  and `Tooltip = DisabledReason`; `ListCursor` skips it (`ClickableRegionTracker` must know a region is
  disabled: add `bool IsDisabled` on `ClickableRegion`). `DropdownItem.Disabled` keeps its own painter; a
  follow-up may route it through this.
- **`Node.Scroll : ListScrollController?`**, fluent **`.WithScroll(controller)`**. `PaintLayout` calls
  `controller.SetExtent(viewport: arranged rect, ...)` ONLY if the consumer has not (add
  `ListScrollController.BindViewport(RectF32)` that sets the viewport and leaves atom extent/count alone,
  so a consumer keeps stating rows), registers the rect with the controller reference on the region
  (`ClickableRegion.Scroll`), and the router (wave 3) delivers `InputEvent.Scroll` to the innermost such
  region under the pointer. Until the router exists, a consumer may call the new
  `PixelWidgetBase.ScrollTargetAt(x, y)` itself.
- **`ListCursor.Open(string listId, int index, int count)`** overload with **`event Action<int>? Moved`**.
  With a count, `MoveListCursor` clamps to `[0, count)` and steps onto an index the last paint did NOT
  register (raising `Moved` so the consumer can `EnsureVisible`); where the target index IS painted, the
  existing reachability rule applies (a disabled or non-registered painted row is skipped). Without a count,
  behaviour is unchanged. Pinned by a test that walks a cursor past a five-row viewport of a twenty-row list.
- **`Grid` per-column sizing**: `Node.Grid.ColumnSizing : ImmutableArray<Sizing>` (empty = today's even
  split), builder `Grid(columns, cells).WithColumns(params Sizing[])`; `Auto` measures the column to its
  widest cell, `Fixed` is fixed, `Star` shares the remainder. Rows unchanged. `DrawTable` becomes a Grid.
- **`PixelWidgetBase.MeasureLayout(Node root, Size<float> available, string? fontPath = null, DesignScale? scale = null)`**
  and **`PixelWidgetBase.MeasureContext(string? fontPath = null, DesignScale? scale = null)`** returning the
  `PixelMeasureContext<TSurface>` the three existing helpers build privately, so measure, arrange and paint
  can share one instance. viewer-layout-engine P0, verbatim.
- **`IPixelWidget.CaretIndexAt(HitResult.TextInputHit, float pointerX)`** on the interface (the base already
  implements it), so a host holding the interface can place a caret. Deletes tianwen's `ICaretPlacingWidget`.
- **`InputEvent.MouseUp` gains `Modifiers`** as an optional trailing parameter with a default, and
  **`MouseMove` gains `Button`** likewise (`MouseButton.None` added to the enum). **Wave 1a found the
  spec wrong here**: a trailing defaulted parameter does NOT keep a positional pattern compiling either, because a
  record's synthesized `Deconstruct` takes its arity from the primary constructor; so both records carry an
  explicit old-arity `Deconstruct` AS WELL AS an explicit old-arity constructor (the 9.1 lesson), and
  `HitResult.TextInputHit(TextInputState)` is restored, so Console.Lib 4.33 runs against 9.2 unrebuilt.

### Wave 1b: the text field behaves like a field

Files: `TextInputState.cs`, `TextInputInteraction.cs`, `TextInputFocus.cs`, `TextInputRenderer.cs`,
`InputKey.cs` (`ToTextInputKey`), `Layout/Content.cs` (`TextInput` only), `Layout/Builder.cs`
(`TextInput` factory only), `SelectableTextRegion.cs`, `README.md` (the text-input paragraph).

- **`TextInputFocus.Focus(input, initialText)` SELECTS the seeded text** when `initialText` is given, as its
  doc already promises (`Activate(initialText)` then `SelectAll()`); no change when it is null.
- **`TextInputKey` gains `WordLeft`, `WordRight`, `WordBackspace`, `Cut`**; `ToTextInputKey` maps
  Ctrl+Left / Ctrl+Right / Ctrl+Backspace / Ctrl+X (and Shift+those for the word motions extends, through
  the existing `extend` idea: add `TextInputState.MoveCaretToWordBoundary(direction, extend)`, reusing
  `IsWordChar`). `TextInputInteraction.HandleKey` routes `Cut` as copy-then-delete-selection through
  `SetClipboardText`. `HandleKey` on the state handles the three motions/deletes.
- **`TextInputRenderer` scrolls the text horizontally so the caret stays visible** in an over-long value:
  a per-field `TextInputState.ScrollOffsetPx` (internal set) the renderer maintains and `CaretIndexAt`
  honours (subtract it before measuring). Pinned by a test with a value three times the field's width that
  asserts the caret rect stays inside the field after `End`, and after `Home`.
- **`Content.TextInput.FocusOnOpen : bool`**, builder `TextInput(state, ..., focusOnOpen: false)`. Inert in
  the painter except that `PaintLayout` reports the flag on the registered region
  (`ClickableRegion.FocusOnOpen`); the router (wave 3) applies "first painted field that asks, once, since
  it was last not painted" in its after-paint hook. Semantics stated on the property doc: it never steals
  from a field the user is typing in (only fires when `Focus.Current` is null or is itself unpainted).
- **`Content.Text.Selectable : bool`**, fluent **`.Selectable()`** on a Text leaf: `PaintLayout` routes the
  run through `DrawSelectableText` (the path `LinkHit` already takes), so a raster host's router (wave 3)
  can select it and a DOM host gets its native span. No interaction in this wave.
- **README**: delete the word "undo" from the `TextInputState` line; there is none and nothing asks for one.

### Wave 2: two content kinds that carry their own behaviour (after 1a lands)

- **`Content.Slider(SliderState State)`** with `sealed class SliderState { float Value; float Min; float Max;
  float Step (0 = continuous); bool Enabled; Action<float>? OnChanged; }`. Intrinsic height = the track
  height `DrawTrackSlider` uses today; width `Star` by default. `PaintLayout` paints through
  `DrawTrackSlider`, registers the rect with `OnPress` returning a `DragCapture` whose Move maps x through
  `TrackFrac` to `Value` (clamped, stepped) and calls `OnChanged`; disabled paints dim and registers inert.
  `HitResult.SliderStateHit(SliderState)` is the hit, so a consumer can still recognise it.
- **`Popover`**: `sealed class PopoverState : IKeyboardClaimant { bool IsOpen; void Open(); void Close();
  void Toggle(); event Action? Closed; }` whose `HandleKeyDown` closes on Escape and declines everything
  else. **`Builder.Popover(RectF32 anchor, Node content, PopoverState state, DockSide side = Bottom,
  RGBAColor32? backdrop = null)`** returns `Overlay(Base: Spacer().Stretch().Bg(backdrop).Clickable(new
  HitResult.ChromeHit(), _ => state.Close()), Top: Anchored(content, side, anchor...))` with
  **`Node.Popover : PopoverState?`** set on the Overlay root. `PaintLayout`, meeting a node with `Popover`
  set: paints nothing when `!IsOpen`; otherwise paints, sets `Ui.KeyboardClaimant = state` (retired in
  10.0 when the router asks the popover directly) and sets `Ui.PointerOwner = the popover's arranged content
  rect` (new on `WindowUiSettings`, cleared in `BeginFrame`), which `PaintLayout`'s hover resolution consults
  so nodes outside it do not light. `RenderDropdownMenu` is left alone in 9.2.

### Wave 3: the router (after 1a, 1b, 2 land)

File: `InputRouter.cs` (new), plus `WindowUiSettings.cs` for what it shares, tests.

```
public sealed class InputRouter(WindowUiSettings ui, BackgroundTaskTracker tracker, Action requestRedraw)
{
    public Func<IReadOnlyList<IPixelWidget>> Widgets { get; set; }     // paint order; the router walks it top-most first
    public Func<InputEvent, bool>? Unhandled { get; set; }             // the app's own routing (the active tab)
    public Func<string?>? GetClipboardText { get; set; }
    public Action<string>? SetClipboardText { get; set; }
    public Func<SearchInteraction?>? ActiveSearch { get; set; }        // KeyContext.ActiveSearch, until SearchInteraction is discoverable from the field
    public event Action<string>? OpenUrl;
    public event Action<KeyChord, Layout.Node>? ShortcutFired;         // telemetry / tests
    public bool Handle(InputEvent evt);
    public void AfterPaint();                                          // BlurIfUnpainted over every widget's painted fields; FocusOnOpen; tooltip timer
    public CursorKind? CursorAt(float x, float y);
    public TooltipRequest? Tooltip { get; }                            // (text, anchor rect) once the hover delay elapsed; the host or PaintLayout paints it
}
```

Order, fixed and tested one branch per test:

- **MouseDown**: regions top-most first across `Widgets`. `TextInputHit` -> `Focus`, `CaretIndexAt`,
  `HandlePointer(clicks, Shift)`, and an implicit `DragCapture` that extends the selection through
  `HandlePointer(extend: true)` on Move. `LinkHit` -> `OpenUrl`. A region with `OnPress` -> its capture (or,
  when it returns null, fall through to `OnClick`). Else `OnClick`. A press not on a text field blurs the
  focused field (after the dispatch, so a button's handler still sees the field's text). Then `Unhandled`
  if nothing was hit.
- **MouseMove**: active capture -> `Move` (with the press's button and modifiers). Else set `Pointer` on
  every widget, note hover changes (request a redraw only if a `HoverBackground` or `Tooltip` region's state
  changed), start the tooltip delay for the region under the pointer, then `Unhandled`.
- **MouseUp**: capture -> `Release`, done. Else `Unhandled`.
- **Scroll**: innermost region with `Scroll` under the pointer -> `controller.HandleInput`; else `Unhandled`.
- **KeyDown**: `Ui.KeyboardClaimant` (the popover) first. Then every painted node with a `Shortcut` equal to
  the chord, **but only when `chord.BeatsFocusedField || Focus.Current is null`**: a `TextInput` leaf is
  focused (seeded and selected), a node with `OnActivate`/`OnClick` is activated, a `Popover` node is
  toggled. Then the focused field through `TextInputInteraction.HandleKey` with a `KeyContext` the router
  builds (`TabFields` = every widget's `GetRegisteredTextInputs()` concatenated in `Widgets` order). Then
  `Unhandled` (which is where a widget's own `HandleListKey` lives today).
- **TextInput**: focused field -> `HandleText`.
- **AfterPaint**: `Focus.BlurIfUnpainted(union of painted fields)`; the `FocusOnOpen` rule; expire the
  tooltip if its region was not painted.

Hosts keep: the `FocusChanged` binding to the platform, the clipboard delegates, `AfterPaint` after their
paint, and their own `Unhandled`. Tests build two `PixelWidgetBase<RgbaImage>` widgets, paint them, and
drive the router with `InputEvent`s; no host code.

### The release

9.2 is one DIR.Lib release with everything above, then Console.Lib 4.35 / SdlVulkan.Renderer 7.x /
WebGl.Renderer 1.x rebuilt against it (the MouseUp/MouseMove records are the reason the chain is not
optional this time either), then tianwen's pins move together. Each wave lands on a DIR.Lib integration
branch `feat/dir-lib-9.2` off `main`; the release is cut from there when wave 3 is green. The DIR.Lib
CHANGELOG entry is written per wave into a `## 9.2` section as the waves land, so the release cannot ship
without its notes.

## What is breaking, and why each cut is worth a major

Only what has to be. Everything else above is additive and ships first.

| cut | why it cannot be additive |
|---|---|
| `TextInputState.Activate` / `Deactivate` internal | a public transition beside the owner is the bug class `TextInputFocus` exists to remove; leaving it is two mechanisms for one rule |
| `IPixelWidget.HitTestAndDispatch` retired; `HitTest` stays | dispatch without a position is what forced the viewer to bypass the region model; the router dispatches, and a widget that invokes `OnClick` during hit-testing would double-fire |
| `HitResult.SliderHit(int)` removed | replaced by `Content.Slider`; a positional hit with no state was never enough to drag |
| `IKeyboardClaimant` removed | the popover is the claimant, and the router knows the popover |
| `TextInputHit.Painted` required | a hit a host cannot place a caret from is a hit that reproduces the 9.0 behaviour |
| `LayoutInspection` deleted | already `[Obsolete]` and inert since 8.8, kept only because removing a public type is a major; this is that major |
| `Ui.KeyboardClaimant` becomes the router's popover stack | one-deep by construction today; a modal over a popover is the first thing the `Popover` node makes possible and the first thing the slot cannot express |

Per the one-wave rule, the removals land together in 10.0, after tianwen is already on the additive
replacements from 9.2, so the 10.0 consumer diff is deletions only.

## Phasing

Release chain for every DIR.Lib change: `/release-lib DIR.Lib`, then Console.Lib and SdlVulkan.Renderer
(and WebGl.Renderer) in parallel, then the tianwen pin. Each tianwen phase lands on a published pin.

### T0. Wire what 9.1 already ships (tianwen only, a day)

Move the pin to `9.1.*`. In `GuiEventHandlerBase`, `Planner.razor` and the TUI, replace the
`clicks >= 2 -> SelectAll` lines with `HandlePointer(field, CaretIndexAt(hit, px), clicks, shift, ctx)`
and route the TUI's inline editor through `TextInputInteraction.HandleKey`. Add a mouse-move branch that
extends the selection while the button is down. Acceptance: a double-click on a word selects the word,
on all three surfaces, pinned by a test that presses twice on a field and reads `SelectionStart/End`.
This is the user's exact complaint and needs no engine change.

**T0 is IMPLEMENTED on `feat/text-field-pointer` (2026-09-15 evening, not pushed). It WAS blocked on a
Console.Lib release; Console.Lib 4.34.1821 (the rebuild against 9.1) shipped the same evening and the branch
pins it, so the TUI hit test is green on the package path.** DIR.Lib 9.1 changed `HitResult.TextInputHit(TextInputState)` to
`TextInputHit(TextInputState, TextInputGeometry Painted = default)`. Source-compatible, and the 9.1 notes
say "additive throughout", but **an added optional parameter on a record's primary constructor DELETES the
old constructor from the assembly**: Console.Lib 4.33.1811, compiled against 9.0, calls
`new HitResult.TextInputHit(field.State)` in `CellLayout.HitOf`, so against 9.1 every TUI mouse hit test
throws `MissingMethodException`. It is the only break in 9.1 (SdlVulkan.Renderer only type-tests the
record; WebGl.Renderer never names it), and it is INVISIBLE on a dev box, where `UseLocalSiblings` compiles
Console.Lib from source. It surfaced because the agent worked in a `.claude/worktrees/` checkout, where the
sibling probe fails and the build takes the package path CI takes. Two rules fall out, one per repo:

- **DIR.Lib: an optional parameter added to a record's primary constructor is a binary break, so it is a
  MAJOR or it ships with an explicit old-arity constructor.** 9.1 should have carried
  `public TextInputHit(TextInputState input) : this(input, default) {}`. Fix at the source: a 9.2 that adds
  it, so Console.Lib 4.33 works against 9.2 without a rebuild. The general form belongs in DIR.Lib's
  `CLAUDE.md`.
- **tianwen: a DIR.Lib pin move is never alone.** `/release-lib` already says a DIR.Lib minor releases every
  downstream lib; 9.1 did not run the chain. Moving the pin here needs Console.Lib (and, for hygiene,
  SdlVulkan.Renderer and WebGl.Renderer) republished against 9.1 or 9.2 first, then all pins move together.

What T0 delivered, for the record: a `TextFieldPointerInteraction` shared by the GUI and the web host that
resolves the caret index from the widget that PRODUCED the hit (an `ICaretPlacingWidget` every
`PixelWidgetBase` already satisfies; the clean answer is `CaretIndexAt` on `IPixelWidget`, D1); the TUI
inline editor on `TextInputInteraction.HandleKey` with the private focus pointer gone; `OpenSearch` through
the focus owner; and a fail-first test pressing at measured x positions. It also found that **the TUI site
editor could never accept a typed character** (`HandleKey` swallows every key while a field is focused, so
the `ToChar` branch after it was unreachable), fixed by answering the printable case first, gated on the
key not being a `TextInputKey`. Left out on purpose: drag-to-extend (`MouseMove` carries no button state;
the router's job), `SessionTab`'s exposure edit (no route to the focus owner from there; T1), and
`CloseSearch`'s by-hand `Deactivate`.

### T0b. The adoption sweep (tianwen only, parallelisable per file group)

The ranked table above, top to bottom, on the current pin. Each row is mechanical once the first
instance is done and reviewed, so it is sub-agent work, one agent per file group, each on its own branch
with the existing tests as the guard: spacer-as-gap (`EquipmentTab.*`, then the rest); marks to `Icon`;
`.BgFocus` + `ListCursor`
for the nine selected-row ternaries and the three hand-written Up/Down blocks; `Text.Trim` for the three
`TextFit` calls; `WidthSample` for the two reserved-width helpers; `Anchored` for `OverlayPlacement`;
`Dock` for the ten `PixelLayout` calls; `Builder.Box` for the 41 coloured spacers; the 15 web-host
fire-and-forget calls onto the tracker that file already owns (plus the three untracked ones with a
tracker in scope); `TruncateToWidth` deleted for `TextFit.TrimToWidth`; the two hand-built result lists
onto `RenderDropdownMenu`; the four pixel-sweep tests rewritten to read arranged nodes; the
`tianwen-fits` cursor predicate deleted in favour of the region's own `cursor:`; `GuiAppState.TextInputFocus`
replaced by the window's `Ui.Focus` so there is one instance, not two. **Not** the viewer's
chrome panes (row 2), which is T3, and with them not `.BgHover` (row 5): all five hover sites are in that
imperative chrome, so the property has nothing to sit on until the panes are trees. Not `LayoutDamage` (row 6), which needs the GUI host to consume a
damage list it does not consume today and belongs with the router (D1/T1).

Acceptance is the grep: `Spacer().WFixed(` and `Spacer().RowH(` as gaps at zero, `"+"` / `"-"` /
`"\u2212"` / `"\u25b6"` in a `Builder.Text` at zero, `OverlayPlacement.cs` and `PixelDockStyle` gone,
`.BgHover` and `.BgFocus` non-zero, `ListCursor` non-zero. Pixel-boring throughout; a baseline image
test that moves is a regression, not a redesign.

### D1. DIR.Lib 9.2, additive: the router and the declarations

`InputRouter`, `OnPress` + `DragCapture`, `.Shortcut`, `Focus` selects, `focusOnOpen`, `Content.Slider`,
`.Selectable()` + `TextSelection`, `Popover`, `.Disabled(reason)`, `MeasureLayout`. Plus `ListCursor.Open(listId, index, count)` with a `Moved` callback, `OnActivate` beside `OnClick`, `CaretIndexAt` on
`IPixelWidget` (so a host holding the interface can place a caret without tianwen's `ICaretPlacingWidget`),
the old-arity `TextInputHit` constructor if 9.2 has not shipped it by then, and the small text-field
gaps the review turned up, since they are the same "a field behaves like a field" promise: `TextInputKey` gains
`WordLeft` / `WordRight` / `WordBackspace` / `Cut`, and `TextInputRenderer` scrolls an over-long value so the caret
stays visible. The README's "undo" claim is either implemented or deleted; **delete it**, nothing in tianwen has asked
for undo and a false claim is worse than a missing feature. All new surface; every existing consumer compiles
unchanged. Tests in DIR.Lib against a stub context and `RgbaImageRenderer`:
the router is the piece that was "untestable inside a UI project" before U6 and must not be again.

### T1. tianwen on the router

`GuiEventHandlerBase.HandleInput` becomes `router.Handle(evt)` plus the tab policy. Delete the web copy
and the TUI's `_activeInlineInput`. The nine tab shortcuts and F3 become `.Shortcut` declarations; the
sky-map search box gets `.WithShortcut(InputKey.F, InputModifier.Ctrl)` as well, which is the user's example. The four
by-hand focus sites go through `Focus(input, seed)`. The three hand-written Up/Down blocks go to
`ListCursor` (GUI) and `ScrollableList.MoveCursor` (TUI).

**SHIPPED 2026-09-15**, with three corrections.

**Acceptance, corrected.** `grep SelectAll()` over `TianWen.UI*` and `TianWen.Cli` finds **nothing at
all**, not even tests. `grep "InputKey.F3"` finds **one hit, the declaration itself**, and the original
"finds nothing" cannot hold: a chord has to name its key somewhere and this plan's own model puts that
on the node, so the hit IS the acceptance rather than a violation of it. There are **eight** tab chords,
not nine; the ninth in the old count named a Planetary tab that is not in `TabOrder`.

**Two of the four Up/Down blocks converted.** The GUI session config form (which is what
`ListCursor.Open(..., count)` was added for) and the TUI planner list. The GUI planner list did NOT: its
rows deliberately register nothing so an unclaimed press falls through to the scroll controller for
tap-on-release and drag-to-scroll, and a cursor cannot walk a list nothing painted. It needs a row
declaration that is keyboard-reachable and pointer-transparent. The TUI config form did not either: it
interleaves group headers with fields in one index space while the selection is indexed by field.

**A host bug this surfaced, and the rule it leaves.** DIR.Lib gates "are this widget's registered regions
current" on `Ui.FrameId`, and tianwen never touched that property, so the gate was always true and a
widget the host had stopped drawing went on answering with its last paint. Harmless until the router owns
the Tab ring, because the chrome composes ALL the tabs rather than only the visible one: the ring spanned
every tab that had ever been on screen. The GUI counts frames now. **The general rule: a DIR.Lib
behaviour gated on an opt-in the host never sets is a rule that is silently OFF**, which is the same
shape as the `PointerOwner` bug wave 2 shipped and 9.2 fixed. Pinned by
`InputRouterTests.TabSkipsAWidgetTheHostHasStoppedPainting`.

### T2. popovers and sliders

White balance and tone become `Popover` nodes; their sliders become `Content.Slider`. Delete
`OverlayPlacement`, two of the five drag flags, `ISelfDispatchingInputWidget`, the two bespoke slider hit
types and the two bespoke `IKeyboardClaimant` classes.

**SHIPPED 2026-09-15.** Three items this section originally listed are NOT T2, and the corrections are
the interesting part.

- **The toolbar dropdowns and the help menu are not convertible in 9.2, by this plan's own design.** D1's
  wave 2 ends "`RenderDropdownMenu` is left alone in 9.2", and it was, so tianwen still owns their list
  painting, scrolling, keyboard navigation, disabled rows and tooltips. Converting them means moving all
  of that into a `Layout.Node` tree, **the largest remaining block of hand-written chrome**. The help menu
  is a toolbar dropdown (`ToolbarAction.Shortcuts` opens one), so it is the same item. This is the
  upstreaming the user asked for, and it deletes code rather than adding it. **The two halves are not in
  the same wave** (corrected 2026-09-16, this bullet said "D2 / T3 work" flat): the DIR.Lib half is a node
  over `PopoverState` and is additive, so it goes out in a **9.x** on its own schedule, while the tianwen
  half is the conversion and rides with T3.
- **The sky palette is not a popover by nature.** `FloatingPaletteState` is draggable, snappable,
  fade-timed and persistent, with no backdrop and no Escape dismissal. `Popover` would delete its
  behaviour rather than express it. Leave it.
- **`OverlayOwnsPointer` survives T2**, and the reason is worth keeping: `WindowUiSettings.PointerOwner`
  looks like its replacement and answers a different question. The engine's is a RECORD of what was
  painted, claimed by being painted, confining hover for anything painted after it, which a `PaintLayout`
  tree gets for free. The host flag is a PREDICTION, and all four of its consumers are hand-painted chrome
  resolving hover BEFORE any overlay has drawn. It goes when the chrome goes on the tree, in T3.

**Acceptance, corrected.** The original read "`ViewerTonePopoverTests` passes unchanged (it reads
arranged nodes, which is why it survives)". That is true of its shape and false of five of its
assertions, each naming something this same phase deletes: a `Content.Fill` dial found by key (a slider
leaf has no key), `ToneSliderHit` (now `HitResult.SliderStateHit`), `ToneDragSlider` (a deleted drag
flag), `TonePanelOpen` (now a `PopoverState`) and `OverlayOwnsPointer`. **A phase cannot both delete a
name and require a test asserting on it to pass untouched**, so the acceptance is: that test is adapted
to the new names and gets STRONGER where the old assertion only existed because of a flag ("release ends
the drag" becomes "a move after the release changes nothing", which is the property the flag was for).
`ViewerWhiteBalancePopoverTests` stops sweeping, which it already had by the T0b sweep.

### D2. DIR.Lib 10.0: the cuts

The five removals above, one wave, `MIGRATION.md` entry. tianwen's diff is deletions.

**Sweep B (2026-09-15, `refactor/list-cursor-and-dock`, not pushed) converted the two fully painted lists
(the planner's suggestion dropdown and the sky-map search results) to `ListCursor` + `.BgFocus`, and all
three `PixelLayout` sites to `Dock` (`PixelLayout` / `PixelDockStyle` now have zero uses). It could NOT
convert the planner target list or the session config form, and the reason is a `ListCursor` contract gap
for D1:** `MoveListCursor` steps only over rows the last paint REGISTERED, and both of those lists are
virtualised on purpose (the planner paints `VisibleRows()` only; the config form filters the arranged tree
to the nodes intersecting the panel before painting, so off-screen rows register no clickables). A cursor
that cannot step onto an unpainted row cannot walk a list past its own viewport, which is exactly what
`SessionTabTests.KeyboardDown_ScrollsSelectedFieldIntoView` asserts it must. D1 therefore gives
`ListCursor.Open` an optional row count (the painted-regions walk stays the reachability filter where a
row IS painted) and a `Moved` callback a consumer hangs `ListScrollController.EnsureVisible` off. Two
smaller ones from the same sweep: the planner's target rows register NO `ListItemHit` by design (an
unclaimed press falls through to the scroll controller for tap-on-release and drag-to-scroll, pinned by
`RowBodyIsUnclaimed_ButPinButtonStaysRegistered`), so a cursor on that list needs a row declaration that
does not claim the press, which is what `OnPress` returning "not mine" gives; and `ActivateListCursor` is
all-or-nothing on the click handler, so a list whose Enter differs from its click (Enter pins, click
selects) keeps Enter by hand until the row can declare an `OnActivate` beside `OnClick`. Both belong on
the node in D1.

**Readiness, counted 2026-09-16.** D2's whole premise is that the consumer diff is DELETIONS only,
which holds only once tianwen is off every replaced thing -- a countable question. Counted over
`src/**/*.cs` and `*.razor`, **production only** (a test that drives a retired API is deleted with it)
and **uses only** (a mention in a comment is not a use). The receiver is checked, so
`ViewContexts.Activate` and `sdlWindow.Activate` do not count as `TextInputState.Activate`; counting by
name alone gets both wrong in both directions.

| cut | prod uses | where, and what has to happen first |
|---|---|---|
| `LayoutInspection` | **0** | nothing. Ready. |
| `ISelfDispatchingInputWidget` | **0** | already gone; the one match is a comment recording its deletion. Ready. |
| `HitResult.SliderHit(int)` | **0** -- DONE 2026-09-16 | the planner's handoff dividers are regions that arm their own `DragCapture` (`PlannerTab.RegisterSliderHitRegions`). The chart registers a click-to-place surface, each divider a handle OVER it, and order is load-bearing: inverted, every handle is unreachable and every grab becomes a click-to-place, which still moves a divider and so looks correct. Ready. |
| `IKeyboardClaimant` / `Ui.KeyboardClaimant` | **1** | `ImageRendererBase.Input.cs:206`, the viewer's Escape order. Goes when the viewer's remaining overlays are `Popover` nodes -- the router already knows a popover. |
| `TextInputState.Activate` / `Deactivate` | **0** -- DONE 2026-09-16/17 | was 9. The three GUI sites went through the owner; the TUI's six were the site editor SEEDING three fields and focusing one, which `Activate(text)` cannot express -- it does both, so all three painted as focused (`SiteEditRow` picks its pen from `IsActive`) and the row said nothing about where typing would go. Seeded text and claimed keyboard are separate acts now, and `TextInputFocus.Focus(input, seed)` is the single call for where they are one. Ready. |
| `IPixelWidget.HitTestAndDispatch` | **10** | the large one. Four TUI tabs call it on their tracker; the viewer calls it on itself (`ImageRendererBase.Input.cs:845`); `Program.cs:822`; and two OVERRIDES compose it (`ImageRendererBase.Sky.cs:95`, `VkGuiRenderer.cs:202`). Each needs the router instead, and an override needs what it re-states to move onto the node -- which is what 9.5 did for the rail's chord and handler, leaving that one a `Tab:<name>` re-label. |
| `TextInputHit.Painted` required | n/a | consumers already read it; making it required IS the break. |
| `Ui.KeyboardClaimant` -> popover stack | rides the claimant row | one change, not two. |

**The two cuts left are ONE piece: the viewer adopts `InputRouter`.** Verified 2026-09-17 rather than
assumed:

- `TianWen.UI.FitsViewer/Program.cs` contains **zero** `InputRouter` references. The viewer is its own
  host and dispatches input itself -- `ImageRendererBase.Input.cs` is 1265 lines with 44 `case InputKey`
  arms.
- That is why the ONE `Ui.KeyboardClaimant` consult exists (`Input.cs:206`): it is the viewer hand-doing
  what the router does for every other surface. It goes with the adoption, NOT with declaring the
  overlays -- the overlays are already `Popover` nodes and DIR.Lib sets the claimant itself when it
  paints one (`PixelWidgetBase.cs:1284`).
- The same adoption is what removes the viewer's own `HitTestAndDispatch` call and the two overrides
  that compose it, so the 10 sites and the 1 claimant are not two jobs.

**`OverlayOwnsPointer` is NOT one of D2's cuts, and it is not blocked on any of this.**
`PixelWidgetBase.PointerWithin` already consults `WindowUiSettings.PointerOwner` -- "no owner, or inside
the owner" -- so a DECLARED node's hover is confined by an open popover with no node knowing one exists.
What keeps the viewer's six hand-written consults alive is **paint order**: its chrome paints before its
overlays, so `PointerOwner` is still unset when the toolbar, file list and histogram resolve hover, and a
flag read from the popover's own `IsOpen` is the only thing available that early. Retiring it is
therefore its own small change -- set `PointerOwner` from state at the top of the frame instead of at
paint time -- and not part of the gate.

So D2 is **not startable today**, and the gate is T2's remainder plus the viewer's last painted
overlays -- not anything in DIR.Lib. Nothing above waits on a new engine feature; every replacement
has shipped.

### C1. Console.Lib: one list model (independent, any time after D1)

`ScrollableList<T>` is a second implementation of `ListCursor` + `ListScrollController` on the cell
surface, sharing the tree and the hit test but not navigation or scrolling. Rebase it on the two, so a
list behaves the same way under the arrows on both surfaces. Nothing in this plan depends on it.

### T3. the chrome on the engine

viewer-layout-engine P1 to P4, unchanged, now on the seam D1 shipped.

## Acceptance for the whole plan

- **Adding a text field** is the state, its business callbacks, and `Builder.TextInput(state)` with
  whatever `.Shortcut` / `focusOnOpen` it wants. Zero lines in any host or dispatcher.
- **Adding a popover** is `Builder.Popover(...)` and a `.Clickable` that toggles its state. Zero dispatcher
  lines; the `CLAUDE.md` warning about the dead button is deleted because the failure is unrepresentable.
- **Adding a slider** is `Content.Slider(state)`. No flag, no branch.
- `ISelfDispatchingInputWidget`, `OverlayOwnsPointer`, `IKeyboardClaimant`, the `Ctrl+letter` map, the
  `F3` special case and `clicks >= 2` no longer exist in tianwen.
- One key router per SURFACE KIND (pixel, cell, DOM), all three instances of `InputRouter`.
- `grep ListCursor` over tianwen is no longer zero; `grep -c RegisterClickable` over `TianWen.UI*` is under
  ten, all inside `drawFill` bodies or overlay labels placed on the image; one tooltip painter, in DIR.Lib.

## What this does NOT do

- **It does not redesign anything visible.** Every phase is pixel-boring; the popovers look as they do.
- **It does not move domain glue into DIR.Lib.** `PlannerSearchInteraction`, `PlannerSliderInteraction`
  (click-to-place is a planner semantic), `SplitCompareController` stay; they become consumers of
  `OnPress` rather than of a bypass.
- **It does not make the engine draw the image, the sky map or a chart.** `Fill` remains the escape hatch
  for a surface the app paints; what changes is that a control is no longer one.
- **It does not touch the viewer's pan/zoom gesture**, which is on `PanZoomController` already.
- **It does not change `DesignScale.One` trees to scaled ones**; the six device-pixel trees are a separate
  decision about what those panels should do on a high-DPI display.
- **It does not add a `Collapsible` node.** A heading with `.Clickable` and a child included or not is already
  a tree; `DrawCollapsibleHeading` goes in T3 without any engine change.

## Where the pieces are

| piece | where |
|---|---|
| the pointer rule nobody calls | `DIR.Lib/TextInputInteraction.cs` `HandlePointer`; `PixelWidgetBase.CaretIndexAt` |
| the three hand-written routers | `TianWen.UI.Abstractions/GuiEventHandlerBase.cs`, `TianWen.UI.Web/Pages/Planner.razor`, `TianWen.Cli/Tui/TuiEquipmentTab.cs` |
| the four by-hand focus sites | `SkyMapSearchActions.OpenSearch`, `SessionTab.cs` exposure edit, `TuiEquipmentTab._activeInlineInput`, `TextInputFocus.Focus` (the promise it does not keep) |
| the popover obligations | `ImageRendererBase.WhiteBalancePanel.cs`, `.TonePanel.cs`, `.Toolbar.cs` `OpenToolbarDropdown`, `.Input.cs` `HandleViewerMouseDown`, `ViewerState.OverlayOwnsPointer` |
| the drag flags | `ViewerState.cs` (five), `ISelfDispatchingInputWidget.cs` |
| the layout arithmetic | [viewer-layout-engine.md](viewer-layout-engine.md) |
| the adoption sweep's targets | the two ranked tables above; `OverlayPlacement.cs`, `ImageRendererBase.Damage.cs`, `EquipmentTab.TruncateToWidth`, `PlannerTab.RenderSuggestionDropdown`, `SkyMapTab.BuildSearchResults`, `Planner.razor` |
| the engine's rules for a consumer | `CLAUDE.md` "Layout DSL", "UI Primitives", `docs/architecture/widgets-and-controls.md` |
