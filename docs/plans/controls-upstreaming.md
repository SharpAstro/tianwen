# Controls upstreaming: promote generic controls to DIR.Lib

**Status: PLANNED (2026-07-21).** Follow-on to
[interaction-primitives.md](interaction-primitives.md); taxonomy + inventory in
[../architecture/widgets-and-controls.md](../architecture/widgets-and-controls.md).

Trigger: the widget-vs-control survey found three controls that are generic (no TianWen domain
dependency) but live in `TianWen.UI.Abstractions`, violating the layering rule that generic controls
belong in DIR.Lib next to `PixelWidgetBase`/`TextInputState`. Two of them (the search pair) are also
a duplication: the planner autocomplete and the sky-map F3 modal hand-roll the same
input + suggestion-list + key-nav + commit machinery around `TextInputState`.

**Release vehicle: DIR.Lib 6.16 (a minor AFTER the pending 6.15 lockstep chain ships).** Do NOT grow
the P2 release -- 6.15 is frozen (primitives + fits-again re-pin) and tianwen is already stacked on
it. Each U-phase below is independently shippable.

## U1 -- `TrackSlider` -> DIR.Lib

`ImageRendererBase.TrackSlider.cs` is ~50 lines with zero domain dependencies: `DrawTrackSlider`
uses `FillRect`/`RegisterClickable`/`DpiScale` (all `PixelWidgetBase` members) + `RectF32`/
`RGBAColor32`/`HitResult` (all DIR.Lib); `TrackFrac` is pure math. Only the `TransportTrackBg`/
`TransportHandle` chrome colours are tianwen's.

- Move both methods onto `PixelWidgetBase<TSurface>` as `protected` helpers; the track + handle
  colours become parameters with the current values as defaults (or a small `TrackSliderChrome`
  record). `TrackFrac` becomes `protected static`.
- tianwen: delete `ImageRendererBase.TrackSlider.cs`, pass its chrome at the three call sites
  (WB / wavelet / scrub). Zero behaviour change; pixel-identical.
- Tests: the slider maths gets DIR.Lib headless tests (fraction clamp, sliver-track handle clamp --
  the minimize-to-sliver crash guard).

## U2 -- `SearchInteraction` base -> DIR.Lib, tianwen searches become subclasses

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

Design: `DIR.Lib.SearchInteraction<TResult>` (abstract class next to `TextInputState`):

- Owns `TextInputState Input`, `ImmutableArray<TResult> Results`, `int SelectedIndex`,
  `string LastQuery`; wires the four `TextInputState` callbacks in its ctor.
- Abstract seams: `UpdateResults(string query)` (domain resolve), `CommitResult(TResult)`,
  `DisplayText(TResult)`; virtual `OnDismissed()`. Host callbacks (`deactivateFocus`,
  `requestRedraw`) passed in, like `PlannerSearchInteraction.Wire` today.
- The key-nav protocol lives ONCE in the base (today it is split three ways:
  `TextInputInteraction`'s suggestion cycling, `PlannerSearchInteraction.OnKeyOverride`, and the
  sky map's inline Up/Down).
- tianwen: `PlannerSearchInteraction : SearchInteraction<string>`,
  `SkyMapSearchInteraction : SearchInteraction<SkyMapSearchResult>`; the per-widget rendering
  (dropdown rows vs modal rows) stays in the widgets -- the base is interaction state, not paint.
- Evaluate during U2: `TextInputInteraction` itself (key routing + clipboard over `TextInputState`,
  `TextInputKey` is DIR.Lib) looks promotable wholesale -- decide when the seams are cut; anything
  signal-bus-flavoured stays behind the host callbacks.
- Web note: the `CanvasTextOverlay` binds to `TextInputState` and already serves both searches --
  unchanged by this refactor.

## U3 -- `DropdownMenuState` overflow scrolling (opportunistic)

`PixelMenuWidget`/`DropdownMenuState` clip at `visibleRows` by design (the degenerate mode the
controller explicitly supports zero-config). When a menu first outgrows its viewport, adopt an
internal `ListScrollController` inside DIR.Lib -- zero consumer change. Not scheduled; recorded so
the door stays marked.

## U4 -- `ProgressBar` node factory -> `DIR.Lib.Layout.Builder` (opportunistic)

`FormRowLayout.ProgressBar(fraction, track, fill, label?, labelFontSize?, labelColor?)` (tianwen,
added 2026-07-22) is a declarative fractional progress bar composed purely from existing primitives --
a track `Box` overlaid with a fractional-width fill (two `Star`-weighted spacers) plus an optional
centred label, via `Box`/`Overlay`/`HStack`. Zero domain dependency, zero engine/painter change (it is
pure `Builder` sugar over records the engine already measures/arranges/paints), so it belongs next to
`Builder.Split`/`Builder.Dock` in `DIR.Lib.Layout.Builder` as `Builder.Progress(...)`. Moving it there
lets every `DIR.Lib.Layout` consumer (incl. the web port) drop hand-drawn gauge painters. Pinned by
tianwen `ProgressBarLayoutTests`; the same arrange assertions move to DIR.Lib headless tests on promotion.
No behaviour change, pixel-identical. Not scheduled; recorded so the door stays marked.

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
- Both ride DIR.Lib 6.16 + a tianwen repin; "no push before NuGet" applies as usual.
