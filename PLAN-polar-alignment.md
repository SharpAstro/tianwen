# Plan: Polar Alignment Routine (SharpCap-style)

Goal: assist the user in mechanically polar-aligning the mount before a session
by computing the actual mount RA-axis from two plate-solved frames separated by
a known RA rotation, decomposing the offset against the true celestial pole into
**altitude** and **azimuth** errors, and showing live error updates while the
user adjusts the polar-aligner knobs — exactly as SharpCap does, but inside
TianWen's GUI/TUI and reusing TianWen's plate solvers, cameras, and guider
shims.

Reference behaviour: <https://www.sharpcap.co.uk/sharpcap/features/polar-alignment>

## Preconditions (gating)

The polar-alignment tab/action is **only enabled** when:

1. **Mount is manually connected** (i.e. the user is *not* in a running
   `Session`; we don't co-opt session resources mid-run).
2. **Site location is known** — `Profile.SiteLocation` has lat/lon/elevation.
3. **A plate solver is configured** in the active profile (ASTAP or
   astrometry.net).
4. **A capture source is selected** — either:
   - One of the `Setup.Telescopes[i].Camera`s (main imaging cam), or
   - The connected `IGuider` (built-in or PHD2). PHD2 must be configured to
     save frames to disk; the existing `IGuider.SaveImageAsync` contract
     already requires this.

   **Auto-selection policy** (when more than one source is available):
   we want the **fastest combo** — shortest practical exposure to a
   plate-solve-able frame. Rank candidates by score and pick the highest:
   1. Compute **f-ratio** = `focalLengthMm / apertureMm` for each candidate
      OTA (telescope or guide scope).
   2. Compute **pixel scale** in arcsec/px = `206.265 × pixelSizeUm /
      focalLengthMm`. Plate solvers want roughly 1-5"/px; outside that
      band the solve is slower and less reliable.
   3. Score = `1 / fRatio` × `clamp(pixelScale, 1, 5)` (smaller f-ratio
      and pixel-scale-in-band both raise score). A 50mm f/4 mini-guider
      with a small-pixel guide cam typically wins over an f/10 SCT main
      cam, which matches operator intuition: short FL = wide field = more
      reference stars and more refraction-resilient at low pole altitudes.
   4. Tie-break: prefer **main camera** over guider (no PHD2 RPC latency,
      no `Save Images` configuration dependency).
   5. UI surfaces the chosen source and the runner-up; user can override.
5. **Mount can slew freely** in RA — no tracking-only mounts; we need at
   minimum a `BeginSlewToCoordinatesAsync` to apply the calibration delta.

The button surface explains *which* precondition failed when disabled, no
silent greying-out.

## Algorithm

### Phase A — Two-frame axis solve

Given two plate-solved frames separated by a **pure RA-axis** mount rotation
of known angle Δ (default 60°, configurable 30°-90°), compute the mount's
polar axis in J2000:

1. **Capture-and-solve P1** at the current mount position. Output: J2000
   RA/Dec of the frame center, `v1 = unit(RA1, Dec1)`.
2. **Pure-axis rotate** the mount around its primary (RA) axis by Δ via
   `MoveAxisAsync(TelescopeAxes.Primary, rate)` for a precise duration
   `Δ / rate`. **Do not** use `BeginSlewToCoordinatesAsync` here — see the
   "Cone error & rotation mechanism" subsection below for why goto is wrong.
3. Wait `Configuration.PolarAlignmentSettleSeconds` (default 5 s) for mount
   settle, then **capture-and-solve P2**. Output: `v2 = unit(RA2, Dec2)`.
4. **Compute the rotation axis** `A` such that `Rot(A, Δ) · v1 = v2`. Δ is
   the *commanded* rotation — accurate by construction because `MoveAxis` is
   a raw axis-rate command (no pointing model, no encoder coordinate
   transform). `A` is a unit vector in J2000 — the mount's polar axis as
   the mount sees it right now.

   Math: with v1, v2, Δ given, the axis lies in the plane perpendicular to
   the chord `(v2 - v1)` passing through the chord midpoint, at a known
   angular distance from each endpoint determined by the half-cone:
   ```
   chord = v2 - v1
   d     = |chord| / 2                       // half-chord length
   tan(α/2) where α = Δ                      // rotation angle
   r     = d / sin(Δ/2)                      // small-circle radius (sphere)
   m     = normalize((v1 + v2) / 2)          // chord midpoint on sphere
   n     = normalize(v1 × v2)                // chord plane normal
   axis  = m * cos(asin(r)) ± n * sin(asin(r))   // two candidates
   ```
   Pick the candidate sign with `(v1 × v2) · A > 0`. Tested against
   synthetic rotations in unit tests; `PolarAxisSolver` class.

   **Sanity check.** Independently compute `θ_observed = acos(v1·v2)` and
   the predicted chord angle `θ_predicted = 2·asin(sin(Δ/2)·sin(d_axis))`
   where `d_axis = acos(v1·A)` is the cone half-angle to the recovered axis.
   `|θ_observed − θ_predicted|` should be < ~5 arcsec; larger means
   mid-rotation slew error or a miscommanded Δ — flag it to the user.

5. **Decompose against the apparent pole** for the site (refraction-aware):
   - Compute the true celestial pole's J2000 unit vector `P_true`.
   - Use `Transform` (SOFA) with `Refraction = true`, site lat/lon/elev, plus
     **live pressure + temperature** (see "Refraction at low pole altitudes"
     subsection below) to convert `P_true` to topocentric **(Az_apparent,
     Alt_apparent)** — this is where the mount axis *should* point so that
     refraction-corrected tracking holds a star steady.
   - Convert `A` to topocentric `(A_az, A_alt)` via the same `Transform`.
   - `azError = A_az - Az_apparent` (wrap to [-180°, 180°])
   - `altError = A_alt - Alt_apparent`
   - Both reported in **arcminutes** with sign convention "user must rotate
     polar adjuster in this direction by this amount".

### Cone error & rotation mechanism

Cone error is the angle between the OTA optical axis and the *plane*
perpendicular to the mount's Dec axis. With a guide scope ringed onto the
saddle this can be 0.5°-2° easily; even with a main OTA on a Losmandy plate
it's typically a few arcmin. The user's question: does this bias polar
alignment?

**Math answer: no.** A pure rotation around the RA axis traces *any* point
in space (including a cone-offset OTA optical axis) along a small circle
centred on the RA axis. The recovered axis from `(v1, v2, Δ)` is
mathematically the rotation axis, regardless of where v1 sits relative to
that axis. Cone error simply changes the small-circle radius (the
`d_axis = acos(v1·A)` term in the sanity check), not the recovered axis
direction. Same applies to a guide scope: as long as both frames are
captured through the *same* optical path (same OTA, no rotation/refocus
mid-routine), the geometry holds.

**Mechanism answer: critical.** The math only holds if the rotation between
P1 and P2 is a *pure RA-axis rotation*. `BeginSlewToCoordinatesAsync(RA1+Δ,
Dec1)` is **not** that — it's a goto to a sky coordinate, which on a mount
with an active pointing model (T-Point, PointXP, ASCOM-derived sync model)
will adjust *both* axes to land on the commanded sky position, breaking
the pure-RA-rotation assumption. We therefore use `MoveAxisAsync(Primary,
rate)` for a timed duration:

- ASCOM: `MoveAxis(TelescopeAxes.axisPrimary, rate)` — explicitly raw axis,
  bypasses the model.
- Skywatcher: documented motor-rate command, raw axis.
- OnStep: `:Mn`/`:Ms`/`:Me`/`:Mw` at custom slew rate, raw.
- iOptron (SgP): `:MnXXXX#` direction at rate, raw.
- All present in `IMountDriver` — no new mount API needed.

A 60° rotation at 16× sidereal (240"/s) takes 60° × 3600 / 240 = 900 s = 15 min.
Too slow. We use the **rate one step below max** from the mount's
`AxisRates` list (already wired in PR `dab10b6` "ASCOM: implement AxisRates
and MoveAxis via IDispatch sub-objects"). Max slew rate introduces
mechanical wobble on most amateur mounts — saddle flex, gear-mesh chatter,
counterweight oscillation — which biases the chord midpoint and shows up
as a recovered-axis error of several arcmin even on a perfectly aligned
mount. The rate just below max is typically 50-70% of max in absolute
terms (e.g. on a Skywatcher EQ6-R: max=800× sidereal, one-below=600×; on
an iOptron CEM40: max=64×, one-below=32×) and still finishes a 60°
rotation in 10-30 s — fast enough.

`AxisRates` enumeration policy:
1. If the driver returns a discrete rate list of length ≥ 2, pick `rates[Length - 2]`.
2. If it returns a single rate or a contiguous range, pick `0.7 × max_rate`
   (rounded into the supported range).
3. If `AxisRates` is unavailable, fall back to a configured default of 8°/s
   (well below the typical max for any mount that has `MoveAxis` at all)
   and document the precision impact.

**Δ in practice:** we measure with a `Stopwatch`, multiply by the chosen
rate, and use that as Δ. The chord-angle sanity check from Phase A step 4
catches both rate-table errors and mechanical wobble (wobble shows up as
chord-angle inflation beyond what `(Δ, d_axis)` predicts).

If a mount lacks `MoveAxis` entirely (hypothetical), fall back to
pulse-guide RA-only commands at sidereal multiples — slower but model-free.

### Refraction at low pole altitudes

Refraction lifts the apparent position of every object near the horizon —
including the celestial pole as seen from low-latitude sites. A user at
**lat = 15° (e.g. Hawaii, southern Mexico)** sees the celestial pole at a
true altitude of 15° but an apparent altitude of ~15° + 3.4' (Bennett's
refraction at h=15° at standard atmosphere). At **lat = 0° (equator-ish,
not a polar-mount-friendly latitude but still)** refraction at h=0° is ~34'
— half a degree off. Even a temperate site at lat = 35° has ~1.4' of pole
lift. For a polar alignment routine targeting **1' arcmin accuracy**, this
is a first-order correction, not a polish.

The decomposition therefore aligns the mount axis with the **apparent
pole** (true pole + refraction), so that a star at HA=0 stays put under
refraction-corrected tracking. This requires:

- **Site pressure (hPa)** and **temperature (°C)**.
- Source priority:
  1. `IWeatherDriver.Pressure` / `IWeatherDriver.Temperature` if a weather
     device is connected (already exposed by `OpenMeteoDriver` and any
     ASCOM ObservingConditions device).
  2. `Profile.SiteTemperature` / `Profile.SitePressure` if user-configured
     (these don't exist yet; **add** them to `ProfileData` in this plan,
     defaulting to standard atmosphere 10°C / 1010 hPa).
  3. Standard atmosphere fallback (10°C, 1010 hPa). Show the source on the
     UI panel so the user knows whether refraction is *measured* or
     *assumed* — a lat=15° user with assumed sea-level pressure but actual
     1500m elevation gets ~1' of residual error, worth surfacing.
- Pass into `Transform.SiteTemperature` / `Transform.SitePressure` with
  `Transform.Refraction = true` (flag already exists). The SOFA pipeline
  produces refraction-corrected topocentric coordinates directly.

This **also retires the stale TODO at `IMountDriver.cs:395-396`**
(`SitePressure = 1010 // standard atmosphere`, `SiteTemperature = 10 //
TODO check either online or if compatible devices connected`) — which is
exactly the gap the user is flagging. Same code path, same fix.

### Adaptive exposure ramp

Plate-solve speed is dominated by stars-found-per-second, which is dominated
by exposure time. We do **not** hardcode a 2 s exposure — we ramp from short
to long until a solve succeeds, then **lock that exposure for the rest of
the routine** (refraction and pole-altitude don't change between phases A
and B, so the FOV brightness is stable enough that the first successful
exposure is the right one for every subsequent capture).

**Ramp**: 100 ms → 150 ms → 200 ms → 250 ms → 500 ms → 1 s → 2 s → 5 s.
Stop at the first exposure that yields a successful plate solve (≥ N stars
matched, default N=15, same gate as `InitialRoughFocusAsync`). On failure
at 5 s, surface a clear "no solve at 5 s — check focus, dew, light pollution"
message instead of climbing to 30 s; if the user can't solve a 5 s wide-FOV
frame, longer exposures are not the cure.

**Persistence within a session**: the discovered exposure is stored on
`PolarAlignmentSession._lockedExposure` after the first success and reused
for the second-frame capture and the entire Phase B refinement loop. No
per-frame ramping during refinement — the user expects a steady cadence
once the routine starts producing numbers.

**Persistence across runs** (optional, low priority): cache last successful
exposure per `(captureSourceKey, fRatio bucket)` in `Profile.PolarAlignment`
state so a returning user starts at the previously winning exposure, not
at 100 ms. v1 keeps this in-memory only.

**Wired into `ICaptureSource`** as:
```csharp
ValueTask<CaptureResult> CaptureAndSolveAsync(
    TimeSpan exposure, IPlateSolver solver, CancellationToken ct);
// CaptureResult = (success, wcs, starsMatched, exposureUsed, fitsPath?)
```
The orchestrator owns the ramp loop; the capture source just exposes-and-solves.

### Phase B — Live refinement

After the two-frame solve completes, enter a **live refinement loop**:

1. Continuously **capture-and-solve** at the locked exposure from the
   adaptive ramp (so cadence is `exposure + solve_time`, typically
   200 ms - 2.5 s depending on optics; see "Adaptive exposure ramp" above).
2. For each new solve, recompute `A` using:
   - The original anchor frame (`v1`) plus the new live frame (`v_live`),
     **OR**
   - A rolling pair (the most recent two solves) — anchor frame becomes the
     stale one as the user rotates the knobs.
   We use the **anchor strategy**: keep `v1` fixed (it was a known, deliberate
   geometry); the live frame moves as the user adjusts. This is what
   SharpCap does. The user is told not to rotate the mount in RA during
   refinement (only adjust Alt/Az knobs).
3. Update the display with the new `(altError, azError)` ~once per solve.
4. Show the **trend**: where the error vector was 5 s ago vs. now → an arrow
   indicating "you're moving in the right/wrong direction".
5. **Convergence**: when both errors fall below
   `Configuration.PolarAlignmentTargetAccuracy` (default 1 arcmin), play a
   confirmation sound / colour-flash the panel. User clicks "Done" to exit.

### "Bring it back" — pole-centric error overlay

Numerical arcmin readouts are necessary but not sufficient. SharpCap's
polar-alignment tool overlays the live frame with a **pole-centric
target reticle** (see reference image): two pole crosses (true vs
refracted), concentric rings at 5'/15'/30' centred on the refracted pole,
and a separate marker for the **current rotation axis**. The user
adjusts the polar-alignment knobs and watches the rotation-axis marker
drift toward the bullseye. When it sits inside the 5' ring on the
refracted pole cross, alignment is achieved.

This converts a 2-DOF arcmin readout ("Az 4'12" left, Alt 1'48" up") —
which is hard to map to physical knob turns, especially because alt and
az knobs cross-couple on most wedges — into a single direct manipulation:
move the marker into the bullseye.

**Overlay elements** (drawn on the live FITS surface in render order):
1. **Concentric error rings** at 5', 15', 30' (configurable; mirrors
   SharpCap exactly), centred on the refracted pole's pixel position,
   labelled "5'", "15'", "30'" along the right side. Faint green stroke,
   2 px line width.
2. **True pole cross** (`+`, white, 12 px) labelled "NCP (True)" /
   "SCP (True)" depending on hemisphere. Computed from `P_true` (J2000
   pole as a unit vector) projected through `WCS_live`.
3. **Refracted pole cross** (`+`, green, 12 px) labelled "NCP (Refracted)"
   / "SCP (Refracted)". Computed from `P_apparent` (true pole pushed
   through the refraction transform from the "Refraction at low pole
   altitudes" subsection). The rings centre on **this** point.
4. **Current rotation axis marker** (filled circle + ring, 8 px, colour
   ramps green→yellow→red as distance from refracted pole grows past
   5'/15'/30' thresholds). Computed from `A_live` projected through
   `WCS_live`. As the user adjusts knobs, this marker drifts toward
   the centre.
5. **Detected stars** (already drawn by TianWen's star overlay — small
   coloured squares, yellow for solve-matched, red for over-saturated).
   Same look as the reference image.
6. **Status / instruction text** in orange below centre, e.g. "Press
   'Next' before rotating the RA axis", "Refining: 4'12" Az / 1'48" Alt",
   "Aligned within 1' — done".
7. **Direction hints** as small arrows on the rotation-axis marker
   pointing toward the refracted pole, plus per-axis text "Alt: ↑ 1'48"",
   "Az: ← 4'12"" so the user knows which knob to turn even when the
   marker is off-frame. See "Correction buttons / direction hints" below.

**Re-using TianWen's WCS grid pipeline**. The fragment-shader-based WCS
grid overlay (`WcsGridOverlay`, see CLAUDE.md "FITS Viewer / GPU Stretch"
— "WCS coordinate grid overlay rendered in the fragment shader with
per-pixel TAN deprojection") already has everything needed: per-pixel
sky-coord lookup, line-list rendering, label placement. The polar overlay
extends it with one new mode `WcsGridMode.PolarAlignment` that:
- Suppresses the normal RA/Dec grid lines.
- Draws great-circle small-radius rings around `P_apparent` at the
  configured radii — same geometry as a parallel of declination, but
  centred on `P_apparent` instead of the J2000 pole.
- Draws the four pole/axis markers via a tiny additional vertex/index
  buffer, each as a 12 px screen-space cross+label.

No new shader pipeline needed; we add ring-radius and pole-position
uniforms to the existing grid UBO.

**Off-sensor handling**. If the rotation-axis marker is way off-sensor
(initial misalignment > FOV / 2), draw an edge arrow pointing toward it
with the offset distance in arcmin: "axis 47' off-frame ↗". Once the
marker enters the sensor, switch to the in-sensor circle.

**Computation**:
- `P_true` and `P_apparent` are computed once per live solve (refraction
  changes slowly; recompute on each frame is cheap, no caching needed).
- `A_live` updates per solve; live frame WCS gives the pixel projection
  for free.
- All published via `LiveSolveResult.PolarOverlay` (an immutable record):
  ```csharp
  internal readonly record struct PolarOverlay(
      Vector2 TruePolePx,
      Vector2 RefractedPolePx,
      Vector2 CurrentAxisPx,
      ImmutableArray<float> RingRadiiArcmin,
      double AzErrorArcmin,
      double AltErrorArcmin,
      Hemisphere Hemisphere);
  ```
  GUI and TUI consume it via the existing tab state. TUI degenerates
  into a text panel: "NCP-R at (320, 240) | axis at (412, 198) | Az 4'12"
  E, Alt 1'48" S".

### Correction buttons / direction hints

Beyond the visual overlay, surface explicit nudge guidance:
- **Az: ← 4'12"** with a left-arrow icon, beside an info tooltip telling
  the user which knob direction maps to "left" (mount-dependent — for an
  EQ6-R wedge, west is one specific knob; we can't know the exact knob
  rotation, but we can tell them which sky direction to push toward).
- **Alt: ↑ 1'48"** likewise. Direction maps to elevation knob.
- The **arrows blink** when the user appears stuck (no improvement over
  the last 10 s), suggesting they're turning the wrong way.
- **No motorised "auto-correct" button**: this is a manual mechanical
  process. We don't issue any motion commands during refinement other
  than the optional reverse-restore on Done (see below).

### Mount-position recovery — the literal "bring it back"

The user's phrase has a second, separate meaning that's also worth handling:
after Phase A's MoveAxis rotation, the mount is physically **60-90° off
where it started** in RA. Some users will naturally want to move the mount
back to a known parking position before/after refinement. We handle this
two ways:

1. **Auto-restore on cancel/done**: `PolarAlignmentSession.DisposeAsync`
   issues `MoveAxisAsync(Primary, -rate)` for the *recorded duration of
   the original rotation* (Stopwatch from Phase A step 2, inverted),
   bringing the mount back to within ~arcmin of its pre-routine HA. We
   intentionally do **not** use a goto — same reasoning as Phase A: the
   pointing model has already been invalidated by the mid-routine rotation
   on a model-using mount, and a goto would slew through the model and
   miss. Raw-axis reverse is correct.

2. **"Park here" / "leave here"** dialog on completion: after convergence,
   ask the user whether to (a) reverse-axis back to pre-routine pose so
   they can resume their planned target list, (b) park, or (c) leave the
   mount where it is (e.g. they want to keep imaging from this pose).
   Default = (a). User preference persisted to `Profile.PolarAlignment.OnDone`.

Both behaviours need only the original Stopwatch reading + the original
rate — no new mount API, no encoder-position polling, no driver-specific
logic. Records of (rate, duration) live on the session struct.

## Components

### New code

- `src/TianWen.Lib/Astrometry/PolarAxisSolver.cs`
  - Pure math; no I/O. `(Vector3 v1, Vector3 v2) → Vector3 A`,
    `(A, siteLat, siteLon, time) → (azError, altError)`.
  - Tested in `TianWen.Lib.Tests/Astrometry/PolarAxisSolverTests.cs` with
    synthetic inputs (rotate `v1` by known axis/angle → expect axis recovery
    to <1 arcsec; rotate by Δ near-pure-pole → expect zero error; offset
    pole by 1° → expect 60' error).
- `src/TianWen.Lib/Sequencing/PolarAlignment/PolarAlignmentSession.cs`
  - Orchestrator. Takes `IExternal`, `IMountDriver`, the chosen `ICaptureSource`
    (see below), `IPlateSolver`, `Site`. Exposes:
    ```csharp
    ValueTask<TwoFrameSolveResult> SolveAsync(double deltaRaDeg, CancellationToken ct);
    IAsyncEnumerable<LiveSolveResult> RefineAsync(CancellationToken ct);
    ValueTask CancelAsync();
    ```
  - **Not** part of `Session` — runs against the manually-connected mount
    only. Uses `ResilientCall.AbsoluteMove` for the slew; idempotent reads
    for status polls.
- `src/TianWen.Lib/Sequencing/PolarAlignment/ICaptureSource.cs` — abstraction
  unifying main-camera and guider-camera capture for solver use:
  ```csharp
  internal interface ICaptureSource
  {
      ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(
          TimeSpan exposure, IPlateSolver solver, CancellationToken ct);
      ValueTask<ImageDim?> GetImageDimAsync(CancellationToken ct);
      string DisplayName { get; }
      double FocalLengthMm { get; }
      double ApertureMm { get; }
      double PixelSizeMicrons { get; }
      double FRatio => FocalLengthMm / Math.Max(ApertureMm, 1);
      double PixelScaleArcsecPerPx => 206.265 * PixelSizeMicrons / FocalLengthMm;
  }

  internal readonly record struct CaptureAndSolveResult(
      bool Success,
      WCSGrid? Wcs,
      int StarsMatched,
      TimeSpan ExposureUsed,
      string? FitsPath);
  ```
  The optics properties drive the auto-selection ranking (see
  Preconditions §4) and the BringItBackOverlay computation (pixel scale
  feeds the WCS-pixel-to-world conversion). Two implementations:
  - `MainCameraCaptureSource` — wraps `ICameraDriver`, calls
    `StartExposureAsync` + `GetImageAsync` + writes FITS via the existing
    `FitsWriter`, then invokes the plate solver.
  - `GuiderCaptureSource` — delegates to `IGuider.LoopAsync` + `SaveImageAsync`
    (already implemented for PHD2 and built-in guider; PHD2 needs
    `Save Images` enabled in its profile, which is the existing contract),
    then invokes the plate solver on the saved frame.
- `SessionConfiguration` additions (re-used as `PolarAlignmentConfiguration`
  if we want a separate type — cheaper to extend the existing one):
  - `ImmutableArray<TimeSpan> PolarAlignmentExposureRamp = [100ms, 150ms, 200ms, 250ms, 500ms, 1s, 2s, 5s]`
  - `int PolarAlignmentMinStarsForSolve = 15`
  - `double PolarAlignmentRotationDeg = 60.0`
  - `double PolarAlignmentSettleSeconds = 5.0`
  - `double PolarAlignmentTargetAccuracyArcmin = 1.0`
  - `PolarAlignmentOnDone PolarAlignmentOnDone = PolarAlignmentOnDone.ReverseAxisBack`
    (`enum { ReverseAxisBack, Park, LeaveInPlace }`)
  - `bool PolarAlignmentSavePAFrames = false`

  The fixed `PolarAlignmentExposure = 2 s` setting is removed — exposure
  is now discovered by the ramp at routine start. See "Adaptive exposure
  ramp" subsection.
- `ProfileData` additions for refraction fallback when no weather device:
  - `double? SiteTemperatureCelsius` (null → 10°C standard atmosphere)
  - `double? SitePressureHPa` (null → 1010 hPa standard atmosphere)
  These also retire the `IMountDriver.cs:395-396` TODO and are useful
  beyond polar alignment (any refraction-aware transform).

### Edits

- `src/TianWen.UI.Abstractions/AppSignalHandler.cs` — new signals:
  - `StartPolarAlignmentSignal(captureSourceKey, deltaRaDeg)`
  - `CancelPolarAlignmentSignal`
  - `PolarAlignmentResultSignal(result)` — broadcast each refine tick.
  Subscribe lambdas only **route**: dispatch to a new
  `PolarAlignmentActions` static helper (mirror `EquipmentActions`,
  `PlannerActions`). Math + I/O lives in `PolarAlignmentSession`.
- `src/TianWen.UI.Gui/Tabs/VkPolarAlignmentTab.cs` — new tab:
  - Top bar: capture source dropdown (auto-picked, runner-up shown in
    grey + tooltip explaining the ranking), Δ-RA dropdown (30/45/60/90°),
    Start/Cancel buttons.
  - Live frame view (re-uses `VkImageRenderer`'s FITS pipeline if main cam,
    or guider's image surface if guider). Overlays driven by
    `LiveSolveResult.PolarOverlay` and the extended WCS-grid shader
    (`WcsGridMode.PolarAlignment`):
    - **5'/15'/30' concentric error rings** centred on `RefractedPolePx`,
      green stroke, ring labels along the right side.
    - **True pole cross** (`+`, white, labelled "NCP/SCP (True)") at
      `TruePolePx`.
    - **Refracted pole cross** (`+`, green, labelled "NCP/SCP (Refracted)")
      at `RefractedPolePx`. Rings centre here.
    - **Current rotation-axis marker** (filled circle + ring) at
      `CurrentAxisPx`, colour ramping green→yellow→red as it crosses
      the 5'/15'/30' thresholds.
    - **Detected stars** (existing TianWen star overlay) — yellow squares
      for solve-matched, red for saturated.
    - **Off-sensor edge arrow** + arcmin label when `CurrentAxisPx` falls
      outside the frame.
    - **Direction hint badges**: "Alt: ↑ 1'48"" and "Az: ← 4'12"" beside
      the axis marker (or pinned to a corner if it's off-sensor).
    - **Status / instruction text** overlay (orange) for "Press 'Next'
      before rotating the RA axis" / "Refining…" / "Aligned ✓".
  - Error gauge sidebar: two horizontal needles (azError, altError), each
    with a coloured zone (green < 1', yellow 1-5', red > 5'); arcmin
    readout; a trend arrow above each.
  - Exposure indicator: shows the locked exposure picked by the ramp
    (e.g. "200 ms · 23 stars matched") so the user knows at a glance
    whether the chosen optics are working well.
  - Status line: "Probing exposure (250 ms)…", "Frame 1/2 ✓", "Rotating",
    "Frame 2/2 ✓", "Refining (1.2 Hz)", "Done — restoring mount".
- `src/TianWen.UI.Abstractions/PolarAlignmentTabState.cs` — new state class
  exposing the latest `LiveSolveResult` as an immutable snapshot. **Use
  `ImmutableArray` and atomic property replacement** per CLAUDE.md.
- `src/TianWen.UI.Gui/Tabs/VkPolarAlignmentTab.cs` and a corresponding
  `TuiPolarAlignmentTab.cs` (TUI gets text gauges instead of graphical
  needles; same state class).

## Phasing

| Phase | Scope | Tests |
|------|------|------|
| **1** | `PolarAxisSolver` math + tests | Unit tests with synthetic rotations |
| **2** | `PolarAlignmentSession` orchestrator + `ICaptureSource` shims + adaptive exposure ramp + auto-selection ranking + axis reverse-restore | Functional test using `FakeMountDriver` + `FakeCameraDriver` (synthetic star field with known mount pole offset, ramp picks shortest exposure that yields ≥15 stars, reverse restores within arcmin) |
| **3** | GUI tab (`VkPolarAlignmentTab`) + signals + actions + pole-centric overlay (extend WCS grid shader with `PolarAlignment` mode + ring/pole markers + axis marker + direction hints) | Manual GUI test |
| **4** | TUI tab parity (text reticle: ASCII arrow toward target offset) | Manual TUI test |
| **5** | PHD2 path verified end-to-end with `Save Images` enabled | Manual real-rig test |

Phases 1-2 are pure code + tests, mergeable independently. Phase 3 is the
big-bang UX. Phases 4-5 can land separately afterward.

## Risks / open questions

- **Plate-solve speed.** ASTAP solves a 1280×960 guide-cam frame in ~0.5-2 s
  on a Pi-class CPU. Refinement at 1 Hz is feasible; 2 Hz is the upper bound.
  The adaptive exposure ramp helps here: a 50mm f/4 mini-guider may solve
  reliably from 100-200 ms exposures, giving a refine cadence of
  ~300-700 ms (exposure + solve). An f/10 SCT with a small-pixel main cam
  may need 2 s, dragging cadence to ~3-4 s — still usable for refinement
  but the user should prefer the guide scope per the auto-selection policy.
  If solves are persistently slow, we may need to plate-solve only every
  Nth live frame and rely on **anchor-fixed math** (cheap: the J2000
  vectors don't change unless the mount moves).
- **Rotation accuracy.** Δ is the time-multiplied axis rate, validated
  against the chord-angle sanity check (see Phase A step 4). If the rate
  reported by `AxisRates` is wrong by 1% (rare but possible on knock-off
  mounts), the recovered axis is biased by ~1% of |Δ| — i.e. ~36 arcmin
  for a 60° Δ. Flag the chord-angle mismatch as an error and fall back to
  the optional 3-frame mode below.
- **Optional 3-frame mode** (deferred to v2): three captures at known
  rotations (0, Δ, 2Δ) determine the small circle uniquely without trusting
  the rotation amount — purely geometric. More robust on suspect mounts at
  the cost of one more capture+solve cycle. Keep the 2-frame mode as the
  default.
- **Pier-side semantics.** With raw `MoveAxis`, the mount stays on the same
  pier side throughout (no flip during a 60-90° axis nudge from a sensible
  starting region). If the user starts at HA ~ -30° going west, a 90°
  rotation would cross the meridian; we should clamp the rotation so it
  doesn't, or warn and let the user pick a different start.
- **Backlash & mount settle.** Allow a configurable settle time (default 5 s)
  after the slew before P2 capture.
- **Refraction.** *Now in v1*, see "Refraction at low pole altitudes"
  subsection above. Pulls live pressure+temperature from `IWeatherDriver`
  if connected (works for `OpenMeteoDriver` and any ASCOM
  ObservingConditions device), else `Profile.SitePressureHPa` /
  `Profile.SiteTemperatureCelsius`, else standard atmosphere. UI surfaces
  the source so the user knows when refraction is *measured* vs *assumed*.
- **Pole crossing the chord.** If the user's mount is *very* badly aligned
  (e.g. axis 30° off the pole), the two-frame chord may not bracket the
  pole well and the trig becomes ill-conditioned. Mitigate by: warning if
  `θ < 5°`, and recommending a larger Δ (90°). For severe misalignment, the
  refine loop is what saves us — it converges from any starting point.
- **PHD2 frame-save latency.** Each `save_image` RPC + disk read is
  ~500-1000 ms on PHD2. Acceptable for the two-frame solve, slow for 1 Hz
  refine. Document this; users with built-in guiders or main-cam mode get
  faster refine.
- **Site-pole projection.** The pole's apparent (Az, Alt) at the site is
  trivially `(0°, lat)` for the celestial pole in the local horizon frame
  (azimuth depending on hemisphere — north pole: az=0, south pole: az=180).
  No precession needed if we *also* express the mount axis in topocentric
  coordinates from the same instant. Self-consistency is what matters.
- **No autoguider hijacking.** If the guider is the chosen capture source
  but is currently calibrated/guiding from a previous session, we must
  `StopCaptureAsync` first and restore state on exit.

## Out of scope (v1)

- **Drift alignment** (alternative method using sustained tracking). The
  plate-solve method is strictly better for amateurs and lacks the 30+ min
  wait drift requires.
- **Camera-rotation in two-step.** SharpCap also supports rotating the
  *camera* (rather than the mount) for some setups; we don't need this —
  TianWen always has a mount.
- **Saving frames to disk for later analysis.** Config key
  `PolarAlignmentSavePAFrames` is reserved but the FITS-write path is
  Phase 6+ (mirroring `SaveScoutFrames` deferral pattern).
- **Sub-pixel refinement of the chord midpoint** beyond plate-solve accuracy.
  The plate solver already gives ~0.5-1 arcsec center accuracy on a
  reasonable star field; that bounds our floor at ~30 arcsec misalignment
  resolution from a single two-frame solve. Refinement improves this.
- **Multi-target (3+) frame solve** for ill-conditioned geometries — could
  add later if v1 two-frame proves too sensitive in practice.

## Memory updates after landing

- New project memory `project_polar_alignment.md` pointing at this PLAN.
- Reference memory `reference_polar_alignment_math.md` if the
  `PolarAxisSolver` math turns out to have a subtlety worth recalling
  (most likely the sign convention for axis decomposition).
