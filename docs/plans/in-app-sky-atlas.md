# The atlas the app already has, instead of a link to the one on the web

**Status: P0 SHIPPED (PR #237, 2026-09-10); P4 + P4b(header) + P4c SHIPPED 2026-09-10; P1-P3, P5, P6
and the rest of P4b OPEN.** Raised by the user as *"Lets draft out what needs to be done to use the
actual atlas. Not the browser link."*, and the shape of the viewer half was settled by them once P0
was merged: *"P4c, but with horizon and take time from when we took the frame (this is optional if we
do not have the time or the site), use that new floating thing that is activated if the overlay + sky
mode is selected... grid + overlay become one button, where pressing O once is grid, second item is
grid + overlay, third one is the more advanced sky view. this works as we will show the toolbox that
gives more freedom once in this mode."* See [What shipped](#what-shipped-in-the-viewer) below.

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
| **P0 DONE** | `FloatingPalette<TSurface>` (DIR.Lib) + a palette on `SkyMapTab` itself carrying the ten layer toggles, the mode and the time / FOV controls. **Independent of every other phase and the first one worth doing**: it is the GUI atlas's first visible control surface and the web atlas's only reachable one. The DIR-level foundation (`Layout.Builder.Anchored` + the palette marks) is already in the pinned 8.15, so this generalises the PDF viewer's `ToolPalette` into DIR.Lib rather than starting one; the viewer inherits it free at P4 | DIR.Lib, `TianWen.UI.Abstractions` | Medium |
| **P1** | Hoist the pointing recipe out of Razor into `SkyMapViewActions.ApplyPointing(state, plannerState, raHours, decDeg, fovDeg, capturedUtc)` -- time first, `SetView`, `ExternalViewPending` -- and re-point `Planner.razor` at it. No behaviour change; the existing E2E is the guard | `SkyMapViewActions.cs`, `Planner.razor` | Low |
| **P2** | GUI: a `ShowInSkyAtlasSignal(ra, dec, fov, capturedUtc)` whose handler calls P1's helper and sets `ActiveTab = GuiTab.SkyMap`. Route-only, per the signal-handler rule | `GuiSignals.cs`, `AppSignalHandler.SkyMap.cs` | Low |
| **P3** | Menu vocabulary: the atlas entry posts a signal instead of carrying a URL, gated on a host-declared "I have an atlas" capability, with the browser link as the fallback item and the copy-link item beside it | `ImageContextMenu.cs`, `ImageRendererBase.ContextMenu.cs` | Low |
| **P4 DONE** | Viewer hosts `VkSkyMapTab`: the milky-way asset, a `PlannerState`, input routing, and the DB init kicked off without blocking the render thread. Ships site-free (equatorial, no horizon), so it does not wait on P4b | `TianWen.UI.FitsViewer` | **The bulk of it** |
| **P4b HALF** (header + remembered tiers shipped; the `CENTALT`/`CENTAZ` solve and the profile read are open) | `FrameSiteResolver` (pure, in `TianWen.Lib`, testable against the 10P headers): `SITELAT`/`SITELONG` -> solved from `CENTALT`/`CENTAZ` + centre + `DATE-OBS` -> single-profile read -> remembered -> typed, each answer carrying its provenance so the UI can say which. Turns the horizon and Alt/Az layers on | `TianWen.Lib`, viewer | Low |
| **P4c DONE** (as a rung of the context ladder, and WITHOUT the tessellated quad -- see below) | The composite: `O` draws the sky behind the frame, image on top, as a tessellated WCS-projected quad inside the sky pipeline; zoom is continuous from pixel to constellation | `TianWen.UI.Shared`, viewer | Medium |
| **P5** | Viewer signal subset: answer search / click-select / info-panel; deliberately do NOT offer pin, slew, planner or solve-sync. Decide whether this is a second small router or a shared one with the GUI's | `TianWen.UI.FitsViewer`, possibly `AppSignalHandler.SkyMap.cs` | Medium |
| **P6** | Web: the item applies the pointing in-process through P1's helper rather than navigating; the URL stays the shareable artifact it already is | `Planner.razor` | Low |

P1 through P3 are one sitting and leave the GUI complete. P4 and P5 are the viewer.

## What shipped in the viewer

**A rung of a ladder, not a mode.** The user's call, and a better shape than either the draft's "O
toggles the sky" or the checklist the *What bites* section below argued for: `O` now steps
`none -> grid -> grid + objects -> grid + objects + sky`, the toolbar's Grid and Objects buttons
became ONE button whose mark says which rung, and the palette is what gives per-layer freedom once
the sky is up. The rung is DERIVED from the three layer flags rather than stored beside them, so `G`
-- which still toggles the grid alone -- moves the ladder with it instead of leaving the button lit
for a layer that is off. `Shift+O` and a wheel over the button walk it backwards.

**The photograph stays the master; the sky follows it.** The draft proposed drawing the image as a
tessellated WCS-projected quad INSIDE the sky pipeline. What shipped is the reverse and is far
smaller: `SkyBackdropView.Solve` reads the viewer's existing placement (zoom, pan, fit, crop) back
through the frame's own WCS and states the same view in the map's terms -- centre, roll, field of
view and handedness -- and the map draws under the image quad. Nothing about the image pipeline
changes and every existing gesture keeps working. The tessellation the draft wanted was for the
projection difference, which turns out not to need modelling (below).

**Measured against the frame's own header** (a 10P/Tempel drizzle master, 4114x2711 at 4.72"/px,
5.4 degrees across, driven live and read back through the inspector's new `skyCentre*` telemetry):

| | header | live | agreement |
|---|---|---|---|
| centre RA | 22.136114 h | 22.136064 h | 2.7 arcsec (0.57 px) |
| centre Dec | -30.315830 deg | -30.315138 deg | 2.5 arcsec (0.53 px) |
| field of view | 4.6993 deg | 4.7002 deg | 0.019 percent |
| roll | rotation 92.654 deg | -87.348 deg | exactly rotation - 180 |
| parity | det(CD) > 0 | not mirrored | consistent |

The half-pixel residual is the pixel-centre convention plus `CRPIX` not being exactly the image
centre. `SkyBackdropViewTests` pins the same claim in units nobody has to interpret: it projects the
frame's four CORNERS through the map's own projection and asserts they land where the image quad
draws them, under rotation, both parities, near the pole, panned and zoomed. Disabling the roll fails
7 of its 15 cases; asserting only the centre would fail none of them, which is why it asserts corners.
The live composite was then checked a second way, which is the one worth repeating on a new frame:
turn the map's own object layer on and both engines draw the same catalogue objects outside the
frame -- the image's overlay from its WCS, the map's from the view matrix -- and their markers
coincide.

**Three findings the draft did not have, each of which fails silently:**

- **A rotation cannot fix PARITY.** Roughly half of all light paths put east on the other side of the
  frame, and a star chart laid behind such a photograph is its mirror image however it is rolled --
  every star lines up along one axis and walks off along the other, which reads as a wrong plate
  solution rather than as handedness. `SkyMapState.MirrorView` negates the view's right axis AFTER
  the roll (taking `up` from a negated right would be a 180 degree rotation instead, leaving the
  handedness exactly as it was). A reflection is still ORTHOGONAL, so the projection maths is
  untouched: the inverse stays the transpose, which is what `UnprojectWithMatrix` assumes.
- **The cached image layer would blit OVER the sky.** It clears to opaque black across the whole
  pane, so everywhere the picture does not reach -- exactly where the sky is worth looking at -- the
  blit would paint it out. It stands down while the backdrop is on and says so in its own miss
  diagnostic (`the sky is drawn behind the frame`, readable from the inspector).
- **The two projections do not need reconciling.** A frame's WCS is gnomonic and the map is
  stereographic; they agree exactly at the view centre and separate by about `theta^2 / 4` of the
  distance out to a point `theta` away. That is a hundredth of a pixel at the corner of a
  one-degree frame and 1.85 px on a ten-degree one -- and it is only ever visible at the frame's
  BORDER, since the photograph is drawn opaque over everything inside it.

**The view is solved by PROBING, not derived from the CD matrix**: three `WCS.PixelToSky` calls (the
pane centre, one pixel right, one pixel up) answer all four unknowns, so the solver needs no opinion
about FITS conventions, matrix handedness or which way the map's east points, and a convention that
changes on either side moves both probes together. What it cannot express is a NON-CONFORMAL frame (a
sheared CD, or different column and row scales): the map's view is rigid, so such a frame matches
along the vertical and drifts along the horizontal. Real frames are square-pixel and shear-free to
well under a pixel, and one that is not is already drawn with the wrong aspect ratio by the image
quad.

**What `ViewDrivenExternally` turns off, and why each matters.** The home pass (which overwrites the
pointing on the first frame and again whenever the site changes), the roll servo (which walks a
matched rotation back to celestial north over about a second), and the map's own pan and zoom (which
would move the sky out from under the picture). Also the map's info strip and centre crosshair, which
the viewer's own status bar and content have already answered -- and which would be drawn UNDER the
photograph anyway. It is deliberately not a MODE: everything else about the map is identical either
way, and a mode invites a second answer for each of them.

**Site and time come from the frame, and degrade separately.** `FrameSiteResolver` (pure, in
`TianWen.Lib`, with `FrameSite` carrying provenance) reads `SITELAT`/`SITELONG`, remembers the last
site across frames so a folder walk does not lose the horizon on an arrow key, and reads an exact
`(0, 0)` as UNSET rather than as the Gulf of Guinea -- capture software writes the profile's site
whether or not anyone filled it in. `DATE-OBS` puts the planets, the comets and the horizon where
they were while the shutter was open (a missing one parses to year 1, so the test is for a plausible
instant rather than for a null). No instant means the wall clock; no site means no horizon and no
Alt/Az grid, and everything else is unchanged.

**Layer availability became honest, which the GUI needed too.** `SkyMapLayer.IsAvailable` was carried
by the milky way alone; the horizon and the Alt/Az grid now require a site
(`SkyMapState.SiteAvailable`, stamped per frame from the resolved `SiteContext`) and the mount
reticle requires a mount. The viewer is the host where all three are routinely absent, and an
unavailable layer is drawn dimmed and leaves its key UNHANDLED rather than swallowing it.

**The palette comes along without its key hints.** It doubles as the map's legend in a tab host,
where those ten letters are the only way to reach the layers; in the viewer every one of them already
means something else (`S` detects stars, `C` cycles the channel, `D` the demosaic), so a printed key
would be a row teaching a shortcut that does something quite different.

**Two things fixed on the way past.** The Objects button demanded an ALREADY-created object catalog
to be enabled at all, so a frame carrying its own WCS -- rather than one solved here, which warms the
catalog on the way -- could never reach the overlays: a disabled button cannot warm the database it
is waiting for. The rung now asks for the catalog itself, once, off-thread, from the render pass
rather than from each of the three inputs that can move the ladder. And the viewer's inspector now
reports `PaintedRegions()` rather than its own registered ones, because the renderer is a
`CompositeWidget` now and the palette's rows live on the map: without it an agent could see a panel
in a screenshot and had no way to address it.

### What the live review changed

Six rounds of driving it against a real frame, each of which moved a decision the desk could not
have settled:

**The sky's LINES cross the photograph; its IMAGERY stays behind it.** The first build drew the
whole map behind the frame, which is right for a star field and wrong for a constellation: a figure
line that stops at the frame's edge and resumes on the far side reads as two unrelated marks, and
the boundary you most want to place is the one running through your subject. The pass is now split
by KIND (`SkyMapDrawPhase.Backdrop` / `Lines`), not by convenience -- imagery (twilight ground,
milky way, star field, horizon fill) under, continuous geometry (figures, boundaries, the grid, the
horizon line, the meridian, Alt/Az) over, with each label riding with the thing it names. **Point
markers stay UNDER**, because the photograph shows those objects itself and a marker over a star it
is a marker FOR hides the evidence.

**One grid switch, two grids, and geometry picks which -- the third arrangement, not the first.**
The first put a second checkbox in the palette, so turning "grid" off showed MORE grid (the fine
per-pixel one going away, the map's coarse one appearing behind it), which is correct and
unreadable. The second removed the palette's row, which left a layer that answered its key and
appeared nowhere, on the panel whose whole purpose is to make the layers visible. What shipped is
ONE switch with two faces: the ladder and `G` write the viewer's flag, the palette's Grid row writes
the map's, and whichever moved since the last frame wins (`_lastSkyGridFlag`). Which grid then
DRAWS is not a choice at all.

**That handover is at 20 degrees FROM THE TANGENT POINT, and the pole is why it exists.** The frame's
grid is drawn on the frame's own TANGENT PLANE, and a tangent plane cannot represent a point 90
degrees away: near the south celestial pole the meridians swept past it instead of converging, which
is what the live review caught. `PaneGridMaxTangentAngleDeg = 20.0`, compared against
`SkyBackdropView.MaxTangentAngleDeg` -- how far the pane's furthest corner lies from the frame's
reference pixel -- hands over to the map's spherical grid before that can happen. **The bound is a
judgement, not a measurement**: the gnomonic-stereographic separation is about 0.8 percent of the
distance out at 10 degrees and 7 percent at 30, so 20 is where a whole-pane grid stops being worth
its error. One constant if it proves wrong in use. What the reader sees at the handover is a change
of DENSITY, which is honest -- they are grids of different things -- while the COLOUR is now one
definition (`SkyMapGpuGeometry.GridLineColor`, read by both GPU backends, the CPU renderer and
`image.frag`) and no longer changes with it.

**The first version of that gate read a FIELD OF VIEW, and that is the whole bug.** It asked the map
for `State.FieldOfViewDeg`, which is the map's own projection parameter and says nothing about how far
past its reference the FRAME's deprojection is being extrapolated -- so the frame's tangent-plane grid
went on drawing at a view with the south celestial pole on screen. Reported as *"at 10 percent, grid
looks okay, at 12 percent, not"*, where the good one was the map's spherical grid and the bad one the
tangent plane; measured, the tangent angle at that zoom is over 60 degrees while the nominal field is
a small fraction of it. The measure is computed IN the tangent plane (`theta = atan(r * scale)`, exact
for a gnomonic projection, monotonic in r, cannot wrap) rather than by deprojecting the pane's corners
through the WCS -- **asking the WCS about a corner far outside the sensor is the very extrapolation
the gate exists to catch**, so a guard built on it would share the failure. Default
`double.PositiveInfinity` until a solve has run, because the spherical grid is correct everywhere and
is therefore the safe default of the two. Pinned by four cases in `SkyBackdropViewTests`, including
monotonicity across nine zooms and the NaN answer for a frame with no usable scale.

**A pane-wide grid needs its own shader mode, and its own UBO slot.** `image.frag` gained a
`gridMode == 2` branch that returns the grid alone on transparent black, drawn as a second pass over
the whole pane rather than the image quad; `StretchUboSlots` went to 3 because a Vulkan UBO is read
at EXECUTE time, so two draws sharing a slot both get the second one's uniforms.

**The zoom flip was the solver extrapolating outside the sensor.** Zooming out far enough made the
sky snap to a mirrored orientation. The probes were being taken at the PANE centre, which at a
wide-enough zoom is far outside the frame, where a gnomonic deprojection wraps past 90 degrees and
comes back with a direction on the other side of the sky. They are taken at `CRPix` and one pixel
either side now -- always inside the sensor, whatever the view does -- and the view is fitted as a
rigid rotation through those three points. **The first regression test for it passed against the
sabotaged code**, because zooming about the frame's centre keeps the probe on top of the answer; it
zooms about a fixed off-centre anchor now and fails at 8 percent on the old probe.

**The "?" panel is a menu.** It had grown to about 35 rows, which runs off a laptop screen -- and
the one panel someone opens when the viewer has misbehaved is the worst one to have running off the
bottom. The root is now nine rows with marks, drilling into *Keyboard shortcuts* and *AI
enhancement*, each page's row 0 being the way back so the handler needs no per-page bookkeeping.
**The re-open has to be DEFERRED by a frame**: the dropdown closes itself after its selection
callback returns, so opening the next page from inside that callback is undone a moment later and
the panel simply vanishes.

**Two smaller ones from the same session.** The palette's default position is derived from the
histogram's own metrics rather than a constant, because it opened on top of it; and the context
menu's "Open in sky atlas" says "(web)", because in a viewer that now draws the sky itself, an entry
that opens a browser owes the reader that word.

**Four more from a second sitting at the same frame, of which three were only visible from the
arranged geometry rather than from any screenshot:**

- **The EQ grid has ONE colour because it now has one definition.** `SkyMapGpuGeometry.GridLineColor`
  was already canonical -- both GPU backends read it, the CPU renderer held a duplicate literal of the
  same numbers -- and `image.frag` was the outlier with a cyan of its own, so the 20 degree handover
  changed colour as well as density. The shader reads it from the UBO now, written there
  unconditionally rather than passed as a parameter: it is not a per-draw choice, and a parameter is
  something a caller can forget.
- **The pan lets go of the frame while the sky is behind it.** The clamp confined the frame to cover
  the pane zoomed in and sit inside it zoomed out, which is right for a picture on a background and
  wrong for a picture on the SKY -- it is what stops the sky beside the photograph being brought to
  the middle of the pane. Free only while `SkyBackdropActive`, i.e. while the backdrop is DRAWN and
  not merely switched on, so an unsolved frame keeps the confined pan instead of turning loose over
  blank ground. An intermediate version that kept a quarter of the frame on screen was tried and
  rejected by the user on sight: it still reads as clipped, only later.
- **"The top bar is flickering" was a relabelling button dragging the run sideways.** The toolbar is
  packed left to right, so a button whose label changes width shoves every button after it along, and
  Zoom relabels continuously as the wheel turns ("Fit", a ratio, a percentage). Measured from two
  inspector snapshots one zoom apart: Zoom 107.1 -> 133.5 px, with AutoCrop, Overlays, Stars and
  Enhance each moving by exactly that 26.4 px. Zoom and Enhance reserve their widest label now. **Not
  a damage-tracking bug**, which is where this looked like it was going -- the per-swapchain-image
  damage that made P15's tooltip read as a flicker is a real mechanism and was the wrong suspect here.
  The tests say which cases have teeth: with the reservation removed only "800%" and "1:16" move the
  run, while "Fit", "51%" and "1:4" all measure the same, so a suite of three-character zooms would
  have passed straight over it.
- **A share link about an OBJECT names it.** The menu knew "NGC 7204A" and the link carried
  ra/dec/fov/t alone, so the atlas opened on the right sky with nothing picked out of it. Writer-side
  only -- the web build already parses `object=` and re-tries the resolve as the catalog and the comet
  set arrive. The token is the DESIGNATION falling back to the name (a catalogue number is what the
  search resolves unambiguously), and `EscapeObjectToken` moved onto `SkyAtlasLink` because that class
  is where the link's vocabulary is defined for both ends and the web had grown its own copy of the
  keep-the-slash-literal rule.

**Open questions the draft listed, as answered by what shipped:** the sky FOLLOWS the file (site and
instant are restated per frame, with the site remembered when a frame does not carry one); the chrome
does NOT change when the view zooms out past the frame (the toolbar and histogram keep describing the
image, and the map's own chrome stands down instead); Grid and Objects DID lose their separate
buttons, which is what paid for the third rung. Search in the viewer (P5) is still open.

**What is left here:** P4b's geometric solve from `CENTALT`/`CENTAZ` and its single-profile read (the
header tier covers every N.I.N.A. and TianWen frame measured, so this is for third-party captures);
P1-P3, which are the GUI half and unblocked; P5's signal subset; and P6's web build. First-open cost
measured on this machine: a 45 ms seed build plus a 654 ms async Tycho-2 build off-thread, 6 ms of it
on the render thread to swap -- the map's own existing behaviour, not new.


### Deferred from the 2026-09-10 sitting (HIGH PRIORITY)

Everything raised while driving the shipped backdrop that was deliberately NOT built, recorded here so
the next pass starts from the findings rather than from the screenshots. The user's instruction on
locking the release scope was *"as long as we track everything we skipped in a plan, with high prio"*.

- **P5's click-select, and the fact that most of it already exists.** `FindObjectAt`
  (`ImageRendererBase.ContextMenu.cs`) already resolves the nearest catalogued object at a pixel from
  the frame's own WCS and `DeepSkyCoordinateGrid`, with an FOV-scaled tolerance, and deliberately
  gathers from the same grid the overlay draws from so it can only ever name something the overlay
  would have drawn. **It is wired to right-click only.** What is missing is downstream: the viewer has
  no notion of a SELECTED object (the sky map has `Search.InfoPanel`; the viewer has nothing), so
  there is nowhere for a left click's answer to go and nothing drawn to show it landed. **It needs no
  sky map** -- the resolver is WCS plus catalog, so this works at every rung of the ladder.
- **Hover versus click was left undecided, on purpose.** The user went "just a hover thing would be
  okay" -> "or just clicking on it" -> "scratch that maybe" within a minute. Both need the same
  missing piece above; pick one deliberately rather than inferring it from that exchange.
- **Clicking a STAR is a different resolver.** `document.Stars` holds DETECTED CENTROIDS, not
  catalogue entries, so it is a nearest-centroid search over the star list, not `FindObjectAt`. Worth
  saying because "click an object or a star" reads like one feature and is two.
- **Constellation stars appearing at wider fields.** Whether a star is drawn at all is
  `OverlayEngine.GetStarMagCutoff(fovArcmin)` -- mag <= 1.0 above 5 degrees, 2.5 at 2-5, 4.0 at 1-2,
  5.5 at 0.5-1, 7.0 below. tau PsA at about mag 4.9 therefore needs the field under one degree, which
  is what "esp. the ones in the constellation should certainly trigger earlier" is about. The
  mechanism is already to hand: `ConstellationFigures.AllFigureStarHipNumbers` is the roughly 1000
  figure stars the sky map uses for its bright-star seed, and membership in it means "this star draws
  a constellation" -- so it can raise or waive the cutoff for those alone. **The gate to move is the
  magnitude cutoff, NOT the label tier**: making figure stars appear earlier is the ask, making them
  show five lines earlier is not.
- **Trimming the identification stack itself.** Five lines (tau PsA / 15 PsA / HIP 109422 / HR 8447 /
  HD 210302) is correct and probably more than a reader wants; the Bayer name plus one catalogue
  number may be the right full-zoom label, with the rest left to the right-click menu and the info
  panel. Raised by the assistant, not decided by the user. The in-frame gate shipped instead, which
  removes the stack where it was pure decoration without answering this.
- **Matching the two grids' DENSITY at the 20 degree handover.** The colour is now one definition, so
  what still changes across the handover is density (the frame's fine per-pixel grid, the map's
  coarser spherical one). It is honest -- they are grids of different things -- but if it reads as a
  seam, the map's grid can take the viewer's spacing.
- **`PaneGridMaxTangentAngleDeg = 20.0` is a judgement, not a measurement.** It comes from the
  gnomonic-stereographic separation being about 0.8 percent of the distance out at 10 degrees and 7
  percent at 30. If it hands over too early or too late in use, it is one constant. (What the bound is
  MEASURED AGAINST is no longer a judgement -- see the tangent-angle section above.)
- ~~**The pan gate has no test.**~~ **DONE 2026-09-10** (`ViewerSkyPanTests`, three cases). **The
  blocker recorded here was wrong**: `AstroImageDocument.Wcs` is `private set`, but
  `AdoptImageAsync(image, algorithm, wcs, ...)` takes the WCS as a parameter, so an in-memory frame
  with a CD matrix needs no synthetic FITS at all -- the stand-up is a plain `SkyMapTab<RgbaImage>`, a
  `FakeTimeProviderWrapper`, `SharedCatalogDB` and that document. **The case worth having is the third
  one, which neither report would have asked for**: the sky switched ON over an UNSOLVED frame, where
  nothing is drawn behind the picture and the clamp must therefore stay. Sabotaging the gate three ways
  confirmed the split -- always-clamp kills only the free-pan case, no-clamp kills the other two, and
  the naive `ShowSkyBackdrop` gate kills the unsolved-frame case ALONE, which is exactly what that
  third test is for.
- **The enhance colour cast is NOT tracked here**: it is a viewer defect with nothing to do with the
  atlas, and it is [`viewer-prerelease-fixes.md`](viewer-prerelease-fixes.md) P30, open and high
  priority.

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
