# Alt-Az (and fork) mount support

Status legend: ✅ shipped · 🔜 next · ⛔ blocked/deferred

## Summary

SkyWatcher mounts like the **AZ-GTi** are dual-mode: the *same* hardware runs equatorial (EQ, on a
wedge) or alt-azimuth (AZ, flat). TianWen's SkyWatcher driver is **equatorial-only** today. This plan
covers (a) what already ships to make alt-az *safe*, and (b) the full work to make alt-az actually
*usable*.

**Shipped (Phase 0):** a user-selectable `?alignment=GermanPolar|Polar|AltAz` setting on the SkyWatcher
device (default `GermanPolar`). `AltAz` is **report-only**: `GetAlignmentAsync` returns it, pier-side
reads return `Unknown`, and the session's GEM-only meridian-flip gate skips the mount — but coordinate
slews, sidereal tracking, and RA/Dec sync are **refused** with `NotSupportedException`, so an alt-az
mount can never be driven with the equatorial transforms and silently mis-point. See
`SkywatcherMountDriverBase.EnsureEquatorialAlignment`. The meridian-flip gate itself is GEM-only as of
the `AlignmentMode.GermanPolar` check in `Session.Imaging.cs` (`ImagingLoopAsync`).

**Not done:** everything needed to actually *point, track, guide, and image* in alt-az (Phases 1–3).

## Why alignment is a user setting, not auto-detected

The SkyWatcher motor-controller protocol **cannot report whether the mount is in AZ or EQ mode** —
it's the same hardware either way:

- `:e` (firmware/model) returns the same model code (AZ-GTi = `0xA5`/`0xC5`) regardless of how the
  mount is physically set up.
- `:q` capabilities exposes a `CanAzEq` bit, but that only means "supports both modes", not "currently
  in AZ".

This is why the SynScan app *asks* at connect and GSServer makes `AlignmentMode` a persisted user
setting. We mirror GSServer: a setting, defaulting to `GermanPolar`.

**Home position:** in alt-az the SkyWatcher home is the raw encoder zero — **az 0 (north), alt 0
(horizontal)** — not the equatorial pole (`HomeAxisX/Y = 0/0` in GSS's AltAz profile vs `90/90` for
GermanPolar). The driver's `MaybeSyncToPoleAfterSiteSetAsync` is skipped in alt-az for this reason.

## Reference: how GSServer handles AZ vs EQ

GSS uses ASCOM's standard `AlignmentModes` (`algAltAz` / `algPolar` / `algGermanPolar`) as a user
setting (`SkySettings.AlignmentMode`) with a separate settings profile per mode. In `algAltAz`:

| Concern | GSS behavior (`Axes.cs` / `SkyServer.cs`) |
|---|---|
| Axis transforms | **Identity** — the two motor axes *are* Az/Alt (no through-pole / hemisphere fold) |
| RA/Dec ↔ axes | `RaDec2AltAz(lst, latitude)` / `AltAz2RaDec` |
| Meridian flip | `IsFlipRequired → false`; `DetermineSideOfPier → pierUnknown`; `CanSetPierSide → false` |
| Tracking | Dual-axis predictor: a timer recomputes **both** Az and Alt rates (vs EQ single-axis sidereal `:I`) |
| Pulse guiding | Mini-GOTO in Az/Alt space; Dec sense is a plain South sign-flip (no pier side) |
| ASCOM reporting | `AlignmentMode=algAltAz`, `CanSetPierSide=false`, `DestinationSideOfPier=pierUnknown` |

## Gap analysis — what full alt-az support needs

Each item lists the concrete TianWen touchpoint.

### 1. Az/Alt ↔ encoder-step transforms 🔜
`SkywatcherMountDriverBase.RaToSteps` / `StepsToRa` / `DecToSteps` / `StepsToDec` are equatorial
(HA/Dec, south-mirror, through-pole). Alt-az needs a parallel path: target RA/Dec → Az/Alt (via LST +
site latitude) → axis steps (≈identity, home = az0/alt0), and the inverse for position reads. The
shared transform helper `IMountDriver.TryTransformJ2000ToMountNativeAsync` already yields `Az`/`Alt`
alongside `RaMount`/`DecMount`, so the input is available — the SkyWatcher step math is what's missing.

### 2. `BeginSlewToTargetAsync` pier-side gate 🔜 (shared, affects all drivers)
`IMountDriver.BeginSlewToTargetAsync` refuses the slew when `DestinationSideOfPierAsync == Unknown`.
For an alt-az mount that *legitimately* has no pier side, this is a hard block (today it's exactly
what makes Phase-0 reject work). Full support must bypass the pier-side gate when alignment is alt-az.
This is an `IMountDriver`-layer change, so it also unblocks **ASCOM/Alpaca** alt-az mounts (which
already report `algAltAz` natively).

### 3. Dual-axis alt-az tracking 🔜
`SkywatcherMountDriverBase.SetTrackingAsync` runs a single RA axis at sidereal. Alt-az tracking needs
both axes driven at continuously-recomputed rates (a predictor + periodic re-rate, like GSS's
`AltAzTrackingTimerEvent`). New subsystem in the driver.

### 4. Alt-az guiding ⛔ (after 1–3)
`PulseGuideCoreAsync` maps N/S/E/W to the Dec/RA axes (equatorial). In alt-az, corrections map to
Alt/Az and the field **rotates continuously**, so:
- the built-in guider's calibration would drift as the field rotates (calibration assumes a fixed
  camera-to-axis angle);
- the pier-side Dec-sense invariant (`CalibrateGuiderAsync` slews to HA −0.5h; see CLAUDE.md "Guider
  calibration pier-side invariant") is meaningless for alt-az and needs an alt-az calibration pose.

Likely outcome: short unguided subs for alt-az, or a substantially different guiding model.

### 5. Field rotation / derotator ⛔ (the imaging blocker)
Alt-az mounts rotate the field while tracking, smearing long exposures. Real imaging needs **one** of:
- a mechanical **derotator** — `IRotatorDriver` / `DeviceType.Rotator`, which **does not exist yet**
  (it's a backlog item: TODO.md "Rotator device type"); or
- short subs + **software derotation** in the stacker; or
- accept alt-az for EAA / plate-solve / visual only (no long-exposure imaging).

This gates alt-az *imaging* independently of the mount-driver work.

### 6. Position reads in alt-az 🔜
`GetRightAscensionAsync` / `GetDeclinationAsync` run the equatorial `StepsToRa`/`StepsToDec` on the
encoder, so at the alt-az home `(0,0)` they report a nonsense RA/Dec. Full support converts the Az/Alt
encoder position → RA/Dec via site + time. `EquatorialSystem` stays `Topocentric`.

### 7. `Polar` (fork-on-wedge equatorial) — minor, separate
`Polar` is equatorial (RA/Dec) with no flip; the `#47` gate already skips flips for non-`GermanPolar`.
Our equatorial transforms are GermanPolar-shaped (south mirror / through-pole); whether a fork-Polar
mount needs different handling (GSS distinguishes Polar Left/Right) is unverified. Low priority — the
AZ-GTi is not a fork mount. For now `Polar` rides the equatorial path and skips the flip.

## Phasing

| Phase | Scope | Enables | Status |
|---|---|---|---|
| 0 | Alignment setting + report-only + reject; GEM-only flip gate | Safe: never mis-points an alt-az mount | ✅ |
| 1 | Items 1, 2, 3, 6 | Correct alt-az **GOTO + tracking + position** (visual / EAA / plate-solve & sync) — no imaging | 🔜 |
| 2 | Item 4 | Alt-az guiding (or a documented "unguided short subs" stance) | ⛔ after 1 |
| 3 | Item 5 | Long-exposure alt-az **imaging** (gated on rotator support, itself a TODO) | ⛔ |

## Testing

- `FakeSkywatcherSerialDevice` already models the motor controller; a fake alt-az path would interpret
  the two axes as Az/Alt. Phase-1 tests: a GOTO lands at the correct az/alt; tracking keeps a target's
  az/alt converging over fake time; position reads round-trip RA/Dec ↔ Az/Alt.
- Phase-0 (shipped) is covered by `FakeSkywatcherMountDriverTests`:
  `GivenAltAzAlignmentThenReportedButCoordinateOpsRefused` (report + reject) and
  `GivenNoAlignmentQueryThenDefaultsToGermanPolar` (default unchanged), plus the GEM-only flip
  `[Theory]` in `SessionObservationLoopTests`.

## Recommendation

Most AZ-GTi *imagers* run the mount on an equatorial wedge (EQ mode) precisely to avoid field rotation
— and that path already works as `GermanPolar`. Phase 0 makes the alt-az case **safe** rather than
silently wrong. Pursue **Phase 1** if there's demand for visual / EAA / plate-solve use of an alt-az
mount; **defer Phase 3 (imaging)** until `IRotatorDriver` support lands, since long-exposure alt-az
imaging is impossible without derotation regardless of how good the mount driver is.
