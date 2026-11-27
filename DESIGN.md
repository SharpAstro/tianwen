# Apple-style switches

**Objective:** A family of rounded, Apple-like stateful controls for DIR.Lib (a switch, a confirm-in-place strip, a busy button and a segmented control), drawn from the theme's roles and shown here in tianwen's palette with its orange accent.

## The switch

**Objective:** Shows the switch on its own: every state, the two sizes, the three themes, and today's controls beside the new one.

```screen id=home
The switch, its states, sizes and themes
```

**Notes:** A rounded track with a round knob. On: the Accent track (tianwen's orange, #ff5e00 in Dark) with a white knob at the right. Off: a HeaderBg track with a SeparatorStrong edge and a DimText knob at the left, as in Drawboard's panel, so on and off differ by knob side and fill, never by colour alone. Two sizes: Regular 40 × 22 (knob 18) for settings and forms, Small 30 × 16 (knob 12) for dense rows such as the equipment list. Hover lightens the track one step. Keyboard focus draws the Focus ring (Accent) 2 px outside the track. Locked (a running session) dims track and knob but keeps the knob's side, so the value can still be read. Changing… parks the knob in the middle and takes no press. Refused: as warboard's dispatcher switches do, the knob moves at once on a press and slides back if the far end says no, with an Error edge and the reason as its tooltip. An optional state label beside the knob names the current state when on/off alone does not say it ("One per pool" / "All by rank" in warboard). Themes: Light uses its darker orange #b83c00 and outlines the white knob when off; Night has no blue in any role, so its knob is black on the orange track instead of white. Only the switch is the hit, never its label or row. The screens use tianwen's Dark palette as hex because the prototype renderer draws DIR.Lib's default roles.

**Where the work lands:** almost all of it is DIR.Lib. Four new builders in `DIR.Lib.Layout.Builder`, each coloured only from `UiPalette` roles (Accent, ControlFill, ControlEdge, Focus, Warn, Error, Success, DimText) so Light, Dark and Night follow without code in the app: `Switch(bool on, Action<bool> onToggle, SwitchSize, string? stateLabel, string? lockedReason, bool changing)`; `ConfirmStrip`, which wraps any button and owns the asking state, Esc, the click-elsewhere and the double-click rule; `BusyButton(label, busyLabel, BusyState, string? estimate, Action? onStop)`; and `Segmented<T>(options, selected, onSelect)`, which supersedes `ButtonGroup`'s look. Plus `IconKind.Spinner`, and short animations for the knob's slide and the spinner's turn. The prototype format would then gain `switch`, `segmented` and `busyButton` kinds so later designs need not draw them from boxes. tianwen's part is small: `ConfigFieldKind.BoolToggle` and the polar setup rows call `Switch`, the equipment list's connect `ButtonGroup` becomes a small `Switch`, its warm/force strip and the profile's Remove, the Sun Goto and Abort go through `ConfirmStrip`, plate solve, autofocus and catalogue updates use `BusyButton`, and the stretch, recenter and theme groups use `Segmented`.

## Confirm in place

**Objective:** Shows how a press on an action with a cost turns, in its own place, into a question with the confirming button after it and Cancel last.

```screen id=confirm
Rest, hover and asking; Remove, Goto the Sun and Abort in tianwen
```

**Notes:** Warboard's Land confirmation, the one you chose there ("its A"), made general. The first press does nothing but open a strip where the button was: the consequence's icon, the question naming the thing ("Remove ZWO EAF?"), the confirming button filled in the consequence colour (Error for remove and abort, Warn for a hazard such as the Sun), then Cancel. The confirming button comes after the question, never under the first press, and the second click of a double-click is never a confirm, so a clumsy double-click lands nothing. Cancel, Esc or a click anywhere else puts the button back; the confirming button takes the keyboard focus. At rest a destroying action shows its label in Error. This replaces tianwen's four ways of asking today: Remove → "Confirm?", Goto → "CONFIRM" with a status-bar warning, Abort's Enter/Escape strip, and the cooled camera's red Off. After Goto, the Sun row runs into the busy button.

## Busy button

**Objective:** Shows a button for a long-running task: once pressed it shows a spinner and the task in progress, says how long it should take, and offers Stop beside it.

```screen id=busy
Rest, busy, past its estimate, done and failed; plate solve, autofocus and a catalogue update
```

**Notes:** Warboard's "Reading the records… about 15 seconds", in DIR.Lib. Pressed, the button keeps its place and size: a spinner (a turning arc in the accent on a SeparatorStrong ring) replaces nothing but sits before the label, which turns into the verb in progress ("Solving…") in DimText, and the button takes no press. Beside it, to its left: the estimate in words ("about 15 seconds") or the count when the task has steps ("step 4 of 9"), and Stop where the task can be stopped. Neither sits under the pointer that pressed the button. Past its estimate the text turns Warn ("longer than usual (45 s)") rather than counting below zero. Done shows a Success tick and edge with the result beside it for three seconds, then the button rests. Failed puts the button back at rest and says why beside it in Error. The spinner is drawn here as a ring with a dot, because the prototype has no arc; the product needs a spinner icon (`IconKind.Spinner`), drawn as an arc that turns, slower under reduced motion.

## Segmented choices

**Objective:** Shows the segmented control that replaces the square button groups for picking one of a few options.

```screen id=segments
Before and after, states, and three choices in tianwen
```

**Notes:** One rounded track holding the options; the chosen one is a raised neutral pill (SeparatorStrong) with BodyText, the others DimText on the track. The accent is kept for "on" and for the primary action, so a chosen segment never reads as a switch that is on. Hover gives an option a faint pill; locked dims the whole control; keyboard focus rings the track and ← → move the choice. Used for the frame preview's stretch, the planetary recenter mode and the home board's theme picker; an on/off pair such as the connect On | Off becomes a switch, not a two-segment control.

## Session settings

**Objective:** Shows the session settings with a switch for each on/off preference, and how they lock while a session runs.

```screen id=settings
Horizon and Focusing groups, switches on the right
```

**Notes:** Each preference is a row: its name, one line saying what it does, and the control at the right edge, as in Drawboard's settings. Only the switch flips the preference, as on the equipment list. Unpark mount, Refocus on a new target and Save intermediate frames are today's three ON/OFF fields; their names are spelt out in full instead of the cut "Save Intermed." and "Refocus on New". Steppers keep − value + but get the family's rounded, edged buttons. Start session in the header locks the page: a Warn banner says why, and the switches show the locked look with their values kept. Stop session unlocks it.

## Equipment

**Objective:** Shows the equipment list with a small switch per device for connecting it, including a connect in flight and a disconnect that needs confirming.

```screen id=equipment
Equipment list, one small switch per device
```

**Notes:** The On | Off pair becomes the small switch, with the state in words beside it (Connected in Success, Connecting… in Info, Not connected in DimText) so it does not rest on colour. Only the switch connects or disconnects; a click on the device's name or row does not. The focuser shows a connect in flight: knob in the middle, no press. A cooled camera no longer telegraphs danger with a red Off segment: turning its switch off rings it in Warn and opens the confirm strip under the row (Warm, then off · Off now · Cancel), and the switch stays on until one of those is chosen. Every other switch flips at once. The terminal UI keeps its own On / Off cells; it is not part of this design.
