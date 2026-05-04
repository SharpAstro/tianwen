# Plan Implementation Summary

Status of every `PLAN-*.md` in the repo root, cross-checked against the codebase on 2026-05-04.

| Plan | Status |
|------|--------|
| [PLAN-serial-probe](PLAN-serial-probe.md) | **DONE ~90%** |
| [PLAN-skymap-milkyway](PLAN-skymap-milkyway.md) | **DONE ~75%** |
| [PLAN-skymap-gpu-overlays](PLAN-skymap-gpu-overlays.md) | **PARTIAL ~35%** |
| [PLAN-tui-live-session-parity](PLAN-tui-live-session-parity.md) | **PARTIAL ~20%** |
| [PLAN-first-light-resilience](PLAN-first-light-resilience.md) | **DONE** (2 of 3 sub-plans shipped; sub-plan 3 deferred) |
| [PLAN-driver-resilience](PLAN-driver-resilience.md) | **DONE** (merged to main as 6 PRs + ARCH doc) |
| [PLAN-fov-obstruction-detection](PLAN-fov-obstruction-detection.md) | **DONE** (merged to main; scout UI/WebSocket surfacing, single-frame retry, Layer-2 recovery test all shipped) |
| [PLAN-catalog-binary-format](PLAN-catalog-binary-format.md) | **PARTIAL ~20%** (Option D pipeline + HR shipped; remaining catalogs pending) |
| [PLAN-polar-alignment](PLAN-polar-alignment.md) | **DONE ~85%** (Phases 1-5 shipped; refraction-corrected apparent pole + live pressure/temperature still pending) |

---

## PLAN-serial-probe — DONE ~90%

- Phase 1 (plumbing / core types): **DONE** — `ISerialProbe`, `ISerialProbeService`, `SerialProbeService`, `SerialProbeMatch`, `ProbeExclusivity`, `ProbeFraming` under `src/TianWen.Lib/Devices/Discovery/`. `ProbeAllAsync` wired into `DeviceDiscovery.RunSerialProbesAsync` (`DeviceDiscovery.cs:164`).
- Phase 2 (logger scopes): **DONE** — scope instrumentation lives in `SerialProbeService` itself; per-source migration made per-loop scopes moot.
- Phase 3 (two-tier pinned-port discovery): **DONE** — `PinnedSerialPort`, `IPinnedSerialPortsProvider`, `ActiveProfilePinnedSerialPortsProvider` all present; `ISerialProbe.MatchesDeviceHosts` implemented; GUI composition root wires the active-profile provider.
- Phase 4 (migrate all sources): **DONE** — `SkywatcherSerialProbe`, `OnStepSerialProbe`, `MeadeSerialProbe`, `IOptronSerialProbe`, `QhyCfw3SerialProbe`, `QfocSerialProbe` all migrated; each source's `DiscoverAsync` reads `probeService.ResultsFor(...)` with no per-port open.
- Phase 5 (cleanup): **DONE** — `WaitForSerialPortEnumerationAsync` / `EnumerateAvailableSerialPorts` callers removed from individual sources.
- Tests: **DONE** — `SerialProbeServiceTests`, `SkywatcherSerialProbeTests`, `OnStepQuirkProbeTests`, `ActiveProfilePinnedSerialPortsProviderTests`, `SerialPortNamesTests`.
- Evolution: `ISerialProbe` uses `ProbeFraming` (ordering within baud group) instead of the simpler `Exclusivity`-only model in the plan — beneficial addition, not a shortfall.

## PLAN-skymap-milkyway — DONE ~75%

- Phase 1 (shader + full-screen quad): **DONE** — `_milkyWayPipeline` in `VkSkyMapPipeline.cs`, alpha push-constant, sun-altitude fade, `ShowMilkyWay` toggle with `[S]` key, `TryLoadMilkyWayTexture` in `SkyMapTab.cs`.
- Phase 2 (texture pipeline + shipped file): **DONE** — `milkyway.bgra.lz` exists at `src/TianWen.UI.Gui/Resources/`, 8-byte header format + lzip compression via `MilkyWayTextureBaker.cs`.
- Phase 3 (bake from real data): **PARTIAL** — `tools/generate_milkyway.cs` is the .NET rewrite; `MilkyWayTextureBaker` + `MilkyWayBakerInputs` implement Tycho-2 luminance binning with Gaussian blur + brightness curve. Planck dust extinction path scaffolded (`DustOpacity`, `--dust-opacity`) but HEALPix reprojection step missing, so real-data bake has not yet replaced the analytical placeholder.
- Phase 4 (Planck HEALPix reader): **PARTIAL** — `tools/reproject_planck_dust.cs` exists, but no FITS download / `ang2pix` verified; dust input path in `MilkyWayBakerInputs.LoadAsync` reads a pre-converted float32 file, not raw FITS.
- Phase 5 (brightness/atmosphere integration): **NOT STARTED** — Bortle index modulation, HSV saturation slider, horizon fade absent; only the Phase 1 sun-altitude fade is present.

## PLAN-skymap-gpu-overlays — PARTIAL ~35%

- Phase 1 (mosaic panels + sensor FOV to `LinePipeline`): **DONE** — `BuildFovLines` at `VkSkyMapTab.cs:726` replaces the old CPU `DrawFovQuadrilateral`; sensor FOV + mosaic panels loop into `_fovFloats` and are emitted via `WriteToRingBuffer`/`DrawLineBuffer` (`VkSkyMapTab.cs:164-183`).
- Phase 2 (planet dots to GPU): **NOT STARTED** — no planet instance buffer, no dedicated planet pipeline.
- Phase 3 (batch glyph rendering): **NOT STARTED** — no `ComputeLabelPositions` or `DrawGlyphAtBaseline`; `DrawConstellationNames` / `DrawGridLabels` still use per-label CPU text path.
- Phase 4 (kill CPU RA/Dec grid scan): **NOT STARTED** — `poleInView` branch still active at `OverlayEngine.cs:620-631`; overlay path doesn't iterate `AllObjectIndices`.
- Phase 5 (cache meridian line geometry): **PARTIAL** — `_meridianFloats` / `_horizonFloats` exist (`VkSkyMapTab.cs:29-30`) and are keyed via `_lastStaticGeomKey` (`VkSkyMapTab.cs:39`), which invalidates only on LST change. Plan's explicit `lstThreshold` sub-pixel guard not separately called out but equivalent behaviour from `_cachedLiveTime` 1s granularity.

## PLAN-tui-live-session-parity — PARTIAL ~20%

- Phase 1 (extract renderer-agnostic draw helpers): **NOT STARTED** — `GuideGraphRenderer` class exists (`src/TianWen.UI.Abstractions/GuideGraphRenderer.cs`) but no generic `Render<TSurface>` static helper; `RenderCompactGuideGraph`, `RenderVCurveChart`, `RenderTimeline`, `RenderMiniSparkline` remain instance methods on `LiveSessionTab<TSurface>` (lines 473, 713, 946, 1151).
- Phase 2 (timeline band in TUI): **NOT STARTED** — no timeline `Panel` row in `TuiLiveSessionTab`.
- Phase 3 (compact guide graph in TUI): **NOT STARTED** — `_guideBar` is plain `TextBar` with RMS text only (`TuiLiveSessionTab.cs:51`).
- Phase 4 (V-curve overlay): **NOT STARTED** — no `VCurveRenderer` call in TUI.
- Phase 5 (preview mount section): **DONE** — `BuildPreviewRows` appends a mount `HeadingRow` block with RA/Dec/pier/HA (`TuiLiveSessionTab.cs:445`); consumes `liveState.PreviewMountState`.
- Phase 6 (clickable mini-viewer toolbar): **NOT STARTED** — `_previewToolbar` is text-only (`TuiLiveSessionTab.cs:54`).
- Phase 7 (ABORT button + modal): **PARTIAL** — keyboard abort flow + `ShowAbortConfirm` state handling exist (`TuiLiveSessionTab.cs:575, 718-730`), shown as status-bar hint; no red `Canvas` modal overlay or `ActionRow` button.
- Phase 8 (per-camera sparkline colors): **NOT STARTED** — `BuildSparkline` at line 600 is monochrome Unicode.
- Phase 9 (integration tests): **NOT STARTED**.

## PLAN-first-light-resilience — PARTIAL (meta only)

- Meta-plan document (sequencing, non-goals, prior-art inventory): **DONE** as a coordination document.
- Sub-plan 1 (driver resilience): **NOT STARTED** — see below.
- Sub-plan 2 (FOV obstruction detection): **NOT STARTED** — correctly gated on driver-resilience PR-B1..B3.
- Sub-plan 3 (site horizon mask): **NOT STARTED** — explicitly deferred.
- Cross-cutting conventions (`ITimeProvider.SleepAsync`, Device/Phase logger scopes, `SessionConfiguration` XML docs): in place from prior work, but the new wrappers that would use them haven't been written.

## PLAN-driver-resilience — DONE ~95%

Shipped on branch `driver-resilience` as 6 commits (PR-B1..B6). See
[`ARCH-driver-resilience.md`](ARCH-driver-resilience.md) for the full architecture
with mermaid state diagrams.

- Phase 1 (`ResilientCall` helper): **DONE** — `src/TianWen.Lib/Sequencing/ResilientCall.cs` +
  `ResilientCallOptions.cs` + 11 tests. Presets: `IdempotentRead`, `NonIdempotentAction`,
  `AbsoluteMove`. PR-B1 `1ce1d56`.
- Phase 2 (hot-path audit / wrapping): **DONE** — idempotent reads (PR-B2 `be911f4`) and
  non-idempotent actions (PR-B3 `b1f02ba`) in `Session.Imaging.cs` and `Session.Focus.cs`.
  Uniform via `Session.ResilientInvokeAsync` which auto-wires `OnDriverReconnect`.
- Phase 3 (in-flight exposure handling): **PARTIAL** — `ImageLoopNextAction.DeviceUnrecoverable`
  added + escalation short-circuit wired. The explicit "GetImageAsync empty after
  reconnect → re-issue StartExposure without counting the frame" detector is not yet
  implemented; mechanical reconnect-counting is in place so two consecutive lost frames
  would trip the fault counter naturally.
- Phase 4 (escalation boundary / fault counter): **DONE** — `SessionConfiguration.DeviceFaultEscalationThreshold` (default 5) and `DeviceFaultDecayFrames` (default 10),
  `_driverFaultCounts` dict on Session, `OnDriverReconnect` / `DecayFaultCountersOnFrameSuccess` /
  `TryFindEscalatedDriver` helpers + 5 tests. PR-B4 `db7ba83`.
- Phase 5 (proactive reconnect from `PollDeviceStatesAsync`): **DONE** — new `PollDriverReadAsync` +
  `PollDriverReadAsyncIf` helpers track consecutive failures and fire one-shot `ConnectAsync`
  at threshold 3 + 4 tests. PR-B5 `1374cbb`. Also applied to cooling ramp polls in PR-B6 `20394c3`.
- Tests: **DONE** — `ResilientCallTests` (11) + `SessionFaultCounterTests` (11) = 22 new tests.
  1672 unit + 78 functional session tests pass.

Not shipped: lost-frame detector (Phase 3 optional extension). Everything else is in.

## PLAN-fov-obstruction-detection — DONE ~95%

Shipped on branch `fov-obstruction-detection` after driver-resilience merged to main.

- Phase 1 (scout frame + compare): **DONE** — `ScoutResult`, `ScoutClassification`, `ScoutOutcome` in `src/TianWen.Lib/Sequencing/ScoutResult.cs`; `ScoutAndProbeAsync`, `ClassifyAgainstBaseline`, `TakeScoutFrameAsync`, `TryGetPreviousObservationBaseline` in `src/TianWen.Lib/Sequencing/Session.Imaging.Obstruction.cs`. Star-count classifier scales by `sqrt(exposure_ratio)` so a 10s scout vs. a 120s baseline compares correctly.
- Phase 2 (altitude-nudge disambiguation): **DONE** — `NudgeTestAsync` slews +N×half-FOV in declination, scouts again, and re-slews back in `finally` regardless of result. `ComputeWidestHalfFovDeg` derives nudge from camera pixel scale × focal length × NumX × BinX.
- Phase 3 (trajectory-aware wait): **DONE** — `EstimateObstructionClearTimeAsync` projects the target's natural altitude forward in 2-min steps until it reaches `current_alt + nudge_deg`, capped at 2 h lookahead. Returns `null` for setting targets.
- Phase 4 (integration with recovery loop): **DONE** — `RunObstructionScoutAsync` wraps the scout result + clear-time policy and returns `ScoutOutcome.Proceed` / `ScoutOutcome.Advance`. Wired into `ObservationLoopAsync` between `CenterOnTargetAsync` and `StartGuidingLoopAsync`. Transparency classification falls through to the existing `WaitForConditionRecoveryAsync`.
- Supporting config: **DONE** — `ScoutExposure`, `ObstructionStarCountRatioHealthy/Severe`, `ObstructionNudgeRadii`, `ObstructionClearFractionOfRemaining`, `SaveScoutFrames` added to `SessionConfiguration` with XML docs and sensible defaults.
- Tests: **DONE** — `SessionScoutClassifierTests` (6 unit tests for `ClassifyAgainstBaseline` + `ComputeWidestHalfFovDeg`) + `SessionScoutAndProbeTests` (5 functional tests covering first-observation, healthy, transparency-no-recovery, rising-target clear time, setting-target null clear time). All 1678 unit + 83 functional Session tests pass.

Not shipped: scout frames are not yet emitted via a `ScoutCompletedEventArgs` for the live-session UI (plan flagged as optional v1). `SaveScoutFrames` config key exists but no FITS write path yet (always discards, matching the false default).

## PLAN-polar-alignment — DONE ~85%

Shipped on `main` between 2026-04-26 and 2026-05-01 (~50 commits).

- Phase 1 (`PolarAxisSolver` math + tests): **DONE** — `src/TianWen.Lib/Astrometry/PolarAxisSolver.cs` + `PolarAxisSolverTests` + `PolarAlignmentHelpersTests`. Two-frame chord geometry with chord-angle sanity check (`6f345a3`).
- Phase 2 (`PolarAlignmentSession` orchestrator + capture sources + ramp + integration tests): **DONE** — `src/TianWen.Lib/Sequencing/PolarAlignment/PolarAlignmentSession.cs`, concrete `ICaptureSource` shims for main camera and PHD2, `PolarAlignmentSessionTests` + `PolarAlignmentRampIntegrationTests`. Adaptive exposure ramp with collapsed two-tier threshold (`37f538f`), backoff on capture failure (`fca6024`), `MinStarsForSolve` tightened to 25 (`fda1be7`), axis reverse-restore on dispose (`6f345a3`, `56941b8`).
- Phase 3a (generic `WcsAnnotationLayer`): **DONE** — `src/TianWen.UI.Abstractions/Overlays/PolarAnnotationBuilder.cs` + reusable `SkyMarker`/`SkyRing`/`SkyEdge` primitives, contributed to live preview alongside the existing WCS grid (`06e926e`, `667cb55`).
- Phase 3b (polar-align mode in `LiveSessionTab`): **DONE** — mode toggle, signals, `PolarAlignmentActions` helper, side-panel widgets, ring labels, meridian + prime-vertical lines, raw-error display, polar-adjuster knob jitter (`4d45462`, `d3c81a3`, `345c910`, `9666359`).
- Phase 4 (TUI parity): **DONE** — text gauges + ASCII arrow + status line driven by the same `LiveSessionState` fields (`4d45462`).
- Phase 5 (PHD2 path): **DONE** — `Save Images` integration verified end-to-end (`4d45462`).
- Refinement loop extras (beyond plan): `IncrementalSolver` fast path with frozen-seed quad matching + adaptive quad tolerance (`c261a8b`, `9e1a371`, `56d9e82`, `6ea03b7`), Jacobian live tracker (`b72ab8f`), sidereal-time normalisation (`7d51671`), reference-frame averaging (`570e4d9`), live WCS binding + CT fix (`03a0e73`), aperture-aware optics (`bbe3d54`), per-stage timing instrumentation (`a2ce471`).

Not shipped (TODO.md lines 142, 146):
- **Refraction-corrected apparent pole.** `PolarAlignmentSession.cs:658-659` literally sets `RefractedPoleRaHours: trueRa` / `RefractedPoleDecDeg: trueDec` — the apparent-pole rings draw on the true pole. Decomposition gauges already use refraction-aware math (correct numbers), only the overlay center is stale. Matters most at lat ≤ 35°.
- **Live site pressure/temperature.** `IMountDriver.cs:395-396` still hardcodes `SitePressure = 1010`, `SiteTemperature = 10`. Same fix unblocks both polar-alignment refraction and the long-standing pressure/temp TODO.

## PLAN-catalog-binary-format — PARTIAL ~20%

Plan updated 2026-05-04 to lead with **Option D** (ASCII-separated text +
`tools/preprocess-catalog.ps1` MSBuild step) instead of Option A (MessagePack).
Tycho2 stays untouched.

- Option D preprocessor (`tools/preprocess-catalog.ps1`): **DONE** — pwsh script reads `*.json.lz`, parses, re-emits with `0x1D`/`0x1E`/`0x1F` separators (G17 doubles, invariant culture), shells to `lzip -9` for `*.gs.lz` output.
- MSBuild `<Exec>` target: **DONE** — `<CatalogPreprocess>` items in `TianWen.Lib.csproj`, batched `Inputs="@(...)" Outputs="@(...->...)"` so the preprocessor only re-runs when the source `.json.lz` is newer than the `.gs.lz`.
- Runtime reader changes: **DONE for HR** — `AsciiRecordReader` (`src/TianWen.Lib/IO/AsciiRecordReader.cs`) provides `EnumerateRecords` / `TakeField` / `ReadDouble` / `ReadNullableDouble` / `ReadStringArray` over `ReadOnlySpan<byte>`. `ParseSimbadGsAsync` decodes SIMBAD records. `ParseSimbadFileAsync` prefers `.gs.lz` if present and falls back to `.json.lz` otherwise — incremental rollout works without touching unmigrated callers.
- Shared `AsciiRecordReader` helper: **DONE**.
- Incremental rollout: HR shipped (1 of 14 SIMBAD catalogs); NGC CSV + remaining SIMBAD files + cross-ref JSONs still on the legacy path.
- Tests: **DONE for HR** — 8 unit tests in `AsciiRecordReaderTests` (record split, field take, G17 double round-trip, nullable double, sub-array). Existing 340 `CelestialObjectDBTests` already exercise HR end-to-end via the migrated path.
- Secondary lookup speed improvement (`ArrayPool` BFS / frozen-dict transitive closure): **NOT STARTED**.

---

## Bottom line

- **Shipped:** serial-probe (merged), driver-resilience (merged), fov-obstruction-detection (merged), polar-alignment (merged, refraction polish pending).
- **Substantially advanced:** milkyway (Phases 1-2 done, 3-4 scaffolded).
- **Partially started:** skymap-gpu-overlays (Phase 1 + cache hit), tui-live-session-parity (preview mount section + partial abort flow), catalog-binary-format (Option D pipeline + HR shipped, remaining catalogs pending).
- **Essentially untouched:** site horizon mask (sub-plan 3 of first-light-resilience) deferred until operational data warrants it.

**First-light-resilience status:** 2 of 3 sub-plans shipped (driver resilience + FOV obstruction).
Sub-plan 3 (static azimuth horizon mask) is intentionally deferred — only spin up if 1+2
in production show too many runtime scout trips against known obstructions.
