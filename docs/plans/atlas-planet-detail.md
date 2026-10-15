# Atlas: planet detail (light curve, visibility curve, next opposition / GE, how a planet is drawn, the solar system in 3D)

**Status: PARTIAL (raised by the user 2026-08-22).** Tracked here rather than in the next release,
per the user's instruction: *"for Atlas lets just track it in the plan"*. Built 2026-10-14: finding 2's
live magnitude, A4's geometry layer (see "What is built" under A4) and A4's textured discs, phases and rings
("A4: the textured disc"). A5's solar system in 3D the same day ("A5. The solar system in 3D"). Open: A1's sparkline, A2, A3, A4's optional side-by-side strip (#1566), A4's textured-disc follow-ups (#1567), A5's follow-ups ("A5: what is open").

The notes, in the order they arrived:

- *"TW Atlas: Spark lines"*
- *"atlas needs the planet light curve and visiblity curve and whatnot"*
- *"at the very least show when the next opposition/GE is"*
- 2026-09-28, with a reference of the planets side by side by brightness and by size in the sky:
  *"improve the display of the planets in the atlas ... possibly also showing the correct Saturn ring
  angle + the phases of Venus + Mercury + Moon"* (A4)
- The same day: *"maybe pressing Alt-S gives us a planetary 3d view of the solar system"*, then *"we don't
  need to show this keyboard shortcut prominently, but maybe have a button when clicking on a planet/the
  sun/comets"* (A5). The user built something like it in MAUI years ago.

They are one theme: **a selected planet's info panel is much poorer than a selected comet's**, and
the last note is the floor to hit if only one thing gets done.

## Two findings that change what this work is

Both were checked in the code before writing this, because each one moves the item from "build it" to
"wire it".

1. **The event detector already exists, is unit-tested, and is already wired -- but only to the
   PATH.** `SkyPathEventDetector` (`TianWen.Lib/Astrometry/SkyPathEvents.cs`) detects stations
   (RA-rate sign reversal), **greatest elongation** for an inferior planet, **opposition** for an
   outer planet (both from a Sun track sampled at the same instants) and comet perihelion, purely
   from the sampled positions with no ephemeris service call. It is consumed by
   `DrawSelectedObjectPath`, which draws each event as a labelled ring ("R", "D", "GE", "Opp", "q")
   at its position along the arc -- see the D-events row of
   [comet-ephemeris.md](comet-ephemeris.md), which is where it was built.

   So "show when the next opposition/GE is" is **not new math**. The gap is that the answer is
   currently a ring on a curve, at a position, with no date beside it: to read it you have to select
   the planet, find the ring, and infer the date from where it sits on the path. The events carry
   `TimeUtc` already (`SkyPathEvent.TimeUtc`), so the info panel can state it as text.

   One thing to settle: the detector only sees events inside the sampled window, and a planet's path
   window is 120 days. Mars oppositions are ~26 months apart, so "the next opposition" is usually
   **outside** the window and the panel would have nothing to say for most of the synodic period.
   That is the actual work: either widen the search for the event query specifically (a coarse
   separate sweep at a much longer step -- the geometry is smooth, so a low sample count over years
   then a refine is cheap), or compute it from the synodic period analytically. The path drawing must
   keep its 120-day window regardless: the ring is for the arc that is on screen.

2. **A planet's magnitude in the info panel is a STATIC catalog value.**
   `SkyMapSearchActions.PlanetInfoPanel` fills `VMag` from `obj.V_Mag` out of `ICelestialObjectDB`,
   while `CometInfoPanel` is handed a live computed magnitude and the panel is rebuilt per frame from
   the live position. For a planet that is wrong in a way worth naming: Mars runs roughly -2.9 to
   +1.8 across its synodic cycle, so a fixed number is off by magnitudes for most of it, and it is
   the same number whatever instant the time scrub is sitting on.

   So the light curve is not only a new widget, it is also the fix for a value the panel already
   shows incorrectly.

   **FIXED (2026-10-14).** The panel's `VMag` is now `PlanetaryMagnitude.Apparent` of the body's
   `PlanetAspect` at the viewing instant, read from the same `SkyMapState.PlanetMarker` cache entry the dot
   draws from, so a time scrub moves it (`SkyMapPlanetDetailTests.ThePanelsMagnitudeIsTheLiveOneAndFollowsTheTimeScrub`:
   Mars from its 2025 January opposition to its 2026 January conjunction moves more than two magnitudes).
   A1's sparkline must sample the same function, which is what keeps its "now" point and the panel's
   number one computation. The catalogue value is left only as the fallback for a body without an aspect,
   which no body the atlas draws is.

## The items

### A1. Planet vmag sparkline (the "spark lines" note)

Give a selected planet the treatment a selected comet already has: a vmag sparkline in the info
panel, brighter-up, with a "now" marker, cached like the comet one.

- The comet side is `CometEphemeris.SampleMagnitudeCurve` (pure, tested) +
  `SkyMapState.GetCometMagnitudeCurveCached`. The planet side needs the equivalent sampler over
  VSOP87a geometry: distance to Sun, distance to Earth, phase angle, and a per-planet
  magnitude/phase law (the standard Meeus / AA formulae per body; Saturn additionally needs the ring
  contribution, which is the one that cannot be faked with a phase term).
- **Cache on `(index, time-BUCKET)`, not `(index, day)`**, and make the cache hit regardless of
  sample count including an empty result. Both rules are already recorded on the comet side and both
  were bugs there: an unbucketed key re-samples every day-scrub frame, and a planet path measured
  ~10 ms to rebuild versus ~1.4 ms for a comet, which is why planets got a 10-day bucket.
- The window wants to be much wider than a comet's +/-45 days: a synodic cycle is the meaningful
  span, so this is per-body (Mars ~780 d, Jupiter ~399 d, Mercury ~116 d).
- Fixing item 2 above (a live magnitude on the panel) should land with this, because the sparkline's
  "now" sample and the panel's number must be the same computation or they will disagree on screen.

### A2. Visibility curve

An altitude-versus-time curve for the selected body, so the panel answers "is it worth pointing at
tonight" rather than only "where is it now". The panel already carries live alt-az and
rise/transit/set from `SkyMapInfoPanelData.FromPosition`, so this is the curve those three numbers
are samples of.

Open questions, deliberately not decided here:

- **Tonight, or the season?** A one-night altitude curve (the planner's altitude chart, which
  already exists as `AltitudeChartRenderer`) answers a different question from a
  transit-altitude-per-date curve across the apparition. The planner tab owns the first; the Atlas
  probably wants the second, next to the light curve, on the same time axis.
- **Whether to reuse `AltitudeChartRenderer`.** It is a static non-widget renderer taking explicit
  font parameters, so it is reusable from the Atlas; the question is only whether the Atlas needs a
  different x-axis. If it does, it is a new sampler feeding the same drawing code, not a second
  chart implementation.

### A3. Next opposition / greatest elongation, as text

The floor, per the user: *"at the very least show when the next opposition/GE is"*. An info-panel row
naming the event and its date, for the currently selected body, chosen by `SkyPathBody`
classification (inferior planet -> GE, outer planet -> opposition, Moon/Sun -> nothing). Needs the
long-window search from finding 1. Cheapest of the three and independently shippable, which is why
it should go first.

### A4. How a planet is drawn: disc, phase, Saturn's rings

Issue: #1037.

A1 to A3 are about what the info panel SAYS; this is what the map DRAWS. Until 2026-10-14 every planet was a dot
sized by its type, in a colour per planet (`SkyMapRenderer.GetPlanetColor`), the same at any zoom whatever the
body's distance, phase or brightness. Now the dot is sized by the live magnitude, and once the disc is big enough
on screen the planet is drawn as itself: a textured disc turned to its true central meridian and pole, phased
toward the Sun, Saturn with its rings at their real tilt.

1. **A disc at its true angular size, where the zoom resolves one; a dot below.** The disc is drawn once it is at
   least `PlanetDiscSprite.MinDiscPx` (6 px) across and at least the dot it replaces; below that the dot runs and
   nothing about a disc is touched (at a 60 degree field Jupiter is about 0.2 px). A PAINTED disc is the planet's hit
   area and its hover wash (`SkyMapSearchActions.TryResolveHit`, the one resolver the click and the hover share: a
   click anywhere on it is the planet, whatever lies behind, and the wash is the disc's radius, never under the dot's
   tolerance); a planet whose texture has not landed is still the dot and is hit as one, read from what the last frame
   painted (`SkyMapState.PlanetDiscsDrawn`), never from the sprite cache. The label moves clear of the disc's edge,
   its click unchanged (`MarkerLabelEntry`).
2. **The disc is a realistic texture, not a drawing from the palette.** This item first said "never a texture, so
   Night mode can tint it"; the user asked instead (2026-10-14) for realistic planet textures "in similar fashion to
   what planet stacking does ... using OPAL", with correct rotation, performance first. Night mode is answered at
   BUILD time: the sprite is the texture's luminance in the Night accent, blue at zero, and the palette generation is
   in the sprite's key. See "A4: the textured disc" below.
3. **Phase: Venus, Mercury, the Moon, Mars's gibbous**, by rendering, not by composing ellipses: the disc is lit
   from the Sun's sub-solar point (`PhysicalEphemeris.SunOnTheDisk`), so the terminator, the crescent and the bright
   limb's position angle are the ephemeris's, on all three renderers alike.
4. **Saturn's rings at their real tilt**: the sprite reaches the ring system, the rings drawn from the ephemeris
   (`SaturnRings.Main`), the near half over the globe, the far half behind it, the globe's shadow on the rings and
   theirs on the globe, as the stacker's twin renders them.
5. **Info-panel rows:** apparent diameter, illuminated fraction for a phased body, ring tilt for Saturn (built with
   the geometry layer, below).
6. **Optional: the planets side by side** (#1566), by size in the sky or by brightness, as in the reference: a strip
   of all of them at the viewing instant, each pressable to select it. Not built; only if it earns its screen space.

**What a user will notice:** the interactive atlas zooms in to half a degree (`SkyMapViewActions.MinFieldOfViewDeg`),
where on a 1,000 px view the Moon and the Sun fill the screen, Venus is about 30 px and Jupiter about 20; the FITS
viewer's sky backdrop goes down to 0.005 degree, where every planet is a large disc. A disc wider than 256 px is the
256 px sprite scaled up. **So the disc is shown in the selected body's info panel too** (the owner, on the built GUI,
2026-10-14: the map cannot zoom far enough for a planet's disc to be seen): see "The info panel's disc" below.

#### What is built (the geometry layer, 2026-10-14)

Everything a drawn disk needs: the textured disc (below) and A5 build on this.

- **`PhysicalEphemeris.Compute(body, utc)` covers every body the atlas draws** (`PhysicalEphemeris.Supports`): the
  Sun, the Moon (the IAU lunar orientation), Mercury, Venus, Mars, Jupiter, Saturn, Uranus and Neptune (IAU WGCCRE
  2015). Its `PlanetAspect` gives the geocentric `DistanceAu`, `HeliocentricDistanceAu`, `HeliocentricEclipticLongitude`
  (J2000), `PhaseAngle`, `IlluminatedFraction`, `BrightLimbPositionAngle` (toward the Sun, east of north of date),
  `PolePositionAngle`, the sub-observer `CentralMeridianIII` / `SubObserverLatitude` (and `...Centric`), the sub-solar
  `SubSolarLongitudeIII` / `SubSolarLatitude` (and `...Centric`), `AngularDiameterArcsec`, `Flattening`, and for Saturn
  `RingOpening` (B), `RingOpeningFromSun` (B') and `RingPositionAngle` (P). **Longitudes are WEST for every body**;
  Venus and Uranus turn retrograde (`PhysicalEphemeris.RotatesRetrograde`), so theirs FALL with time, and Horizons'
  east longitude for them (and for the Moon and the Sun) is 360 less ours. Checked against Horizons at four instants
  for all nine bodies (`PhysicalEphemerisBodiesTests`, fixture `horizons-bodies-2026-10-14.txt`): planets within
  0.011 degree on every angle (the largest Uranus's pole angle in 2026), the Moon within 0.03, the bright limb within 0.03 once Horizons' PsAng (ICRF north) is
  turned to north of date. Meeus 41.a (Venus k = 0.647) and 45.a (B, B' within 0.005, P 0.026 off Meeus's own ring
  model while within 0.0005 of Horizons) pass.
- **The planetary stacker is untouched**: its model gates ask `PhysicalEphemeris.SupportsPlanetaryStacking` (Mars,
  Jupiter, Saturn), so widening `Supports` switched nothing on for a Moon or Venus capture.
- **`PlanetaryMagnitude.Apparent(aspect)`**: Mallama and Hilton 2018 as their own Ap_Mag V3 evaluates it (Mars's
  rotation and season corrections, Saturn's rings by the geometric mean of B and B', Uranus's and Neptune's phase
  curves only past what the Earth sees), the Moon by Allen's phase law, the Sun at its distance. Within 0.003 mag of
  Horizons' APmag on all 36 rows (`PlanetaryMagnitudeTests`).
- **One cache entry per body** (`SkyMapState.GetPlanetPositionsCached` now returns `SkyMapState.PlanetMarker`: RA/Dec,
  `Aspect`, `VMag`; `TryGetPlanetCached`), on the same one-second refresh as before, so the dot, the panel and a disk
  read one instant. The dot is sized by the live magnitude (`SkyMapState.PlanetDotRadius`: 1.5 px at +3.3 and
  fainter to 4 px at -5, the Sun and the Moon a fixed 4 px).
- **The panel's disk row** (`ObjectInfoPanel.DiskLine`, item 5): the diameter (arcmin for the Sun and the Moon), the
  lit fraction where it rounds below 100 %, Saturn's ring opening signed.
- **Found on the way**: `MeeusMoon` added a longitude of the equinox OF DATE to VSOP87's J2000 Earth, so the Moon sat
  0.37 degree east of itself in 2026 (twice that in `VSOP87a.Reduce`'s of-date output, precessed again). The general
  precession is now taken out; the lunar central meridian against Horizons went from 0.29 to 0.38 degree off on the four rows to under 0.006.

#### A4: the textured disc

Built 2026-10-14 (#1037). The pieces, in the order a frame meets them:

- **Textures.** ONE offline atlas, `planet-textures.png` (512 x 512, 301,799 bytes, `PlanetTextureAtlas`), eight
  256 x 128 equirectangular colour maps in `PlanetMap`'s convention (WEST longitude, column 0 at 0 degrees, falling
  to the right), poles filled: Mercury, Venus and Mars from Solar System Scope (CC BY 4.0), the Moon from NASA SVS's
  LROC map (public domain), Jupiter, Saturn, Uranus and Neptune from OPAL's 2025 colour composites. Baked by
  `tools/bake-planet-textures` (its README has every source URL, licence and OPAL's acknowledgement), kept in
  `TianWen.UI.Abstractions/Resources`, copied beside every desktop app and served beside the web page. Never fetched
  for Mercury, Venus, the Moon or Mars.
- **The OPAL upgrade.** The four giants are upgraded at runtime to OPAL's `globalmap-small` colour composite (450 x
  225, 8-bit RGB TIFF, served by MAST with `Access-Control-Allow-Origin: *`), from a hard-coded table of apparitions
  (`OpalColourMaps.TryFor`; no directory listing). The desktop caches them under `AppData/PlanetMaps`
  (`PlanetMapStore`); the browser fetches the same URL through its own client (`BrowserPlanetMapStore`); both decode
  with `SharpAstro.Tiff` in .NET (`OpalColourMaps.Decode`). A failed fetch keeps the atlas and is not asked again for
  an hour (`PlanetTextures.RetryCooldown`). The desktop keeps only a body that decodes as a map: a 200 that is something
  else (a maintenance page) is never cached, and a cached file that does not decode is deleted, so the ask after the
  cooldown fetches again. OPAL's filters are not the eye's, so its maps are credited as "OPAL (HST),
  colour rendering". OPAL leaves black where Hubble never saw the planet (Uranus's whole southern hemisphere in 2025,
  Saturn's north pole); a display fill (`PlanetColourMap.FromRgb8(fillHoles: true)`) holds the nearest kept rows'
  mean colour there. The stacker's `PlanetMap.FilledZonally` is untouched: it copies a row's longitude pattern, which
  streaked a missing hemisphere.
- **Longitudes.** OPAL's readme: "left edge = 0 System III W. longitude, decreasing to the right", which is
  `PlanetMap`'s own convention. Pinned by the Great Red Spot: the reddest feature at 22 degrees south of the 2025b map
  (2025-12-11) lies at System III 321 W, System II 79 at that date, where the spot was in late 2025
  (`PlanetTextureAtlasTests`).
- **The render.** `PlanetaryRender.RenderRgba` traces the SAME scene as the stacker's mono `Render` (one `Scene`, one
  ray trace, `SceneRay`): the oblate spheroid turned to the central meridian, Minnaert limb darkening, lit from the
  sub-solar point, Saturn's rings with both shadows; it lights the trace from each plane of a `PlanetColourMap` and
  adds a coverage channel. The mono render hashes as it did on main (`PlanetDiscRenderTests`). 3 x 3 rays a pixel, not
  the stacker's 8 x 8.
- **The sprite** (`PlanetDiscSprite.Build`): a square straight-alpha RGBA image, the globe or Saturn's ring system and
  a pixel of margin, at most 256 px. Straight, not premultiplied, alpha: every host's blend (Vulkan's `SrcAlpha`,
  WebGL's `AlphaOver`, the CPU `BlendPixelAt`) takes straight alpha, and a premultiplied sprite would darken its limb
  twice. The night side is covered and black, as the Moon's dark limb hides the stars. A body below the horizon is
  baked at 35 % opacity, as its label is. The Sun is the same render lit at full phase from a flat colour.
- **Orientation on screen.** The pole's position angle is east of celestial north, and the VIEW can roll, be
  horizon-up or be mirrored, so the sprite is turned by the pole angle laid off from the screen direction of celestial
  north AT THE PLANET, found by projecting the planet and its neighbours a hundredth of a degree north and east through
  the current view matrix (`PlanetDiscSprite.TryScreenNorth`, `PoleScreenAngle`). The rotation is inside the sprite;
  no new shader.
- **The cache** (`PlanetSpriteCache`): one sprite per body, keyed on `PlanetSpriteKey`: the size in eighth-octave
  buckets, and every angle (central meridian, the Sun's place on the disc, the sub-observer and sub-solar latitudes,
  the turn on screen) in steps of the limb error, one radian over the BUCKETED radius, never under a quarter degree;
  the palette generation, Night, the texture version and the dimming. The sprite is built from the aspect snapped to
  its bucket centres, so it is a function of its key. A changed key starts ONE build on the thread pool (the Task is
  the hand-off; no lock), the stale sprite is drawn until it lands, and its landing asks for a frame (the web host's
  `RequestRepaint` too).
- **The info panel's disc.** Selecting the Sun, the Moon or a planet puts its disc in the panel, under the rows and
  above the picture: a declared slot (`ObjectInfoPanel.DiscFillKey`, in one media stack with the picture section,
  `ObjectInfoPanel.BuildMediaSections`; `DesignDiscHeight` 132), drawn through `drawFill` by `SkyMapTab.DrawPanelDisc`.
  The whole sprite fills nine tenths of the slot's shorter side (`PlanetDiscSprite.PanelRadiusPx`), so Saturn's rings
  fit and every body is as large as the slot allows. **North up and east left**, as the sky shows it to the eye, whatever
  the map's own view is turned to; phased, ringed and Night-tinted as on the map. The same sprite path builds it, from a
  `PlanetSpriteCache` of the panel's own (`SkyMapTab.PanelSprites`, keyed at the slot's size, so a frame that changes
  nothing builds nothing), and every sprite carries the channel that built it (`PlanetSprite.Channel`), so a host keeps
  the map's and the panel's textures of one body apart instead of replacing one with the other each frame. A non-solar
  system object gets no slot. The GUI and web atlases draw it; the FITS viewer's object panel never selects a planet and
  is unchanged; the TUI has no atlas info panel. Tests: `SkyMapPlanetDiscTests` (Saturn's rings past its globe in the
  slot, the Moon's lit limb toward the Sun, no slot for a deep-sky object, no rebuild over ten frames, and the panel
  written as `panel-saturn.png` and `panel-moon.png`).
- **The hosts** (`SkyMapTab.DrawPlanetSprite`): the base draws on the CPU surface (`RgbaImage`: the tests and the
  terminal), `VkSkyMapTab` through `VkPlanetSprites` (a queued upload, the previous texture drawn until the new one
  lands, a replaced texture's destroy deferred by the context), `WebSkyMapTab` through `WebGlPlanetSprites` (the
  sprite handed to the browser as a PNG data URL, since the renderer takes textures only from a URL).

**Measured:** one sprite build, Jupiter from the atlas, after a warm-up, on this Surface Pro 11 (Snapdragon X Elite
X1E80100, 12 cores, the rows spread over them by `ParallelFor`), Release: 0.6 ms at a 64 px sprite and 10.8 ms at
256 px with the JIT fully optimising (`DOTNET_TieredCompilation=0`, which is what an AOT app runs), 3.9 and 49 ms while
tier-0 code still runs (`SkyMapPlanetDiscTests.TheCpuSurfaceShowsTheSpritesPixels` writes `build-cost.txt`). A
wide-field frame does no sprite work at all (`SkyMapPlanetDiscTests.AWideFieldDoesNoSpriteWork`); builds are COUNTED,
never timed, over a pan (none), a zoom (once per size bucket crossed) and a time scrub (some of ten one-minute steps
of Jupiter, once for an hour's jump). On the single-threaded browser a build runs on the main thread, one per bucket
change; it is not measured there (#1565).

**Tests:** `PlanetDiscRenderTests` (the mono render unchanged to the bit; the colour render's planes equal the mono
render's; the lit limb toward the bright limb's position angle for the Moon, Venus and Mars; Saturn's near ring over
the northern globe with B negative; a central-meridian turn moves a feature west; the pole turned by the view's own
north, rolled and mirrored; Night has no blue), `PlanetTextureAtlasTests` (every body, no holes; the Great Red Spot),
`PlanetTexturesTests` (a failed fetch keeps the atlas and cools down; a landed one replaces the map, credits OPAL,
survives a later atlas), `PlanetMapStoreTests` (a 200 that is not a map is never cached; a cached file that does not
decode is deleted), `SkyMapPlanetDiscTests` (no work at a wide field; the build counts; the painted disc as hit area,
the dot's tolerance while it is not painted; the hover wash at the disc's radius; the CPU surface shows the sprite,
written as `planet-discs.png` in the test output).

**Open:**

- **The browser's sprite-build cost, #1565.** On single-threaded WebAssembly a build runs on the main thread, about
  the desktop's tier-0 time (49 ms at 256 px) per size-bucket change; unmeasured there, and the fix (a smaller cap,
  fewer rays, or a build spread over frames) is that issue's.

- **Textured-disc follow-ups, #1567**:
  - a disc wider than 256 px is the 256 px sprite scaled up;
  - the atlas's Uranus holds its edge row's colour over the unseen southern hemisphere;
  - nothing yet shows the texture's credit in the info panel.

### A5. The solar system in 3D

Issue: #1039. Built 2026-10-14.

A view that swaps the atlas's sky for the solar system seen from outside it, at the instant the atlas is
showing: the Sun at the centre, the planets on their orbits, and the selected body highlighted. A "gimmick" in
the user's word, but one that explains the sky beside it: the line from Earth to Venus, against the line from
the Sun, is why Venus is a crescent, and the angle between them at the planet is A4's phase angle.

#### What is built

- **A mode of the sky map tab, not a tab** (`SkyMapState.Orrery`, an `OrreryState`; `SkyMapTab.Orrery.cs`). While it
  is open `SkyMapTab.Render` draws it in the sky's place and the sky's chrome (the layer palette, F3's search, the
  large picture) waits; input goes to the orrery's own handler (`HandleOrreryInput`). A host that DRIVES the map
  (the FITS viewer's backdrop) never calls `Render`, so it never shows it.
- **The way in is the panel's Orrery button** (`ObjectInfoPanel.PanelActions.Orrery`, `ObjectInfoPanel.OrreryAction`),
  offered when the selection's index is a solar-system body (`CatalogIndex.IsSolarSystemObject`: a planet, the Sun,
  the Moon, a comet) and for nothing else. With four buttons each is the engine's measure of its label and
  padding, never a width picked to fit, so all four fit the panel's 348 at any scale (a row of three keeps its widths;
  `OrreryTests.ThePanelAndTheStripLayOutAtEitherScale` checks both rows side by side at 1x and 1.5x). The button is
  the VISIBLE way in. **Alt+S is the quiet shortcut** to the same view, and closes it again: a DECLARED node shortcut
  (`RenderOrreryBinding`, `.WithShortcut(InputKey.S, InputModifier.Alt)`), never an arm in `HandleKey`, where
  `SkyMapLayers.TryToggleByKey` ignores modifiers and would toggle the Milky Way. It is named here and nowhere on
  screen: the sky's key legend does not list it (review round 2, PR #1571). **Escape** (declared beside it while the orrery is open) and the
  strip's Sky button go back, the selection and the sky's view untouched. The view opens framed on the selected
  body's distance from the Sun (the Earth's for the Moon, the span of Mars's orbit for the Sun or no selection).
- **Positions:** `SolarSystemOrbits` (Lib): `VSOP87a.GetBody`'s heliocentric ecliptic coordinates, no light time (the
  orrery shows one instant from outside); the Moon from Meeus's series about VSOP87's Earth. An orbit is one sidereal
  period of the same series (`SolarSystemOrbits.SampleOrbit`, 241 points, closed), so a planet sits on its own. A
  comet is the one the view was OPENED on (`OrreryState.Comet`, kept apart from the highlight, which a click moves), from the elements the comet repository holds (`ICometRepository.TryGet`, apparition
  upgrade included): its place from `CometEphemeris.TryGetHeliocentricEcliptic` and its orbit from
  `CometEphemeris.SampleOrbit` (361 points evenly in true anomaly, the whole ellipse, an open orbit to 40 AU each
  side), both through one perifocal frame (`PerifocalAxes`), so it sits on its drawn orbit.
- **The cache** (`OrreryScene`): the positions per MINUTE of the atlas's clock, the planets' orbits per CENTURY of it,
  built on the thread pool (the Task the hand-off, a landing asks for a frame), and a comet's orbit AND position per
  element set (an apparition upgrade keeps the index and changes the elements, and must move the comet at once).
  Orbits are stored in true AU and the scale is applied as each point is projected, so switching the scale builds
  nothing. A drag changes no key, so it builds nothing (`OrbitBuilds`, `CometOrbitBuilds`, `PositionBuilds`).
- **The camera** (`OrreryCamera`): a 45 degree perspective on a point orbiting the Sun and looking at it, the
  ecliptic's north up; a drag turns its azimuth and elevation (0.35 degree a pixel, the elevation held inside 89),
  the wheel and a pinch its distance, which stays inside a reach of a quarter of an AU to fifty
  (`MinOrreryDistance`, `MaxOrreryDistance`). A pinch is handled as the sky's: it undoes and cancels the drag the first
  finger began, and the touchpad's duplicate wheel event during or just after it is ignored. A proper view, never mirrored. Points are projected on the CPU and drawn with
  the primitives every host has (`DrawPolyline` for orbits, a sprite or `FillEllipse` for a body), so Vulkan, WebGL
  and the CPU surface draw it with nothing new.
- **Scale:** Auto (the default) or True, ONE toggle on the strip labelled with the mode in force (`Distances: auto` /
  `Distances: true`, sized by the longer label so a press moves nothing beside it). Auto is DYNAMIC (`OrreryScaling`):
  `r' = Z (r / Z)^p`, with `Z` the true-AU reach the camera frames (`OrreryScaling.ReachFor`, the inverse of
  `OrreryCamera.DistanceToFrame`) and `p` a smoothstep of `ln Z` from 1 at an AU or less to one half at 20 AU or more: the
  identity close in, continuous in `Z` (a zoom moves no body by more than the zoom's own step), monotonic in `r` (orbits
  never cross) and every direction from the Sun kept, which is what the lighting needs. The camera's distance is AU in
  both scales and a distance at the reach is drawn where it is in both, so the toggle never re-frames. Orbits stay in true
  AU and are scaled as they are projected, so neither a zoom nor the toggle rebuilds one.
- **The bodies** are ray-traced on a GPU host in the camera's own frame ("A5: the bodies on the GPU", below), and on the
  CPU surface are A4's sprites, from a cache of their own (`PlanetSpriteCache.OrreryChannel`), at an exaggerated
  size in their true order (the Sun 14 px, Jupiter 10, down to Mercury 4 and the Moon 3.5, times the DPI scale),
  each labelled, the selected one ringed and its orbit drawn in the accent. Every body is placed first, and each disc but
  the Sun's is then at most 0.4 of its screen distance to the nearest drawn body (to the Sun's EDGE), a 1.5 px dot below
  that: zoomed out the discs do not pile onto the Sun, and a body in FRONT of the Sun (a transit) is a dot, never a disc
  across it. A body behind the Sun is drawn before it (the painter's order, far first). The MOON is placed on SCREEN, a
  steady reach from the Earth's drawn place (the two radii, a gap and a label line), turned by its geocentric ECLIPTIC
  LONGITUDE from the direction the view draws level, so it goes round the Earth on an even circle once a month (turned by
  its direction as projected, it raced through the line of sight and crawled across the sides: six-hour steps of 1.2 to 9.5
  degrees at a 30 degree tilt, against 2.96 to 3.68 now, the Moon's own range). The reach comes in to half the screen gap to
  the nearest neighbouring ORBIT as drawn (Venus's or Mars's), so it never sits on another planet's orbit, and the Moon is
  hidden only where even its nearest place clear of the Earth's disc would not fit; its label steps out a line at a time
  where it has come in close. Placed in the world instead, at 8 % of the Earth's drawn distance, it was foreshortened by the camera's
  elevation and clipped by the floor that held it off the Earth: 27.8 to 68.1 px over a month zoomed in, a spiral in Play.
  Labels go down after every disc: the Earth's first (always drawn, so the Earth's and the Moon's never trade places), then
  the focus body's, then nearest first, each under or over its body where its engine-measured box is clear of other discs
  and labels, else left out; the Moon's always on its far side from the Earth. The Earth, which A4 never draws, has the IAU's rotation model and radii in `PhysicalEphemeris` now
  (not in `Supports`, which the atlas and the stacker gate on) and the palette's Info as a flat colour, there being no
  Earth map in the atlas. Every colour of the view comes from `GuiTheme.Palette` (space, the Earth, a comet, the strip,
  the shared time controls taking the caller's fill and ink), so Night spends no blue.
  A comet is a point with a tail away from the Sun. A body still building its sprite is a flat dot.
- **Lit as the camera sees it.** Each body is stated as a `PlanetView` (Lib): three unit vectors in the body's OWN frame
  (x its prime meridian, z its IAU pole, `PhysicalEphemeris.BodyAxes`): the screen's right and the direction back toward
  the camera along the ray the body is DRAWN on (`OrreryCamera.RayFrame`, so the Moon, placed on screen beside the Earth,
  is lit as it is drawn), and the Sun's true direction from the body (which the compressed scale keeps). So a body
  between the camera and the Sun shows its night side (phase 180). The GPU traces straight from those vectors; the CPU
  sprite reads them back as an aspect (`PlanetView.ToAspect`: the central meridian, the sub-observer and sub-solar
  points, the phase angle, as `PhysicalEphemeris.ComputeSeenAlong` states them) turned by the pole's screen angle
  (`PlanetView.PoleScreenAngleDeg`, the pole projected on the screen's right and up). No sky is involved, so the
  aspect's position angles, distance and angular size are NaN.
- **Time:** ONE clock with the sky (`SkyMapTab.ResolveViewingTime`: the planner's date or the live clock, plus
  `SkyMapState.TimeOffset`). The strip carries the sky's own time controls (`BuildTimeControls`: the steps, the
  landmarks drop-up, Now) and the arrow keys and 0 work as on the sky; Play runs the offset at 10 days a second of
  the wall clock, a frame stepping at most 100 ms of it (`MaxPlayStep`: ten minutes on another tab used to jump
  sixteen years), so the sky shows the same instant when the view goes back. Playing is a STANDING reason to draw
  (`SkyMapState.NeedsContinuousFrames`), asked by the GUI's redraw check: the one-shot `NeedsRedraw` a played frame
  sets is cleared by the GUI after the frame, which held Play to about a frame a second. The browser asks for each
  frame as a coalesced repaint.
- **Click** a body (a press that did not move) to put the highlight on it; the atlas's selection and the comet the
  view was opened on are left as they are.
- **Centre.** With a body other than the Sun in focus the strip has one toggle naming what the camera looks at
  (`Centre: Sun` / `Centre: Jupiter`, `OrreryState.CentredOnFocus`): centred, the camera looks at the body's drawn place
  (the Earth's for the Moon) and follows it as time runs; a click moves it with the highlight, a click on the Sun puts it
  back. `OrreryCamera` only gains a look-at point, so its rotation and the backdrop's stars are unchanged.

#### A5: the backdrop

The sky map's own stars and Milky Way are drawn behind the orrery, at infinity, through the sky's own rendering: one
renderer, a second view of it, never a copy. `SkyMapState.Backdrop` (a `SkyOrreryBackdrop`) is set for the length of one
backdrop draw (`SkyMapTab.DrawOrreryBackdrop`, the first thing the frame draws after its background) and carries:

- **The view.** `OrreryCamera.EquatorialView`: the camera's ROTATION alone (no translation, since a star is at infinity),
  composed with the fixed tilt from the ecliptic of J2000 to the equator the catalogue is in (VSOP87's own,
  `SolarSystemOrbits.EclipticToEquatorial`, the obliquity 23.44 degrees), as a sky-map view matrix. So the stars turn as
  the camera orbits and never move with the planets.
- **The projection.** GNOMONIC at the orrery camera's focal length, the perspective the scene is drawn in, where the sky map
  is stereographic: a `gnomonic` flag in the shared UBO (`SkyMapUbo`, 144 bytes now, with `tint` and `brightness`) that
  `skymap_star.vert` and `skymap_mw.frag` (re-baked SPIR-V) and their WebGL copies honour. The C# mirror is
  `SkyMapProjection.ProjectGnomonicWithMatrix`.
- **The culls**, in force for the orrery's view: both pipelines cull by `SkyMapState.ViewAxis` / `ViewRadiusDeg` (the
  backdrop's cone while one is drawn) and `EffectiveMagnitudeLimit` at the backdrop's field, through
  `StarChunkIndex.BudgetedMagnitudeLimit` as before.
- **Dimming and Night.** A brightness factor (0.35, `OrreryBackdropBrightness`) multiplies every STAR's alpha, so
  orbits and labels stay readable (the stars add at most 86 of 255 to a channel under a label); the Milky Way (NASA SVS
  Deep Star Maps 2020, the atlas's own texture) has its own opacity, 0.6, not the Sun's altitude at the site, since the
  view is not from the ground. It used to take both factors, 0.35 x 0.2 = 0.07, which the owner saw on the built GUI as a
  backdrop "super dim". Under Night the tint draws each as its luminance in the palette's accent, so no blue. In the
  GUI the stars still read as none at all: the Vulkan star pipeline added `skymap_star.frag`'s already premultiplied colour
  SrcAlpha/One, so every desktop star landed at its alpha squared (a magnitude 6 star 10 of 255 behind the orrery); it adds
  One/One now, as the browser's does (`HalvingTheBackdropsBrightnessHalvesAStarsLevel`). The Sun's altitude plays no part:
  a day and a night instant measured the same in the window.
- **What a host skips**: the twilight ground, the horizon fill and the lines (Vulkan draws the `Backdrop` phase, WebGL
  checks the backdrop), and `SkyMapState.CurrentViewMatrix` is left as the map's own.

The CPU surface (the tests, the PNGs) draws `SkyMapRenderer`'s star field through the same view, culled the same two
ways from unit vectors made once per catalogue (7 ms a frame at 900 x 600 where walking the catalogue's lookups cost 249);
the Milky Way is a texture only the GPU hosts draw. `OrreryState.ShowBackdrop` switches it off.

**Measured** (`OrreryTests.TheOrreryDrawsOnTheCpuSurface`, Debug, tiered JIT, this Surface Pro 11, Snapdragon X Elite
X1E80100): an orrery frame at 900 x 600 on the CPU surface while the camera turns half a degree a frame, before the
backdrop median 1.0 ms Compressed and 1.9 ms True (the raster of the larger orbits is the cost); with the backdrop's stars
7.0 ms Auto and 8.0 ms True, max 9.8 and 42 ms on frames whose sprites changed bucket. The projection and ephemeris work is a few thousand points and nine rotation models; a
drag does no VSOP87 at all.

**Tests** (`OrreryTests`): the Earth's heliocentric longitude is the Sun's geocentric longitude (VSOP87's own sky
reduction) plus 180 degrees, to 0.01; the angle the view draws at Venus, Mars and Jupiter between the Sun and the
Earth is `PlanetAspect.PhaseAngle` for the same instant at true scale, and so is the camera-viewpoint aspect's built
looking from the Earth's direction; a body between the camera and the Sun shows its night side (180) and from the
other side its day side (0); a day moves the Earth 0.99 degree, a week 6.95; a comet sits on its own drawn orbit to
a thousandth of its distance (an ellipse of e 0.585, one of 0.92, a hyperbola of 1.004); the panel offers the button
for Jupiter, the Sun, the Moon and a comet and not for M 51; Alt+S through the router opens the view with the Milky
Way layer as it was, and Escape returns with the selection kept; a drag of twenty frames builds no orbit and no
position (one build each before it), a day's step one position and no orbit; and the view drawn on the CPU surface,
Compressed and True with Jupiter selected, written as `orrery-compressed.png` and `orrery-true.png` in the test output.
After review (PR #1571 round 1): a click on Jupiter keeps the comet and builds its orbit no second time; Play stays a
reason to draw however often the one-shot flag is cleared, and stops with Pause and Escape; new elements for the same
comet put it on its new orbit with no change of time; a pinch zooms once and cancels the drag; and under Night no
pixel of the view has any blue (`OrreryNightTests`). Round 2: the Earth and the Moon never overprint, at 1x and 1.5x,
at the opening framing and zoomed in; a played frame steps at most 100 ms. Then: the Earth's and the Moon's labels never
swap over a lunar month of six-hour steps, and the drawn Earth-Moon distance holds within 5 % (27.8 px all month at
every zoom); the distance toggle is one button naming its mode; a body behind the Sun never paints over it (840 framings);
the auto scale is the identity at a reach of an AU and keeps the planets' order and clear of the Sun at 6 and 45; a zoom
from 0.25 to 50 AU in 200 steps moves no body by more than the zoom's own step; the zoom stops at the two limits; fully
zoomed out no disc covers the Sun; a body in front of the Sun is a dot. The backdrop: a bright star (Sirius, Polaris) lands
where the camera's rotation and the obliquity put it, stays put through a month's scrub and moves when the camera orbits;
on a real Vulkan device (`OrreryBackdropGpuTests`, through the sky's own star pipeline) Sirius is drawn 0.1 px from that
prediction (the stereographic shader before the change put it 2.1 px off) and Night's frame has no blue.

#### A5: the bodies on the GPU

Built 2026-10-15. **Why:** the CPU sprites were right at rest (`EveryDiscIsLitTowardTheSun`, 0 to 6 degrees off), but
under Play every body's key changed every frame (306 sprite builds in 40 played frames on the CPU surface, 414 measured
before in the GUI), the stale sprite was drawn while the next one built, so the lit side ran 15 to 29 degrees behind even
one frame late, and the GUI logged a slow frame every 250 to 330 ms.

- **One shader per GPU host, one trace.** `planet_body.vert` / `planet_body.frag` (Vulkan, baked SPIR-V) and
  `WebGlPlanetBodies.cs` (WebGL 2): per pixel, 3 x 3 rays along the direction away from the camera from the screen
  point (an orthographic impostor per body: at the orrery's exaggerated sizes a body's own perspective is a per-mille
  effect, and the disc stays the circle the hit area is). Each ray meets the oblate spheroid, is lit by Minnaert's law
  (`PlanetDiscSprite.MinnaertK`), reads the colour map at its planetographic latitude and west longitude (texelFetch,
  interpolated as `PlanetMap.Sample` does), and with Saturn crosses the ring plane in depth order, the globe's shadow on
  the rings and theirs on the globe, the unlit face dark, coverage as straight alpha; Night draws the luminance in the
  palette's tint. The text between the two "The trace" markers is the SAME in both files
  (`PlanetBodyGpuTests.TheWebGlTraceIsTheVulkanTrace`); each host states only its block, sampler and pixel centre
  (`screenPixel`: WebGL's framebuffer is y up). A varying interpolated across the quad instead of `gl_FragCoord` put up
  to 77 more values more than 4 off the CPU on the Adreno.
- **Inputs:** one block of seven vec4s (`PlanetBodyMap.WriteBlock`: Vulkan's push constants; in WebGL ONE instance of seven
  vec4 attributes in a buffer of the body's own, `PlanetBodyInstances`, since WebGl.Renderer sets a uniform block at once
  but replays the draws at Present, which drew every body with the last body's block, PR #1589 review round 1): the
  disc's centre and radius, the viewport, the axis ratio, k, the view's right, toward and Sun vectors, the map's mean
  albedo per plane (a ring's level is stated against it) and the tint. The ring table is `SaturnRings.Main`'s, written
  into the shader and held to it by `TheShadersRingTableIsSaturnRingsMain`. Colour maps go up ONCE per body and
  `PlanetTextures.Version` (`PlanetBodyMap.FloatTexels`: float RGBA on Vulkan, exact; `Rgba8` as a PNG on WebGL, which
  takes a texture only from a URL); a flat body (the Sun, the Earth) is a 2 x 2 map of its colour.
- **The seam:** `SkyMapTab.TracesPlanetBodies` / `DrawPlanetBody` (`VkSkyMapTab` through `VkPlanetBodies`, `WebSkyMapTab`
  through `WebGlPlanetBodies`). A tracing host never falls back to a sprite: while a map uploads the body is its flat
  dot. The CPU surface (the tests, the TUI, the PNGs) keeps the sprite of the same view, `PlanetaryRender.RenderRgba`,
  the reference the GPU is held to.
- **What is left on sprites:** the sky map's planet discs and the info panel's disc. They are A4's sky-frame chain (the
  view's north at the planet, mirrored views) and are rebuilt only on a size bucket or a time scrub, not per frame;
  moving them is a separate piece of work, not done here.

**Measured** (Surface Pro 11, Snapdragon X Elite X1E80100, Adreno X1-85, the Debug GUI from a scratch org root,
2880 x 1814, the orrery opened on the Sun, Play at 10 days a second, 15 s): before, average frame 11.3 ms; after,
4.6 ms (GPU frame 4.6 and 4.9 ms), 0 sprite builds. Parity on the device (`PlanetBodyGpuTests`, ten views: Saturn open
north and south, nearly edge-on, its dark face, the shadows at quadrature; a crescent Venus, the Moon, Jupiter's Red
Spot, Mars, the Sun): mean difference from the CPU render 0.004 of 255 at most, worst 1 except a shadow edge (16), at
most 3 channel values more than 4 off. A west longitude of the wrong sign, or rings without the globe's shadow, fail
nine of the ten (805 to 65,331 values off). `OrreryGpuTests`: forty played frames on `VkSkyMapTab` trace ten bodies a
frame, build 0 sprites and upload no map again; every disc is lit toward the Sun on the GPU's frame at the instant and
93 weeks on, framed on the Earth's orbit and at 12 AU (Jupiter and Saturn within 9 degrees).

#### A5: what is open

- **Not seen in the browser yet** (#1572, bench). The Vulkan GUI is seen (the bodies on the GPU, above); the WebGL page
  builds the same trace, but nobody has opened it there, nor timed a drag or Play in the browser.
- **The Earth is a flat disc** and **Play's speed is fixed** (#1573): the texture atlas has no Earth map, and Play runs
  at 10 days a second with nothing to change it.
- **The strip fits a narrow view** (#1584, fixed): at 1.5x on 900 px it needed about 1,020 px, the hint already squeezed
  to nothing, because the labelled buttons could only be Auto, which never shrinks. DIR.Lib 11.22's content-capped Star
  (`WStarUpToContent(min)`) is the fix: "Distances: auto", "Centre: <body>" and the time button are each one, direct
  children of the strip (`BuildTimeControls` hands its buttons back for that; the sky strip keeps them in one group,
  `TimeControlsGroup`), and the time text inside its button is one too. With room each sits at its content and the hint
  takes the slack; short, the hint gives way first, then the labels trim their end toward 3 ems (the time button 4).
  `TheStripFitsWithEveryButtonShowing` runs again: every button inside the view at 1.5x (before, `+10m` ended at 928 px
  of 900; now `+1d` ends at 888, the labels at 129 of 142, 129 of 133 and the time 97 of 231 px), and at 1.0x each label
  at least its measured content (94, 89 and 154 px, as before).

## Sequencing

A3, then A1 (which carries the live-magnitude fix), then A2. A3 is a text row over a detector that
already exists; A1 is a new per-body sampler plus a cache with two known traps; A2 has a design
question to settle before it is worth starting.

A4 can run beside them: its first step (the disc at its true size) needs only the distance VSOP87a already
gives, and its phases share A1's geometry. A5 comes after A4, whose discs it draws, and shares A1's heliocentric
geometry too.

## Related

- [comet-ephemeris.md](comet-ephemeris.md) -- where the sparkline, the selection path, the event
  detector and both cache rules were built. Read the "path and sparkline caches must hit regardless
  of sample count" section before adding a third cached curve.
- [skymap-time-scrub.md](skymap-time-scrub.md) -- the time offset every curve here has to agree
  with.
- The deferred comet `MagnitudeChartRenderer` (C2c in comet-ephemeris.md) was skipped on the grounds
  that the sky-map sparkline already covers the curve. If A1 and A2 produce a real chart, that
  decision is worth revisiting for comets at the same time rather than building a second one.
