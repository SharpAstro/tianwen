# Apple-style switches

**Objective:** Replaces tianwen's ON/OFF text blocks and On | Off segment pairs with one pill switch, in the spirit of macOS and of Drawboard's settings panel.

## The switch

**Objective:** Shows the switch on its own: every state, the two sizes, and today's controls beside the new one.

```screen id=home
The switch, its states and sizes
```

**Notes:** A rounded track with a round knob. On: Accent track, white knob at the right. Off: a dark track with a SeparatorStrong edge and a dim knob at the left, as in Drawboard's panel, so on and off differ by knob side and fill, never by colour alone. Two sizes: Regular 40 × 22 (knob 18) for settings and forms, Small 30 × 16 (knob 12) for dense rows such as the equipment list. Hover lightens the track one step. Keyboard focus draws the Focus ring 2 px outside the track. Locked (a running session) dims both track and knob, so the value can still be read. Changing… (a connect in flight) parks the knob in the middle and takes no press. In the product this would be one new DIR.Lib builder (proposed: `Builder.Switch`, coloured from the same control roles as ToggleTile), replacing both `ConfigFieldKind.BoolToggle`'s ON/OFF block and the connect `ButtonGroup`. The knob slides rather than jumps when the renderer can animate; the prototype only shows the end states. Try it: the row at the bottom flips when clicked.

## Session settings

**Objective:** Shows the session settings with a switch for each on/off preference, and how they lock while a session runs.

```screen id=settings
Horizon and Focusing groups, switches on the right
```

**Notes:** Each preference is a row: its name, one line saying what it does, and the control at the right edge, as in Drawboard's settings. The whole row is the hit, not only the switch. Unpark mount, Refocus on a new target and Save intermediate frames are today's three ON/OFF fields; their names are spelt out in full instead of the cut "Save Intermed." and "Refocus on New". Steppers keep their − value + but get the same rounded corners. Start session in the header locks the page: a Warn banner says why, and the switches show the locked look with their values kept. Stop session unlocks it.

## Equipment

**Objective:** Shows the equipment list with a small switch per device for connecting it, including a connect in flight and a disconnect that needs confirming.

```screen id=equipment
Equipment list, one small switch per device
```

**Notes:** The On | Off pair becomes the small switch, with the state in words beside it (Connected, Not connected, Connecting…) so it does not rest on colour. The focuser shows a connect in flight: knob in the middle, no press. A cooled camera no longer telegraphs danger with a red Off segment: turning its switch off opens a Warn strip under the row (Warm, then off · Off now · Cancel), and the switch stays on until one of those is chosen. Every other row flips at once. The terminal UI keeps its own On / Off cells; it is not part of this design.
