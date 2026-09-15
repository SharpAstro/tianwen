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
| `.WithGap(g)` | 38 | **73 `Spacer().WFixed(pad)` nodes** as gaps (`EquipmentTab.*` alone about 25) |
| `HoverBackground` / `.BgHover` (8.1) | **0** | 5 hover tests at the call site (`FileList.cs:250` `rowRect.Contains(mouseX, mouseY)`, `Toolbar.cs:456`, `Histogram.cs:83`, `VkPlannerTab.cs:296`) plus the repaint bookkeeping the feature removes (`_lastHoveredToolbarButton`, `_lastHoveredFileListRow`, `hoverRepaint`, about 50 lines of `Input.cs`) |
| `FocusBackground` / `.BgFocus` + `ListCursor` (8.20) | **0** | 9 `isSelected ? SelectedBg : RowBg` ternaries and about five `SelectedIndex` fields, on rows that ALREADY register `ListItemHit` (14 sites), so they are navigable-shaped and nobody asked |
| `IconKind.Plus` / `Minus` / `CaretUp` / `CaretDown` | 0 / 0 / 0 / 1 | **19 marks as text runs**: 14 steppers (`"+"`, `"-"`, `"\u2212"`, `"[+]"`, `"[-]"` in `PlannerTab`, `SessionTab`, `FormRowLayout`, `SessionConfigLayout`, `EquipmentTab.*`, `LiveSessionTab.*`) and 5 carets/jogs (`"\u25b6"`, `"\u25c0"`, `"\u00ab"`, `"\u203a"`), against `CLAUDE.md`'s own rule that a mark is an `Icon` |
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
| `Builder.Box` (swatch / rule) | 5 | 41 `Spacer().Bg(colour)` nodes spelling the same thing |
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
| 1 | spacer-as-gap | `.WithGap` | 73 nodes |
| 2 | the viewer's imperative chrome inside the five `Fill` panes | subtrees under the arrangement `Layout.cs` already makes | about 55 draw/hit sites (25 `RegisterClickable`, 28 cursor advances) plus four helpers (`DrawTextLine`, `DrawSectionHeading`, `DrawTable`, `DrawWrappedTextLine`) |
| 3 | marks as text runs | `IconKind.Plus/Minus/CaretUp/CaretDown` | 19 |
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
`.Selectable()` + `TextSelection`, `Popover`, `.Disabled(reason)`, `MeasureLayout`. Plus the small text-field
gaps the review turned up, since they are the same "a field behaves like a field" promise: `TextInputKey` gains
`WordLeft` / `WordRight` / `WordBackspace` / `Cut`, and `TextInputRenderer` scrolls an over-long value so the caret
stays visible. The README's "undo" claim is either implemented or deleted; **delete it**, nothing in tianwen has asked
for undo and a false claim is worse than a missing feature. All new surface; every existing consumer compiles
unchanged. Tests in DIR.Lib against a stub context and `RgbaImageRenderer`:
the router is the piece that was "untestable inside a UI project" before U6 and must not be again.

### T1. tianwen on the router

`GuiEventHandlerBase.HandleInput` becomes `router.Handle(evt)` plus the tab policy. Delete the web copy
and the TUI's `_activeInlineInput`. The nine tab shortcuts and F3 become `.Shortcut` declarations; the
sky-map search box gets `.Shortcut(InputKey.F, Ctrl)` as well, which is the user's example. The four
by-hand focus sites go through `Focus(input, seed)`. The three hand-written Up/Down blocks go to
`ListCursor` (GUI) and `ScrollableList.MoveCursor` (TUI). Acceptance: `grep SelectAll\(\)` over
`TianWen.UI*` and `TianWen.Cli` finds only tests; `grep "InputKey.F3"` finds nothing.

### T2. popovers and sliders

White balance, tone, the toolbar dropdowns, the help menu and the sky palette become `Popover` nodes;
their sliders become `Content.Slider`. Delete `OverlayOwnsPointer`, the five drag flags, the
`OpenToolbarDropdown` switch, the `HandleViewerMouseDown` popover line and `ISelfDispatchingInputWidget`.
Acceptance: `ViewerTonePopoverTests` passes unchanged (it reads arranged nodes, which is why it survives);
`ViewerWhiteBalancePopoverTests` stops sweeping.

### D2. DIR.Lib 10.0: the cuts

The five removals above, one wave, `MIGRATION.md` entry. tianwen's diff is deletions.

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
