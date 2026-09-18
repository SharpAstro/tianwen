# Night calendar and a verdict per night (plan)

**Status: NOT STARTED. Written 2026-09-19**, raised by the user while looking at the planner under a
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
| P0 | Open-Meteo side-fetch for OpenWeatherMap profiles, merged per field: the upper-air wind DONE 2026-09-19; the 16-day range open | all (driver code is shared) |
| P1 | `NightSummary` + `NightVerdict` (pure, tested), the verdict beside the status-bar date | GUI |
| P2 | The calendar popover off the date label, click to plan a night | GUI |
| P3 | Per-night score for the PINNED targets (their usable hours that night), shown in the cell | GUI |
| P4 | The web planner and the TUI | web, TUI |

## What bites

- **A night is an EVENING date.** It spans midnight, so a cell keyed on the calendar date shows the wrong
  night's moon for half of it.
- **No weather verdict past the horizon.** Moon only.
- **High latitude has no astro dark in summer.** Use `CalculateNightWindow`'s fallback chain and say which
  twilight the window came from, never demand `-18 deg`.
- **Colours come from the palette**, and Night mode has no blue: the verdict tints are derived, never literals.
- **Nothing here touches the session.** The verdict is a forecast for a person deciding; the session never
  reads it.
