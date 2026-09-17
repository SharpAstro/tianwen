# Seeing forecast from upper-air wind (plan)

**Status: NOT STARTED. Written 2026-09-17** from the astrophoto.app field note in
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

The heuristic is somebody's guess with round numbers. We can do better than copying it, because we hold
the ground truth it is guessing at:

1. For archived sessions, fetch Open-Meteo's historical forecast for the site and night (the same fields,
   `historical-forecast-api`).
2. Pair each hour with that hour's measured median sub FWHM (arcsec) and `GUIDERMS`.
3. Fit the class thresholds, and test whether 500 hPa shear or surface wind adds anything, on those
   nights. Report the correlation honestly; if it is weak, say so in the planner label.

### P3 (optional): 7Timer as a second source

Desktop only, as a weather source whose seeing class overrides the P1 heuristic when present, the way
astrophoto.app layers it. The browser would need a bake or a proxy (no CORS), so it is out of scope
there unless the web build grows a server.

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
