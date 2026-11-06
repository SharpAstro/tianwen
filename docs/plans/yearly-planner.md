# Best time to observe: the atlas's night curve and a yearly planner (plan)

**Status: NOT STARTED. Written 2026-11-07**, raised by the user: "in the atlas, do we already show the best
observability time?", then "the planner might need two modes, a yearly planner, that would tell you, M31 can
best be observed in September etc.", and "the atlas info box should just maybe mark the area under the height
graph that is good for observing (same criteria as the planner) and maybe show a total (plus the score)".

Milestone: `yearly-planner`. Companions: [night-calendar](night-calendar.md) (which night of a month),
[moon-avoidance](moon-avoidance.md) (the moon term in the score), [skymap-time-scrub](skymap-time-scrub.md)
(the atlas's viewing time), [in-app-sky-atlas](in-app-sky-atlas.md).

## What we have

- **The atlas info box gives rise, transit and set, and nothing about the night.** `ObjectInfoPanel` draws
  `Rise hh:mm  Transit hh:mm  Set hh:mm` (or `Circumpolar  Transit hh:mm`) from `SkyMapInfoPanelData`, which
  takes them from `RiseTransitSetHelper` for the viewing date. It has **no altitude graph**: the only curve it
  can draw is the comet magnitude sparkline (`Options.Sparkline`, `DesignSparklineHeight`). Whether that
  transit falls in darkness, how long the object is usable, and how it ranks are not shown.
- **The planner already answers "how good is tonight" per target, by one rule.**
  `ObservationScheduler.ScoreTarget` samples the astronomical night (astro dark to astro twilight, with
  `CalculateNightWindow`'s fallback chain) every 15 minutes (`AstromGridStep`). A sample counts only above
  the profile's `MinHeightAboveHorizon`, and contributes `altitude - minimum`, scaled down by the moon
  (`MoonGrid`: illumination times quadratic proximity inside the avoidance radius). A transit outside the dark
  window halves the total. The result, `ScoredTarget`, carries the per-sample `ElevationProfile`,
  `OptimalStart` and `OptimalDuration` (first to last counting sample), `OptimalAltitude`, `MinMoonSeparationDeg`
  and `TotalScore`; `CombinedScore` multiplies in the object bonus.
- **The planner shows the score only as a rating against tonight's best** (`PlannerActions.ScoreToRating`,
  `PlannerTargetList`), never as a number, since `TotalScore` is a sum of degree-samples with no unit a
  person can read.
- **Plans are per night already.** `PlannerPersistence` keeps `Planner/{profile}/{yyyy-MM-dd}.json` keyed by
  the evening date, so pinning on a future night (status-bar arrows, the night calendar) saves that night's
  plan. A night with no file loads the most recent EARLIER one (`FindMostRecentPriorSession`), so a plan made
  for a later night is invisible until you move to it, and nothing lists the nights that have one.

## A. Atlas night curve

The info box gains a small altitude curve for the viewing night, drawn where the comet sparkline goes (a
host-drawn fill, as the sparkline is), from dusk to dawn:

- **The part that counts is shaded**, by the planner's own rule and nothing else: above the profile's minimum
  altitude, inside the astronomical night, the shade's strength following the moon factor so a moon-washed
  stretch reads paler. It comes from ONE call to `ObservationScheduler.ScoreTarget` for the selected object
  (the `ElevationProfile` samples), never from a second altitude loop in the UI: two copies of the rule would
  disagree the first time the planner's changes.
- **Under it, one line**: the usable time in hours and minutes, the window (`OptimalStart` to its end, in site
  time), and the rating the planner would give it (`ScoreToRating` against tonight's best, the same stars). A
  bare `TotalScore` is not shown, it has no unit.
- The night is the planner's (`PlannerState.AstroDark`/`AstroTwilight`, `PlanningDate`), so the curve moves
  with the status-bar date and the night calendar; the atlas's own time scrub moves only the "now" mark on it.
- **Moving bodies**: `ScoreTarget` takes a fixed RA/Dec. For a planet, the Moon or a comet, sample its
  position at the night's midpoint; a night's drift is well under the
  15-minute grid's resolution for anything but the Moon, which the panel can simply leave without a shade.
- Where the panel has no site (the FITS viewer over a photograph without one) the row is omitted, the rule
  `ObjectInfoPanel` already follows for rise/set.

Cost: one `ScoreTarget` is about 40 SOFA samples (a ten-hour night), done once per selection or date change, off the render
thread, cached on (object, night, profile minimum, moon radius).

## B. Best months per object

The yearly question is the nightly score swept over the year. A pure routine in `TianWen.Lib.Sequencing`
(working name `SeasonalVisibility`) takes a target and a site and returns, for each sampled night of the next
twelve months, the usable dark time and the score from `ScoreTarget`:

- **Sample one night a week** (52 nights) with the moon term OFF: a month's best is about where the object
  stands at night, and the moon would make the answer depend on which week was sampled. The moon belongs to
  the night calendar, which already shows it per night.
- **Summarise as months**: per calendar month the mean usable hours, the best month, and the run of months
  over a threshold (half the best), so the answer reads "Best: Oct (6.5 h a night); good Aug to Dec".
- The atlas info box shows that one line under the night curve (A).
- Cost: 52 nights, each a `CalculateNightWindow` plus a `ScoreTarget`; the night windows are the same for
  every object at a site, so they are computed once per (site, year) and shared, leaving about 52 x 40 SOFA
  samples per object. Cached per (object, site, minimum altitude).

## C. Planner year mode

The planner gets a second mode beside the night view: a year at a glance for the pinned targets and the
current search results, one row per target and twelve month cells, each cell shaded by B's mean usable hours
(the sky ramp, never colour alone: the hours are the cell's tooltip and the best month is marked).

- A click on a month cell moves the planner to a night in that month (the darkest, nearest new moon, from the
  night calendar's own data) and back to the night view, so the year view hands over to the per-night tools
  that already exist rather than duplicating them.
- Sorting by "best now" (the current month's hours) answers "what is coming into season".
- It is a mode of the same tab, one `PlannerState` flag, so the target list, search and pins stay one model.

## D. Upcoming plans

A plan saved for a later night should be findable from tonight:

- The planner lists the nights after the planning night that have a plan file, collapsed by default (one line:
  "Planned: 3 later nights"), each expanded row naming the date and its pinned targets, a click moving there.
- Nights further ahead than the forecast or the current season (say 60 days) stay in the collapsed list only.
- The fallback that carries the most recent earlier plan forward is kept; it is what stops pins vanishing at
  the evening rollover.
