# Automatic text input: declare a field, and it works

**Status: P1 + P2 + U6 + P3a SHIPPED (2026-08-14); P3b (declarative auto-focus) deferred.**

`Layout.Builder.TextInput(state, fontSize)` is the whole declaration on both surfaces, `TextInputFocus`
owns the transition, and `TextInputInteraction` lives in DIR.Lib and serves all three hosts. What
changed against the plan below, and why:

- **One release wave, not two.** The plan sequenced P1 -> release -> P2 -> release. P1, P2, U6 and P3a
  are all DIR.Lib/Console.Lib changes over the same call sites, so batching them into one minor halves
  the lockstep cost and lets P4 migrate against a single repin. The plan's own ordering note ("do U6
  **with or immediately before** P2 rather than as separate migrations of the same call sites") is the
  same argument one step further.
- **P4 ran BEFORE the release, against sibling source.** Validating a new library API at its real call
  sites is what stops a wrong API being frozen in a published package -- and it earned its keep twice
  (see the two design changes below, both forced by a consumer).
- **`KeyContext.ActiveTab` (an `IPixelWidget`) became `TabFields` (a lazy field-list callback).** That
  interface was the one thing keeping a class whose whole purpose is being host-agnostic from working on
  a terminal. Console.Lib gained `CellLayout.TextInputs(arranged)` as the cell-surface answer.
- **`HandleKey` no longer takes the field as a parameter**; it reads `ctx.Focus.Current`. Two ways to
  name the focused field is one too many -- a caller passing a field the owner does not consider focused
  would move focus off a *different* one on the next Tab, silently.
- **`TextInputRenderer` no-ops without a font**, matching the layout text helpers. Not cosmetic: a
  headless render is how the layout tests check what was drawn, and a tree with a LABEL rendered while
  the same tree with a FIELD threw.
- **P3b (`autoFocus: true`) is deferred, deliberately.** It needs a signal the library does not have --
  "the dialog opened" -- and the honest formulations of it ("the field appeared this frame") require
  per-frame painted-set tracking whose semantics are genuinely ambiguous: does a modal opening steal
  focus from a field the user is typing in? Both answers are defensible, which is the tell that the
  consumers should be looked at first. The existing hosts post an activation signal by hand and are
  fine. P3a (blur when unpainted) is shipped and is the half that fixes a real bug.

Everything below is the original plan, kept as written.

---

**Status when raised: PLANNED (by the user 2026-08-14).** No code written.

The goal, in the user's words: *"by defining a text input box, the wiring of it being able to receive
text input is entirely automatic -- like in WinForms I drag a TextBox onto the Form and it can receive
text, that kind of fidelity."*

Prompted by the observation that `GuiAppState.ActiveTextInput` "is kind of still global in a way",
right after a sweep fixed one bad writer to it (the Equipment site-edit cancel path, which cleared the
pointer by hand and so skipped SDL `StopTextInput`).

## Start here: most of the fidelity already exists

Worth stating plainly before proposing work, because it changes what the plan is FOR. Against the
WinForms bar, these already happen with no per-field wiring:

| Behaviour | How it already works |
|---|---|
| Click a field to focus it | `GuiEventHandlerBase` maps `HitResult.TextInputHit` -> `ActivateTextInput`. No per-field code. |
| Click outside to blur | Same handler, the `hit is not HitResult.TextInputHit` branch. |
| Tab / Shift+Tab between fields | `ClickableRegionTracker.GetRegisteredTextInputs()` -- derived from REGION PAINT ORDER, so tab order is the visual order automatically, with nothing to maintain. |
| Typing, selection, clipboard, Home/End, commit on Enter, cancel on Escape | `TextInputInteraction` + `TextInputState`, shared by every field. |
| The I-beam cursor | `RenderTextInput` registers `CursorKind.Text` itself (DIR.Lib 7.22). |

So the gap is **not behaviour**. It is **declaration**: saying "there is a field here" costs far more
than it should, and says it in three places that a compiler cannot keep in agreement.

## The actual gap

Adding one text box today (13 declared `TextInputState` fields plus two runtime-populated collections --
`EquipmentTabState.CameraSetpointInputs` keyed per camera, `LiveSessionState.FocuserGotoInputs` per OTA --
across 11 `RenderTextInput` call sites):

1. Declare the state: `public TextInputState FooInput { get; } = new() { Placeholder = "..." };`
   *(legitimate -- WinForms has a `TextBox` object too)*
2. Wire `OnCommit` / `OnCancel` / `OnTextChanged`. *(legitimate -- this is business logic)*
3. Put a **keyed `Fill` leaf** in the layout tree: `FormRowLayout.LabeledInputRow(..., fillKey: "foo")`
4. Register a painter in the tab's fill dictionary:
   `_profilePanelFills["foo"] = r => RenderTextInput(input, r, fontPath, fontSize * 0.9f);`
5. Ensure the panel's `drawFill` callback dispatches that dictionary.

**Steps 3-5 are the defect.** The field's identity is a magic string shared between a tree and a
dictionary; the painter lambda re-states the font and size the tree already knows; and nothing checks
that a key in one place has an entry in the other -- a typo yields a silently blank field, not an error.
A `Fill` leaf means "the app paints something arbitrary here", which is the right escape hatch for a
chart and the wrong description of a text box, because a text box is not arbitrary: DIR.Lib already owns
its painting, its hit region, its focus semantics and its key handling.

**And the TUI reimplements the whole concept.** `TuiEquipmentTab` hand-rolls caret arithmetic
(`ComposeSiteRow` returning a `(Text, CaretColumn)` pair, `_siteBar.Caret(...)`, pinned by
`TuiSiteRowTests`) because there is no cross-surface way to declare a field. That is a second
implementation of the same idea, with its own bugs to find.

## P1 -- `Layout.Content.TextInput`, a first-class leaf (DIR.Lib)

```csharp
Layout.Builder.TextInput(state, fontSize)     // that is the whole declaration
```

A new `Content.TextInput(TextInputState State, float FontSize)` leaf. When `PixelWidgetBase.PaintLayout`
meets one, it does exactly what `RenderTextInput` does today -- `TextInputRenderer.Render` into the
arranged rect, `RegisterClickable(..., new HitResult.TextInputHit(state), cursor: CursorKind.Text)` --
but driven by the tree instead of by a dictionary. Steps 3-5 collapse into step 3.

This is the WinForms moment: the node IS the control. Focus, tab order, the I-beam and key handling all
follow from the registration, which the painter now always performs because it cannot be forgotten.

**Design notes:**

- **The node carries a reference to caller-owned mutable state.** That is already the precedent --
  `Node.OnClick` carries a delegate closing over live state, and the tree is rebuilt per frame anyway.
  Ownership does not move: the tab still owns the `TextInputState` and its commit wiring.
- **`Fill` stays** for genuinely bespoke fields. This is sugar over the same registration, not a
  replacement for the escape hatch.
- **Fields created per camera / per OTA fall out for free**, which is worth checking against any
  alternative design that gets considered. Two of the collections above are populated at runtime as
  hardware appears, so a field cannot be a statically-declared control the way a WinForms designer
  emits one. A leaf in a per-frame tree handles "one field per connected camera" as an ordinary loop,
  with the state dictionary keyed however the tab already keys it.
- **`CellLayout` (Console.Lib) paints the same leaf** as a terminal field. This is the biggest prize and
  should be scoped in from the start rather than retrofitted: it retires the TUI's hand-rolled caret
  arithmetic and makes a field one declaration on both surfaces, the way `HomeBoardLayout` already made
  a rig card one tree. Note the authoring-unit crossing (`CellMeasureContext.PixelAuthored` /
  `PixelMeasureContext.CellAuthored`) already has precedent to follow.

## P2 -- `TextInputFocus`: keep the singleton, make it unrepresentable to break

**Be honest about the "global" objection: focus is global, and it has to be.** There is one keyboard, so
something must name the one field receiving it. WinForms has exactly the same singleton in
`Form.ActiveControl`. `ActiveTextInput` is not wrong for being global.

What IS wrong is that **the pointer and its platform side effects are separable**. The host's
`ActivateTextInputSignal` / `DeactivateTextInputSignal` handlers are the only things that also call SDL
`StartTextInput` / `StopTextInput`, so any code that assigns the field directly desynchronises the app
from the platform -- the field stops taking input while the IME / on-screen keyboard stays up. That is
not hypothetical; it is the bug fixed on 2026-08-14, and its shape is instructive: the cancel path
deactivated its inputs by hand FIRST, which (the bus being deferred, and the handler being gated on the
input still being active) made posting the signal a no-op, so the direct assignment looked *necessary*.

So: a `TextInputFocus` owner in DIR.Lib.

```csharp
TextInputState? Current { get; }
void Focus(TextInputState input);      // blurs the previous one
void Blur();
event Action<TextInputState?, TextInputState?> FocusChanged;   // old, new
```

- The **host binds `FocusChanged` once** to SDL `StartTextInput` / `StopTextInput`. No other code knows
  the platform calls exist.
- `GuiAppState.ActiveTextInput` becomes a read-only forward to `Current` (or is deleted outright; a
  read-only forward is the cheaper migration and is not a shim, since there is no second implementation
  to keep in step).
- The transition is expressible only one way, so **the class of bug the sweep found stops being
  reachable** rather than being fixed once. That is the actual answer to "it is kind of still global".

**Ordering note:** U6 in [controls-upstreaming.md](controls-upstreaming.md) already proposes promoting
`TextInputInteraction` to DIR.Lib and is deferred-but-unblocked. The focus owner and the key router are
the same concern; do U6 **with or immediately before** P2 rather than as separate migrations of the same
call sites.

## P3 -- The two fidelity gaps left after P1/P2

Both are small, and neither is worth doing before P1 lands.

- **A focused field that stops being painted keeps focus.** Scroll a focused field out of a culled list
  and its region is no longer registered, but `Current` still points at it, so typing edits an invisible
  field. WinForms blurs a control removed from `Controls`. Rule to adopt: if the focused input registered
  no region this frame, blur it. Cheap, since the region list is already walked for tab cycling -- but
  verify against the viewport-culled panels (`SessionTab`'s observation list) before assuming the
  cull is what drops it.
- **Nothing can ask for focus declaratively.** WinForms has `TabIndex` + `Focus()`. A dialog that opens
  wanting its first field hot currently posts an activation signal by hand. `Builder.TextInput(state,
  fontSize, autoFocus: true)` would cover it, with the "first painted node that asks, once per open"
  semantics stated carefully so it cannot re-steal focus every frame.

## P4 -- Migration + verification

- Re-point the 11 `RenderTextInput` call sites to `Builder.TextInput`, deleting the fill-key entries and
  painter lambdas (`_profilePanelFills`, `_otaPanelFills`, and the Session/SkyMap/Planner equivalents).
- Convert the TUI site row and delete `ComposeSiteRow`'s caret arithmetic; `TuiSiteRowTests` becomes a
  test of the shared leaf on a cell surface, which is a strictly better assertion than the one it replaces.
- **Verification bar** ([[feedback_ui_refactor_verification_bar]]): arranged-rect pins plus offline
  `RgbaImageRenderer` renders, now genuinely faithful since DIR.Lib 7.25 made that backend honour clips.
  Add a focus-invariant test: no path may change the focused field without the `FocusChanged` hook firing.

## Cost and sequencing

P1 and P2 are **DIR.Lib** changes, so each rides a DIR.Lib minor plus the Console.Lib /
SdlVulkan.Renderer / WebGl.Renderer lockstep rebuilds and a tianwen repin ("no push before NuGet"
applies). That is the real price of this plan and it should be weighed against the fact that the
*behaviour* is already right -- this buys declaration ergonomics, cross-surface reuse, and the
elimination of a bug class, not new capability.

Suggested order: **P1** (biggest ergonomic win, no focus-model risk) -> **U6 + P2** (one migration of
the same call sites) -> **P4** -> **P3** as polish.
