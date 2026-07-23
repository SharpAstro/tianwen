# Controls upstreaming: promote generic controls to DIR.Lib

**Status: U1 + U2 + U3 + U4 + U5 SHIPPED (2026-07-23); U6 deferred.** Follow-on to
[interaction-primitives.md](interaction-primitives.md); taxonomy + inventory in
[../architecture/widgets-and-controls.md](../architecture/widgets-and-controls.md).

Trigger: the widget-vs-control survey found three controls that are generic (no TianWen domain
dependency) but live in `TianWen.UI.Abstractions`, violating the layering rule that generic controls
belong in DIR.Lib next to `PixelWidgetBase`/`TextInputState`. Two of them (the search pair) are also
a duplication: the planner autocomplete and the sky-map F3 modal hand-roll the same
input + suggestion-list + key-nav + commit machinery around `TextInputState`.

**Release-slot history (IMPORTANT -- the 6.16 earmark is stale):** U1 + U5 shipped as part of the
DIR.Lib **6.15/6.16** line (they landed on `feat/interaction-primitives`, released with the P2 chain and
consumed by tianwen PR #110). The 6.16 minor this plan originally earmarked for U2 was then **consumed
by an unrelated feature** (the planner Wikipedia-link channel, tianwen PR #111). DIR.Lib `main` is now at
**6.17.0** (an in-progress `DeviceTransform` feature). So **U2 rides the next DIR.Lib minor after
DeviceTransform's 6.17** -- 6.17 if that isn't published to NuGet yet, else 6.18 -- + the Console.Lib /
SdlVulkan.Renderer / WebGl lockstep rebuilds + a tianwen repin. Each U-phase is independently shippable;
"no push before NuGet" applies.

## U1 -- `TrackSlider` -> DIR.Lib  **(DONE + SHIPPED, DIR.Lib 6.15/6.16)**

`ImageRendererBase.TrackSlider.cs` was ~50 lines with zero domain dependencies: `DrawTrackSlider`
uses `FillRect`/`RegisterClickable` (`PixelWidgetBase` members) + `RectF32`/`RGBAColor32`/`HitResult`
(all DIR.Lib); `TrackFrac` is pure math. Only the `TransportTrackBg`/`TransportHandle` chrome colours
were tianwen's.

- **DONE (2026-07-22):** both methods moved onto `PixelWidgetBase<TSurface>` as `protected` helpers.
  The track + handle colours arrive as a `TrackSliderChrome` record (a new top-level DIR.Lib type);
  `dpiScale` is now an **explicit param** (the plan assumed `DpiScale` was a base member -- it is a
  tianwen `ImageRendererBase` property, so the generic control cannot read it). `TrackFrac` is
  `protected static`.
- tianwen: deleted `ImageRendererBase.TrackSlider.cs`; the three call sites (WB / wavelet / scrub)
  pass a shared static `TrackChrome` (`TransportTrackBg`, `TransportHandle`) + `DpiScale`.
  Pixel-identical; zero behaviour change; build 0/0.
- Two overloads: the full one takes an explicit `barCenterY` (the scrub bar centres on the tall strip
  while its handle spans the shorter button band); a convenience overload derives
  `barCenterY = handleY + handleH/2` (bar centred in the handle -- the WB/wavelet case), so those call
  sites pass the handle band once instead of repeating `FontSize` as both `handleH` and `barCenterY`.
- Tests: DIR.Lib headless tests for the slider maths (fraction clamp, sliver-track handle clamp --
  the minimize-to-sliver crash guard) still **TODO** on promotion.

## U2 -- `SearchInteraction` base -> DIR.Lib, tianwen searches become subclasses

**Status: SHIPPED (2026-07-23).** DIR.Lib #25 -> **6.18.1581** (`SearchInteraction<TResult>` base);
lockstep **Console.Lib 3.11.1181** + **SdlVulkan.Renderer 6.31.1771**; tianwen **PR #113 merged to main**
(the repin also folded in the already-published DeviceTransform DIR.Lib 6.17 / SdlVkR 6.30 that main had
not yet consumed -- tianwen jumped 6.16->6.18 / 6.29->6.31; the 6 U5 residue casts rode along). WebGl.Renderer
stayed at 1.11 (its packed DIR 6.14.* floats to a `>=min` transitive dep, satisfied by 6.18 -- no rebuild).
DIR.Lib suite 549/0 (20 new `SearchInteractionTests`); tianwen solution 0/0, 128 Planner/SkyMap/TextInput/
Equipment tests green, and full tianwen CI green on #113 (build + functional + unit x2 -- the only leg that
restores the repin from NuGet). Live GUI smoke (inspector): planner autocomplete (type -> dropdown -> Down ->
Enter commits + resets, no auto-highlight) and sky-map F3 (open -> results with first auto-highlighted ->
Escape closes) both behave per their preserved policies; no stderr exceptions.

The planner search (`PlannerSearchInteraction` + suggestion state spread over `PlannerState`) and the
sky-map F3 search (`SkyMapSearchState` + inline key-nav in `SkyMapTab.Search.cs:762-768`) are the
same control with different domain resolution:

| Shared mechanics (base) | Planner specifics (subclass) | Sky map specifics (subclass) |
|---|---|---|
| `TextInputState` wiring (`OnTextChanged` -> re-query, `OnCommit`, `OnCancel`) | resolve via `PlannerActions.SearchTargets`/`UpdateSuggestions`/`CommitSuggestion` | resolve via catalog autocomplete index + `CometEntries` map |
| suggestion/result list + `SelectedIndex` + `LastQuery` | inline dropdown under a persistent box | modal (`IsOpen`), typed `SkyMapSearchResult` rows |
| key-nav protocol: Up/Down cycle, Enter commits highlighted, Escape dismisses, Backspace falls through | commit selects + `EnsureVisible` in the target list | commit centres the map + opens the info panel |
| commit-identical mouse path (`CommitSuggestionAt(index)` == keyboard Enter) | deactivate via `DeactivateTextInputSignal` | close modal |
| clear/dismiss reset (text, list, index, focus release) | | |

### Design (SIGNED OFF 2026-07-23)

**Two-level base in DIR.Lib.** `TextInputKey` has no Up/Down (verified), and mapping arrows into
`ToTextInputKey` would change key routing for *every* text input (incl. the TUI equipment tab) -- too
broad. So Up/Down nav stays an explicit `InputKey` seam the host key-router calls, which means the host
needs a `TResult`-free handle. Hence a non-generic base carrying the protocol + selected-index, and a
generic subclass carrying the typed results:

```csharp
namespace DIR.Lib;

// TResult-free: input wiring + key-nav protocol + selected-index. Hosts (KeyContext) hold THIS.
public abstract class SearchInteraction
{
    protected SearchInteraction(TextInputState input, Action requestRedraw, Action? releaseFocus = null);
    public TextInputState Input { get; }
    public int SelectedIndex { get; set; } = -1;
    public string LastQuery { get; protected set; } = "";
    public abstract int ResultCount { get; }
    public bool HandleNavKey(InputKey key);   // Up/Down over [0, ResultCount); replaces the 2 KeyContext blocks
    public void CommitAt(int index);          // mouse click == keyboard Enter-on-highlight
    protected abstract void Requery(string text);        // OnTextChanged -> resolve results
    protected abstract void CommitSelected();            // Enter-on-highlight / CommitAt
    protected abstract void CommitRawQuery(string text); // Enter with no highlight
    protected virtual  void Dismiss();                   // Escape / OnCancel cleanup
}

// Adds typed results; seals Requery/CommitSelected onto the typed seams.
public abstract class SearchInteraction<TResult> : SearchInteraction
{
    public ImmutableArray<TResult> Results { get; protected set; } = [];
    public sealed override int ResultCount => Results.Length;
    protected sealed override void Requery(string t) => Results = Query(t);
    protected sealed override void CommitSelected() => Commit(Results[SelectedIndex]);
    protected abstract ImmutableArray<TResult> Query(string text);
    protected abstract void Commit(TResult result);
}
```

The ctor wires all four `TextInputState` callbacks **once**: `OnTextChanged` -> set `LastQuery` +
`Requery` + redraw; `OnCommit` (Enter, no highlight) -> `CommitRawQuery`; `OnCancel` -> `Dismiss` +
releaseFocus + redraw; `OnKeyOverride` -> Enter-on-highlight -> `CommitSelected`, Escape -> `Dismiss`,
Backspace/Delete -> `false` (fall through to text edit, `Requery` fires via `OnTextChanged`).

**Host key-router change (one struct field, one production site).** `TextInputInteraction.KeyContext`
drops `PlannerState? Planner` + `SkyMapSearchState? SkySearch`, gains `SearchInteraction? ActiveSearch`.
The two hardcoded Up/Down blocks (`HandleKey` L55-97) collapse to one polymorphic call:

```csharp
if (ctx.ActiveSearch is { } s && activeInput == s.Input && s.HandleNavKey(key)) { ctx.RequestRedraw(); return true; }
```

`TextInputInteraction` thereby loses its `PlannerState`/`SkyMapSearchState` dependency entirely. The only
production `KeyContext` site is `GuiEventHandlerBase:331` (resolve `ActiveSearch` = whichever search's
`Input` is the active one, else null); verify the web host key path (`Planner.razor`) builds the same
context. `SkyMapTab.TryHandleSearchKey`'s Up/Down fallback (L753-767) is already dead while the modal's
input is active (`HandleKey` swallows all keys) -- delete it, keep the F3 toggle.

**tianwen subclasses (thin: protocol in the base, domain callbacks injected).** Commit needs
per-invocation host context (planner: transform/db/ensureVisible; sky: db/site/viewingUtc/comets, resolved
in the signal handler with DI), so the subclasses hold host-supplied callbacks and forward -- exactly the
shape `PlannerSearchInteraction.Wire` + `AppSignalHandler.SkyMap` already inject:

- `PlannerSearchInteraction : SearchInteraction<string>` -- `Query` = the pure part of
  `PlannerActions.UpdateSuggestions` (extract `ComputeSuggestions(cache, text)`); `Commit` =
  `CommitSuggestion`; `CommitRawQuery` = `SearchTargets`; `Dismiss` = clear + deactivate. ctor takes
  today's `Wire` params (db, createTransform, autoComplete, ensureVisible, deactivate, requestRedraw).
- `SkyMapSearchInteraction : SearchInteraction<SkyMapSearchResult>` -- `Query` = pure `FilterResults`;
  `Commit`/`CommitRawQuery` post `SkyMapSearchCommitSignal` (DI/context resolution stays in the handler);
  `Dismiss` = `CloseSearch` + deactivate. `AppSignalHandler.SkyMap`'s three `SearchInput.On*` assignments
  disappear (ctor-wired); the Open/Close/Commit signal subscribers stay.

**State migration = FULL (decision A, 2026-07-23).** The base OWNS `Results`/`SelectedIndex`/`LastQuery`;
`PlannerState`/`SkyMapSearchState` expose the interaction instance and the old fields go away rather than
becoming shims (shims would perpetuate the split U2 exists to remove). Blast radius is bounded:
`PlannerState.Suggestions`/`SuggestionIndex`/`LastSuggestionQuery`/`CommitSuggestionAt` -> `Search.Results`
(`ImmutableArray<string>`)/`Search.SelectedIndex`/`Search.LastQuery`/`Search.CommitAt`, read by
`PlannerTab.cs:443/457/479`; `SkyMapSearchState.Results`/`SelectedResultIndex` -> `Search.Results`/
`.SelectedIndex`, read across `SkyMapTab.Search.cs`. Test updates: `PlannerSearchInteractionTests`,
`SkyMapSearchActionsTests`, `PlannerTabLayoutTests`.

- Web note: the `CanvasTextOverlay` binds to `TextInputState` (which the base still owns as `Input`) and
  already serves both searches -- unchanged by this refactor.

## U6 -- `TextInputInteraction` -> DIR.Lib (DEFERRED, decision 2026-07-23)

After U2, `TextInputInteraction` has NO TianWen-domain deps left (the `PlannerState`/`SkyMapSearchState`
special-cases become the single `SearchInteraction? ActiveSearch` seam), so it becomes a clean DIR.Lib
promotion candidate -- key routing + clipboard + Tab-cycling over `TextInputState`, with `IPixelWidget`
(`GetRegisteredTextInputs`) + `BackgroundTaskTracker` already DIR.Lib types. **Deliberately deferred** to
keep U2 focused: promote it as a separate follow-on once `SearchInteraction` has proven out, cutting the
`IPixelWidget`/`BackgroundTaskTracker`/clipboard seams carefully. Rides its own DIR.Lib minor.

## U3 -- `DropdownMenuState` overflow scrolling (DONE + SHIPPED, DIR.Lib 6.19)

`PixelMenuWidget`/`DropdownMenuState` clip at `visibleRows` by design (the degenerate mode the
controller explicitly supports zero-config). When a menu first outgrows its viewport, adopt an
internal `ListScrollController` inside DIR.Lib -- zero consumer change. Not scheduled; recorded so
the door stays marked.

- **DONE (2026-07-23, DIR.Lib 6.19):** `DropdownMenuState.Scroll` is a row-snapped decorative
  `ListScrollController`; `RenderDropdownMenu` clamps the menu to the space below its anchor and
  feeds the scroll extent, keyboard nav scrolls via `EnsureVisible`, and `HandleScrollInput` is the
  opt-in wheel path (wired in tianwen `EquipmentTab` for the filter-name dropdown). A menu that fits
  resolves to MaxOffset 0 -- no scrollbar, byte-identical rows. `ListScrollController.VisibleAtoms`
  gained an `AtomFitEpsilon` so an exact-fit last row no longer drops to float rounding (the
  atom-model successor to the per-list +0.5px epsilon). Pinned by DIR.Lib `DropdownMenuStateTests`.

## U4 -- `ProgressBar` node factory -> `DIR.Lib.Layout.Builder` (DONE + SHIPPED, DIR.Lib 6.19)

`FormRowLayout.ProgressBar(fraction, track, fill, label?, labelFontSize?, labelColor?)` (tianwen,
added 2026-07-22) is a declarative fractional progress bar composed purely from existing primitives --
a track `Box` overlaid with a fractional-width fill (two `Star`-weighted spacers) plus an optional
centred label, via `Box`/`Overlay`/`HStack`. Zero domain dependency, zero engine/painter change (it is
pure `Builder` sugar over records the engine already measures/arranges/paints), so it belongs next to
`Builder.Split`/`Builder.Dock` in `DIR.Lib.Layout.Builder` as `Builder.Progress(...)`. Moving it there
lets every `DIR.Lib.Layout` consumer (incl. the web port) drop hand-drawn gauge painters. Pinned by
tianwen `ProgressBarLayoutTests`; the same arrange assertions move to DIR.Lib headless tests on promotion.
No behaviour change, pixel-identical. Not scheduled; recorded so the door stays marked.

- **DONE (2026-07-23, DIR.Lib 6.19):** promoted verbatim to `Layout.Builder.Progress(...)`;
  tianwen's `FormRowLayout.ProgressBar` + `ProgressBarLayoutTests` deleted, both call sites
  (LiveSessionTab exposure-state + OTA capture rows) re-pointed. DIR.Lib `LayoutProgressTests` carries
  the arrange assertions.

## U5 -- `RenderTextInput(RectF32)` overload (DONE + SHIPPED, DIR.Lib 6.15/6.16; small residue)

The layout-driven refactor left every arranged-rect text-input call site repeating a four-way
`(int)r.X, (int)r.Y, (int)r.Width, (int)r.Height` cast because `PixelWidgetBase.RenderTextInput` was
`int`-only (its `TextInputRenderer.Render` is genuinely integer-grid -- builds `RectInt`/`PointInt`).
`RegisterClickable` on the same class already took `float`, so the base was already mixed; the int
input was just never given a float sibling. Added (2026-07-22) a `protected void
RenderTextInput(TextInputState, RectF32, string, float)` overload that `MathF.Round`s once at the
boundary and forwards to the int path -- rounding, not truncating, so a fractional arranged rect keeps
the 1px border centred on the intended edge. tianwen: the four arranged-rect callers (SkyMap F3 search,
Session exposure editor, LiveSession focuser goto, Equipment create-profile) drop the cast and pass the
Fill rect directly; PlannerTab's search strip stays on the int signature (still hand-computed cursor
code, not an arranged Fill). Shipped in the DIR.Lib 6.15/6.16 line with a tianwen repin.

- **Residue (2026-07-23):** 6 MORE arranged-Fill callers -- all in `EquipmentTab.*` (`DeviceSettings.cs:123`,
  `FilterTable.cs:89`, `ProfilePanel.cs:171/246/460`, `Telemetry.cs:87`) -- still do the four-way
  `(int)r.X, (int)r.Y, (int)r.Width, (int)r.Height` cast. They were added later, during the
  layout-driven-ui L2 EquipmentTab split, after the U5 cut. Each takes a `RectF32 r` inside a Fill
  dispatch lambda, so they qualify for the same overload. Pure tianwen-only cleanup (no DIR.Lib release);
  drop the casts, pass `r` directly. Fold in as the tail of the U2 tianwen repin, or a standalone commit.

## Explicit non-moves

- `PlannerSliderInteraction` -- click-to-place semantics are planner-domain; stays.
- `AltitudeChartRenderer` / `GuideGraphRenderer` -- domain display; stay.
- Sky-map pan / FOV zoom -- unproject-based sky rotation, not a generic control.
- File-list resize divider -- trivial width math over the layout `Split` divider; not worth a control.

## Verification

- U1: DIR.Lib headless slider tests; tianwen pixel-render layout tests stay byte-identical
  (`PlannerTabLayoutTests` pattern); live viewer smoke (WB drag + scrub).
- U2: DIR.Lib headless tests for the key-nav protocol (cycle/commit/dismiss/backspace-passthrough,
  mouse-commit == keyboard-commit); tianwen planner + F3 behaviour pinned by existing search tests
  before the cut, re-run after; web overlay smoke (planner search + F3 through `CanvasTextOverlay`).
- U1 + U5 shipped in the DIR.Lib 6.15/6.16 line. U2 rides the next DIR.Lib minor after DeviceTransform's
  6.17 (6.17 if unpublished, else 6.18) + a tianwen repin; "no push before NuGet" applies as usual.
