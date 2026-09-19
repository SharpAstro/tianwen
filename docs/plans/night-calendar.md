# Night calendar and a verdict per night (plan)

**Status: PARTIAL. P0 to P2 DONE 2026-09-19 (#311); P3 and P4 open. Written 2026-09-19**, raised by the user while looking at the planner under a
Melbourne sky ("a calendar view would be great too, for places like this that see a clear sky once a full
moon"; "making the date in the top clickable, and showing a calendar which shows score/weather etc/moon and
can be clicked (and set the date)"; "maybe we also show a verdict of the day somewhere").

Companions: [seeing-forecast](seeing-forecast.md) (the seeing class a cell shows),
[moon-avoidance](moon-avoidance.md) (the moon model the planner already scores with).

## The idea

The planner answers "what should I shoot on THIS night". At a site that sees a clear night a few times a
month the first question is "WHICH night", and today the only way to ask it is to press the arrow beside the
date once per night and read each chart. A month view answers it at a glance: per night, the moon, the
forecast where there is one, and one verdict, and a click on a night plans it.

## What we have

- **The date is already a control.** The GUI status bar draws `[<] Tonight 19:24-04:46 [>]`
  (`VkGuiRenderer.RenderStatusBar`); the arrows call `PlannerActions.ShiftPlanningDate`, and the label itself
  is clickable only while a date is pinned, where it resets to tonight (`ResetPlanningDate`). Both hide while
  a session runs.
- **Per-night astronomy is cheap and needs no network.** `ObservationScheduler.CalculateNightWindow` (with its
  no-astro-dark fallback chain) and `PlannerActions.ComputeMoonData` (moon altitude profile, illumination,
  waxing, hemisphere-aware phase glyph) run for one night today; running them for 42 nights is a loop.
- **The popover primitive exists**: `Layout.Builder.Popover` + `PopoverState` (the viewer's tone and white
  balance panels), which brings dismissal, Escape and pointer ownership with it.
- **The forecast is fetched one night at a time.** Both drivers take a `(start, end)` range and cache per range
  (`Weather/<lat>_<lon>_<start>_<end>.json`), and the planner asks for the planning night only.
- **Horizons differ by provider.** Open-Meteo gives hourly data for 16 days in ONE request and is keyless.
  OpenWeatherMap One Call 3.0 gives 48 hours hourly and 8 days daily (our request excludes `daily`), and has
  no upper-air wind at all. The wind half of that is already bridged:
  `IWeatherDriver.GetHourlyForecastWithUpperAirAsync` fills an OpenWeatherMap night's missing upper-air winds
  from Open-Meteo (2026-09-19, see the seeing plan), but it adds no hour OpenWeatherMap lacks, so the range
  half is still P0's.

## Design

### A night, summarised (pure, `TianWen.Lib`)

`NightSummary` per evening date: the dark window (and which twilight it fell back to), moon illumination and
phase, **moon-free dark hours** (dark hours with the moon below the horizon), and, inside the forecast
horizon, the cloud cover and precipitation over the dark window and the median `SeeingForecast` class.

`NightVerdict.For(summary)` is the one verdict function, used by the calendar cell AND the status bar so the
two can never disagree:

- **Inside the forecast horizon**: Go / Marginal / No-go from the **clear dark hours** (dark hours with cloud
  under a threshold), with the moon counting against them and seeing shading a Go. Starting thresholds are
  round numbers, not a fit, exactly as `SeeingForecast` started; calibrating them against nights we actually
  imaged is later work.
- **Beyond it**: no weather verdict at all, only the moon ("dark" / "moonlit"). A calendar that shows a
  confident green square 12 days out is lying; the cell says what is knowable.

### The calendar (GUI first)

- **The date label opens it** (a popover anchored under the status-bar date), whether or not a date is
  pinned. The reset-to-tonight it does today moves INTO the calendar as a "Tonight" button, so no behaviour
  is lost. Hidden while a session runs, like the arrows.
- **A month grid of NIGHTS, labelled by the evening date** (`CoordinateUtils.AstronomicalEveningDate`), with
  month paging. A cell carries the day number, the moon glyph, the verdict as its tint (grey beyond the
  horizon), and the clear dark hours when known. Hover shows the detail: dark window, moon illumination and
  rise/set, cloud, seeing class. A click sets `PlanningDate` to that night and closes the popover.
- **Built off the render thread** with the Task hand-off (`SkyMapTab`'s Milky Way load is the pattern), keyed
  on (site, month, forecast generation), and never blocking the popover from opening: cells fill in as the
  summaries land.

### The forecast behind it

- **One multi-day fetch, not one per night**: Open-Meteo's 16-day hourly range in one request, cached like
  today's per-night file.
- **An OpenWeatherMap profile also asks Open-Meteo** (keyless) and merges PER FIELD, the rule the seeing plan
  already states: the profile's own provider wins inside its horizon, Open-Meteo fills what it lacks. The
  upper-air wind is done (`GetHourlyForecastWithUpperAirAsync`); what remains is days 3 to 16, whole HOURS the
  provider does not cover. The tooltip names the source of each field, so a mixed forecast never passes for
  one provider's; the wind fill needs no marker yet because Open-Meteo is today the only source of an
  upper-air wind at all.

### The verdict in the status bar

The verdict of the planning night sits beside the date (a coloured word, the same `NightVerdict`), so "is
tonight worth it" is readable from every tab without opening anything.

## Phasing

| Phase | Scope | Hosts |
|---|---|---|
| P0 | Open-Meteo side-fetch for OpenWeatherMap profiles, merged per field: the upper-air wind DONE 2026-09-19; the 16-day range DONE 2026-09-19 | all (driver code is shared) |
| P1 | `NightSummary` + `NightVerdict` (pure, tested), the verdict beside the status-bar date. DONE 2026-09-19 | GUI |
| P2 | The calendar popover off the date label, click to plan a night. DONE 2026-09-19 | GUI |
| P3 | Per-night score for the PINNED targets (their usable hours that night), shown in the cell | GUI |
| P4 | The web planner and the TUI | web, TUI |

## What shipped (2026-09-19, #311)

P0 to P2 as designed above, with these decisions and departures:

- **One forecast, not two.** The design had the calendar fetch its own multi-day forecast beside the
  planner's per-night one. That doubles the requests. In the build, the planner's band is a SLICE of the
  calendar's forecast (`NightCalendarActions.RefreshAsync`, called where the planner used to fetch its one
  night). Stepping the date through the next two weeks now asks nothing, where every step used to be a new
  request under a new cache file. The forecast is reused in memory for an hour, the drivers' own cache
  lifetime, so a recompute does not even read the cache file. The request is
  `ExtendedForecast.RangeFor`: from yesterday (UTC) to the last day Open-Meteo accepts, today (UTC) plus 15,
  measured the same day; one day more is a 400. Only a night BEFORE that range still makes its own
  single-night request, and a night after it makes none.
- **The provider split is per hour, at one instant.** `WeatherForecastMerge.Extend` keeps every hour the
  profile's provider covers (upper air filled, as before), and adds Open-Meteo's hours after its last one.
  A night on either side of the split has one provider's cloud, and a night across it has both.
  `ExtendedForecast.SourceFor` names which: the calendar's detail strip always, the band's tooltip only
  for a mixed band (`Forecast:` line).
- **The verdict** (`NightVerdict.For`) runs on useful dark time: clear time, which is cloud under 30
  percent and no precipitation, sampled every 10 minutes. A moonlit hour counts at the unlit fraction of the
  Moon, but never below a quarter, so a clear full-Moon night is Marginal, not No-go (narrowband still
  works). The grades:
  - **Go:** 3 useful hours, or 60 percent of the dark window, so a short high-latitude night can still
    be a Go. A Bad seeing class takes a Go to Marginal.
  - **Marginal:** 1 hour, or 25 percent.
  - **A weather verdict at all** needs a forecast for 75 percent of the dark window. Short of that, the
    night is Dark or Moonlit (60 percent moon-weighted dark), which is what the horizon's edge and every
    night past it get.

  All of these are round numbers, not a fit.
- **The Moon is drawn, not a glyph**:
  - How: a dark disc, its lit half clipped to one side, and a terminator ellipse |1 - 2k| of the diameter
    wide, which makes the lit area k exactly. It is lit on the left for a waxing Moon in the south, like
    `MeeusMoon.GetPhaseEmoji`.
  - Why not a glyph: a colour emoji cannot take the palette, so in Night mode it would be the brightest
    thing on screen.
  - Consequence: this is why the calendar is its own widget (`NightCalendarPopover`). The clip and ellipse
    helpers it uses belong to the widget base.
- **Hover detail is a strip, not a tooltip.** The GUI paints no node tooltip (only the sidebar draws its own),
  so the calendar's bottom strip details the night under the pointer, or the planned one: the dark window
  and its twilight, the Moon, clear hours, cloud, rain, seeing and the source.
- **The label always opens the calendar.** Its old reset-on-click is the calendar's Tonight button, and
  picking tonight un-pins the date rather than pinning today. Picking another night keeps the time of day the
  planner was looking at and clears a sky-map scrub.
- **Found on the first live run:** Open-Meteo's surface arrays were `List<double>`, and the far end of a 16-day
  request is null (`cloud_cover[403]`), so the first request lost all 408 hours to one null. Every hourly array
  is now nullable per element, and the band no longer draws a no-cloud-value hour as clear.
- **Cost:** about 20 ms a night in a Debug test run, so a month is under a second, off the render thread, and
  only nights not already summarised are computed.

## What bites

- **A night is an EVENING date.** It spans midnight, so a cell keyed on the calendar date shows the wrong
  night's moon for half of it.
- **No weather verdict past the horizon.** Moon only.
- **High latitude has no astro dark in summer.** Use `CalculateNightWindow`'s fallback chain and say which
  twilight the window came from, never demand `-18 deg`.
- **Colours come from the palette**, and Night mode has no blue: the verdict tints are derived, never literals.
- **Nothing here touches the session.** The verdict is a forecast for a person deciding; the session never
  reads it.
