# Atlas: planet detail (light curve, visibility curve, next opposition / GE, how a planet is drawn, the solar system in 3D)

**Status: PARTIAL (raised by the user 2026-08-22).** Tracked here rather than in the next release,
per the user's instruction: *"for Atlas lets just track it in the plan"*. Built 2026-10-14: finding 2's
live magnitude, A4's geometry layer (see "What is built" under A4) and A4's textured discs, phases and rings
("A4: the textured disc"). A5's solar system in 3D the same day ("A5. The solar system in 3D"). The Sun's textured,
limb-darkened disc and every texture's credit 2026-10-16 ("A4: the Sun", #1608). Open: A1's sparkline, A2, A3, A4's optional side-by-side strip (#1566), A4's textured-disc follow-ups (#1567), A5's follow-ups ("A5: what is open").

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

- **Textures.** ONE offline atlas, `planet-textures.png` (512 x 640, 400,303 bytes, `PlanetTextureAtlas`; 354,808
  bytes before the Sun, 512 x 512 and 301,799 bytes before the Earth), ten 256 x 128 equirectangular colour maps in `PlanetMap`'s convention (WEST
  longitude, column 0 at 0 degrees, falling to the right), poles filled: Mercury, Venus and Mars from Solar System
  Scope (CC BY 4.0), the Earth from NASA Earth Observatory's Blue Marble: Next Generation (July 2004, cloud-free,
  public domain, credited "NASA Earth Observatory" as its page asks; #1573, drawn only by the orrery), the Moon from
  NASA SVS's LROC map (public domain), Jupiter, Saturn, Uranus and Neptune from OPAL's 2025 colour composites, and the
  Sun in the tenth cell, generated ("A4: the Sun", #1608). A new
  atlas size makes `Decode` refuse a stale file whole (`PlanetTexturesTests.TheAtlasHoldsTheEarthAndAStaleAtlasIsRefused`),
  so a host with an old file draws flat discs, never a map from the wrong cell; an all-black cell is no map, so the
  nine-body atlas, the same size as the ten-body one, leaves the Sun with no map rather than a black one. Since #1617 only the
  orrery then draws it flat: the sky map draws its dot and the info panel's slot stays dark until the atlas loads. Baked by
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
- **The Earth's longitudes** (#1573). The Blue Marble is a plate carree centred on Greenwich, east to the right, read
  as the other centred maps are. Pinned by `PlanetTextureAtlasTests.TheEarthMapHasAfricaAtLongitudeZeroAndNorthUp`:
  the Sahara at 22 N 30 E is sand, the Atlantic at 22 N 30 W sea, Africa at 0 redder than the Pacific at 180, 22 S 8 E
  sea and Antarctica's ice brighter than the Sahara; the same checks fail on the map read mirrored, flipped or half a
  turn round. Its deep sea is near black in the source itself (a tenth of the cell sums under the hole threshold, the
  darkest 12 of 765), so it is not filled and the no-holes test holds the Earth only to never being pure black. Lit
  with Minnaert k 0.75, as Mars (`PlanetDiscSprite.MinnaertK`): a surface map under an atmosphere that scatters light
  back toward the limb.
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
  baked at 35 % opacity, as its label is. The Sun is drawn from its map by its limb-darkening law, never lit ("A4: the
  Sun").
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
  - the atlas's Uranus holds its edge row's colour over the unseen southern hemisphere.
  - (Done with #1608: the info panel draws the disc's credit under it; see "A4: the Sun".)

#### A4: the Sun

Built 2026-10-16 (#1608). The atlas's tenth cell, empty beside Neptune since the Earth, holds the Sun; every host draws it
from that map, self-luminous.

- **The map is generated, not observed.** A real map (SDO/HMI continuum would be the public-domain source) is one day's
  Sun, and its sunspots would be drawn on every date. At 256 x 128 an observation says nothing more anyway: a sample spans
  about 17,000 km by 17,000 km, some 170 granules, so the granulation's contrast (of order 10 to 15 % at a granule's scale)
  averages to about a percent. `tools/bake-planet-textures` makes it (`QuietSun`): a 5772 K black body (IAU 2015 B3) through
  the CIE 1931 functions (Wyman, Sloan and Shirley's 2013 fit) into sRGB, 1.0000 0.9449 0.9171, times per sample 1 plus a
  Gaussian of 0.12 over the root of the granules it holds, seeded. Credited "Generated quiet photosphere, 5772 K (TianWen)".
  The other nine cells re-bake bit for bit (compared pixel by pixel). Pinned by
  `PlanetTextureAtlasTests.TheSunFillsTheTenthCellWithAQuietPhotosphere` (no sample beyond 10 % of the mean: no spot).
- **Limb darkening, no lighting.** `PlanetaryRender.LimbDarkening(body)` is the linear law I(mu) = 1 - u (1 - mu), u = 0.6
  (`SolarLimbDarkening`), for the Sun and 0 for every lit body. The CPU reference (`Scene`) never asks a self-luminous body
  whether it is lit, so it has no terminator from any direction (the ephemeris gives the Sun no sub-solar point, NaN) and
  no Minnaert's law; the sprite's old trick, lighting the Sun at full phase, which darkened its limb as the square root of
  mu (0.548 at mu 0.3 against the law's 0.58), is gone. The trace takes u in the block's `tint.w`, in `planet_body.frag`
  and its WebGL copy alike. One u for the three channels: the limb's reddening is left out.
- **Where it shows.** The sky map's Sun disc at its true size and the info panel's disc (the two guards that kept it
  flat are gone), and the orrery's Sun. Night is its luminance in the palette's tint, as every body's.
- **Every texture is credited.** NOTICE lists every atlas source (Solar System Scope under CC BY 4.0 with its licence
  link, which REQUIRES the attribution wherever the maps ship; the Blue Marble; the SVS Moon; OPAL with its requested
  acknowledgement; the generated Sun), and the info panel draws the disc's credit on a row under the slot
  (`PanelDisplayOptions.DiscCredit`, reserved in `DesignDiscSectionHeight` whether or not the credit has arrived, so the panel
  does not move when the texture lands): the #1567 item.

**Tests:** `PlanetDiscRenderTests` (the law at mu 0.9, 0.6, 0.3 and 0.15 within 0.01; no terminator from the ephemeris or
a sub-solar point anywhere; the Night Sun no blue and its limb the law's), `PlanetBodyGpuTests` (the textured Sun on the
Adreno X1-85: worst 1 of 255, mean 0.000, none more than 4 off; its limb the law's; Night no blue for Saturn and the Sun),
`PlanetTexturesTests` (the nine-body atlas gives the Sun no map), `SkyMapPlanetDiscTests.TheSunsDiscIsItsTexturedPhotosphere`
(near white, mottled, the limb the law's, `sun-disc.png`), `TheDiscsCreditIsDrawnUnderIt` (every body the map shows),
`ObjectInfoPanelTests.EveryAtlasBodysCreditIsDrawnUnderItsDisc`, `SkyMapPlanetDiscTests.TheSunsSpriteIsBuiltOnceForAStillView` (its
NaN sub-solar point keys one sprite, not one a frame), `OrreryTests.TheOrrerysSunIsDrawnFromItsMap`, and the browser's `OrrerySunTests`
(E2E, headless Edge): Alt+S from the atlas, the atlas response a PNG of 400,303 bytes, two frames traced the textured Sun 68 s after
the page opened, and the capture's largest near-white blob a filled round disc (432 px in a 24 x 23 px box, centre 254, 241, 234).

#### A4: the atlas the dev server never sent

Found by that E2E (#1608): `dotnet run` of `TianWen.UI.Web` answered `planet-textures.png` with 200 and NO BODY, on origin/main as on
this branch, so the browser's planets were flat (or dots) in every local run since #1037 and nobody had seen otherwise (#1572). The
atlas is a `Content` item linked into `wwwroot` from `TianWen.UI.Abstractions/Resources`; the static-asset build gave it the PROJECT's
`wwwroot` as its content root, the dev server's runtime manifest so mapped it to `wwwroot/planet-textures.png`, which does not exist,
and the server sent the mapped-but-missing file as an empty 200 (the build manifest, with the real file as its identity and a
Content-Length of 400,303, looked right). The item now states its own `ContentRoot`, the Resources folder, and the dev server sends the
committed file byte for byte. The page logs each step (`[tianwen-web] planet atlas fetched: N bytes`, then `adopted`), and the E2E
fails on a body that is not a PNG. A published build was never affected the same way (publish copies by the asset's identity), but is
not re-checked here.

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
  period of the same series (`SolarSystemOrbits.SampleOrbit`, 241 points, closed), so a planet sits on its own. The
  comets are the app's set ("A5: the comets", below, #1611), among them the one the view was OPENED on (`OrreryState.Comet`, kept
  apart from the highlight, which a click moves), each from the elements the comet repository holds (`ICometRepository.TryGet`,
  apparition upgrade included): its place from `CometEphemeris.TryGetHeliocentricEcliptic` and its orbit from
  `CometEphemeris.SampleOrbit` (361 points evenly in true anomaly, the whole ellipse, an open orbit to 40 AU each
  side), both through one perifocal frame (`PerifocalAxes`), so it sits on its drawn orbit.
- **The cache** (`OrreryScene`): the positions per MINUTE of the atlas's clock, the planets' orbits per CENTURY of it,
  built on the thread pool (the Task the hand-off, a landing asks for a frame), and each comet's orbit per element set, by
  its index; the positions are keyed on the comet set's version too (an apparition upgrade keeps the index and changes the
  elements, and must move the comet at once). Orbits are stored in true AU and scaled once per orbit and scaling
  (`OrreryScene.ScaledOrbit`, into the same array), so a drag or Play only projects and switching the scale builds no orbit. A
  drag changes no key, so it builds nothing (`OrbitBuilds`, `CometOrbitBuilds`, `PositionBuilds`, `ScaledOrbitBuilds`).
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
  (not in `Supports`, which the sky map's discs and the stacker gate on), and since #1573 its Blue Marble day map from the atlas,
  the palette's Info only while the map loads. Every colour of the view comes from `GuiTheme.Palette` (space, the Earth, a comet, the strip,
  the shared time controls taking the caller's fill and ink), so Night spends no blue.
  A comet is a dot with a fan of a tail away from the Sun, never a planet's neighbour in the disc cap ("A5: the comets"). A
  body still building its sprite is a flat dot.
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
  landmarks drop-up, Now) and the arrow keys and 0 work as on the sky; Play runs the offset at the speed in force
  (`OrreryState.DaysPerSecond`), a frame stepping at most 100 ms of the wall clock (`MaxPlayStep`: ten minutes on another
  tab used to jump sixteen years), so the sky shows the same instant when the view goes back.
- **Play's speed** (#1573, 2026-10-16). A label button beside Play names the speed in force ("1 week/s", the up-arrow mark
  saying a press opens a menu) and opens a drop-up of `OrreryState.PlaySpeeds`, TheSkyLive's fifteen: 1 second = 6 hours,
  12 hours, 1, 2 and 4 days, 1 and 2 weeks, 1, 2, 4 and 6 months, 1, 2, 5 and 10 years (a month a twelfth of a Julian
  year). It is the time menu's own drop-up (`RenderDropUp`, which the landmarks now go through too), a second caller, not a
  copy. A pick sets the rate and nothing else, so Play keeps running across it. The default is **1 week**: the entry nearest
  the ten days a second Play ran at before (3 days off against 2 weeks' 4; on the ladder's ratio scale the two all but tie,
  and the slower keeps the Moon readable, a lunar month in four seconds rather than two). The button is a content-capped
  Star like the strip's other labels, sized by `PlaySpeedWidthSample` ("00 months/s": a sample exactly as wide as "4
  months/s" still trimmed it at 1x). **Play stops at `OrreryState.PlayEnd`**, 4000-01-01 12:00 UTC, J2000 plus 2000 years:
  VSOP87 keeps its stated precision 2000 years either side of J2000 for Jupiter and Saturn, its narrowest (4000 for the
  inner planets, 6000 for Uranus and Neptune), and at ten years a second the clock would otherwise leave DateTimeOffset's
  range (year 9999) in about thirteen minutes (from 2026, 7,973 years in 797 s), which threw in the render path. The bound is checked in `AdvancePlay` against
  the offset BEFORE it is added to the base (`ResolveBaseTime`, split out of `ResolveViewingTime` for it), so an offset
  scrubbed past the end is brought back by the first played frame. `SkyMapState.FormatOffset` reads Julian years
  ("+1974y", "+5y 2w") so a long play keeps the strip's time short. `OrreryTests.EachSpeedRunsItsSpanInAPlayedSecond` (each
  of the fifteen picked through the router, then ten 100 ms frames move the clock by exactly that span),
  `PlayAtTenYearsASecondStopsAtTheEndOfTheEphemeris`, `APlayedOffsetReadsInYears`; in the browser,
  `OrreryPlaySpeedTests.APickedSpeedIsTheRatePlayRunsAt` (Alt+S, the drop-up, "1 second = 2 years", Play). Playing is a STANDING reason to draw
  (`SkyMapState.NeedsContinuousFrames`), asked by the GUI's redraw check: the one-shot `NeedsRedraw` a played frame
  sets is cleared by the GUI after the frame, which held Play to about a frame a second. The browser asks for each
  frame as a coalesced repaint.
- **Click** a body (a press that did not move), a comet's head among them, to put the highlight on it; the atlas's selection
  and the comet the view was opened on are left as they are.
- **Centre.** With a body other than the Sun in focus the strip has one toggle naming what the camera looks at
  (`Centre: Sun` / `Centre: Jupiter`, `OrreryState.CentredOnFocus`): centred, the camera looks at the body's drawn place
  (the Earth's for the Moon) and follows it as time runs; a click moves it with the highlight, a click on the Sun puts it
  back. `OrreryCamera` only gains a look-at point, so its rotation and the backdrop's stars are unchanged.

#### A5: the comets

Issue: #1611 (2026-10-16), with its amendments A1 (every orbit in its body's colour) and A2 (thirty comets were "super
crowded": a magnitude filter, comet orbits a dim grey), after TheSkyLive's orrery: coloured orbits, comets as a head with a
pale fan of a tail away from the Sun, labelled.

- **Which comets: the 30 brightest at the VIEWING INSTANT, ranked per MONTH** (`OrreryScene.CometSetSize`, `RankAt`). Ranked
  first at PEAK (`SkyMapState.PeakMagnitude`), the thirty were great comets of the last century, far out and tailless today,
  while TheSkyLive's orrery shows the comets of NOW: 10P/Tempel, 161P/Hartley-IRAS, 220P/McNaught and P/2026 R2 (Leonard) all
  ranked 9 to 12 magnitudes past that cutoff. The rule now:
  - **The pool is every comet with a magnitude model and an index** (1,995 of 4,080 in the catalogue cached on 2026-10-16),
    NOT the sky's candidates (`SkyMapState.IsCometCandidate`, a peak estimate of 18.0 or brighter, which stays the sky's and its
    per-frame sweep's alone): Leonard's peak estimate is 18.1, so the candidacy left it out while it was magnitude 16, the
    seventeenth brightest in the month.
  - **The score** is each comet's best predicted magnitude over four instants spread over the view's month
    (`RankSamplesPerMonth`), through the sky's own calls (one `CometEphemeris.TryGetEarthState` per instant,
    `TryGetEquatorialJ2000WithMagnitude` per comet), so a comet bright for part of the month counts. A member of the set is
    scored a magnitude brighter than it is (`MembershipHysteresisMag`), so the edge does not flicker month to month.
  - **The month is the bucket** (`MonthBucket`): a ranking holds while the clock stays in its month, so two instants of a month
    give one ranking (`AMonthIsRankedOnce`). A fixed set within the month, so nothing blinks under Play the way the sky's
    magnitude limit at the instant would.
  - **What the model cannot be trusted for is not ranked** (in `RankAt` only, never `IsCometCandidate`, which has no instant): a
    D/ (defunct or lost) designation, and an element set more than `MaxRevolutionsSinceEpoch` = **5** revolutions past its
    epoch at the month's middle. Two-body propagation drifts in PHASE from an old set, and the magnitude law then models a
    perihelion that is not happening. Measured over the catalogue at 2026-10-16: unfiltered, C/2020 P4-B ranked first at 5.8, its
    set 5.97 revolutions old, at r = 0.095 AU near a modelled perihelion about 2026-10-27 (the month's fourth sample; magnitude
    13.6 on the 16th), six magnitudes ahead of the next; 5D/Brorsen (lost since 1879, 27 revolutions) ranked second at 6.5, and
    3D/Biela, D/1978 R1 and 34D/Gale were in the thirty. Every other set among the 45 brightest was at most 4.1 revolutions old
    (the known periodics 0.4 to 2.9), so five keeps those and drops the fragments decades out (79P-A and B, 8.6 and 8.7). An open
    orbit has no period and is kept. The sky still draws such comets from the same stale sets: #1630.
  - **No comet of the thirty came within 0.05 AU of the Earth** at its best sample (the closest, Leonard, at 0.32 AU), so the
    score needs no floor on the geocentric distance; the magnitude law is the sky's, unchanged.
  - **The thirty at 2026-10-16**, best magnitude of the month: 332P/Ikeya-Murakami 11.9, 260P/McNaught 13.4, C/2024 T5 (ATLAS)
    13.5, 16P/Brooks 13.5, **10P/Tempel 13.9 (5th)**, C/2026 A2 (Bok) 14.0, **161P/Hartley-IRAS 14.6 (7th)**, 128P-A/Shoemaker-Holt
    15.0, C/2024 J3 (ATLAS) 15.0, 78P/Gehrels 15.2, C/2024 R4 (PANSTARRS) 15.4, P/2010 H2 (Vales) 15.6, C/2025 V2 (Rankin) 15.8,
    C/2023 R1 (PANSTARRS) 15.9, C/2015 D1 (SOHO) 16.0, **P/2026 R2 (Leonard) 16.0 (16th)**, **220P/McNaught 16.0 (17th)**,
    C/2014 UN271 (Bernardinelli-Bernstein) 16.1, C/2025 Y1 (ATLAS) 16.1, 88P/Howell 16.2, 169P/NEAT 16.3, 11P/Tempel-Swift-LINEAR
    16.3, C/2025 M2 (PANSTARRS) 16.4, 123P/West-Hartley 16.7, C/2022 QE78 (ATLAS) 16.7, C/2022 N2 (PANSTARRS) 16.9, 2P/Encke 16.9,
    84P/Giclas 16.9, P/1996 R2 (Lagerkvist) 17.0, 112P/Urata-Niijima 17.1. The ranking reads the BULK element sets (as the sky's
    sweep does); the drawing reads each comet's through `ICometRepository.TryGet`, apparition upgrade included.
  - **Off the render thread.** A month is ranked on the pool (`Task.Run`, the Task the hand-off, a landing asks for a frame),
    the set already drawn kept until the ranking lands; at most one is in flight, so fast Play, which crosses a month nearly
    every frame, ranks the month the clock is in when the last one lands. Measured on the Snapdragon X Elite, Debug, the
    catalogue of 1,995: a ranking takes 13 to 16 ms on the pool (median 14.6); on the render thread a played frame crossing into
    a new month costs 0.69 ms (median of 12) against 0.57 ms for a frame a minute on, and the frame that adopts the ranking 0.7
    ms (2.3 at most), one to four newcomers' orbits built and nothing else.
  - **A cooldown between rankings** (R1 Q2 of PR #1631): the next ranking starts only once `OrreryScene.CooldownAfter` the last
    one's duration has passed since it landed, `RankCooldownFactor` (4) times that duration plus `RankCooldownFloor` (100 ms, one
    played step, `MaxPlayStep`), on the scene's clock (an `ITimeProvider` given to `OrreryScene`, the system's by default, a fake one
    in the tests). So ranking takes at most about a fifth of the time on any host, and the months between are skipped: the set is the
    month's the clock is in when the next ranking starts. A view held still inside a cooldown asks for one frame when it ends
    (`ITimeProvider.SleepAsync`), so the month it moved to is still ranked. The cause was the browser: its single-threaded
    WebAssembly build runs `Task.Run` on the page's one thread between frames, and fast Play crosses a month nearly every frame.
    Measured in headless Edge on the Snapdragon X Elite (X1E80100), the interpreted Lightweight build, a generated catalogue of
    SBDB's size (4,080 comets, 1,995 with a model; `OrreryPlaySpeedTests.PlayAtTenYearsASecondRanksTheCometsBoundedly`), Play at
    ten years a second: before the cooldown, 51 rankings started in 57 frames over 14.9 s (3.8 frames a second), each ranking 55 to
    86 ms against a frame of about 74 ms; after it, 12 or 13 rankings over 8 to 23 s, ranking 3 to 8 % of the time, the frame 66 to
    78 ms, 2.7 to 8.1 frames a second (the frame rate swings with the machine's load from run to run; the rankings do not). What
    is left is the interpreted frame itself. `FastPlayRanksOnlyAfterTheCooldown` (120 frames a month apart: 15 rankings, 133 ms
    apart, where there were 120) and `SlowPlayRanksEveryMonth` (a week a second still ranks each month) pin it.
  - **Always added**, whatever the ranking and the filter: the comet the view was opened on, the focused one (a click) and the
    PINNED ones, however faint or stale (`OrreryCometChoice`; `TheOpenedFocusedAndPinnedAreAlwaysIn`,
    `ADefunctOrLongStaleCometIsNotRanked`). Tests: `TheSetIsTheBrightestAtTheViewingInstant` (a faint-peak comet at
    perihelion in, ten years on out; a great comet fifty years past perihelion out, which ranking at peak put first),
    `ACometPastTheSkysCandidacyButBrightNowIsIn`, `AMemberJustPastTheCutoffStays`.
  - **A ranking that throws** (#1631 R2) is logged through the tab's logger, its exception read (so observed), and its month,
    repository and count of comets are not ranked again until one of them moves: before, a faulted ranking adopted nothing,
    its cooldown was the 100 ms floor (no duration), and it was started again ten times a second, unseen. The set already drawn
    stays. `OrreryScene.Rank` is the seam a test makes throw: `ARankingThatThrowsIsLoggedAndNotRetriedEveryFrame` (ten frames a
    second apart in one month: 1 attempt and one warning, where there were 10 and none; the next month is tried afresh). The
    held view's wake has its test too, `AViewHeldInTheCooldownIsWokenWhenItEnds` (a scene with a frame request on a pumped fake
    clock: held inside the cooldown nothing is ranked and no frame asked for; when it ends one frame is, and it ranks the month;
    seen failing, a 30 s timeout, with the wake taken out).
- **The comet filter** (amendment A2 of #1611): of the ranked set, the comets whose MONTH-BEST magnitude (the ranking's own
  score, carried through it beside each index, so Play inside a month never flickers one at the limit) is at or brighter
  than the step's limit: **Naked eye 6, Binoculars 9, Small telescope 12 (the default), All** (the whole ranked set,
  `CometSetSize` 30, which stays as the performance cap). `OrreryCometFilter.Steps`; the step is `OrreryState.CometFilter`,
  kept for the session as the view's other settings are, never saved. It filters the SET, so orbits, heads, tails, labels and
  hits all follow; the opened, focused and pinned comets bypass it as they bypass the ranking. The real catalogue at
  2026-10-16 has no comet brighter than 11.9 (332P/Ikeya-Murakami, above), so the default draws one and Naked eye or
  Binoculars none but what is opened, focused or pinned. **On the strip** it is one label button after the speed, "Comets
  ≤12 ▲" (or "All comets"), its press opening the four steps as a drop-up (the shared `RenderDropUp`, as the speeds'), sized
  by the widest of its forms so a pick never moves what follows. Where the strip is short it takes its compact form ("≤12",
  no mark, half the padding) BEFORE the speed takes its own (`ReadsWhole`, the speed's escape hatch generalised: the long
  forms first, then the filter compact, then both). To fit it `OrreryLabelMinEm` went from 3 to 2.25 and `TimeLabelWeight`
  from 4 to 6, the speed's compact form to half padding as well. Measured on 900 px: at 1x the filter shows "Comets ≤12" in
  60 px (Consolas) and 69 (DejaVu Sans), the long form; at 1.5x it is compact, "≤12" in 21 to 32 px, the button 34 px
  (262 to 296) in Consolas and 41 (259 to 300) in DejaVu Sans, the time keeping 85 px past its date and time in Consolas
  and 38 (2.5 ems) in DejaVu Sans, `+1d` still ending at 888. Tests: `EachFilterStepKeepsTheCometsBrightEnoughForIt`
  (month-bests 1.5 to 17.6: 2, 4, 6 and 30 comets), `TheOpenedFocusedAndPinnedBypassTheFilter`,
  `APlayedMonthDoesNotFlickerAtTheLimit` (a comet 6.7 to 12.3 over the month stays drawn under Binoculars),
  `TheStripsFilterPicksAStepAndTheViewDrawsOnlyIt` (through the router, at 1x and 1.5x: 2, 4, 6 and 30 heads),
  `TheCometFilterAlwaysNamesItsStep` (1x, 1.25x, 1.5x, both faces), `TheStripFitsWithEveryButtonShowing` re-measured.
- **The comet layer** (`SkyMapState.ShowComets`, the atlas's comet toggle) gates the ranked set (and so the filter); the opened, focused and pinned
  comets are drawn with it off, as a pin shows a comet on the sky with its layer off (`TheCometLayerOffDrawsNone`: the set, its
  tails, orbits and labels are gone, the opened comet stays).
- **The cache.** The set is read again only when its KEY moves: the repository (by reference), its `ICometRepository.Version`
  (new: raised by a load, a refresh and every apparition upgrade), the ranking adopted, or the choice; never by comparing thirty
  element sets on every frame. Its orbits are kept by index (rebuilt only for an index whose elements changed, so a new month
  builds only its newcomers'), the positions per minute of
  the clock and per set version, all of them in one sweep. A drag builds nothing and scales no orbit again; a played frame
  inside the same minute builds nothing; five years on, the positions are computed once and no orbit is built or scaled
  (`OrreryCometTests.ADragOrAPlayedMinuteBuildsNothing`).
- **The current apparition** is asked for (`RequestCurrentApparition`) only for a comet DRAWN in the view whose element set is a
  revolution or more old at the instant (`OrreryBody.ElementsStale`), as the sky asks for its stale markers.
- **Heads are dots** (`OrreryCometRadius`, 2.5 design px), never discs, and a comet is never a planet's nearest neighbour in the
  disc cap: a comet a degree and a half along Mars's orbit from it shrank Mars's disc from 5 to 2.19 px; the planets' radii are
  now the same with comets beside them or without (`ThePlanetsDiscsAreTheSameSizeWithAndWithoutComets`, which failed so with the
  exclusion taken out).
- **The tail** points AWAY from the Sun on screen: its direction is the projection of a point 5 % further out on the comet's
  radial line (the scaled frame keeps every direction from the Sun), so it lies on the line from the Sun's drawn place through
  the head (0.03 degrees off in the test). **Its length follows the comet's activity** as the sunlight it gets, `1/r^2`, less its
  value at 3 AU, where water ice stops subliming, so there is no tail from 3 AU out, taken through a SQUARE ROOT (amendment A2
  of #1611: the linear share gave 22 px at 1 AU and 8 at 1.5, and the real catalogue's tails did not read):
  `L(r) = max(48 px * sqrt(min(1, a(r) / a(0.7 AU))), floor(r))`, `a(r) = max(0, 1/r^2 - 1/9)`, with a floor of 14 design px
  (`OrreryTailMinLength`) inside 2.5 AU (`OrreryTailFloorAu`) falling linearly to nothing at 3 AU, so no tail pops in or out:
  48 px at 0.7 AU or closer, 32.6 at 1, 19.9 at 1.5, 14.0 at 2, 7.0 at 2.75 (`OrreryTailLength`). It is then foreshortened by
  the sine of the angle between the radial line and the ray from the camera, to no less than 0.4 of its length
  (`OrreryTailMinForeshortening`), so a tail pointing at the camera is short, never lost. It is drawn first, under every head
  and disc, as three nested wedges of the comet's colour at alphas 0x70, 0x90 and 0xC0 (half those did not read), a brighter
  core near the head (`DrawTriangles`, which every host fills: Vulkan, WebGL, the CPU surface).
- **Labels**: placed as the planets' are, under or over the head where clear, else left out. **Every label lies inside the
  view**, for every body (amendment A2 of #1611: the real catalogue's captures cut names at the edges): a candidate box is
  shifted sideways into the content rect before the clear test (trimmed if wider), a box outside it above or below is no
  candidate, so the other side is tried, and the Moon's outward loop and the forced fallback for the focus and the Earth are
  clamped too; a body drawn outside the view is unlabelled (`EveryLabelLiesInsideTheView`, six views at 1x and 1.5x, 3 to 14
  labels each, none outside). The planets (the Sun and the Moon
  with them) outrank every comet; of the comets, the focused one and the one the view was opened on come first
  (`CometsAreLabelledAfterThePlanetsAndNoLabelOverprints`: 18 of 30 comet labels placed beside 7 planets', none overprinting).
- **Click and Centre**: a press that did not move on a head puts the highlight on the comet; the strip then offers
  `Centre: <comet>`, and centred the camera follows it as time runs (`AClickSelectsACometAndCentreFollowsIt`).
- **Orbit colours (A1).** Every orbit is drawn in its body's colour (`OrreryOrbitColour`): a planet's is its own
  (`OrreryFlat`: `SkyMapRenderer.GetPlanetColor`, read and not changed, the Earth's the palette's Info) taken a fifth toward the
  dim text, at the dim alpha 0x70; a comet's is a DIM GREY from the palette (`CometOrbitColour`, the dim text halfway to the
  body text, a neutral in the light and dark palettes) at 0x40, always weaker than a planet's (amendment A2 of #1611: a violet
  at 0x90 drew every comet orbit stronger than the planets' at 0x70). The selected planet's stays the accent; the focused comet's (else
  the one the view was opened on) is drawn LAST, over every other orbit, in the accent at full strength and a pixel thicker
  (`OrbitsCarryTheirBodysColour`).
  Under Night every orbit is the palette's tint (`OrreryNightTests`: no blue in any pixel with thirty-one comets' orbits and
  tails drawn, and no orbit's colour with any).
- **Cost.** Thirty comets' 361-point orbits are 10,830 samples a frame. On a GPU host a line costs per SEGMENT (each is a box of
  its own, `VkRenderer.DrawPolyline`), and drawn whole they made the frame's CPU time several times the planets' alone. So the
  orbits are drawn SIMPLIFIED on screen (`OrbitTolerance`, 0.35 design px: the samples between two vertices are skipped while
  each lies within that of the segment, and a segment wholly off the view breaks the line) and scaled once per scaling
  (`ScaledOrbit`). Measured on the Snapdragon X Elite (X1E80100, Adreno X1-85), Debug, 900 x 600, thirty synthetic comets
  (`OrreryCometTests.DemoComets`), medians of 20 frames: on the GPU host (`OrreryGpuTests.TheCometsDrawOnTheGpuHost`) the orbits
  went from 5,562 segments in 4.2 ms to 1,171 segments in 1.9 ms, the frame from 8.2 ms to between 1.9 and 3.2 ms (runs vary
  on this machine) against 0.4 to 0.8 ms with the layer off; on the CPU surface (`TheOrreryDrawsOnTheCpuSurface`, where the
  raster is the cost) 5.65 and 5.27 ms (auto, true scale) against 5.56 and 6.60 ms before the comets came. In the browser
  (`OrreryPlaySpeedTests.TheCometsAreDrawnWithTheirOrbitsAndTails`, headless Edge, eight comets served as `comets-sbdb.json`)
  a frame with them drew in 33 ms on the interpreted Lightweight build.

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
- **What the pass leaves behind it, on Vulkan**: the sky pipeline clips the viewport and scissor to the map rect, and
  `VkSkyMapTab.RestoreFullTarget` puts back the whole target, `FullTargetExtent`: the SWAPCHAIN's extent when there is
  one, the renderer's size (`renderer.Width` x `Height`, as `VkPlanetBodies` sizes its own) only when there is none. Both
  cases exist because a window must keep what it drew with: on the published 7.64 renderer a window maximised to the work
  area (#1574) has a renderer of 1920 x 1233 over a 1920 x 1200 swapchain, and the renderer's size would shift everything
  after a sky pass by up to 33 px (`VkSkyMapTabTargetTests`). Its three copies sized it by the swapchain alone, which an
  offscreen frame does not have: 0 x 0. So
  on the headless Vulkan host every 2D primitive after the backdrop fell into a zero viewport until the planet bodies set
  their own, and the orbits (drawn before the bodies) vanished, while the bodies, the labels and the strip still drew.
  Found by the comets leg (PR #1631): 229 of 1,625 orbit samples lit with the backdrop on (the stars along the paths)
  against 1,473 with it off, the same at the #1584 commit ("fix(atlas): the orrery strip's time keeps its date and time on Linux's glyphs
  too (#1584)"), whose real window drew every orbit over the stars, so a window was
  never affected; not the sky pass's pipelines, push constants or descriptor sets (skipping its draws outright left the
  orbits as missing). After: 1,396 of 1,625 (`OrreryGpuTests.TheOrbitsAreDrawnOverTheBackdrop`).

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
  (`PlanetDiscSprite.MinnaertK`) or, the self-luminous Sun, drawn by its limb-darkening law (`tint.w`, "A4: the Sun"), reads the colour map at its planetographic latitude and west longitude (texelFetch,
  interpolated as `PlanetMap.Sample` does), and with Saturn crosses the ring plane in depth order, the globe's shadow on
  the rings and theirs on the globe, the unlit face glowing with the light the rings pass through them ("The rings' two
  faces", below), coverage as straight alpha; Night draws the luminance in the
  palette's tint. The text between the two "The trace" markers is the SAME in both files
  (`PlanetBodyGpuTests.TheWebGlTraceIsTheVulkanTrace`); each host states only its block, sampler and pixel centre
  (`screenPixel`: WebGL's framebuffer is y up). A varying interpolated across the quad instead of `gl_FragCoord` put up
  to 77 more values more than 4 off the CPU on the Adreno.
- **Inputs:** one block of seven vec4s (`PlanetBodyMap.WriteBlock`: Vulkan's push constants; in WebGL ONE instance of seven
  vec4 attributes in a buffer of the body's own, `PlanetBodyInstances`, since WebGl.Renderer sets a uniform block at once
  but replays the draws at Present, which drew every body with the last body's block, PR #1589 review round 1): the
  disc's centre and radius, the viewport, the axis ratio, k, the view's right, toward and Sun vectors, the map's mean
  albedo per plane (a ring's level is stated against it), the tint and a self-luminous body's limb darkening. The ring table is `SaturnRings.Main`'s, written
  into the shader and held to it by `TheShadersRingTableIsSaturnRingsMain`. Colour maps go up ONCE per body and
  `PlanetTextures.Version` (`PlanetBodyMap.FloatTexels`: float RGBA on Vulkan, exact; `Rgba8` as a PNG on WebGL, which
  takes a texture only from a URL); a flat body (one whose map has not loaded) is a 2 x 2 map of its colour.
  The Earth reads its day map since #1573 with no shader change, as any other map; the palette's Info is its colour only
  while the atlas loads.
- **The seam:** `SkyMapTab.TracesPlanetBodies` / `DrawPlanetBody` (`VkSkyMapTab` through `VkPlanetBodies`, `WebSkyMapTab`
  through `WebGlPlanetBodies`). A tracing host never falls back to a sprite: while a map uploads the body is its flat
  dot. The CPU surface (the tests, the TUI, the PNGs) keeps the sprite of the same view, `PlanetaryRender.RenderRgba`,
  the reference the GPU is held to.
- **What is left on sprites:** the sky map's planet discs and the info panel's disc. They are A4's sky-frame chain (the
  view's north at the planet, mirrored views) and are rebuilt only on a size bucket or a time scrub, not per frame;
  moving them is a separate piece of work, not done here.

**Measured** (Surface Pro 11, Snapdragon X Elite X1E80100, Adreno X1-85, the Debug GUI from a scratch org root,
2880 x 1814, the orrery opened on the Sun, Play at 10 days a second, 15 s): before, average frame 11.3 ms; after,
4.6 ms (GPU frame 4.6 and 4.9 ms), 0 sprite builds. Parity on the device (`PlanetBodyGpuTests`, eleven views: Saturn open
north and south, nearly edge-on, its dark face, the shadows at quadrature; a crescent Venus, the Moon, Jupiter's Red
Spot, Mars, the Sun; the Earth over Africa since #1573, worst 1 of 255, none more than 4 off): mean difference from
the CPU render 0.004 of 255 at most, worst 1 except a shadow edge (16), at
most 3 channel values more than 4 off. A west longitude of the wrong sign, or rings without the globe's shadow, fail
nine of the ten (805 to 65,331 values off). With the Earth the wrong sign fails ten of the eleven, every view with a map
(the Sun had none then; it has its generated map since #1608): measured on the CPU reference with the map read at minus its west longitude, 2026-10-16, the Earth
84,762 values more than 4 off and the least of the others 98 (Saturn at quadrature), against the test's 12. `OrreryGpuTests`: forty played frames on `VkSkyMapTab` trace ten bodies a
frame, build 0 sprites and upload no map again; every disc is lit toward the Sun on the GPU's frame at the instant and
93 weeks on, framed on the Earth's orbit and at 12 AU (Jupiter and Saturn within 9 degrees).

#### A5: the rings' two faces

Built 2026-10-15, after the controller saw the orrery's rings dark from a camera about 13 degrees above the ecliptic and doubted it.
**The lit face was right, and so were the dark rings.** The Sun crossed the ring plane in May 2025, so it lights the SOUTH face
now; Saturn's pole leans 28 degrees from the ecliptic's (ecliptic latitude 61.9), so a camera 13 degrees above the ecliptic is on
the north side from most directions. `SaturnRingFaceTests` holds it: from the Earth, B and B' (planetocentric) are JPL Horizons' to
0.1 degree, +21.69 and +21.69 on 2020-07-20 (north face lit and seen) and -7.15 and -7.83 on 2026-10-13 (south face lit and seen);
for the orrery's camera at 13 and 45 degrees above and below the ecliptic, from four sides, B is the camera's own ray against the
IAU pole (WGCCRE 2015) to 0.1 degree and B' Horizons', and the face seen lit exactly when the two share a sign. The controller's view
(azimuth 90, 13 degrees up, Saturn and Neptune left of the Sun and Uranus below) has B +37.8 and B' -7.9 on 2026-10-15: the unlit
north face. A flipped Sun direction in the orrery fails both dates.

**What was missing was the unlit face's own light.** It was radiance 0; a real ring's unlit face glows with the sunlight scattered
through it. `SaturnRing.UnlitShare` is single scattering in a slab (Chandrasekhar 1960, *Radiative Transfer*; the form Cuzzi et
al. 1984 and Dones, Cuzzi and Showalter 1993, Icarus 105, 184, use for the rings): with mu = |sin B| and mu0 = |sin B'| the
diffusely transmitted intensity is wP/4 mu0/(mu0 - mu) (exp(-tau/mu0) - exp(-tau/mu)), tending to wP/4 (tau/mu) exp(-tau/mu) at mu =
mu0, and the reflected one wP/4 mu0/(mu0 + mu) (1 - exp(-tau (1/mu0 + 1/mu))). The albedo w and phase function P are unknown here and
taken isotropic, so they cancel in the ratio and the lit level carries them: the unlit face draws the lit level times transmitted
over reflected, and nothing in the globe's shadow. From 25 degrees north with the Sun 8 south: 0.97 for C and the Cassini division,
0.56 for A, 0.017 for B. Measured (`PlanetBodyGpuTests.TheDarkFaceGlowsThroughTheThinRings`, the rings outside the globe and its
shadow, each ring's median of 255): C 21, Cassini 17, A 60, B 3 on the CPU reference and the Adreno X1-85 alike (GPU against CPU worst
1, mean 0.007); before, all 0. In absolute terms the A ring is the brightest unlit ring, since its lit level (0.6) is five times the
C ring's. The ten parity views and Night's no-blue still pass, and `TheWebGlTraceIsTheVulkanTrace` holds the WebGL copy to it.

#### A5: what is open

- **The orbits over the backdrop on the headless Vulkan host** (found in PR #1631, fixed): the offscreen frame's zero
  viewport after the sky pass ("A5: the backdrop", what the pass leaves behind it). Nothing of it is left.

- **The comets (#1611, done but for a look in a real browser)**: the set (ranked at the viewing instant), tails, labels,
  clicks and orbit colours are built ("A5: the comets"), and amendment A2 with them: the magnitude filter (Small telescope
  by default), grey comet orbits, tails that read, every label inside the view, and #1631 R2's two notes (a failed ranking
  logged and not retried every 100 ms; the held view's wake tested). The sky's own drawing of lost and defunct comets from stale sets is #1630. Seen in headless Edge by the E2E only, with eight synthetic comets; nobody has yet looked at the real
  catalogue's thirty in the app or the browser, nor timed Play with them there.

- **Seen in the browser once, by the E2E only** (#1572, bench). `OrrerySunTests` (2026-10-16) opened the orrery in headless Edge
  and read back a textured, limb-darkened Sun and textured planets, once the dev server sent the atlas at all ("A4: the atlas the dev
  server never sent"). Nobody has looked at it in a real browser yet, nor timed a drag or Play there.
- **Play's speed** (#1573, done): fifteen speeds from a drop-up beside Play (above, under "What is built"). The issue's
  other half, the flat Earth, is done too (the Blue Marble day map, under A4's textures and the Earth's longitudes,
  2026-10-16; `OrreryTests.TheOrrerysEarthIsDrawnFromItsDayMap`: the blue share across the lit disc spreads 0.54 where the
  flat disc's spread 0.015). Nothing of #1573 is left.
- **The Earth's clouds and night lights** are not drawn: the day map is cloud-free and the night side black. Each needs
  a SECOND texture per body, so a second sampler and binding in `VkPlanetBodies`' descriptor layout and in
  `WebGlPlanetBodies`, a second upload through `PlanetBodyMap`, and in the trace (both `planet_body.frag` and the WebGL
  copy, held equal by `TheWebGlTraceIsTheVulkanTrace`) clouds blended over the day colour before the lighting and the
  lights mixed in on the unlit side across a terminator ramp; then the same in `PlanetaryRender.RenderRgba`, the CPU
  reference, or the parity tests compare different scenes. Sources would be NASA's own (a Blue Marble cloud layer,
  the Black Marble night lights, public domain), two more 256 x 128 cells (the atlas to 512 x 768, perhaps 40 to
  60 KB). A static cloud map is one instant's weather, which the orrery's other bodies are not; the owner has not asked
  for either.
- **The strip fits a narrow view** (#1584, fixed): at 1.5x on 900 px it needed about 1,020 px, the hint already squeezed
  to nothing, because the labelled buttons could only be Auto, which never shrinks. DIR.Lib 11.22's content-capped Star
  (`WStarUpToContent(min)`) is the fix: "Distances: auto", "Centre: <body>" and the time button are each one, direct
  children of the strip (`BuildTimeControls` hands its buttons back for that; the sky strip keeps them in one group,
  `TimeControlsGroup`), and the time text inside its button is one too. With room each sits at its content and the hint
  takes the slack; short, the hint gives way first, then the labels trim their end toward 3 ems (the time button 4).
  The time button weighs 3 against the labels' 1 (`TimeLabelWeight`), so it keeps its whole date and time with room to
  spare on any platform's glyphs: at weight 1 the three shared 129 px each and the time showed "2028-07-26…"; at 2 it
  kept 29 px past the date and time on Windows and lost the last digit on the Linux runner, whose glyphs measure wider.
  `TheStripFitsWithEveryButtonShowing` runs again: every button inside the view at 1.5x (before, `+10m` ended at 928 px
  of 900; now `+1d` ends at 888, the labels at 77 of 142 and 77 of 133 px and the time text at 200 of 231, showing
  "2028-07-26 23:00  (+93w…", 68 px or 4.5 ems past its date and time; the test asks two ems), and at 1.0x each label
  drawn in full at its measured content (94, 89 and 154 px, as before), the last button ending at the strip's right
  padding (892) as it did. **Re-measured with the speed button (#1573)**, a fourth content-capped label: at weight 3 the
  time kept only 26 px (1.7 ems) past its date and time at 1.5x and the test failed, so `TimeLabelWeight` is 4. At 1.5x
  the labels sit at 54 of 142, 54 of 133 and 54 of 91 px (the speed's text 22 of 91, its mark showing), the time at 185 of
  231 showing "2028-07-26 23:00  (+1…", 53 px or 3.5 ems past its date and time, and `+1d` still ends at 888; at 1.0x every
  label is drawn in full (94, 89, 60 and 154 px), the last button ending at 892. **Those numbers were one face's**: the
  test drew with `FontResolver.ResolveSystemFont()`, a monospace face that is Consolas on Windows and DejaVu Sans Mono on
  Linux, a tenth wider (102 against 94 px for "Distances: auto" at 1x), so the Linux CI leg kept 27 px at 1.5x and failed
  (PR #1613); forcing DejaVu Sans Mono here reproduced it to the pixel. Neither is the face the app draws with, DejaVu Sans
  (`BundledFonts`), in which the strip kept only 23 px on every platform. The test now measures in the app's face as well
  (`OrreryTests.AppFace`, the same file everywhere), and the orrery strip's gap is 2 units, the time controls' own on the
  sky strip, not 4: thirteen gaps gave back 39 px at 1.5x, and the time keeps 46 px (3 ems) in DejaVu Sans, 49 in DejaVu
  Sans Mono and 75 in Consolas, the labels 53 to 60 px.
- **The speed button names its speed at any scale** (round 2 of PR #1613). At 1.5x its 3 em floor held the button's padding,
  gap and mark, and the text got 21 to 28 px of its 91: "1…". The strip is now arranged with the long form ("1 week/s") first,
  and where the engine leaves that leaf short of its width it is arranged again with the compact form (`OrreryPlaySpeed.ShortName`:
  "6h/s", "1w/s", "2mo/s", "10y/s"), Auto at its own width sample ("0mo/s") and without the up mark; only the winner is
  painted. The test is the width SAMPLE's, so the form follows the strip and never the pick. It is an escape hatch, two arranges
  when short: the engine has no text node with shorter alternates, which would let the painter's own fit choose. With the mark the compact button took 78 px and the time kept 29 px in DejaVu Sans, short of its two ems; without it,
  at 1.5x on 900 px, the time keeps 38 px (2.5 ems) in DejaVu Sans, 45 in DejaVu Sans Mono and 76 in Consolas, the labels 51 to
  60 px, and the speed reads "1w/s" in 41 to 46 px; 1.25x is compact too (36 to 40 px), and 1x keeps the long form, 60 to 66 px.
  `OrreryTests.TheSpeedButtonAlwaysNamesItsSpeed` (1x, 1.25x and 1.5x in both faces, four speeds) failed before, showing "…",
  "1 …", "1 we…" and "6 hours…".

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
