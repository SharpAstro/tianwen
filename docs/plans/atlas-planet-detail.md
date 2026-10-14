# Atlas: planet detail (light curve, visibility curve, next opposition / GE, how a planet is drawn, the solar system in 3D)

**Status: PARTIAL (raised by the user 2026-08-22).** Tracked here rather than in the next release,
per the user's instruction: *"for Atlas lets just track it in the plan"*. Built 2026-10-14: finding 2's
live magnitude, A4's geometry layer (see "What is built" under A4) and A4's textured discs, phases and rings
("A4: the textured disc"). Open: A1's sparkline, A2, A3, A4's optional side-by-side strip (#1566), A4's textured-disc follow-ups (#1567), A5.

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

Issue: #1039.

A view that swaps the atlas's sky for the solar system seen from outside it, at the instant the atlas is
showing: the Sun at the centre, the planets on their orbits, and the selected body highlighted. A "gimmick" in
the user's word, but one that explains the sky beside it: the line from Earth to Venus, against the line from
the Sun, is why Venus is a crescent, and the angle between them at the planet is A4's phase angle.

- **The way in is a button in the object panel**, for a planet, the Sun, a comet or the Moon. It opens the view
  centred on that body. The panel already carries per-kind actions (`ObjectInfoPanel.PanelActions`: Goto, View
  in Planner, Pin, and Solve and Sync for the mount), so this is one more entry, shown only for a solar-system
  body (`CatalogIndex.IsSolarSystemObject`, which already covers comets).
- **Alt+S is a quiet shortcut** to the same view, in the shortcuts help and nowhere else, per the user. It must
  be a declared node shortcut (`.WithShortcut(InputKey.S, InputModifier.Alt)`): the atlas's layer toggles
  ignore modifiers (`SkyMapLayers.TryToggleByKey` in `SkyMapTab.HandleKey`), so an arm in the key switch would
  also toggle the Milky Way, while the router matches a painted node's shortcut before the tab's own keys.
  Firefox takes Alt+S for its History menu and macOS Option+S types a character; for a convenience with a
  button beside it, that is acceptable. Escape goes back to the sky with the selection kept.
- **Positions:** `VSOP87a.GetBody` already returns each planet's heliocentric rectangular coordinates, so no new
  ephemeris. An orbit is one sampled period (Mercury 88 days to Neptune 165 years), cached per body like the
  selection path. A comet's orbit comes from the elements the comet repository holds, which is also where its
  position in this view must come from, or the comet would sit off its own orbit.
- **The camera:** drag orbits it about the Sun, the wheel zooms, and a scale choice between true distances and a
  compressed radial scale (Neptune is 75 times Mercury's distance, so at true scale the inner planets are one
  dot). Bodies are drawn with A4's discs at an exaggerated, labelled size, since at true size none is visible.
- **Time:** the atlas's time controls drive it (`SkyMapState.TimeOffset`, the strip's steps and landmarks), so
  a scrub of a month moves the planets, and a play control animates it. One clock for both views, or the
  orrery and the sky disagree about the instant.
- **Drawing:** a small CPU perspective projection feeding the 2D primitives all three renderers already have
  (polylines for orbits, ellipse fills for bodies). No new shaders, so the web and `RgbaImageRenderer` (and
  therefore tests) get it for free. The view is a mode of the sky map tab rather than a tab of its own, as the
  FITS viewer's sky backdrop is a mode of the map.

**Tests:** Earth's heliocentric longitude is the Sun's geocentric longitude plus 180 degrees; the angle the view
draws at a planet between the Sun and Earth is A4's phase angle for the same instant; the panel shows the
button for a planet, the Sun, a comet and the Moon and not for a galaxy; Alt+S opens the view and leaves the
Milky Way layer as it was.

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
