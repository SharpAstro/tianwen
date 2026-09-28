# Atlas: planet detail (light curve, visibility curve, next opposition / GE, how a planet is drawn)

**Status: PLANNED (raised by the user 2026-08-22).** Tracked here rather than in the next release,
per the user's instruction: *"for Atlas lets just track it in the plan"*.

The notes, in the order they arrived:

- *"TW Atlas: Spark lines"*
- *"atlas needs the planet light curve and visiblity curve and whatnot"*
- *"at the very least show when the next opposition/GE is"*
- 2026-09-28, with a reference of the planets side by side by brightness and by size in the sky:
  *"improve the display of the planets in the atlas ... possibly also showing the correct Saturn ring
  angle + the phases of Venus + Mercury + Moon"* (A4)

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

A1 to A3 are about what the info panel SAYS; this is what the map DRAWS. Today every planet is a dot sized by
its type (`SkyMapTab.DrawPlanetLabels`: the Sun and the Moon 4 px, Jupiter and Saturn 3, the rest 2), in a
colour per planet (`SkyMapRenderer.GetPlanetColor`). It looks the same at any zoom, whatever the body's
distance, phase or brightness.

1. **A disc at its true angular size, where the zoom resolves one.** The apparent diameter is the body's
   equatorial radius over its geocentric distance, and `VSOP87a.Reduce` already returns that distance. Jupiter
   runs about 30 to 50 arcseconds, which is some 11 px across at a 1 degree field on a 1,000 px view, so a disc
   is real from moderate zoom inward. Below a few pixels the dot stays, but sized by the LIVE brightness (A1's
   magnitude) rather than by type. The disc is also the planet's hit area, as a shaped object's ellipse already
   is for the resolver.
2. **A drawn disc per planet, from the palette.** Jupiter's and Saturn's bands, Mars's red, the ice giants'
   tints, as the reference draws them. Never a texture or an emoji, for the reason the night calendar's Moon mark
   is drawn: Night mode must be able to tint it (CLAUDE.md, "Night Calendar"). A disc is ellipse fills, which
   all three renderers draw natively.
3. **Phase: Venus, Mercury, the Moon, and Mars's gibbous.** The illuminated fraction is (1 + cos i) / 2 for the
   phase angle i (Sun, body, Earth), from the heliocentric and geocentric distances A1 needs for the magnitude
   anyway, so the two share one geometry and whichever lands first builds it. The terminator is a half-ellipse
   whose minor axis is the disc's radius times |cos i|, with the bright limb turned toward the Sun's position
   angle from the body. `MeeusMoon.GetPhase` already has the Moon's fraction and not its bright-limb angle. The
   crescent is either composed from ellipse fills or a small primitive added to DIR.Lib's `Renderer`, which is
   where it belongs if the composition cannot be exact on all three renderers.
4. **Saturn's rings at their real tilt.** The ring opening B (the Earth's saturnicentric latitude) and the
   position angle P, per Meeus, Astronomical Algorithms, ch. 45. The ring is an ellipse of axis ratio sin |B|,
   its back half drawn behind the disc and its front half over it. The rings were edge-on at the ring-plane
   crossing of March 2025 and are opening again, so a near edge-on ring is today's picture and a good check.
5. **Info-panel rows:** apparent diameter (40.9"), illuminated fraction for a phased body, ring tilt for Saturn.
6. **Optional: the planets side by side**, by size in the sky or by brightness, as in the reference: a strip of
   all of them at the viewing instant, each pressable to select it. Only after 1 to 5, and only if it earns its
   screen space.

**Tests:** the apparent diameter and the illuminated fraction against published ephemeris values for a date;
Saturn's B and P and Venus's illuminated fraction against Meeus's worked examples (ch. 45 and ch. 41); and
pixels for the phase's orientation (the lit limb faces the Sun) and for the ring's front half covering the disc.

## Sequencing

A3, then A1 (which carries the live-magnitude fix), then A2. A3 is a text row over a detector that
already exists; A1 is a new per-body sampler plus a cache with two known traps; A2 has a design
question to settle before it is worth starting.

A4 can run beside them: its first step (the disc at its true size) needs only the distance VSOP87a already
gives, and its phases share A1's geometry.

## Related

- [comet-ephemeris.md](comet-ephemeris.md) -- where the sparkline, the selection path, the event
  detector and both cache rules were built. Read the "path and sparkline caches must hit regardless
  of sample count" section before adding a third cached curve.
- [skymap-time-scrub.md](skymap-time-scrub.md) -- the time offset every curve here has to agree
  with.
- The deferred comet `MagnitudeChartRenderer` (C2c in comet-ephemeris.md) was skipped on the grounds
  that the sky-map sparkline already covers the curve. If A1 and A2 produce a real chart, that
  decision is worth revisiting for comets at the same time rather than building a second one.
