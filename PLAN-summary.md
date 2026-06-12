# Plan Implementation Summary

Status of every `PLAN-*.md` in the repo root, cross-checked against the codebase on 2026-05-16.

| Plan | Status |
|------|--------|
| [PLAN-serial-probe](PLAN-serial-probe.md) | **DONE ~90%** |
| [PLAN-skymap-milkyway](PLAN-skymap-milkyway.md) | **DONE ~75%** |
| [PLAN-skymap-gpu-overlays](PLAN-skymap-gpu-overlays.md) | **PARTIAL ~35%** |
| [PLAN-tui-live-session-parity](PLAN-tui-live-session-parity.md) | **PARTIAL ~20%** |
| [PLAN-first-light-resilience](PLAN-first-light-resilience.md) | **DONE** (2 of 3 sub-plans shipped; sub-plan 3 deferred) |
| [PLAN-driver-resilience](PLAN-driver-resilience.md) | **DONE** (merged to main as 6 PRs + ARCH doc) |
| [PLAN-fov-obstruction-detection](PLAN-fov-obstruction-detection.md) | **DONE** (merged to main; scout UI/WebSocket surfacing, single-frame retry, Layer-2 recovery test all shipped) |
| [PLAN-catalog-binary-format](PLAN-catalog-binary-format.md) | **PARTIAL ~85%** (Option D + Phase 2A + 2B shipped; Phase 2C Tycho-2 bulk load deferred) |
| [PLAN-polar-alignment](PLAN-polar-alignment.md) | **DONE ~85%** (Phases 1-5 shipped; refraction-corrected apparent pole + live pressure/temperature still pending) |
| [PLAN-gpu-stretch-tests](PLAN-gpu-stretch-tests.md) | **DONE ~90%** (Phases 1-4 + follow-ups D & F shipped; Phase 5 separate-CI-job replaced by inline `test-unit` run with mesa-vulkan-drivers) |
| [PLAN-icc](PLAN-icc.md) | **DONE ~95%** (Tiff 3.0 + new SharpAstro.Jpeg consumed; display helper + Nina JPEG injection wired) |
| [PLAN-stacking](PLAN-stacking.md) | **DONE ~85%** (Phases 1-12 + 8.0-8.3 shipped — selector + 6 strategies + FrameCache + PartialFitsReader; Phase 13 CLI orchestrator + 14/15 LiveStacker wiring + 10 MMF sink still pending) |
| [PLAN-ai-enhancement](PLAN-ai-enhancement.md) | **DONE Phases 0-7** (CLI side, merged via PR #10) -- model fetch, MTF helpers, ChunkedInference, IStarRemover/IStellarSharpener/INonStellarDeconvolver/IDenoiseEnhancer/IGradientCorrector atomic enhancers, step-based `SharpenPipeline` orchestrator with `SharpenIntermediates` retention selector, ChunkedNafnetRunner shared, GraXpert BGE gradient correction with `--save-gradient`, dual-stretch pipeline (Frank StarStretch + auto-MTF or opt-in GHS + bg-reduce + Reinhard highlight roll-off + Screen recombine + per-plate float TIFFs), `tianwen image {sharpen,remove-stars,flatten,render,stats}` CLI verbs. Plus refinements: 16-px chunk pad, ArrayPool input tensors, `--png` flag, `SubtractiveChromaticNoise` + `Lerp`, `StarsScnrMode` + per-step AI blends, DirectML-on-Adreno for win-arm64 (2.4x speedup), pre-stretched-input auto-detect, `Image.Histogram` median bugfix, `Image.BilinearResize`, `ModelResolver` cross-platform path-separator guard. Phase 7 GUI menu entry + Phases 8-10 (runtime model self-bootstrap, classical fallbacks, NPU INT8 quant) NOT STARTED -- see [PLAN-ai-enhancement-next.md](PLAN-ai-enhancement-next.md) for the priority order of follow-on work. |
| [PLAN-ai-enhancement-next](PLAN-ai-enhancement-next.md) | **NOT STARTED** (Frank-parity Star Stretch port: ColorSaturation + SCNR `preserveLightness`; per-plate stretch via `StretchTransform` abstraction with asinh + GHS + gamma + MTF concrete impls; reroutes the deferred items from PLAN-ai-enhancement into priority order) |
| [PLAN-ghs](PLAN-ghs.md) | **DONE ~90%** (Phases 0-11 shipped on branch `ghs-converge`: reference math port + B-branch curve + auto-converge `Image.ConvergeGhsStretchFactor`. Subsequent work added mode-target convergence so the bg peak (not the median) lifts to ~0.25 -- median-target left the output dim because median sits above mode for typical astro frames. `--ghs-stages 1|2|3` exposes Cranfield's canonical multi-stage chain (gh-astro 2.7-2.9). CLI refactored to three orthogonal selectors `--star-stretch-mode / --starless-stretch-mode / --stretch-mode` + `--ghs-converge auto|manual`. Post-recombine stretch wired via new `MtfStretchFinalStep` + `GhsStretchFinalStep` (single-pass only on final); `ApplyGhsChain` helper shared between starless + final dispatchers. GHS stays **opt-in** per `feedback_ghs_not_default` -- MTF remains the default starless stretch. Canonical recipe = `--dual-stretch --starless-stretch-mode Ghs --ghs-target Mode --ghs-target-value 0.25 --ghs-stages 3`. {broadband, narrowband, single-light} corpus validation outside SoL drizzle still pending; `--star-stretch-mode mtf|ghs` and multi-stage on `GhsStretchFinalStep` parked.) |
| [PLAN-background-extraction](PLAN-background-extraction.md) | **NOT STARTED** (design captured; classical poly + optional RBF gradient removal, ports SAS Pro `abe.py`; placed pre-stretch / pre-AI in the linear domain) |
| [PLAN-moon-avoidance](PLAN-moon-avoidance.md) | **DONE** (branch `feat/moon-avoidance`, 2026-06-12): per-bin Moon penalty in `ScoreTarget` via shared `MoonGrid` - illumination x quadratic proximity, Moon-below-horizon gate; radius is an optional param on Schedule/TonightsBest/ScoreTarget (default 30, ON, 0 disables). Dropped the planned `SessionConfiguration` knobs - the session never scores (planner-only path) so they would have been dead code. 6 tests in `MoonAvoidanceTests.cs`; full unit (2596) + functional (286) suites green |
| [PLAN-scheduled-starts](PLAN-scheduled-starts.md) | **DONE** (branch `feat/top-5-todo`, 2026-06-12): `WaitForScheduledStartAsync` + `ScheduledStartOutcome` in `Session.Timing.cs`, called at top of `ObservationLoopAsync`; waits until `Start - ScheduledStartLeadTime` (new `SessionConfiguration` knob, default 3 min) on the mount clock. Same-/past-Start short-circuits (hosted API + legacy + existing tests unchanged), beyond-session-end skips cleanly, late starts proceed unclamped, cancellation unwinds via OCE. 4 tests in `SessionObservationLoopTests` (branch outcomes, real wait with frame-timestamp gap, session-end skip, cancel-during-wait); full unit (2596) + functional (290) green |
| [PLAN-site-conditions](PLAN-site-conditions.md) | **NOT STARTED** (authored 2026-06-12; 3-tier `SiteConditions.Resolve` (weather -> profile -> standard/auto-derive), new `ProfileData.SitePressureHPa`/`SiteTemperatureCelsius`, retires `IMountDriver.cs` 1010/10 TODOs, feeds polar alignment tier 2) |
| [PLAN-skymap-time-scrub](PLAN-skymap-time-scrub.md) | **NOT STARTED** (authored 2026-06-12; Stellarium-style `SkyMapState.TimeOffset` wall-clock-relative scrub - arrows/PageUp/PageDown step, `0` reset, `N` midnight jump, HUD offset chip; redraw-only, never planner recompute; mount reticle stays wall-clock) |
| [PLAN-obstruction-first-light-oracle](PLAN-obstruction-first-light-oracle.md) | **NOT STARTED** (authored 2026-06-12; catalog-floor oracle for the no-baseline first scout - `CatalogStarCounter.CountStarsInField` at Tycho-2-clamped mag 11 x `OracleFactor`, suspicious -> existing NudgeTest; extracts shared `StarDetectionModel.DetectabilityMagCutoff`) |

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

## PLAN-catalog-binary-format — PARTIAL ~85%

Plan now leads with **Option D** (ASCII-separated text + `tools/preprocess-catalog.ps1`
MSBuild step) instead of Option A (MessagePack). Tycho2 stays untouched (parallel
multi-member lzip already optimal — gzip would lose parallelism, see plan doc).

**Phase 1 (Option D format migration) — SHIPPED end-to-end (2026-05-04):**

- Option D preprocessor (`tools/preprocess-catalog.ps1`): **DONE** — pwsh script reads `*.json.lz` / `*.csv.lz`, parses, re-emits with `0x1D`/`0x1E`/`0x1F` separators (G17 doubles, invariant culture), gzip-compresses to `*.gs.gz` via `System.IO.Compression.GZipStream` (Optimal). Encoder + decoder match one-to-one, no external lzip binary needed.
- MSBuild `<Exec>` target: **DONE** — `<CatalogPreprocess>` items in `TianWen.Lib.csproj`, batched `Inputs="@(...)" Outputs="@(...->...)"` so the preprocessor only re-runs when the source `.json.lz` is newer than the `.gs.gz`.
- Runtime reader: **DONE** — `AsciiRecordReader` (`src/TianWen.Lib/IO/AsciiRecordReader.cs`) provides `EnumerateRecords` / `TakeField` / `ReadDouble` / `ReadNullableDouble` / `ReadStringArray` over `ReadOnlySpan<byte>`. `ParseSimbadGsAsync` + `MergeNgcGsData` decode the migrated catalogs.
- Shared `AsciiRecordReader` helper: **DONE**.
- Catalog rollout: **DONE** — all 13 SIMBAD catalogs (HR, GUM, RCW, LDN, Dobashi, Sh, Barnard, Ced, CG, vdB, DG, HH, Cl) and both NGC (NGC, NGC.addendum) shipped on the `.gs.gz` path.
- Compression swap (lzip → gzip on `.gs` files): **DONE** — bench showed BCL GZipStream is 4-8× faster on small single-stream payloads, +2.3 % on size (1,001 KB → 1,024 KB embedded).
- Tests: **DONE** — 1,841 unit tests pass against the migrated format; `CelestialObjectDBBenchmarkTests` prints per-phase breakdown.
- Secondary lookup speed improvement (`ArrayPool` BFS / frozen-dict transitive closure): **NOT STARTED**.

**Phase 1 numbers (post-migration, post-gzip):**

| Build | First `InitDBAsync` |
|---|---|
| Release + warm runtime (test bench) | **716–906 ms** (run-to-run variance) |
| Debug + cold disk + cold JIT (GUI cold launch) | **2,411 ms** |

The hot phases are now dict-mutation work, not parse work — what Phase 2 is for.

**Phase 2 (pre-bake init state) — 2A SHIPPED:**

- 2A SHIPPED (2026-05-05): `tools/precompute-hd-hip-cross/` bakes the post-`BuildHdHipCrossIndicesViaTyc` state into `hd_hip_cross.bin.gz` (~2.4 MB embedded). Runtime apply takes ~110 ms (parallel SHA-256 input hash + gzip read + dict mutation) vs ~460 ms live compute. Net saving: ~350 ms on the hd-hip-cross phase. CI guards in `HdHipCrossSnapshotTests` catch staleness + algorithm-vs-snapshot drift. Re-bake via `pwsh tools/precompute-hd-hip-cross.ps1`.
	- 2B SHIPPED (2026-05-05): `tools/precompute-simbad-merge.ps1` bakes the post-SIMBAD-merge state into `simbad_merge.bin.gz` (~754 KB embedded). Runtime apply skips ~180 ms of parse + dict-mutation work across 14 catalogs. Same hash-verify-then-apply pattern as 2A. CI guards in `SimbadMergeSnapshotTests` (commit `8da9b16`). Re-bake via `pwsh tools/precompute-simbad-merge.ps1`.
	- 2C: Tycho-2 bulk load **deferred**; BFS pooling for secondary lookups also not started.

## PLAN-gpu-stretch-tests — DONE ~90%

GPU-vs-CPU pixel-parity tests for the stretch pipeline. Enabled by the offscreen
path that shipped in `SdlVulkan.Renderer` (`VulkanContext.CreateOffscreen` +
`VkRenderer.BeginOffscreenFrame` + `VulkanContext.ReadbackOffscreenRgba`).

- Phase 1 (smoke test + class fixture): **DONE** — `OffscreenGpuFixture.cs` owns
  `(instance, ctx, renderer, pipeline)` and skips with a diagnostic when Vulkan
  init fails; `GpuStretchPipelineTests.cs` carries the smoke `[Fact]`.
- Phase 2 (skip-when-unavailable + CI driver install): **DONE** — `.github/workflows/dotnet.yml`
  installs `mesa-vulkan-drivers libvulkan1 vulkan-tools` and emits a
  `vulkaninfo --summary` diagnostic before build. `IsVulkanInitFailure` short-circuits
  to `Assert.Skip` on missing ICD.
- Phase 3 (8 Vela theory cases parity): **DONE** — `GpuMatchesCpuForVelaStretchCases`
  runs the same 8 inline cases the CPU `StretchTests_NewPipeline` covers; tolerances
  `mean < 1.5`, `max < 16`, outliers `< 1%`.
- Phase 4 (synthetic SPCC GPU verification): **DONE** — `GivenSyntheticSpccField_GpuRenderMatchesCpuRender`
  runs the full SPCC pipeline (filter throughput, Tycho-2 match, pivot1, curves, HDR)
  through both paths; additional Luma-weighting, luma-blend, HDR-normalize, sensor-matched
  theory tests included.
- Phase 5 (opt-in `[Trait("Category","GPU")]` CI job): **REPLACED** — GPU tests run inside
  the regular `test-unit` job (lavapipe is fast enough on the test images); no separate
  trait filter or split job needed. Functionally equivalent to the plan, simpler infra.
- Follow-up D (primitives parity): **DONE** — `VkRendererPrimitiveTests.cs` covers
  rectangle/ellipse/circle/line vs `RgbaImageRenderer`.
- Follow-up F (line tessellation): **DONE** — `SkyMapLineTessellationTests.cs` asserts
  GPU line vertex output without rasterisation.
- Not shipped: follow-ups A (Bayer demosaic), B (histogram), C (WCS grid), E (sky map
  stars), G (milky way), H (overlay ellipses). All optional extensions to the same
  `OffscreenGpuFixture`.

## PLAN-icc — DONE ~95%

ICC profile tagging across our display output paths. Sibling work landed under
`../sharpastro/StbImageSharp/` as new packages `SharpAstro.Color.Icc`, `SharpAstro.Jpeg`,
and a breaking-change bump on `SharpAstro.Tiff`.

- Phase 1 (`SharpAstro.Tiff` breaking change to `ReadOnlyMemory<byte>` `IccProfile`): **DONE** —
  consumed at `SharpAstro.Tiff 3.0.*` in `Directory.Packages.props:54`.
- Phase 2 (new `SharpAstro.Jpeg` library with `JpegIccInjector.EmbedIccProfile`): **DONE** —
  sibling project exists; consumed at `SharpAstro.Jpeg 3.0.*` (single-version family
  alongside Tiff/Png/Color.Icc, not the `1.0.*` originally planned).
- Phase 3 (publish to NuGet): **DONE** — all four packages resolve from nuget.org.
- Phase 4a (display TIFF helper consolidation): **DONE** — `src/TianWen.Lib.Tests/Helpers/DisplayImageWriter.cs`
  centralises sRGB-tagged 8-bit RGB TIFF + PNG output for the three test files the plan
  flagged. Plan's working name `TestDisplayTiffWriter.cs` was renamed to `DisplayImageWriter.cs`
  since it also handles PNG.
- Phase 4b (`NinaImageEndpoints` JPEG injection): **DONE** — `src/TianWen.Hosting/Api/NinaV2/NinaImageEndpoints.cs:133`
  wraps `WriteJpg` output through `JpegIccInjector.EmbedIccProfile(..., IccProfiles.SRgbV4)`.
- Phase 4c (`Image.WriteTiffAsync` stays untagged): **HONOURED** — scientific 32-bit float
  TIFF deliberately ships with no ICC tag per scope decision 2.
- Phase 4d (production PNG writers): **N/A** — no production display-PNG paths in
  `TianWen.Lib` proper. Test/tools PNGs use `DisplayImageWriter.EncodePng` when tagging
  is desired.
- Not shipped: ICC consumption on read (out-of-scope per "Non-goals"), multi-segment
  APP2 (deferred until we have a non-trivial device profile).

---

## Bottom line

- **Shipped:** serial-probe (merged), driver-resilience (merged), fov-obstruction-detection (merged), polar-alignment (merged, refraction polish pending), gpu-stretch-tests (Phases 1-4 + follow-ups D & F), icc (all four phases).
- **Substantially advanced:** milkyway (Phases 1-2 done, 3-4 scaffolded).
- **Partially started:** skymap-gpu-overlays (Phase 1 + cache hit), tui-live-session-parity (preview mount section + partial abort flow), catalog-binary-format (Option D + Phase 2A + 2B shipped; 2C deferred).
- **Essentially untouched:** site horizon mask (sub-plan 3 of first-light-resilience) deferred until operational data warrants it.

**First-light-resilience status:** 2 of 3 sub-plans shipped (driver resilience + FOV obstruction).
Sub-plan 3 (static azimuth horizon mask) is intentionally deferred — only spin up if 1+2
in production show too many runtime scout trips against known obstructions.

## PLAN-stacking — DONE ~85%

The original phasing table (Phases 1-12) is shipped: `Image` arithmetic, masters,
calibrator, registrator, normalizer, rejectors (sigma + winsorized + LFC +
percentile + minmax), the in-memory `Integrator`, and FITS write via
`IntegrationFitsWriter` (Phase 9). End-to-end validated on real datasets via
`StackingEndToEndManualTest` (skips when `C:\temp\stack\` is absent so CI stays
green).

Phase 9 caveat: the `IIntegrationSink` interface + `ArraySink` abstraction
mentioned in the plan are *not* shipped -- only the concrete static
`IntegrationFitsWriter` plus a sidecar `<master>.rejection.fits` companion file
for the rejection-map diagnostic. The interface was meant as a seam for Phase
10's `MemoryMappedFitsSink`; with Phase 10 deferred, pre-introducing the
interface for a single in-memory consumer would be YAGNI -- right time to
extract is when MMF lands. MEF (multi-extension FITS) is also deferred per the
file's own xmldoc: most FITS viewers don't render the second HDU, so two files
keep the rejection diagnostic usable.

**Phase 8 ("tile integrator") evolved into a strategy-pattern selector** with
six executors picked at runtime by an `IntegrationStrategySelector` against an
`IntegrationProbe` (physical RAM + disk + frame geometry) gated by a
`ResourceBudget` (default 75% RAM / 80% disk). Shipped sub-phases this session
(2026-05-16):

- **8.0** `bbab3c8` — `IntegrationJob` v2 surface + scaffolding.
- **8.1** `f4a7013` — `PartialFitsReader` (mmap'd FITS sub-region reader, 9 unit tests).
- **8.2** `330b4b3` — `Image.WarpRegionAsync` + strip-pipelined `TilePipelinedStrategy.RunAsync`.
- selector `da67544` — physical-RAM hard gate + free-RAM soft penalty.
- **8.2 perf** `7415671` — strong+weak cached debayered frames → ~10× speedup (Liberty 120s 10.9 min → 1.6 min).
- refactor `40fce57` — `FrameCache` extracted as shared helper.
- **8.3** `7ece80b` — BDN bench: PartialFitsReader **36× faster** on tile reads, 6000× less alloc.
- cache wire `e17d585` — `StreamingFrameReader.SetCachedImage` + FrameCache wired into FootprintStaged + Float16Staged.

**Still pending:**
- Phase 10 `MemoryMappedFitsSink` for tier-3 mosaic outputs (not user-blocked).
- Phase 13 `tianwen stack` CLI orchestrator (`StackingEndToEndManualTest` is the
  flying sketch).
- Phase 14 `LiveStacker` Welford engine + Phase 15 session integration
  (`LiveAccumulatorStrategy` is the selector-level placeholder).
- Phase 8.2 sub-region debayer + halo-aware per-tile warp (the cached-debayered
  fast path already covers the common roomy-host case; this is the next
  optimization tier when memory is genuinely tight AND the cache misses dominate).
- SIMD byte-swap in `PartialFitsReader` for full-image reads (production hot
  path only does tile reads where mmap already wins 36×, so low priority).

See `PLAN-stacking.md` § "Phase 8 implementation status (2026-05-16)" for the
cold-start guide to the codebase: every file that holds the strategy machinery,
the test entry points, and the benchmark numbers worth remembering.
