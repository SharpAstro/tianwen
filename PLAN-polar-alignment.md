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

The polar-alignment toolbar toggle in the live preview is **only enabled** when:

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
   5. **UI integration: reuse the live-view OTA selector**
      (`SelectedCameraIndex` on the viewer state, drawn as the
      `#1`/`#2`/... buttons at `LiveSessionTab.cs:270-284`). The ranker
      provides the *initial default* selection when the polar-alignment
      panel opens; clicking a different OTA button rebinds the routine to
      that camera. No separate dropdown — one OTA-selection control across
      the entire live view. The runner-up is still displayed in the
      polar-alignment panel header as a hint ("auto-pick: #1, runner-up: #2").
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

**Generic WCS annotation primitive — not a polar-specific shader mode**.
The shader extension we add for polar alignment is a *general-purpose
sky-annotation layer* that lives alongside the existing WCS grid in
`VkImageRenderer`, useful far beyond this routine:

- **FITS viewer**: highlight plate-solve catalog matches as crosses,
  draw a search-radius ring around a clicked target, mark the frame
  centre and known asterisms.
- **Live session preview**: drop a target marker at the scheduled
  observation centre, ring the dither circle, mark the current pier
  side's safety-zone boundary.
- **Plate-solve verification**: cross-mark the solver's reported centre
  versus the mount-reported pointing to surface sync errors at a glance.
- **Mosaic composer**: draw the next panel boundary plus its centre
  cross while the user adjusts the panel grid.
- **Polar alignment**: composes from the same primitives — two pole
  crosses (true + refracted) plus rings around the refracted pole plus
  one rotation-axis marker.

**API: data-driven, not mode-driven.** Define a single immutable input
type that the shader consumes, and let any caller fill it:

```csharp
public readonly record struct WcsAnnotation(
    ImmutableArray<SkyMarker> Markers,    // crosses / dots / labels at sky positions
    ImmutableArray<SkyRing> Rings,        // concentric circles of arcmin radius around a sky position
    ImmutableArray<SkyEdge> Edges);       // optional: line segments between two sky positions

public readonly record struct SkyMarker(
    double RaHours, double DecDeg,
    MarkerGlyph Glyph,    // Cross | Dot | CircledCross | Square
    Vector4 Color,
    string? Label,
    float SizePx);

public readonly record struct SkyRing(
    double CenterRaHours, double CenterDecDeg,
    float RadiusArcmin,
    Vector4 Color,
    string? Label);
```

The polar-alignment tab builds:
```csharp
new WcsAnnotation(
    Markers: [
        new(0, ±90, Cross, White, "NCP (True)" / "SCP (True)", 12),
        new(P_apparent_ra, P_apparent_dec, Cross, Green, "NCP (Refracted)" / "SCP (Refracted)", 12),
        new(axisRa, axisDec, CircledCross, ColorByDistance(axisErrorArcmin), null, 8),
    ],
    Rings: [
        new(P_apparent_ra, P_apparent_dec, 5,  GreenFaint, "5'"),
        new(P_apparent_ra, P_apparent_dec, 15, GreenFaint, "15'"),
        new(P_apparent_ra, P_apparent_dec, 30, GreenFaint, "30'"),
    ],
    Edges: []);
```

Other consumers feed in their own marker/ring sets and get the same
fragment-shader rendering for free. The renderer doesn't know what
"polar alignment" is — it just projects each (RA, Dec) through the
current frame's WCS, draws crosses/dots in screen-space, and for rings
walks N=64 great-circle points around the centre at the requested arcmin
radius and emits a closed line strip. Labels go through the existing
glyph batcher.

**Implementation lives in `TianWen.UI.Shared`** alongside `VkImageRenderer`,
not in any polar-alignment-specific file. Its name is `WcsAnnotationLayer`
(or similar — a primitive, not a feature). Polar alignment is one
caller; FITS viewer and live preview are obvious next callers; nothing
in the shader code is polar-specific.

The existing `WcsGridOverlay` (RA/Dec grid lines) is a sibling of this
layer, not a parent. They both live behind the same WCS pixel-to-world
infrastructure and run as separate draw passes on the same uniform
buffer. Either or both can be enabled per frame.

**Off-sensor handling**. If the rotation-axis marker is way off-sensor
(initial misalignment > FOV / 2), draw an edge arrow pointing toward it
with the offset distance in arcmin: "axis 47' off-frame ↗". Once the
marker enters the sensor, switch to the in-sensor circle.

**Computation**:
- `P_true` and `P_apparent` are computed once per live solve (refraction
  changes slowly; recompute on each frame is cheap, no caching needed).
- `A_live` updates per solve.
- All published via `LiveSolveResult.PolarOverlay` as **sky positions
  only** — pixel projection happens in the renderer:
  ```csharp
  public readonly record struct PolarOverlay(
      double TruePoleRaHours,    double TruePoleDecDeg,
      double RefractedPoleRaHours, double RefractedPoleDecDeg,
      double AxisRaHours,        double AxisDecDeg,
      ImmutableArray<float> RingRadiiArcmin,
      double AzErrorArcmin,
      double AltErrorArcmin,
      Hemisphere Hemisphere);
  ```
  The GUI tab maps this to a `WcsAnnotation` (three `SkyMarker`s + N
  `SkyRing`s) and hands it to `WcsAnnotationLayer`. The TUI ignores
  the layer entirely and renders a text panel: "NCP-R at RA=12.3h
  Dec=89.6° | axis at RA=11.9h Dec=88.4° | Az 4'12" E, Alt 1'48" S".

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
- **No new tab.** Polar alignment is a third **mode** of the existing
  `LiveSessionTab` alongside *preview* and *session*. The image surface,
  WCS pipeline, OTA selector (`#1`/`#2` buttons at `LiveSessionTab.cs:270-284`),
  star overlay, and FITS preview are all already wired — polar mode just
  contributes additional annotations to the renderer plus a small
  side-panel of polar-specific UI. Keeping the routine in-tab means the
  user stays in the same context they were already in (looking at a live
  preview frame), and we don't duplicate ~half a tab's worth of plumbing.
  Mode toggle: a "Polar Align" button on the live-view toolbar enabled
  when preconditions hold (manual mount + site + solver + capture source).
  Clicking flips `LiveSessionState.Mode = PolarAlign`; clicking again or
  pressing Cancel returns to preview mode.
- `src/TianWen.UI.Abstractions/LiveSessionState.cs` additions for polar
  mode (gated behind the `Mode` enum so they don't pollute preview/session
  state):
  - `PolarAlignmentPhase Phase` (`Idle`, `ProbingExposure`, `Frame1`,
    `Rotating`, `Frame2`, `Refining`, `Aligned`, `RestoringMount`)
  - `LiveSolveResult? LastSolve` — atomic replacement per CLAUDE.md
  - `string? PolarStatusMessage` — orange instruction line
  - `TwoFrameSolveResult? PhaseAResult` — kept for the chord-angle sanity
    readout and the locked exposure indicator
  All `ImmutableArray`-backed if collection-typed, atomic property
  replacement on writes — the PolarAlignmentSession runs on a thread-pool
  task and writes complete each refine tick; the render thread snapshots.
- `LiveSessionTab` polar-mode rendering (additive, gated on `Mode`):
  - **Toolbar**: shows "Polar Align" toggle button; when active, also
    shows Δ-RA dropdown (30/45/60/90°) and Start/Cancel.
  - **Image surface**: same `VkImageRenderer`. Polar mode adds a
    `WcsAnnotation` (composed from `LiveSolveResult.PolarOverlay`) on top
    of the standard WCS grid + star overlay:
    - 5'/15'/30' rings around the refracted pole.
    - True-pole cross (`+`, white) and refracted-pole cross (`+`, green).
    - Current rotation-axis marker (filled circle + ring), colour ramping
      green→yellow→red across the ring thresholds.
    - Off-sensor edge arrow + arcmin label when the axis marker falls
      outside the frame.
    - All via the generic `WcsAnnotationLayer` from Phase 3a — no
      polar-specific shader code.
  - **Side panel** (replaces the session-specific panels when polar mode
    is active): two error needles (Az / Alt arcmin, green<1', yellow 1-5',
    red>5'), trend arrows, exposure indicator
    ("200 ms · 23 stars matched"), status line ("Probing exposure
    (250 ms)…", "Frame 1/2 ✓", "Rotating", "Refining (1.2 Hz)",
    "Aligned ✓ — click Done"), direction-hint badges
    ("Alt: ↑ 1'48"", "Az: ← 4'12""), `IsSettled` / `IsAligned` LEDs.
- The TUI version (`TuiLiveSessionTab`) follows the same pattern: a
  third mode that swaps in a text panel rendering the same state.
  No annotation layer (text-only); ASCII arrow toward the target offset
  ("axis 47' off-frame ↗"), text gauges, status line.

## Phasing

| Phase | Scope | Tests |
|------|------|------|
| **1** | `PolarAxisSolver` math + tests | Unit tests with synthetic rotations |
| **2** | `PolarAlignmentSession` orchestrator + `ICaptureSource` shims + adaptive exposure ramp + auto-selection ranking + axis reverse-restore | Functional test using `FakeMountDriver` + `FakeCameraDriver` (synthetic star field with known mount pole offset, ramp picks shortest exposure that yields ≥15 stars, reverse restores within arcmin) |
| **3a** | Generic `WcsAnnotationLayer` in `TianWen.UI.Shared`: data-driven `SkyMarker` + `SkyRing` + `SkyEdge` inputs, fragment-shader render alongside existing WCS grid. Reusable by FITS viewer / live preview / mosaic composer / polar alignment. | Visual smoke test in FITS viewer |
| **3b** | Polar-align mode wired into existing `LiveSessionTab` (no new tab): mode toggle, signals, `PolarAlignmentActions` helper, side-panel widgets, polar mode contributes `WcsAnnotation` to the layer. Reuses image surface + OTA selector + WCS pipeline. | Manual GUI test |
| **4** | Polar mode parity in `TuiLiveSessionTab`: text gauges + ASCII arrow + status line, same `LiveSessionState` fields. | Manual TUI test |
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
