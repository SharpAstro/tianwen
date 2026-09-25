# Seeing forecast from upper-air wind (plan)

**Status: PARTIAL (P0 + P1 shipped 2026-09-19; P2 calibration and P3 7Timer open). Written 2026-09-17** from the astrophoto.app field note in
[inbox.md](../todo/inbox.md) ("`wind_speed_250hPa` is a seeing forecast for free"), after reading that
site's shipped JavaScript and querying Open-Meteo. Raised by the user for the desktop GUI and the web
app alike ("not just for web, but having more in the web app is a plus").

Companions: [site-conditions](site-conditions.md) (the weather driver as the live tier for refraction),
[web-showcase](web-showcase.md) (the browser build, which has the planner and can call Open-Meteo
directly).

## The idea

Fast wind at the jet-stream level (about 250 hPa, 10 km) means turbulent upper air and soft stars
however clear the night is. A forecast of that wind is a cheap proxy for seeing, and our weather request
already goes to a provider that has it.

## What we have

- `OpenMeteoDriver` requests eleven hourly fields and eight current ones, all at the surface
  (`HourlyParams` / `CurrentParams`). Nothing above ground level.
- `HourlyWeatherForecast` carries surface wind, gust and direction and nothing aloft.
- `IWeatherDriver.StarFWHM` is contracted as "seeing measured as star FWHM in arcsec", and
  `OpenMeteoDriver` correctly returns NaN for it. **A forecast proxy must not go there.**
- The planner's altitude chart draws a weather band from `HourlyWeatherForecast`
  (`AltitudeChartRenderer`).
- We MEASURE seeing-adjacent quantities every night: a light carries `GUIDERMS` (arcsec RMS guide error
  over its own exposure), the session keeps a median HFD per frame for focus drift, and the dataset bake
  records per-sub FWHM (`SessionFrameAnalyzer`, `DatasetPsfNoiseReport`).

## Measurements (2026-09-17)

**Open-Meteo serves pressure-level wind hourly, not only as "current".** One request for Munich returned
`wind_speed_250hPa`, `wind_speed_500hPa` and `wind_speed_850hPa`, units km/h; the first six hours at
250 hPa read 141, 153, 156, 159, 157 and 143.

**astrophoto.app's heuristic** (read from its bundle):

```
jet   = wind_speed_250hPa ?? wind_speed_10m * 3
worst = max(jet, 2 * wind_speed_10m)            // km/h
score = worst < 30 ? 5 : worst < 60 ? 4 : worst < 90 ? 3 : worst < 130 ? 2 : 1
```

By that scale the Munich forecast above is 1, "Bad". Where its server can reach 7Timer's ASTRO product it
uses that product's seeing class instead. **7Timer sends no CORS header** (checked: `200 OK`, no
`Access-Control-Allow-Origin`), which is why that site proxies it through its own server. Our desktop can
call 7Timer directly; the browser build cannot without a bake or a proxy.

OpenWeatherMap: our One Call request asks for no upper-air field, and whether any OpenWeatherMap product
offers one is unverified.

## Design

### P0: fetch it

- Add `wind_speed_250hPa` to `OpenMeteoDriver.HourlyParams`, plus `wind_speed_500hPa` and
  `wind_speed_850hPa` for P2's shear term. One request, no new call.
- New nullable-NaN fields on `HourlyWeatherForecast` (`WindSpeed250hPa`, `WindSpeed500hPa`,
  `WindSpeed850hPa`), defaulted like `PrecipitationProbability` so every existing constructor call keeps
  compiling. OpenWeatherMap leaves them NaN, and `WeatherForecastMerge` must prefer a number over NaN
  field by field, not source by source.
- Wire model and JSON context entries; the browser build shares the driver code, so it gets the fields
  for free.

### P1: show it as a forecast, not a reading

- A `SeeingForecast` value in `TianWen.Lib` (pure: winds in, class 1..5 and the jet speed out), starting
  with the heuristic above so the two apps agree on day one.
- A row in the planner's weather band beside cloud cover, labelled as an estimate, in the GUI, the TUI
  and the web planner. **Never** fed into `StarFWHM`, and never used by the session to make a decision.

### P2: calibrate it against our own nights

Tracked by #887.

The heuristic is somebody's guess with round numbers. We can do better than copying it, because we hold
the ground truth it is guessing at:

1. For archived sessions, fetch Open-Meteo's historical forecast for the site and night (the same fields,
   `historical-forecast-api`).
2. Pair each hour with that hour's measured median sub FWHM (arcsec) and `GUIDERMS`.
3. Fit the class thresholds, and test whether 500 hPa shear, surface wind or the mixing height
   (`BoundaryLayerHeight`, the ground-layer candidate recorded since 2026-09-19) adds anything, on those
   nights. Report the correlation honestly; if it is weak, say so in the planner label.

### P3 (optional): 7Timer as a second source

Tracked by #888.

Desktop only, as a weather source whose seeing class overrides the P1 heuristic when present, the way
astrophoto.app layers it. The browser would need a bake or a proxy (no CORS), so it is out of scope
there unless the web build grows a server.

## As built (2026-09-19)

- **P0.** `HourlyWeatherForecast.WindSpeed250hPa` / `500hPa` / `850hPa`, m/s like the surface wind, NaN where a
  provider has none (OpenWeatherMap). Open-Meteo's pressure-level arrays are `List<double?>`: an hour past a
  level's horizon comes back `null`, and a null in a `List<double>` fails the WHOLE response. The cache merge
  keeps a known upper-air wind per field when a refetch lacks it.
- **A cache trap found on the way, and fixed for every field**: the JSON source generator built the record
  through its implicit parameterless constructor and passed every init-only member with no default, so a
  field an OLDER cache lacks read back as 0, not NaN. For the jet wind that is a dead-calm night, the best
  class there is. The primary constructor is now `[JsonConstructor]`, so a missing field takes its parameter
  default (`OpenMeteoPressureLevelWindTests` failed at 0 before it).
- **P1.** `SeeingForecast.For(hour)` (`TianWen.Lib/Devices/Weather`): the heuristic above, as `SeeingClass`
  Bad..Excellent. **One deliberate departure: no 250 hPa wind is `Unknown`**, not three times the surface wind,
  because the class claims to come from the air aloft and an OpenWeatherMap profile would otherwise get a
  confident estimate built from the surface alone. The planner's weather band grows a "Seeing" row under the
  humidity row (class 1..5, the humidity row's severity colours), reserved only when some hour has a jet
  wind, so an OpenWeatherMap profile gets no empty row; the tooltip says "estimated from wind" and gives the
  250 hPa speed. `AltitudeChartRenderer` is shared, so the GUI, the TUI (sixel) and the web planner all have it.
  `StarFWHM` is untouched and nothing in the session reads the class.
- **An OpenWeatherMap profile gets its winds from Open-Meteo (2026-09-19).** It showed no row at first, the
  case on the user's own rig: the cache merge is fresh against cached within ONE provider, so nothing filled
  the upper-air wind OpenWeatherMap lacks. The planner now fetches through
  `IWeatherDriver.GetHourlyForecastWithUpperAirAsync` (`WeatherDriverExtensions`), which for an OpenWeatherMap
  driver also asks keyless Open-Meteo for the same window and fills ONLY the missing upper-air fields at the same
  instant (`WeatherForecastMerge.FillUpperAir`): OpenWeatherMap's own numbers always win, and no hour only
  Open-Meteo covers is added. Every other driver passes through, so a fake or hardware driver never reaches the
  network from a test. Open-Meteo's own file cache keeps it to one request an hour. Pinned by
  `WeatherUpperAirSupplementTests`, which drives both real drivers off fresh file caches and fails with the
  fill disabled.
- **The mixing height is recorded and shown, not scored (2026-09-19).** `HourlyWeatherForecast.BoundaryLayerHeight`
  (metres above ground, Open-Meteo's `boundary_layer_height`, what the Bureau of Meteorology labels "mixing
  height") rides the same request, fills across providers with the winds, and reads "Mixing height: 280 m" in
  the band's tooltip. It is the ground-layer half of the atmosphere the jet wind does not see: a clear calm night
  collapses it to a shallow stable layer (still low air, but moisture and smoke trapped under it), while a night
  that stays deep is being stirred by wind. **It is a MODEL quantity and the models disagree most exactly where
  it matters**: for the user's site on 2026-09-19 BOM's ACCESS gave 1763 / 1561 / 711 m at 16:00 / 17:00 / 18:00
  and a flat 110 m all night, Open-Meteo's default model 1680 / 1560 / 380 m and 265 to 300 m overnight (a
  second point at Moorabbin, 13 km nearer the bay, read within 30 m of it, so the gap is the model, not the
  place). Open-Meteo serves no boundary layer from its `bom_access_global` or `ecmwf_ifs025` models, so BOM's
  own number is not reachable through it. `SeeingForecast` deliberately ignores it until P2 has weighed it
  (`TheMixingHeightDoesNotMoveTheSeeingClassYet`); recording it now means every cached night carries it for
  that calibration.

## Phasing

| Phase | Scope | Hosts |
|---|---|---|
| P0 | Pressure-level winds on the forecast record | all (driver code is shared) |
| P1 | `SeeingForecast` + planner row | GUI, TUI, web |
| P2 | Calibration against archived FWHM and guide RMS | offline analysis, then P1's thresholds |
| P3 | 7Timer seeing class | desktop |

## What bites

- **A forecast is not a measurement.** `StarFWHM` stays NaN on a forecast driver.
- **Merge per field.** A NaN from one provider must not overwrite a number from the other.
- **7Timer has no CORS.** Anything the browser needs from it has to be baked or proxied.
