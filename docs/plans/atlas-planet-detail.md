# Atlas: planet detail (light curve, visibility curve, next opposition / GE)

**Status: PLANNED (raised by the user 2026-08-22).** Tracked here rather than in the next release,
per the user's instruction: *"for Atlas lets just track it in the plan"*.

The notes, in the order they arrived:

- *"TW Atlas: Spark lines"*
- *"atlas needs the planet light curve and visiblity curve and whatnot"*
- *"at the very least show when the next opposition/GE is"*

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

## The three items

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

## Sequencing

A3, then A1 (which carries the live-magnitude fix), then A2. A3 is a text row over a detector that
already exists; A1 is a new per-body sampler plus a cache with two known traps; A2 has a design
question to settle before it is worth starting.

## Related

- [comet-ephemeris.md](comet-ephemeris.md) -- where the sparkline, the selection path, the event
  detector and both cache rules were built. Read the "path and sparkline caches must hit regardless
  of sample count" section before adding a third cached curve.
- [skymap-time-scrub.md](skymap-time-scrub.md) -- the time offset every curve here has to agree
  with.
- The deferred comet `MagnitudeChartRenderer` (C2c in comet-ephemeris.md) was skipped on the grounds
  that the sky-map sparkline already covers the curve. If A1 and A2 produce a real chart, that
  decision is worth revisiting for comets at the same time rather than building a second one.
