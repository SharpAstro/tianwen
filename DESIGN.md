# Apple-style switches

**Objective:** Revamps tianwen's stateful controls in one rounded, Apple-like family: a pill switch for on/off, a two-step confirm button, and a segmented control, all in tianwen's own palette and orange accent.

## The switch

**Objective:** Shows the switch on its own: every state, the two sizes, the three themes, and today's controls beside the new one.

```screen id=home
The switch, its states, sizes and themes
```

**Notes:** A rounded track with a round knob. On: the Accent track (tianwen's orange, #ff5e00 in Dark) with a white knob at the right. Off: a HeaderBg track with a SeparatorStrong edge and a DimText knob at the left, as in Drawboard's panel, so on and off differ by knob side and fill, never by colour alone. Two sizes: Regular 40 × 22 (knob 18) for settings and forms, Small 30 × 16 (knob 12) for dense rows such as the equipment list. Hover lightens the track one step. Keyboard focus draws the Focus ring (Accent) 2 px outside the track. Locked (a running session) dims track and knob but keeps the knob's side, so the value can still be read. Changing… (a connect in flight) parks the knob in the middle and takes no press. Themes: Light uses its darker orange #b83c00 and outlines the white knob when off; Night has no blue in any role, so its knob is black on the orange track instead of white. Only the switch is the hit, never its label or row. The screens use tianwen's Dark palette as hex because the prototype renderer draws DIR.Lib's default roles. In the product this would be one new DIR.Lib builder (proposed: `Builder.Switch`, coloured from the control roles ToggleTile already uses), replacing `ConfigFieldKind.BoolToggle`'s ON/OFF block, the connect `ButtonGroup` and the polar setup's On/Off rows. The knob slides when the renderer can animate; the prototype shows end states only.

## Confirm buttons

**Objective:** Shows the two-step button for actions with a cost: the first press arms it, a second press within five seconds acts, and anything else disarms it.

```screen id=confirm
Rest, armed and working; Remove, Goto the Sun and Abort in tianwen
```

**Notes:** One pattern for what tianwen does today in four different ways (Remove → "Confirm?", Goto → "CONFIRM" with a status-bar warning, Abort's Enter/Escape strip, the cooled camera's red Off). At rest the button is neutral, with its label in Error when the action destroys something. Armed, it fills with the consequence colour (Error for remove and abort, Warn for a hazard such as the Sun), its label turns into the question, a thin bar under it runs down the five seconds, and the reason sits beside it in words. The button keeps its width when armed, so nothing moves under the pointer and a double-click cannot confirm by accident (the second press counts only after 300 ms). Esc, a click elsewhere or the bar running out disarm it. Working shows the verb in progress and takes no press. A switch whose change has a cost uses the same idea without a second press: see the camera on Equipment.

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

**Notes:** The On | Off pair becomes the small switch, with the state in words beside it (Connected in Success, Connecting… in Info, Not connected in DimText) so it does not rest on colour. Only the switch connects or disconnects; a click on the device's name or row does not. The focuser shows a connect in flight: knob in the middle, no press. A cooled camera no longer telegraphs danger with a red Off segment: turning its switch off rings it in Warn and opens a strip under the row (Warm, then off · Off now · Cancel), and the switch stays on until one of those is chosen. Every other switch flips at once. The terminal UI keeps its own On / Off cells; it is not part of this design.
