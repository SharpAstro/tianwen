# Plan: Live Site Pressure/Temperature for Refraction

Status: SHIPPED 2026-06-12 (branch `feat/top-5-todo`) -- two-tier resolver (live weather -> standard atmosphere). Authored 2026-06-12; file/line facts verified against main @ `ae85cd8`.

### Design correction (2026-06-12): no profile-stored override

The draft proposed a middle tier of user-configurable `ProfileData.SitePressureHPa` /
`SiteTemperatureCelsius`. **Dropped by design.** Pressure and temperature are *varying*
environmental values -- unlike the static site geometry (latitude/longitude/elevation, which only
change when the rig is physically relocated). Persisting a single value would be stale within
hours and actively misleading. So they are never written to the profile. The no-weather fallback
instead derives pressure barometrically from the profile's *static* elevation (the correct static
proxy), which `Transform.CalculateSitePressureIfRequired` already does when `SitePressure` is left
NaN. The live value, when a weather device is connected, is read fresh each call -- the driver
already caches its last reading (`OpenMeteoDriver.Pressure` is a `_pressure` field read, refreshed
on the driver's own cadence), so there is nowhere else to cache it.

## Implementation notes (as shipped)

- **Phase 1** `src/TianWen.Lib/Astrometry/SiteConditions.cs`: `SiteConditionsSource` enum
  (`Weather`, `StandardAtmosphere`) + `readonly record struct SiteConditions(PressureHPa,
  TemperatureCelsius, PressureSource, TemperatureSource)`. `Resolve(IWeatherDriver?)` resolves each
  value independently (weather non-NaN + in-range wins, else standard). Weather readings are
  range-validated (pressure 300..1100, temp -80..60) so a garbage station reading can't poison
  refraction; `static readonly Standard` (1010/10). `ApplyTo(Transform)` always sets
  `SiteTemperature`, sets `SitePressure` only when `PressureSource != StandardAtmosphere` (standard
  tier left NaN -> transform auto-derives from elevation).
- **Phase 3** `IMountDriver`: kept `TryGetTransformAsync(CancellationToken)` (now delegates to
  `TryGetTransformAsync(SiteConditions.Standard, ct)`) and ADDED an overload
  `TryGetTransformAsync(SiteConditions conditions, CancellationToken ct)`. This is the deviation
  from the draft's single optional-param signature: an overload keeps `CancellationToken` last in
  both (C# convention / feedback_cancellation_token_last) AND avoids reordering the 13 existing
  positional `(ct)` call sites. `Session.ResolveSiteConditions()` (guards on a *connected* weather
  driver, reads it live) feeds the 4 session call sites (Timing, Imaging x2, Obstruction).
  `TransformFactory.FromProfile` keeps its 15 C planner default (not 10, to avoid re-baselining
  planner output) and leaves pressure unset for the elevation auto-derive -- no profile temp/pressure
  reads.
- **Phase 4** Polar-align handler (`AppSignalHandler`) replaced its inline weather block with
  `SiteConditions.Resolve(weather)`; `PolarAlignmentSite` reads
  `conditions.PressureHPa`/`TemperatureCelsius` (standard tier = 1010 there, since it needs a
  concrete number rather than a Transform to auto-derive into).
- **Phase 6** Tests: `SiteConditionsTests` (weather-wins / per-value NaN independence / out-of-range
  fall-through / no-weather standard + `ApplyTo` auto-derive-vs-pin, using `FakeTimeProviderWrapper`
  for a deterministic Transform). `IMountDriver.cs:344/345` TODOs ticked; `:347` (refraction-support
  assumption) left open per the out-of-scope note below.

Goal (as realized): replace the hardcoded `SitePressure = 1010` / `SiteTemperature = 10` in
`IMountDriver.TryGetTransformAsync` with a two-tier resolution: (1) live
`IWeatherDriver.Pressure`/`Temperature` when a weather device is connected, (2) standard
atmosphere as fallback, with pressure derived from the profile's static elevation. Matters for
refraction at low altitudes (a lat-15 deg pole sees ~3.4 arcmin of refraction lift; lat-35 deg
still ~1.4 arcmin) and unblocks the polar-alignment refraction work (polar-alignment.md
"Refraction at low pole altitudes" names exactly this tier chain).

## Current state (verified facts)

- `IMountDriver.TryGetTransformAsync` (`src/TianWen.Lib/Devices/IMountDriver.cs:382-403`,
  default interface method) hardcodes:
  ```csharp
  SitePressure = 1010, // TODO standard atmosphere
  SiteTemperature = 10, // TODO check either online or if compatible devices connected
  Refraction = true // TODO assumes that driver does not support/do refraction
  ```
  Call sites: `Session.Imaging.cs:672,1245` (altitude checks), `Session.Timing.cs:58`
  (`SessionEndTimeAsync`), `Session.Imaging.Obstruction.cs:505`, and the one-shot
  `GetRaDecJ2000Async(ct)` convenience overload (IMountDriver.cs:508).
- `Transform` (`src/TianWen.Lib/Astrometry/SOFA/Transform.cs`):
  - `SitePressure` initialises to `NaN`; when still NaN at conversion time,
    `CalculateSitePressureIfRequired` (lines 949-958) derives it barometrically from
    elevation + temperature: `1013.25 * exp(-elev / (29.3 * (tempC + 0.0065*elev + 273.15)))`.
    **So leaving pressure unset is already better than flat 1010 at altitude.**
  - `SiteTemperature` setter range -273.15..100 C; `Refraction = false` passes 0/0 to SOFA.
- `TransformFactory.FromProfile` (`src/TianWen.UI.Abstractions/TransformFactory.cs:17-58`)
  reads profile lat/lon/elev, hardcodes `SiteTemperature = 15`, never sets pressure
  (auto-derive kicks in), never sets `Refraction`.
- `IWeatherDriver` (`src/TianWen.Lib/Devices/Weather/IWeatherDriver.cs`): `double Pressure`
  (hPa, site-level), `double Temperature` (C). **No capability flags - NaN is the
  "unsupported" contract.** `OpenMeteoDriver` provides real values for both (surface
  pressure from the API, already site-level); current conditions populate on
  `GetHourlyForecastAsync`, not on connect. `FakeWeatherDriver` returns 12.5 C / 1013.25 hPa.
- Weather resolution pattern already in production
  (`AppSignalHandler.cs:2385-2401`, polar alignment):
  ```csharp
  double pressureHPa = 1010.0; double temperatureC = 10.0;
  if (profileData.Weather is { } weatherUri
      && hub.TryGetConnectedDriver<IWeatherDriver>(weatherUri, out var weather) && weather is not null)
  {
      if (!double.IsNaN(weather.Pressure)) pressureHPa = weather.Pressure;
      if (!double.IsNaN(weather.Temperature)) temperatureC = weather.Temperature;
  }
  ```
  Tier 1 + tier 3 exist here; tier 2 (profile) is missing because the fields do not exist.
- `ProfileData` (`src/TianWen.Lib/Devices/ProfileDto.cs:20-36`): readonly record struct
  with `SiteLatitude/SiteLongitude/SiteElevation` as `double?` trailing params. JSON via
  source-generated `ProfileJsonSerializerContext` - new public properties on the record
  are picked up automatically (AOT-safe).
- `Setup.Weather` exists (`src/TianWen.Lib/Sequencing/Setup.cs:21-27`, `Weather?` optional,
  wrapper exposing `.Driver` as `IWeatherDriver`); `SessionFactory.cs:94-96` constructs it
  from `profileData.Weather`.
- `PolarAlignmentSite` record struct already carries `PressureHPa`/`TemperatureC`
  (`PolarAlignmentSession.cs:935-940`) and feeds `PolarAxisSolver.DecomposeAxisError`.
- Site editing UI: `EquipmentTab.cs` site section ~lines 400-465 (lat/lon/elev inputs +
  "Save Site"), write path `EquipmentActions.SetSite(data, lat, lon, elev?)`
  (`EquipmentActions.cs:89`), TUI twin in `TuiEquipmentTab.cs`.
- Tests: `TransformTests` covers explicit pressure/temp in `EventTimes`; **no test covers
  the NaN auto-derive path under refraction, and no test covers tier resolution** (it
  currently lives inline in a signal handler).

## Design

### New pure resolver (single source of truth)

`src/TianWen.Lib/Astrometry/SiteConditions.cs`:

```csharp
public enum SiteConditionsSource { Weather, Profile, StandardAtmosphere }

public readonly record struct SiteConditions(
    double PressureHPa, double TemperatureCelsius, SiteConditionsSource PressureSource, SiteConditionsSource TemperatureSource)
{
    public const double StandardPressureHPa = 1010.0;   // keep existing behavior constant
    public const double StandardTemperatureCelsius = 10.0;

    public static SiteConditions Resolve(IWeatherDriver? weather, double? profilePressureHPa, double? profileTemperatureC);
}
```

`Resolve` is per-value (pressure and temperature resolve independently - a station can
report temperature but NaN pressure): weather non-NaN wins, else profile value (range-
validated: pressure 300..1100 hPa, temp -80..60 C; out-of-range treated as unset with a
log at the call site), else standard. Pure + static => directly unit-testable, and both
the session and the signal handler call the same function (feedback_one_path).

Note on tier 3 vs auto-derive: `Transform` derives pressure barometrically from
elevation when left NaN, which beats flat 1010 for elevated sites. So for *pressure*
the standard-atmosphere tier should be expressed as "leave `Transform.SitePressure`
unset (NaN)" wherever a Transform is the consumer, and only collapse to the 1010
constant where a concrete number is required (`PolarAlignmentSite`). Encode this as:
`SiteConditions.ApplyTo(Transform t)` sets `SiteTemperature` always, sets `SitePressure`
only when `PressureSource != StandardAtmosphere`.

### Wiring (consumers)

1. **`ProfileData`**: append `double? SitePressureHPa = null, double? SiteTemperatureCelsius = null`
   trailing params. Serializer picks them up; old profiles deserialize with nulls.
2. **`IMountDriver.TryGetTransformAsync`**: the default interface method has no access
   to weather/profile. Add optional params:
   ```csharp
   async ValueTask<Transform?> TryGetTransformAsync(CancellationToken ct, SiteConditions? conditions = null)
   ```
   When `conditions` is null keep temperature 10 but **drop the hardcoded 1010** in
   favour of leaving pressure NaN (auto-derive) - this alone retires the "standard
   atmosphere" TODO. When provided, `conditions.ApplyTo(transform)`.
   (Optional-param addition on a default interface method is source-compatible for all
   existing call sites; check no class overrides `TryGetTransformAsync` - none known.)
3. **Session**: add a small private helper `Session.ResolveSiteConditions()` that calls
   `SiteConditions.Resolve(Setup.Weather?.Driver, Configuration.SitePressureHPa, Configuration.SiteTemperatureCelsius)`
   and pass it at the four session call sites (Imaging x2, Timing, Obstruction).
   `SessionConfiguration` gains `double? SitePressureHPa = null, double? SiteTemperatureCelsius = null`
   trailing params; `SessionFactory` copies them from `ProfileData` (mirror how
   `SiteLatitude/SiteLongitude` already flow).
4. **`TransformFactory.FromProfile`**: use profile temp when set (else keep the existing
   15 C to avoid re-baselining planner outputs - note the 15-vs-10 inconsistency in the
   xmldoc), set pressure only when the profile provides it.
5. **Polar alignment handler** (`AppSignalHandler.cs:2385-2401`): replace the inline
   block with `SiteConditions.Resolve(weather, profileData.SitePressureHPa, profileData.SiteTemperatureCelsius)`
   - this inserts the missing tier 2 and deletes duplicated logic. Signal handler stays
   route-only (the logic moves INTO the lib resolver, per feedback_signal_handler_routing).
6. **Equipment tab site editing** (GUI + TUI): two optional inputs "Pressure (hPa)" and
   "Temperature (C)" in the site section, blank = unset; extend
   `EquipmentActions.SetSite(data, lat, lon, elev?, pressureHPa?, temperatureC?)`.
   Render under the existing site block; consider `IsAdvanced` placement
   (reference_advanced_device_settings) since most users should leave these blank and
   let a weather device or the standard atmosphere win.

### Weather staleness caveat

`OpenMeteoDriver` populates current conditions on `GetHourlyForecastAsync`, not on
connect. `Resolve` must therefore tolerate NaN from a connected-but-not-yet-polled
weather driver (it does, by design). Do NOT add a forecast fetch inside `Resolve` -
it is a pure function; freshness is the caller's existing concern
(`FetchWeatherForecastAsync` already runs on profile load / weather assignment).

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | `SiteConditions` resolver + `ApplyTo(Transform)` + unit tests | S |
| 2 | `ProfileData` + `SessionConfiguration` fields; `SessionFactory` pass-through | S |
| 3 | `TryGetTransformAsync` optional param (drop 1010 -> NaN auto-derive); Session helper + 4 call sites; `TransformFactory.FromProfile` | M |
| 4 | Polar-align handler swap to resolver (tier 2 inserted) | S |
| 5 | Equipment tab GUI + TUI site-section inputs + `EquipmentActions.SetSite` extension | M |
| 6 | Docs: tick TODO items (`IMountDriver.cs:344-347` trio + the Sequencing/Session item), polar-alignment.md cross-ref, summary.md row | S |

## Tests

Unit (`TianWen.Lib.Tests`):

1. `SiteConditionsTests.Resolve_WeatherProvidesBoth_WeatherWins` (FakeWeatherDriver:
   12.5 C / 1013.25 hPa).
2. `Resolve_WeatherNaNPressure_ProfilePressureUsed_TemperatureFromWeather` - per-value
   independence (NSubstitute `IWeatherDriver` with `Pressure` returning NaN).
3. `Resolve_NoWeatherNoProfile_StandardAtmosphere` + `ApplyTo` leaves
   `Transform.SitePressure` NaN (assert auto-derive: a 2000 m site computes ~795 hPa,
   not 1010 - lock the barometric formula in).
4. `Resolve_ProfileOutOfRange_FallsThrough` (pressure 50 hPa -> standard).
5. `ProfileDataTests`: JSON round-trip with and without the new fields (old-profile
   compat: deserialize a fixture without the fields -> nulls).
6. `TransformTests`: new case - refraction on, pressure unset, elevation 1500 m:
   event times differ from the flat-1010 result (pins the behavior change of phase 3).

Functional: existing Session suites must stay green (the transform call sites change
shape but same values at sea level with no weather device: temp 10, derived pressure
~1013 vs old 1010 - verify no pinned RA/Dec/time assertions move; investigate any that
do rather than re-pinning blind).

## Out of scope

- Refracted-pole back-projection in `PolarOverlay` (`RefractedPoleRaHours` aliasing the
  true pole) - separate "Phase 4 polish" item in polar-alignment.md; this plan
  only feeds it correct inputs.
- Using weather temperature for camera warm-up target (`CoolCamerasToAmbientAsync`
  ambient) - separate TODO item, different consumer.
- Humidity/wavelength refinement of SOFA refraction args (currently fixed 0.85 rh /
  0.57 um at Transform.cs:758-772) - could read `IWeatherDriver.Humidity` later;
  keep out to limit re-baselining.
- Per-driver native refraction handling (the third TODO at IMountDriver.cs:398,
  `Refraction = true` assumption) - tracked separately in TODO.md Mount/Meade section.
