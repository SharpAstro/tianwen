# One colour core across the org site, the GUI and the viewer

Status: **C0, C1 and C2 are DONE (2026-08-07/08).** Three palettes ship on one 16-role `UiPalette`,
F12 toggles Night, and the sweep is down to **98 literals from the original 317**, all of them
deliberate and noted at their declaration. C3 to C5 are open, and C4 has shrunk.

Three surfaces carry three unrelated palettes today. This unifies them on one token set with
four states (System / Light / Dark / Night), where Night is a dark-adaptation mode for use at
the mount, not a restyling of dark mode.

## Why now

The immediate trigger was wanting the desktop's own light/dark setting to drive the app, which
the sibling `pdf-viewer` recently gained. But following the desktop is meaningless without a
light theme to follow it to, and adding a second theme to the app exposes the real problem:
the theme records only cover the eight shared chrome roles, and **317 colour literals outside
tests** sit outside them.

```
266  TianWen.UI.Abstractions
 43  TianWen.UI.Gui
  6  TianWen.Cli
  1  TianWen.UI.FitsViewer
  1  TianWen.Lib
```

Both `GuiTheme` and `ViewerTheme` say in their doc comments that tab-specific colours
"deliberately stay local to their owner". That was correct for a single fixed dark theme and is
exactly wrong for a second one, because a state switch has to reach every literal. Turning those
into role lookups is the bulk of this work; the palette values are an afternoon.

**And the blast radius does not stop at this repo.** `DIR.Lib` already carries `UiPalette`, but it
*also* carries concrete colours of its own that a state switch has to reach, baked as
`private static readonly` with no seam to override them:

- `TabBar` holds eight (`BarBg #14141c`, `ActiveBg #2c2c3c`, `Separator #3a3a48`,
  `ActiveAccent #4488ff`, and so on). Those values are not arbitrary; they are the sibling
  `pdf-viewer`'s dark palette, baked into a shared widget from its first consumer.
- `MenuColors` is a `record` with five defaults, so it is at least overridable per instance, but
  its defaults are `VkMenuWidget`'s original palette rather than anything role-derived.

`pdf-viewer` has already solved the TabBar half in the `drawboard` fork, and the shape is the one
to copy rather than reinvent: a `TabBarColors` record with a `FromPalette(UiPalette)` factory, plus
a settable `TabBar.Colors`, derived by the consumer **only when the theme moves** rather than per
frame. Porting that upstream is a small, well-specified change with a working reference. `TabBar`
is consumed beyond TianWen, so it wants to land as a DIR.Lib minor rather than as a local patch.

## The three palettes as they stand

| Role | Site dark | `GuiTheme` | `ViewerTheme` |
|---|---|---|---|
| Deepest bg | `#070a12` | `#16161e` | `#1a1a1a` |
| Panel | `#0d1220` | `#1e1e28` | `#262626` |
| Header / strip | `#111829` | `#22222c` | `#2e2e33` |
| Body text | `#dbe2ee` | `#cccccc` | `#e6e6e6` |
| Dim text | `#8895ac` | `#888888` | `#b3b3b3` |
| Accent | `#66ddcc` teal | `#88aadd` blue | `#99ccff` cyan |
| Separator | `#1b2334` | `#333344` | `#4d4d59` |

Three accents with nothing tying them together, and three neutral ramps that differ in hue bias:
the site's is strongly blue, `GuiTheme` faintly blue, `ViewerTheme` pure neutral grey (R=G=B
exactly). The GUI hosts the viewer in a tab, so the last two sit side by side and the viewer
reads visibly colder and flatter than its host.

Two smaller findings, both fixed by the table below:

- `GuiTheme.HeaderText` is doing double duty as header colour and de-facto accent, while
  `Selection` (`#203050`) is a near-black navy that barely registers.
- The org profile README has no colour surface at all. GitHub renders it, so the only levers
  there are the avatar and the images. Nothing to do.

## Candidates

`tools/theme-mocks.py` renders three candidate cores, each in Light / Dark / Night, as the same
slice of the GUI (rail, header, mount telemetry, guide graph, severity trio, progress) so they are
comparable. Output plus a contact sheet and an `index.html` contact sheet land in
`docs/plans/colour-theme-mocks/`. Re-run it after changing any value; it also prints the contrast
and rod numbers.

| Core | Argument | Night rod index |
|---|---|---|
| **A. Observatory** | The site palette extended to the app. Teal reads as instrument readout, and it is continuous with what ships at `sharpastro.github.io`. | 0.034 |
| **B. Plate** | Cool neutrals, one accent, no secondary. Closest to today's `GuiTheme`, so the lowest visual shock, and the strictest Night (nearest to R-only). | **0.024** |
| **C. Ember** | Warm neutrals, amber-led. Dark to Night becomes a shift in degree rather than in kind, so the app keeps one identity across all four states. | 0.053 |

The numbers say what the pitches cannot. C. Ember's continuity is real but it is the most
rod-stimulating Night of the three, by roughly 2.2x over B, because the warmth it carries into Dark
is green-channel content it then cannot drop. B. Plate wins Night on the metric and loses on
identity, being the least distinguishable from any other dark tool. A sits between them and is the
only one already shipping somewhere.

Night body-text contrast tracks the same axis: B `#cc0f00` is 3.60:1, A `#d92200` is 4.13:1, C
`#e04a00` is 4.98:1. Only C clears AA, and it clears it by spending exactly the green the rod index
is penalising. That trade is the decision.

## The core

Twelve roles, four states, identical role names on every surface so consumers are state-blind.

**CHOSEN 2026-08-07 (user, from the studio): B. Plate for Light and Dark, C. Ember for Night.**

| Role | Light | Dark | Night |
|---|---|---|---|
| `ContentBg` | `#f2f4f6` | `#101318` | `#000000` |
| `PanelBg` | `#ffffff` | `#171b22` | `#0c0400` |
| `HeaderBg` | `#e9edf1` | `#1e232c` | `#180800` |
| `Separator` | `#d8dee5` | `#2a3039` | `#2e1200` |
| `SeparatorStrong` | `#bcc5cf` | `#3c444f` | `#4d1e00` |
| `BodyText` | `#14181d` | `#e2e6ec` | `#e04a00` |
| `DimText` | `#5a626c` | `#8b939f` | `#b83c00` |
| `Accent` | `#0a63a8` | `#7cc4ff` | `#ff6a00` |
| `AccentAlt` | `#0a63a8` | `#7cc4ff` | `#a83c00` |
| `Info` | `#0a63a8` | `#7cc4ff` | `#8c3000` |
| `Warn` | `#8a5000` | `#e8a33c` | `#cc5c00` |
| `Error` | `#b02a20` | `#ff7a70` | `#ff1500` |

`HeaderText` folds into `Accent` (see the widening section: they were the same field, which is why
the app had no accent). Light and Dark share one accent, so `AccentAlt` equals `Accent` there and
the guide graph's two traces separate by dash rather than hue; Night gives `AccentAlt` a muted
variant so it does not collide with `Warn`.

**Dark is `GuiTheme` retuned, not replaced.** Same cool-neutral family, but the ramp gains a real
blue bias (today's `#1e1e28` has R=G with only blue lifted, which reads faintly violet; `#171b22`
steps R<G<B and reads as a true cool grey), and every text pair gains contrast: body 10.29:1 ->
13.78:1, dim 4.66:1 -> 5.57:1, accent 6.96:1 -> 9.21:1.

**Mixing cores across states is deliberate.** Taking Ember's Night loses its Dark-to-Night
continuity, which was that core's whole pitch. That is the right trade: continuity is only a virtue
if Dark and Night should feel like the same place, and Night is a mode that must never be entered
by accident and must be unmistakable once entered. A visible discontinuity at that boundary is
doing useful work.

**The site keeps its own Dark and Light** (`#070a12` void, teal `#66ddcc`, amber `#ffb35c`). The
original plan had the app adopt them; the studio comparison went the other way. So the site and the
app now share the token *vocabulary* and the four-state model but not the values, and that is a
deliberate split rather than drift: a landing page wants a memorable identity, and an instrument
panel wants neutral surrounds that do not tint the images and charts on top of them. Do not
"reconcile" them later without re-reading this paragraph.

Mapping onto `DIR.Lib.UiPalette`'s eight roles:

```
ContentBg   -> void
PanelBg     -> panel
HeaderBg    -> panel-2
HeaderText  -> accent
BodyText    -> text
DimText     -> dim
Separator   -> line
Selection   -> line-strong, or the desktop accent where one is published
```

`ViewerTheme` keeps its own `UiMetrics` (18px base font against the GUI's 14px) and its five
translucent panel fills. Those are alpha decisions rather than colour ones and do not map onto
the opaque shared roles.

## Widening `UiPalette` (in scope, and cheapest now)

**Nothing outside this repo consumes `UiPalette` yet.** Declared in one DIR.Lib file, referenced by
zero files in `Console.Lib`, `SdlVulkan.Renderer` and `WebGl.Renderer`, and by three in TianWen
(`GuiTheme`, `ViewerTheme`, `EquipmentPanelLayout`), plus `pdf-viewer`'s `ChromePalette` in the
`drawboard` fork. So the "cut an API in one wave, no shims" rule costs about four files here. It
will not stay that cheap: the moment the other renderers adopt it the same change becomes a
coordinated multi-repo release. Widen it before they do.

Eight roles cannot express a four-state theme. What is missing:

| Add | Why |
|---|---|
| `Accent` | `HeaderText` is currently doing double duty as both, which is why `GuiTheme.Selection` ended up a near-black navy with no accent anywhere. Splitting them is finding 4 above. |
| `AccentAlt` | The guide graph needs two traces. Today they are literals. Defaults to `Accent` for a single-accent core like B. |
| `SeparatorStrong` | Two rule weights, which the site already uses (`line` against `line-strong`). Defaults to `Separator`. |
| `Focus` | Keyboard focus is not selection, and conflating them means a focus ring that vanishes on the selected row. Defaults to `Accent`. |
| `Info` / `Warn` / `Error` | See below. |
| `IsDark` | `pdf-viewer` threads a separate `dark` bool through every draw call because the palette cannot answer it. A palette that knows what ground it is makes overlay alpha, shadow direction and icon inversion derivable instead of parameters. |

**The semantic three belong on the palette, and the current reasoning against it does not hold.**
`GuiTheme` keeps them out on the grounds that `UiPalette` "lives in DIR.Lib and knows nothing about
notifications". But info / warn / error are generic UI severity, not a notification concept; the
notifications feed is merely the first consumer. More decisively, the moment a second state exists
they must switch in lockstep with everything else, so leaving them out means every consumer
re-derives three colours per state by hand. That is the same duplication the theme records were
extracted to kill.

**Change the shape too: `readonly record struct` to `sealed record`.** Three reasons, and the
middle one is the one that bites:

- **Additive growth stops breaking call sites.** A positional record means every new role edits
  every construction. Fifteen positional arguments is also unreadable.
- **`required` becomes enforceable.** A record *struct* always has an implicit parameterless
  constructor that property initializers do not run, so `default(UiPalette)` and `new UiPalette()`
  both yield all-zero, which for a palette is transparent black painted silently everywhere. This
  is the recorded record-struct default-constructor gotcha, and a palette is the worst possible
  place for it: the failure is invisible rather than loud. A `sealed record` makes the omission a
  compile error and a null a clean throw.
- **It is cheaper to pass.** A reference against fifteen inline fields, provided the palette is
  derived when the theme *moves* rather than per frame, which `pdf-viewer` already learned and
  documented.

Roles that default to another role need a nullable backing field rather than a property
initializer, since an initializer cannot reference a sibling:

```csharp
private readonly RGBAColor32? _separatorStrong;
public RGBAColor32 SeparatorStrong
{
    get => _separatorStrong ?? Separator;
    init => _separatorStrong = value;
}
```

`MenuColors` in the same library is already a `record` class with defaulted init properties, so
this is the library's own established shape rather than a new one.

**Then give the widgets the seam.** `TabBarColors.FromPalette(UiPalette)` plus a settable
`TabBar.Colors`, ported from the `drawboard` fork; `MenuColors` gains the same factory so its
defaults stop being `VkMenuWidget`'s frozen palette. Both are additive.

## Night mode

### It is a fourth state, never what "dark" resolves to

Most TianWen hours are desk hours: planning, stacking, reviewing subs, in a normally lit room.
Red on black there is fatiguing for no benefit, because dark adaptation only matters within a few
metres of the eyepiece. So Night must be unreachable by accident (System-dark must never resolve
to it) and reachable in one keystroke at the mount.

This mirrors what `pdf-viewer/src/PdfViewer/View/ContentTheme.cs` concluded for its own four
states: "four states rather than a toggle, so 'follow the desktop' is a real choice and not merely
the initial value of a boolean the reader then has to keep in step by hand."

### Red is the only cheap channel. Green and blue are both expensive

The rule that shapes the whole Night column: **B = 0 everywhere, and G is the scarce currency you
spend to buy hue separation.**

Rods peak near 507 nm, but neither sRGB primary sits there. Taking the scotopic luminous
efficiency V'(lambda) at each primary's dominant wavelength:

| Primary | Dominant | V'(lambda) | Relative cost |
|---|---|---|---|
| Red | ~611 nm | 0.0155 | 1x |
| Green | ~549 nm | 0.49 | 32x |
| Blue | ~464 nm | 0.61 | 39x |

So the framing is not "avoid blue". Blue is marginally *worse* than green per unit radiance, and
both are roughly 30 to 40 times worse than red. The popular "blue light" advice is about circadian
melatonin, a different mechanism, and following it here would protect the wrong thing by implying
green is safe. It is not.

Green-channel budget in the table above: text 34, dim 24, accent 59, info 48, warn 92, error 21.
Blue is zero in every one.

`tools/theme-mocks.py` computes this as a **rod index** per palette so the trade-off is read rather
than guessed. What it shows for the candidates: warn `#cc5c00` costs 0.062 against error
`#ff1500` at 0.019, so the hue separation between them is bought at roughly 3.3x the rod
stimulation. That is the price of a distinguishable warn, stated rather than hidden.

### Semantic hue survives, which is why Night is worth doing

Hue discrimination continues inside the long-wavelength band because cones keep working there
while rods do not. Of the three current accents only `SeverityInfo` `#5588cc` is blue-dominant
and has to change character; `SeverityWarn` `#cc9933` is already amber and needs its green
restrained; `SeverityError` `#cc4444` is already red.

One trap, because it inverts the obvious choice. Green carries 71.5% of relative luminance, so a
true amber warn out-shouts a red error:

| Role | Night | Relative luminance |
|---|---|---|
| `info` `#993000` | recedes, as it should | 0.088 |
| `warn` `#cc5c00` | burnt orange | 0.205 |
| `error` `#ff1500` | red | **0.218** |

Dialing warn back from amber to burnt orange restores the ordering on luminance while keeping the
hues apart. That pair is the tightest call in the palette (about 22 degrees of hue) and wants
validating at the mount, so error should also carry a form cue (filled stripe against warn's
outline) as cheap insurance.

Anything that currently encodes meaning in hue needs the same treatment: cooling state, the flip
countdown. In Night they take positions in the warm band plus a dash or weight difference.

**The guide graph is the exception, by the user's call (2026-08-08): RA and Dec keep their blue and
orange in every state.** The two traces are the one place where a Night-safe pair genuinely cannot
be found, since neither blue nor green is available and two warm hues 20 degrees apart are not
separable at speed on a scrolling plot. Recolouring them would have meant inventing a dash or
weight distinction for the sake of a rule, on the surface most likely to be read at a glance.
Leaving them is a deliberate hole in Night's coverage rather than an oversight, and it is a small
one: the guide graph is a bounded region, not a full-screen ground.

### In Night, labels use BodyText. DimText is for chrome nobody reads

Found by eye in the studio, then confirmed as structural rather than as a bad hex. Pure red on
black caps at 5.25:1, so the **entire** Night text ladder has to fit between 1:1 and that ceiling.
Body sits at 4.98:1, close to it. Any `DimText` that clears AA at 4.5:1 is then within a hair of
body and stops reading as secondary at all, while anything clearly secondary falls under AA. Both
cannot hold. Lifting Ember's dim from `#a83500` (3.07:1) to `#b83c00` (3.57:1) is the whole
available margin.

So the rule, not the colour, carries it: **anything that has to be read uses `BodyText` in Night.**
In the Mount card, "RA" and its value both need reading and are already separated by position (left
label, right value), so the label does not need to be dimmer as well. `DimText` survives in Night
only for chrome that genuinely need not be read.

This is the same "encode in form, not in colour" principle the section above applies to hue,
extended to luminance. It is also why the Light and Dark states keep a normal three-step text
ladder: they have the headroom, and Night does not.

### Contrast ceiling, stated plainly

Red's luminance coefficient is 0.2126, so **pure red on black tops out at 5.25:1 and AAA is
structurally unreachable in Night.** `text` at `#d92200` on black is 4.16:1, just under AA.

Night buys dark adaptation by spending measured contrast. The compensation is type size and
weight, not a brighter palette. Dark adaptation responds to absolute luminance while WCAG measures
a ratio, and the two are orthogonal: a global brightness control preserves every ratio in the
palette while dropping the absolute level, which is the lever that actually matters at the mount.

### The image is left alone

No red render path for frames, and therefore no work in the GLSL or CPU stretch paths and no risk
to the pipeline. Astro exposures are faint by nature, so the bright surface at the mount is the
chrome, not the picture.

The one case that is genuinely not dark is a hard autostretch of a bright target, which lifts the
background to mid-grey. If that ever proves annoying in the field the answer is the window-level
luminance scale above, which recolours nothing.

## System theme detection

Not in `SdlVulkan.Renderer`; it carries no theme code at all. Everything `pdf-viewer` built sits
in its own `View/` directory on four primitives, three of which we already have:

| Piece | Mechanism | Status here |
|---|---|---|
| Light/dark detection | `SDL3.SDL.GetSystemTheme()` | Available. `TianWen.UI.Gui` and `.FitsViewer` both reference `SDL3-CS` directly. |
| Title bar follows theme | `DwmSetWindowAttribute`, `DWMWA_USE_IMMERSIVE_DARK_MODE` (20, falling back to 19) | Win32 P/Invoke, app-local, about 40 lines. |
| Desktop accent | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentPalette` | Registry read, app-local. |
| Palette roles | `DIR.Lib.UiPalette` | Already in use by both theme records. |

Two details worth copying rather than rediscovering:

- **Resolve the theme once per frame and pass the answer down.** It reaches a P/Invoke and the
  draw asks "am I dark" per element.
- **Take the right entry from the accent ramp.** Windows publishes eight colours, not one. Dark
  chrome takes `Light2` (index 1) and light chrome takes `Dark1` (index 4), the pairing WinUI
  itself applies, because the user picked their accent against a wallpaper rather than against our
  panels. The fourth byte of each entry is not an alpha; passing it through as one paints nothing.

The three Windows-specific pieces have a genuine claim on `SdlVulkan.Renderer` (a windowing
library is the right home for "what theme is the desktop" and "theme the title bar"), and the
accent reader arguably belongs in `DIR.Lib`. Neither is a blocker; upstream them once the shape
has settled in one consumer.

## Phases

| Phase | Work | Blocks |
|---|---|---|
| C0 | **DONE 2026-08-07. DIR.Lib**: `UiPalette` widened to 16 roles as a `sealed record` with computed `IsDark`; new `TabBarColors` + `FromPalette` and a settable `TabBar.Colors`; `MenuColors.FromPalette`. Pinned by `UiPaletteTests` (11). | C1 |
| C1 | **DONE 2026-08-07.** `GuiTheme` now carries all three palettes plus `Apply`/`Resolve`; `ViewerTheme` follows it instead of holding a second scheme. | everything |
| C2a | **DONE 2026-08-08.** Unfreeze the palette snapshots: 34 `static readonly` fields initialised from `GuiTheme.Palette` across six tabs, plus `EquipmentPanelStyle.Default` and `HomeBoardStyle.Default`, to computed properties. This was the actual reason F12 only moved the Home board. | C2b |
| C2b | **DONE 2026-08-08.** Window chrome (`VkGuiRenderer`) onto roles: 17 literals to 0. `GuiTheme.InkOn` for ink on a semantic fill. A 16-pair contrast matrix pinned in `GuiThemeTests`. | |
| C2c | **DONE 2026-08-08.** The sweep proper: 300 literals to 98. Adds the derivations it needed (`Mix`, `AltRowBg`, five named button fills, `SkyBand`/`SkyInk`) so a tint stays derived instead of becoming a new literal. What remains is categorical series and the two-trace exemption, noted at each declaration. | C4 |
| C3 | `SDL3.SDL.GetSystemTheme()`, title bar, desktop accent. Persist the choice; add the state cycle. **The keystroke half is done** (F12 toggles Night, restoring the state it came from). | |
| C4 | Non-hue reinforcement wherever meaning was carried by hue: cooling, flip countdown, severity fill-vs-outline. **Guide RA/Dec is out of scope** (user, 2026-08-08: stays as is). | |
| C5 | Field-validate Night at the mount, especially the warn/error pair, and tune. | C1 to C4 |

The site needs no change. `profile/README.md` needs no change.

## What C0 and C1 actually landed (2026-08-07)

Three things came out of building it that the design above did not predict.

**`Success` had to join the semantic set.** The home board draws a green online dot, and green is the
one channel Night cannot spend. So `UiPalette.Success` exists and, alone among the semantic roles,
defaults to `Accent` rather than being `required`. That default is the substance, not a placeholder:
a palette with the headroom states a green, and one without gets the accent, which is the correct
positive mark there.

**The optional roles resolve through nullable backing fields, and `with` had to be checked.** A record's
copy constructor copies *fields*, so an unstated `AccentAlt` stays unstated through a clone and keeps
tracking `Accent`. Had it copied resolved property values instead, the first `with` would have frozen
every default. Pinned by `CloningKeepsAnUnstatedRoleUnstated`.

**Two consumers were already frozen, and both would have failed silently.** `NotificationsTab` held its
colours in `private static readonly` fields initialised from `GuiTheme.Palette`, and
`HomeBoardStyle.Default` was a `static readonly` instance built the same way. A field initialiser
snapshots at type-init, so both would have gone on painting the old scheme through every theme switch,
with nothing to show for it but two screens that did not change. Both are computed properties now.
**This is the shape C2 has to hunt**, and it is worse than a bare literal: a literal is visible in a
grep for colour constants, whereas a field initialised *from the palette* looks correct.

`GuiTheme` resolves once in `Apply` and swaps a single `UiTheme` reference, so `Palette` is one
reference read and can never be observed torn between a state change and the desktop's light/dark
answer. `Apply` returns whether the palette actually moved, which is the signal a consumer needs to
rebuild anything it projects (a `TabBarColors`, a cached gradient) without doing it per frame.

## What C2 landed, and what it did not (2026-08-08)

Shipped as PRs #143 and #144 against DIR.Lib 7.11.1921.

**Be clear about the scope, because the phase name oversells it.** C2 was written as "sweep the 317
literals". The literal count today is **300**, so the sweep moved about 17. What actually shipped is
the part that was blocking everything else, and it is worth separating the two:

| | Then | Now |
|---|---|---|
| Frozen palette snapshots | 34 fields + 2 style `Default`s | 0 |
| `VkGuiRenderer` literals | 17 | 0 |
| Literals everywhere else | ~300 | ~300 |

**The frozen snapshots were the whole reason F12 appeared broken, and they are worse than a
literal.** `private static readonly RGBAColor32 X = GuiTheme.Palette.Y;` and
`public static Style Default { get; } = new(...)` both run once at type-init, so six tabs captured
the startup palette and painted it forever. A literal shows up in a grep for colour constants; a
field initialised *from the palette* reads as correct. C0/C1 had already found two of these and
predicted more, and there were 34. Fix is a computed property (`=>`); `RGBAColor32` is four bytes,
so there is nothing to weigh.

Grep for them **loosely** (`static readonly RGBAColor32.*Palette\.`). A pattern anchored on
`= GuiTheme.` misses a fully-qualified `= TianWen.UI.Abstractions.GuiTheme.Palette.X`, which is
exactly how one survived the first pass.

**Four defects, and every one was found by running the app or computing a number.** None was
visible in the source, and two of them had been introduced within the hour:

- The Home board's online dot went blue. `Success` was unstated in Light and Dark so it fell back
  to `Accent`, and in Dark `Info` *is* `Accent`, which collapsed "online" and "running" onto one
  colour. Both states now state a green.
- The `Connect All` button measured about 1.4:1 (dim label on the green fill).
- `PlaceholderText` measured 1.50 to 1.89:1 in all three states, from mapping it to
  `SeparatorStrong`.
- Night's `Info` sat at 2.46:1.

So the lesson is procedural rather than technical: **a role lookup makes a colour consistent, not
legible. Only the pairing is legible**, and nothing in the type system checks a pairing.

**Two rules now enforced by tests** in `GuiThemeTests`, which is what stops the above recurring:

- A **contrast matrix**: 16 text pairs x 3 states, floor 4.5 (Night 3.4, per the ceiling above).
  Worst pair per state today: Light 5.02, Dark 5.08, Night 3.43.
- **`GuiTheme.InkOn(fill)`** is the single ink-on-a-filled-chip rule, and it *measures both
  candidates* rather than branching on `RGBAColor32.Luminance`. That property is a gamma-encoded
  NTSC approximation, which is not the quantity a contrast ratio is defined on, and it guessed the
  crossover wrong. Any *fixed* ink is wrong too, because a semantic fill's lightness flips between
  states.

Two things deliberately sit outside the matrix. The locked-tab icon is around 1.5:1, because WCAG
exempts inactive components and a locked tab should read as unavailable. And a selection fill is
*supposed* to be subtle against its panel: 1.27 Light, 1.28 Dark, 1.21 Night. Do not "fix" Night's
by lightening it, which drops BodyText-on-Selection from 4.11 to about 2.9. Both are pinned as
tests stating the intent, so a later reader finds the reasoning instead of the number.

## What C2c landed (2026-08-08)

300 literals to 98, and the 98 are the point: each is either a **categorical series** (schedule
target N, camera N, weather class, sky-map object class) where a role lookup would collapse
distinct things onto one colour, or the **two-trace exemption**. Every one carries a comment
saying which, so a future sweep does not "finish the job" by breaking them.

**A tint has to stay derived, or the sweep just relocates the problem.** Replacing
`#1a1a24` with a hand-picked "slightly lighter than the panel" is the same frozen literal in a new
place. So the sweep added the derivations first: `Mix(a, b, t)`, `AltRowBg`, five named button
fills (neutral / primary / go / caution / danger), and `SkyBand` + `SkyInk`. Every call site then
names an intent rather than a colour, and a theme switch reaches all of them.

**Two surfaces DEPICT the sky rather than framing it**, and they need the opposite treatment from a
panel: the altitude chart's plot area and the session timeline's twilight bar stay dark in every
state, exactly as image data is never recoloured. What the theme changes there is the *hue*, which
`SkyBand` takes from `Info`, so Night gets a warm sky instead of the one colour it cannot afford.

`SkyBand` is anchored on **black, not `ContentBg`**, and that is load-bearing. Anchoring on the page
inverts the twilight ordering in Light, where further from the ground means darker while on a dark
ground it means brighter, so civil twilight would have rendered darker than astronomical in exactly
one state. A test pins the ordering in all three.

**The frozen-snapshot bug had a second costume, and only running the app found it.** After the
sweep, F12 turned every surface red except the planner's altitude chart, which stayed blue.
`VkPlannerTab` renders that chart to a **cached GPU texture** keyed on the data it draws, and a
theme switch changes none of that data. So `GuiTheme` now exposes `PaletteGeneration`, bumped
whenever the resolved palette moves, and the cache key carries it. Note why `Apply`'s existing
boolean was not enough: it tells the caller who asked, and a cache is usually held by someone who
did not. **Anything caching a projection of the palette owes that generation in its key.**

Three tests added, all pinning things a role lookup cannot guarantee on its own: every derived
button fill can carry its `InkOn` label in all three states, the twilight ramp keeps its ordering,
and an alternate row is visible against the panel but quieter than a selection.

## Decisions taken

- **A new shared core rather than one surface adopting another**, designed against all four states
  and all three surfaces at once, so Light and Night are not retrofits.
- **Chrome only. The image is never recoloured.**
- **The app gets a light theme**, because system detection without one is a no-op for half its
  users, and daytime planning and stacking are real work.
- **F12 toggles Night**, matching SharpCap. Toggling off restores the *state* it came from, not the
  resolved palette, so a rig sitting on System stays on System.
- **The guide graph's RA and Dec keep their hues in Night** (user, 2026-08-08). See the Night
  section for why this is the one place the encode-in-form rule is not worth applying.
- **The sky map keeps its own colours entirely, star colours included** (user, 2026-08-08), and the
  reason is worth stating because it looks wrong until you check *when*. Its background is
  `SkyMapState.SkyBackgroundColorForSunAltitude`, a ramp keyed on **solar altitude rather than on
  the theme**. A screenshot at 17:00 shows a full-screen blue-grey star chart, which reads as the
  worst remaining Night gap; but F12 is used at the mount, where the sun is below -18° and the same
  function already returns `#020308`, near-black with 8 units of blue. Nobody dark-adapted ever sees
  the blue state. This also spares a shader change: star colour is computed in GLSL (`bvToRgb` in
  `skymap_star.vert`, mirrored by `SkyMapProjection.StarColor`), so recolouring stars would mean a
  uniform, a re-bake of the SPIR-V and a CPU/GPU mirror update, for a state that is not observed.
  **Before judging any Night surface, check whether its colour is a function of something other
  than the theme, and reproduce that state** (`TIANWEN_NOW` anchors the clock to a real night).
  De-magicking those constants in place is welcome; re-deriving them from the palette is a
  behaviour change and is not.
- **The tab emoji stay full-colour in Night** (user, 2026-08-08). They come from the bundled emoji
  font, so a blue camera and a yellow bell are the only non-red things on screen. The alternatives
  were a monochrome fallback in Night only, or drawing them as shapes; neither is worth the work
  for eight glyphs on a rail, and both trade a legible icon for a compliant one. Revisit only if
  field use at the mount says otherwise, which is C5's job.
