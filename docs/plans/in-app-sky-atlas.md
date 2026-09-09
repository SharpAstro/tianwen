# The atlas the app already has, instead of a link to the one on the web

**Status: DRAFT (raised by the user 2026-09-10).** *"Lets draft out what needs to be done to use the
actual atlas. Not the browser link."*

Right-clicking a plate-solved pixel offers **"Open in sky atlas"** (`ImageContextMenu.cs:144`). It
builds a `SkyAtlasLink.For(...)` URL, posts `OpenUrlSignal`, and the host hands it to the shell
(`TianWen.UI.FitsViewer/Program.cs:526`, `TianWen.UI.Gui/Program.cs:225`) -- so clicking it leaves
the application and opens `sharpastro.github.io/tianwen/` in a browser. Both desktop hosts already
contain a full sky atlas. The GUI is looking at one in the next tab.

## Four findings that change what this work is

Each was read out of the code before writing, because each moves an item between "build it" and
"wire it".

1. **The host contract for the atlas is ONE method with three arguments.**
   `SkyMapTab<TSurface>.Render(PlannerState, RectF32, ITimeProvider)` (`SkyMapTab.cs:88`), plus the
   `Bus` and `Logger` setters the GUI assigns at construction (`VkGuiRenderer.cs:401`). Everything
   the map needs travels on `PlannerState`: `ObjectDb` (`PlannerState.cs:225`), the site, the
   proposals, the comets and the planning date. There is no GUI type in the signature. That is the
   whole reason this is a wiring job rather than an extraction.

2. **`VkSkyMapTab` moved into `TianWen.UI.Shared` on 2026-09-09** (`8ef381a2`, "belongs beside its
   pipeline, not in the GUI"), which is the assembly `tianwen-fits` already references. The map, its
   pipeline and its shaders are on the viewer's compile path **today**; nothing new has to be built
   or moved for the viewer to be able to name the type.

3. **The recipe for pointing the atlas at a link already exists, is complete, and is the part nobody
   would rediscover.** `Planner.razor:2082-2135` does three things in a required order: apply `t` to
   `PlannerState.PlanningDate` **before** the pointing (so the first frame is drawn at the right
   instant rather than jumping one frame later), call `SkyMapViewActions.SetView` (which owns the RA
   normalise, the Dec pole clamp and the FOV clamp), and then set
   `SkyMapState.ExternalViewPending = true`. That third step is invisible until it is missing:
   `SkyMapTab.Render` installs a HOME position on its first pass -- the site's LST at the visible
   pole -- and overwrites the pointing on the very next frame. The browser E2E is what caught it,
   *"nothing on either side of the URL could have caught it, because both ends were correct"*. It
   lives in a `.razor` file, which no `--include=*.cs` grep sees.

4. **The GUI half is nearly free; the viewer half is the actual project.** The GUI has
   `GuiTab.SkyMap`, a live `SkyMapState` (`VkGuiRenderer.cs:240`), and a `SkyMapSetViewSignal` whose
   handler is already route-only (`AppSignalHandler.SkyMap.cs:289`). Its two gaps are that the
   signal carries no TIME and that nothing switches the tab. The viewer, by contrast, is a
   **single-widget host**: `Program.cs` renders `imageRenderer.Render(controller.Source, state)`
   every frame and routes input straight to it. There is no mode, no second widget, no
   `AppSignalHandler`, no `PlannerState` and no site.

## What the viewer already has, and what it is missing

Checked rather than assumed, because the answer decides the size of P4.

| Needs | Viewer today |
|---|---|
| `ICelestialObjectDB` | **Present.** `AddAstrometry()` + an `AsyncLazy` that calls `InitDBAsync` (`Program.cs:157`), already handed to the context menu's object resolver |
| Star field / constellations / catalogs | **Present**, embedded in `TianWen.Lib`, which the viewer references |
| `milkyway.bgra.lz` | **Missing.** A GUI-local resource copied to output (`TianWen.UI.Gui.csproj:25`); the map degrades gracefully without it (`MilkyWayAvailable`), so this is one linked `None` item, exactly like the two fonts |
| `PlannerState` | **Missing.** A plain class with defaults whose `Bus` is optional, so constructing one costs nothing; the question is what goes IN it (see below) |
| Site latitude / longitude | **Missing, and the one real design question** |
| A signal router for what the map posts | **Missing.** `AppSignalHandler.SkyMap.cs` is 451 lines and GUI-shaped (planner pins, mount slews, solve-sync) |
| A way to show a second full-window widget | **Missing.** No tab strip, no mode |

## The overlay half is already there

Worth stating before the site, because it shrinks P4. The viewer **already** projects the object
catalog over a solved frame: `ImageRendererBase.RenderOverlays(state, wcs, db)` calls
`OverlayEngine.ComputeOverlays(layout, wcs, db, MeasureText, BaseFontSize)` and then
`OverlayEngine.PlaceLabels` (`ImageRendererBase.Overlays.cs:333-393`). So the host already holds the
DB, already resolves what is in a field, and already does collision-avoiding label placement.

What that does NOT carry over is the rest of a sky map: the whole-sky projection, the Tycho-2 star
field, constellation lines and figures, the planets, the comets, the milky way, and the GPU pipeline
that draws them. Those are `VkSkyMapPipeline` and are the reason P4 hosts the tab rather than
growing the overlay. **The useful consequence is different: the overlay path needs no site at all.**
It is pure WCS. So is every "where am I pointing, what else is in this field" question a photograph
raises.

## The shape: the sky BEHIND the image, on `O`

Not a second screen. `O` today toggles `ViewerState.ShowOverlays`
(`ImageRendererBase.Input.cs:314`), which draws catalog objects over the frame. The ask extends that
same key one step outward: **keep the photograph where it is and draw the sky it came from behind
it** -- milky way, constellations, the star field, the planets, and the horizon underneath, all at
the instant the shutter was open, with the image composited on top at its solved position, scale and
rotation.

This is a better shape than a mode switch, and it is the reason the site question is worth answering
at all:

- **It answers the question the photo actually raises.** Not "show me the sky" but "where in the sky
  is *this*, and what is around it". A separate atlas screen makes the reader rebuild that
  correspondence by eye; a composite states it.
- **The image is already in the right coordinate system.** A solved frame has a WCS; the map has a
  projection. Placing one in the other is projecting the frame's corners (tessellated, so the
  curvature at wide FOV survives) through `WCS.PixelToSky` and then through the map's own view
  matrix. No new astrometry.
- **Zoom becomes one continuous gesture** from the pixel to the constellation, which is what makes
  the milky way behind a widefield worth drawing at all.
- **It subsumes the atlas mode rather than competing with it.** Zoom out far enough and the frame is
  a small rectangle on a whole-sky view, which IS the atlas -- so P4 delivers both, and the context
  menu's "Show in sky atlas" becomes "zoom out to here" rather than a screen change.

What it costs beyond hosting the map: an image layer drawn INSIDE the sky pipeline (its own quad,
tessellated and WCS-projected, alpha over the sky), and a decision about what the chrome does while
zoomed out.

## The site question, which blocks less than it looks like

The site is needed for exactly one family of layers: the horizon, the Alt/Az grid, the below-horizon
dimming and the alt/az readouts. Equatorial mode with the horizon off is a complete, coherent atlas
with no site whatsoever, which is also what the link's own doc comment argues -- a link
*"deliberately carries NO SITE"* because only the horizon overlays are site-dependent and those
should be the recipient's own. **So the resolution chain below is a nicety, never a prerequisite,
and nothing in P4 waits on it.**

**1. The frame, and N.I.N.A. does write it (measured 2026-09-10).** `ImageMeta` already carries
`Latitude` / `Longitude` / `SiteElevation` from `SITELAT` / `SITELONG` / `SITEELEV`
(`Image.Fits.cs:328-330`), NaN when absent, and TianWen stamps the same three on its own captures
(`Image.Fits.cs:883-885`). Scanned 40 lights of the 10P/Tempel set (`SWCREATE = 'N.I.N.A. 3.2.0.9001
(x64)'`): **40 of 40 carry all three**, in decimal degrees, east-positive, plus a `SITENAME`. Two
committed test fixtures from unrelated January and February nights
(`TianWen.Lib.Tests/Data/2026-01-18_*.fits.gz`, `2026-02-15_*.fits.gz`) carry them too, which is the
control that matters: those have never been through the 2026-08-25 SITEELEV amendment that rewrote
525 headers, so the cards are N.I.N.A.'s own and not ours. No `OBSGEO-B/L/H` and no `LAT-OBS` /
`LONG-OBS` anywhere, so `SITE*` is the only spelling worth reading. Caveat: it is the *profile's*
site, not a fix -- the two fixtures disagree by 0.0006 deg (about 60 m) between sessions. Unmeasured
here: SharpCap and other capture software, since this machine holds no such frames.

**2. SOLVE it from the frame's own geometry, which works (measured 2026-09-10).** N.I.N.A. also
writes `CENTALT` / `CENTAZ` (the frame centre's altitude and azimuth) and `AIRMASS`, on every light
in the set. Altitude, azimuth and declination determine latitude through one equation of the
pole-zenith-star triangle:

```
sin(dec) = sin(lat) sin(alt) + cos(lat) cos(alt) cos(az)
```

which has a closed-form solution in `lat` (auxiliary-angle form, the second root discarded by the
azimuth component). The hour angle then falls out of `cos(dec) sin(H) = -cos(alt) sin(az)`, and
longitude is `H + RA - GMST(DATE-OBS)`. SOFA is already here for the last step (`SofaFunctions.Gmst06`,
`SiteContext`). **Run against 9 frames of the 10P set and scored on the `SITELAT`/`SITELONG` the same
headers carry: median error 0.15 deg in latitude and 0.18 deg in longitude, about 16 km.**

**The residual is a header self-consistency limit, not a limit of the maths.** The error is
systematic and decays as the target rises (871 arcsec down to 483 arcsec over five hours), which
looks like refraction until it is measured: recomputing the frame's alt/az from its own `RA`/`DEC`
card against the KNOWN site misses `CENTALT` by +755 arcsec reading the card as J2000 and by -518
arcsec reading it as JNow. Neither convention closes it, so the two cards simply do not describe the
same position to better than about 0.2 deg -- `RA`/`DEC` is what the mount reported, which is the
same reason CLAUDE.md ranks `OBJCTRA`/`OBJCTDEC` above it for a solve hint. The refinement to try
first is feeding the solve the frame's PLATE-SOLVED centre instead of the mount's card, which
removes the pointing-error half; not yet measured.

**0.15 deg is far better than this feature needs**, since the horizon's tilt and the milky way's
placement behind a photo are degree-scale questions. It is emphatically NOT good enough to be reused
as an observatory position for anything astrometric: it is a display site and should be typed as one.

**3. `DATE-LOC` against `DATE-OBS` gives the UTC offset**, hence longitude to about +/-7.5 deg
(+10 h on this set, and the solved longitude 145.0 sits where that predicts). Latitude stays unknown,
so this is a cross-check on (2) rather than a substitute -- but it is free and it is present on every
N.I.N.A. frame.

**4. The user's own profile is a WEAKER fallback than it looks.** `%LOCALAPPDATA%/TianWen/Profiles/<guid>.json`
does carry `SiteLatitude` / `SiteLongitude` / `SiteElevation` (verified here: -37.876389, 145.177778,
72), but the viewer carries no ACTIVE profile and there may be several, and a Store install of Astro
Photo Viewer is on a machine with no TianWen profile at all. So: read it only when exactly one
profile exists, and rank it BELOW the frame's own geometry, which is right for the frame in front of
the reader rather than right for this machine's owner.

**5. OS geolocation is deliberately NOT in the chain.** Windows `Geolocator` is a WinRT/COM call
under NativeAOT, needs a consent prompt and an MSIX location capability for the Store build, and
answers a different question anyway -- where the READER is, not where the PHOTOGRAPH was. A frame
shot from a dark site and opened at home would draw the wrong horizon with full confidence. Park it.

**6. Remember the last site, and let it be typed.** The floor, and what carries a site across files:
a frame that knows where it was seeds the view, and the next frame without one keeps that rather than
losing the horizon.

Falling out of all this: `SkyMapTab`'s no-site placeholder says *"Connect a mount to auto-seed, or
set the site manually in the Equipment tab"*, naming a tab this host does not have. It needs a
host-neutral wording, and in the viewer it should not appear at all, since no site now means
equatorial rather than an error.

## Keep the link, demote it

The current menu comment records that copying the link was the FIRST shape and was replaced because
*"it made the reader do the other half of the job"*. This is not a revert of that. The URL is how
you show the sky to **someone else**; the in-app atlas is how you look at it **yourself**, and the
right-click is overwhelmingly the second. So **"Show in sky atlas"** becomes the primary and acts
in-process, and **"Copy sky atlas link"** returns beneath it as the sharing affordance. A host with
no atlas of its own keeps opening the browser, which is what makes P2 shippable before P4 exists.

## Phasing

| # | What | Where | Risk |
|---|---|---|---|
| **P0** | `FloatingPalette<TSurface>` (DIR.Lib) + a palette on `SkyMapTab` itself carrying the ten layer toggles, the mode and the time / FOV controls. **Independent of every other phase and the first one worth doing**: it is the GUI atlas's first visible control surface and the web atlas's only reachable one. The DIR-level foundation (`Layout.Builder.Anchored` + the palette marks) is already in the pinned 8.15, so this generalises the PDF viewer's `ToolPalette` into DIR.Lib rather than starting one; the viewer inherits it free at P4 | DIR.Lib, `TianWen.UI.Abstractions` | Medium |
| **P1** | Hoist the pointing recipe out of Razor into `SkyMapViewActions.ApplyPointing(state, plannerState, raHours, decDeg, fovDeg, capturedUtc)` -- time first, `SetView`, `ExternalViewPending` -- and re-point `Planner.razor` at it. No behaviour change; the existing E2E is the guard | `SkyMapViewActions.cs`, `Planner.razor` | Low |
| **P2** | GUI: a `ShowInSkyAtlasSignal(ra, dec, fov, capturedUtc)` whose handler calls P1's helper and sets `ActiveTab = GuiTab.SkyMap`. Route-only, per the signal-handler rule | `GuiSignals.cs`, `AppSignalHandler.SkyMap.cs` | Low |
| **P3** | Menu vocabulary: the atlas entry posts a signal instead of carrying a URL, gated on a host-declared "I have an atlas" capability, with the browser link as the fallback item and the copy-link item beside it | `ImageContextMenu.cs`, `ImageRendererBase.ContextMenu.cs` | Low |
| **P4** | Viewer hosts `VkSkyMapTab`: the milky-way asset, a `PlannerState`, input routing, and the DB init kicked off without blocking the render thread. Ships site-free (equatorial, no horizon), so it does not wait on P4b | `TianWen.UI.FitsViewer` | **The bulk of it** |
| **P4b** | `FrameSiteResolver` (pure, in `TianWen.Lib`, testable against the 10P headers): `SITELAT`/`SITELONG` -> solved from `CENTALT`/`CENTAZ` + centre + `DATE-OBS` -> single-profile read -> remembered -> typed, each answer carrying its provenance so the UI can say which. Turns the horizon and Alt/Az layers on | `TianWen.Lib`, viewer | Low |
| **P4c** | The composite: `O` draws the sky behind the frame, image on top, as a tessellated WCS-projected quad inside the sky pipeline; zoom is continuous from pixel to constellation | `TianWen.UI.Shared`, viewer | Medium |
| **P5** | Viewer signal subset: answer search / click-select / info-panel; deliberately do NOT offer pin, slew, planner or solve-sync. Decide whether this is a second small router or a shared one with the GUI's | `TianWen.UI.FitsViewer`, possibly `AppSignalHandler.SkyMap.cs` | Medium |
| **P6** | Web: the item applies the pointing in-process through P1's helper rather than navigating; the URL stays the shareable artifact it already is | `Planner.razor` | Low |

P1 through P3 are one sitting and leave the GUI complete. P4 and P5 are the viewer.

## What bites

- **RA travels in DEGREES in a link and in HOURS everywhere else.** Settled once on `SkyAtlasLink`,
  with a test. The hoisted helper takes HOURS; do not re-open the question at a third call site.
- **`ExternalViewPending`, never `Initialized`.** The flag's own remarks explain why setting
  `Initialized` from outside does not work: the home pass also fires whenever the site differs from
  the one it last homed for, and that comparison starts against `NaN`.
- **The time is applied before the pointing**, and it is not optional for a moving target: a link to
  a planet, the Moon or a comet points at nothing at all without it. A host that jumps the atlas to
  a capture instant owes an obvious way back to live.
- **A viewer mode is not a GUI tab.** CLAUDE.md's six-place checklist (`GuiTab`, `TabOrder`,
  `TabChrome`, the Ctrl+letter map, two `VkGuiRenderer` switches, the order test) is the GUI
  sidebar's. The viewer has no sidebar and none of it applies.
- **First open is not free.** The viewer's catalog init is lazy, and the map builds its star buffer
  and milky-way texture off-thread on first sight; `docs/todo/ui.md:544` already records an ~800 ms
  first-open stall for the GUI's atlas. Nothing here may block the render thread.
- **Two `NeedsRedraw` flags.** The map has its own (`SkyMapState.NeedsRedraw`) and the GUI's frame
  gate reads both (`TianWen.UI.Gui/Program.cs:454`). A viewer that gates redraws on `ViewerState`
  alone will render a frozen atlas.

- **The sky's controls belong on a FLOATING PALETTE that appears with it -- and the SHIPPED atlas
  needs it more than the viewer does.** `SkyMapState` carries eleven `Show*` toggles, **ten of which
  are bound to a bare letter key and to nothing else**: `G` grid, `H` horizon, `B` boundaries, `C`
  figures, `S` milky way, `A` Alt/Az grid, `O` objects, `D` dark nebulae, `M` mount overlay, `E`
  comets (`SkyMapTab.cs:1367-1410`). **Correction to the first draft of this doc, found by running
  it:** there IS a legend, a bracketed key list crammed into the status strip beside the coordinates
  (`SkyMapTab.cs:1007`). It states the letters and nothing else -- **never which layers are ON, and
  never clickable** -- so it is a hint, not a control. The clickable things in the whole map remain
  object labels, the search overlay and the info panel's own buttons. **And because `SkyMapTab` is
  renderer-agnostic and the web build hosts the same class, on a phone those ten layers are not
  merely undiscoverable but UNREACHABLE**, there being no keyboard to press and nothing to tap. That
  is what makes the palette the fix for a shipped gap rather than a viewer nicety, and it is why it
  is P0: it belongs on `SkyMapTab` itself, so the GUI and the web get it now and the viewer inherits
  it at P4. In the viewer it additionally keeps the top bar describing the IMAGE, which is what a top
  bar there is for. The status strip's key list is then one legend too many, so it yields to the
  panel and reappears when the panel is put away -- which is also the only place that can say how to
  bring it back. The precedent is Drawboard's PDF viewer (`../../drawboard/pdf-viewer`,
  `PdfViewer.Abstractions/View/ToolPalette.cs`): `ToolPalette<TSurface> : PixelWidgetBase<TSurface>`,
  627 lines on the same DIR.Lib primitives this repo uses -- `Layout` trees, `Layout.IconKind` marks
  -- giving a grip-dragged palette that docks left / right / top, re-clamps itself into the content
  area every frame (so a sidebar toggle or a resize shoves it back rather than stranding it), does
  its own zone hit-testing and tooltips, and animates the dock.
  **Its coupling to that app is exactly three seams**: the item list, an icon switch over the tool
  enum, and a `UserPreferences` for placement. Everything else is generic, so the right home is a
  `FloatingPalette<TSurface>` in DIR.Lib taking items, check states and a placement read/write pair.
- **The DIR-level half of that is ALREADY HERE, and features flow between the two libraries by
  established practice.** The two clones are different remotes (`SharpAstro/DIR.Lib`;
  `DrawboardLtd/DeviceIndependentRenderingLibrary`, which the PDF viewer references through its
  `lib/` junction) but the fork carries the SharpAstro clone as its `upstream`, and the palette's
  foundation exists on both sides under one title -- *"a node can float inside a rect, and two marks
  for a tool palette"* (`a063cb0` here, 2026-08-25; `acfdc0d` there). `Layout.Builder.Anchored`
  (dock side, offset along the edge, and the clamp, all owned by the arrange pass) is byte-identical
  at the same line numbers in both, the `IconKind` sets diff empty, and `a063cb0` is an ancestor of
  the 8.15 commit -- **so TianWen already has all of it at its current `8.15.*` pin.** P0 therefore
  needs NO DIR.Lib release for its foundation. What is left is the widget itself, which today lives
  in the PDF app rather than in the library: generalising it into DIR.Lib is the move, and both apps
  then take it from there rather than keeping two.
- **The viewer has no preferences store at all today**, so the palette's third seam has nowhere to
  land yet: placement, dock edge and which layers were on all want one. The GUI and the web have
  their own persistence and do not.
- **A palette of toggle buttons also RETIRES the checklist problem.** Each button is independently
  lit or not, which is what layers are, so `OpenDropdown`'s radio-only semantics stop being a
  blocker and the dropdown below is only needed if the palette is not built.
- **The overlay button becomes a dropdown and `O` becomes its cycle key, which is the house pattern
  -- but overlays are LAYERS, not a choice.** The toolbar already opens ten dropdowns
  (`ImageRendererBase.Toolbar.cs`), three of them paired with a cycle key (`C` channel, `D` debayer,
  `T` stretch link), so a menu button with a key beside it needs no invention. What does need care is
  that every one of those selects ONE value from a set, while `Objects` (`ToolbarAction.Overlays`,
  group 4) sits beside `Grid` and `Stars` as three INDEPENDENT toggles, and the sky-behind makes
  four: sixteen states, which no cycle key traverses. So the two affordances mean different things.
  **The dropdown is a checklist** (each layer toggled on its own, all four visible and discoverable);
  **`O` cycles an ordered set of PRESETS by how much context** -- `none -> objects -> objects + sky
  -> all` -- never the layers themselves. `G` and `S` keep their own keys: the dropdown is the
  discoverable surface, the keys stay the fast path.
- **`OpenDropdown` cannot render a checklist today.** Its signature is labels plus an optional
  `selectedIndex`, i.e. radio semantics, with no per-item check state and no disabled items. The
  checklist needs both, because the items are not equally available: `Objects` already gates on
  `document?.Wcs is { HasCDMatrix: true }` and a created DB (`Toolbar.cs:823`), the sky needs the
  same WCS plus an instant, and the horizon needs a site that may not exist. A greyed item that says
  why is the difference between "this frame cannot do that" and "this feature is broken".
- **A layer toggle and the zoom-out continuum must not fight.** Once P4c makes zoom continuous, the
  sky is necessarily visible when the view is wider than the frame, whatever the checkbox says -- and
  a box reading "off" while the milky way is plainly on screen is a bug report. State it once: the
  checkbox means *draw the sky behind the frame at ANY zoom*; past the point where the view exceeds
  the frame, the sky is drawn regardless, because there is otherwise nothing to look at.
- **An inferred site must SAY it is inferred.** A horizon drawn from a solved site is right to about
  a sixth of a degree and a horizon drawn from `SITELAT` is right to metres, and nothing on screen
  distinguishes them. The resolver returns provenance with the answer, and the readout names it.
- **Never promote a display site to an astrometric one.** 0.15 deg is fine for a horizon and useless
  for an ephemeris; the solved value must not reach a comet track, a topocentric correction or a
  written header.

## Open questions

- **Search in the viewer?** F3 is most of what makes the atlas useful on its own, and it is also the
  largest slice of P5.
- **What happens to the chrome when the view zooms out past the frame?** The toolbar and histogram
  describe an image that is now a rectangle in the corner. Fade, keep, or switch.
- **Does the sky follow the file?** Re-point and re-time on every new document, or hold the view.
  Probably follow, since the sky is context FOR the frame -- but a file-list walk would then re-home
  the view on every arrow key.
- **Do `Grid` and `Stars` lose their toolbar buttons when they join the dropdown?** Folding four
  toggles into one button reclaims two slots and costs a click for two layers that are currently one.
  The keys stay either way.
