# Plan Implementation Summary

Status of every `PLAN-*.md` in the repo root, cross-checked against the codebase on 2026-05-16.

| Plan | Status |
|------|--------|
| [serial-probe](serial-probe.md) | **DONE ~90%** |
| [skymap-milkyway](skymap-milkyway.md) | **DONE ~75%** |
| [skymap-gpu-overlays](skymap-gpu-overlays.md) | **PARTIAL ~35%** |
| [tui-live-session-parity](tui-live-session-parity.md) | **PARTIAL ~20%** |
| [first-light-resilience](first-light-resilience.md) | **DONE** (2 of 3 sub-plans shipped; sub-plan 3 deferred) |
| [driver-resilience](driver-resilience.md) | **DONE** (merged to main as 6 PRs + ARCH doc) |
| [fov-obstruction-detection](fov-obstruction-detection.md) | **DONE** (merged to main; scout UI/WebSocket surfacing, single-frame retry, Layer-2 recovery test all shipped) |
| [catalog-binary-format](catalog-binary-format.md) | **PARTIAL ~85%** (Option D + Phase 2A + 2B shipped; Phase 2C Tycho-2 bulk load deferred) |
| [polar-alignment](polar-alignment.md) | **DONE ~85%** (Phases 1-5 shipped; refraction-corrected apparent pole + live pressure/temperature still pending) |
| [gpu-stretch-tests](gpu-stretch-tests.md) | **DONE ~90%** (Phases 1-4 + follow-ups D & F shipped; Phase 5 separate-CI-job replaced by inline `test-unit` run with mesa-vulkan-drivers) |
| [icc](icc.md) | **DONE ~95%** (Tiff 3.0 + new SharpAstro.Jpeg consumed; display helper + Nina JPEG injection wired) |
| [stacking](stacking.md) | **DONE ~85%** (Phases 1-12 + 8.0-8.3 shipped — selector + 6 strategies + FrameCache + PartialFitsReader; Phase 13 CLI orchestrator + 14/15 LiveStacker wiring + 10 MMF sink still pending). Deep-sky/sidereal only; planetary is the separate [planetary-stacking](planetary-stacking.md) plan |
| [planetary-stacking](planetary-stacking.md) | **DONE Phases 1-9 + Bayer drizzle + live viewer + perf** (branch `feat/planetary-stacking`, 2026-06-24): planetary lucky-imaging, separate from deep-sky [stacking](stacking.md) (star-quad align/sigma-clip don't apply to a disk). CPU engine in `TianWen.Lib.Imaging.Planetary` (+ `TianWen.Lib.Stat`): `SerFrameStream` → Laplacian/gradient quality grade → 2D phase-correlation (cached reference FFT) + disk-COM global align → feature-driven alignment points + displacement-mesh warp → per-AP quality-weighted **split-CFA** stack + **Bayer drizzle** (AP-mesh forward-scatter) → 6-level **wavelet sharpen** (default/bandpass/combo presets, `PlanetaryMaster` shared finalize). CLI `tianwen planetary-stack` emits linear + sharpened FITS masters + a high-key planetary PNG. **Live (Phase 9):** `RollingWindowStacker` (frame-capped sliding window, O(pixels) add/evict, align-bound) + `LiveStackPreviewSource` drive a RAW/STACK toggle + Registax-style 6-layer wavelet sliders in `tianwen-fits` (and the GUI viewer tab, shared `ViewerState`); **follow-the-playhead**, off-thread with sharpen-priority + per-request cancellation so slider adjustments are instant regardless of stack time. BenchmarkDotNet + `profile planetary` stage breakdown added (align is ~85-89% of the stack). Validated on a real 30k-frame Jupiter SER. **Remaining:** Meeus-CM derotation (P10/11), live camera stream (P12), GPU compute (P13). |
| [ai-enhancement](ai-enhancement.md) | **DONE Phases 0-7** (CLI side, merged via PR #10) -- model fetch, MTF helpers, ChunkedInference, IStarRemover/IStellarSharpener/INonStellarDeconvolver/IDenoiseEnhancer/IGradientCorrector atomic enhancers, step-based `SharpenPipeline` orchestrator with `SharpenIntermediates` retention selector, ChunkedNafnetRunner shared, GraXpert BGE gradient correction with `--save-gradient`, dual-stretch pipeline (Frank StarStretch + auto-MTF or opt-in GHS + bg-reduce + Reinhard highlight roll-off + Screen recombine + per-plate float TIFFs), `tianwen image {sharpen,remove-stars,flatten,render,stats}` CLI verbs. Plus refinements: 16-px chunk pad, ArrayPool input tensors, `--png` flag, `SubtractiveChromaticNoise` + `Lerp`, `StarsScnrMode` + per-step AI blends, DirectML-on-Adreno for win-arm64 (2.4x speedup), pre-stretched-input auto-detect, `Image.Histogram` median bugfix, `Image.BilinearResize`, `ModelResolver` cross-platform path-separator guard. Phase 7 GUI menu entry + Phases 8-10 (runtime model self-bootstrap, classical fallbacks, NPU INT8 quant) NOT STARTED -- see [ai-enhancement-next.md](ai-enhancement-next.md) for the priority order of follow-on work. **NOTE (2026-06-30):** the CLI-side dual-stretch render + the self-contained `DualStretchPlates` described above were **replaced** by the unified WB-once / per-plate self-stretch / zero-pedestal render in `MasterPreviewRenderer` + `StretchSolver` (both moved into `TianWen.Lib`, CPU-only); the CLI now renders nothing. See [stacking-render-pipeline.md](../architecture/stacking-render-pipeline.md). |
| [ai-enhancement-next](ai-enhancement-next.md) | **NOT STARTED** (Frank-parity Star Stretch port: ColorSaturation + SCNR `preserveLightness`; per-plate stretch via `StretchTransform` abstraction with asinh + GHS + gamma + MTF concrete impls; reroutes the deferred items from ai-enhancement into priority order) |
| [rc-astro-enhancers](rc-astro-enhancers.md) | **Phases 1-3 DONE** (1+2 merged to main via PR #58, branch `feature/rcastro`; Phase 3a-3d on `feat/rc-astro-phase3`, 2026-06-30): RC-Astro CLI-wrapper enhancers (sxt/nxt/bxt via the `rc-astro --json` NDJSON protocol -- encrypted ONNX, so not in-proc ORT), preferred over SETI Astro when present + licensed via the `DeferredEnhancer` proxy (license probe + RC-vs-SAS choice deferred to first `EnhanceAsync`, never at DI build). BlurX-first enhance program when a deblurrer is live. **Phase 3 (options + progress + UI + server):** 3a immutable `EnhanceOptions`/`EnhanceTuning` threaded through `SharpenPipeline.ProcessAsync` + `--ai-backend`/`--bxt-sharpen`/`--nxt-*` on `image sharpen` + `stack --enhance` (no mutable singleton); 3b per-step `EnhanceProgress` (boundary tick + RC NDJSON sub-step %) -> CLI printer; 3c interactive Enhance action in `tianwen-fits` (toolbar + 'E', right-click cycles backend, off-render-thread, `AdoptImageAsync` swap) -- landed in the FITS viewer since the GUI has no document-viewer tab; 3d `tianwen-server` `POST /api/v1/image/enhance` + `GET .../status` + `ENHANCE-PROGRESS`/`-COMPLETED` WebSocket via `EventBroadcaster`, single-flight `HostedImageEnhancer`, shared `EnhanceOptions.TryParse` (one parser for CLI + server), AOT DTOs registered + win-arm64 publish-smoke-tested. **Deferred nice-to-have:** server job-id/queue model -> [server-enhance-job-model.md](server-enhance-job-model.md). |
| [ghs](ghs.md) | **DONE ~90%** (Phases 0-11 shipped on branch `ghs-converge`: reference math port + B-branch curve + auto-converge `Image.ConvergeGhsStretchFactor`. Subsequent work added mode-target convergence so the bg peak (not the median) lifts to ~0.25 -- median-target left the output dim because median sits above mode for typical astro frames. `--ghs-stages 1|2|3` exposes Cranfield's canonical multi-stage chain (gh-astro 2.7-2.9). CLI refactored to three orthogonal selectors `--star-stretch-mode / --starless-stretch-mode / --stretch-mode` + `--ghs-converge auto|manual`. Post-recombine stretch wired via new `MtfStretchFinalStep` + `GhsStretchFinalStep` (single-pass only on final); `ApplyGhsChain` helper shared between starless + final dispatchers. GHS stays **opt-in** per `feedback_ghs_not_default` -- MTF remains the default starless stretch. Canonical recipe = `--dual-stretch --starless-stretch-mode Ghs --ghs-target Mode --ghs-target-value 0.25 --ghs-stages 3`. {broadband, narrowband, single-light} corpus validation outside SoL drizzle still pending; `--star-stretch-mode mtf|ghs` and multi-stage on `GhsStretchFinalStep` parked.) |
| [background-extraction](background-extraction.md) | **NOT STARTED** (design captured; classical poly + optional RBF gradient removal, ports SAS Pro `abe.py`; placed pre-stretch / pre-AI in the linear domain) |
| [moon-avoidance](moon-avoidance.md) | **DONE** (branch `feat/moon-avoidance`, 2026-06-12): per-bin Moon penalty in `ScoreTarget` via shared `MoonGrid` - illumination x quadratic proximity, Moon-below-horizon gate; radius is an optional param on Schedule/TonightsBest/ScoreTarget (default 30, ON, 0 disables). Dropped the planned `SessionConfiguration` knobs - the session never scores (planner-only path) so they would have been dead code. 6 tests in `MoonAvoidanceTests.cs`; full unit (2596) + functional (286) suites green |
| [scheduled-starts](scheduled-starts.md) | **DONE** (branch `feat/top-5-todo`, 2026-06-12): `WaitForScheduledStartAsync` + `ScheduledStartOutcome` in `Session.Timing.cs`, called at top of `ObservationLoopAsync`; waits until `Start - ScheduledStartLeadTime` (new `SessionConfiguration` knob, default 3 min) on the mount clock. Same-/past-Start short-circuits (hosted API + legacy + existing tests unchanged), beyond-session-end skips cleanly, late starts proceed unclamped, cancellation unwinds via OCE. 4 tests in `SessionObservationLoopTests` (branch outcomes, real wait with frame-timestamp gap, session-end skip, cancel-during-wait); full unit (2596) + functional (290) green |
| [site-conditions](site-conditions.md) | **DONE** (branch `feat/top-5-todo`, 2026-06-12): pure `SiteConditions.Resolve(IWeatherDriver?)` (live weather -> standard, per value) + `ApplyTo(Transform)`; `IMountDriver.TryGetTransformAsync(SiteConditions, ct)` overload drops the 1010/10 hardcode (standard tier auto-derives pressure from elevation -> retires the `IMountDriver.cs:344/345` TODOs); `Session.ResolveSiteConditions()` reads the live weather driver and feeds 4 session call sites; polar-align swapped to the resolver. **Design correction:** the proposed profile-stored pressure/temp override was dropped -- they are varying values (unlike static lat/long/elevation) so they are never persisted; the no-weather fallback derives pressure from the profile's static elevation. `SiteConditionsTests` (7 unit tests) |
| [skymap-time-scrub](skymap-time-scrub.md) | **DONE** (branch `feat/top-5-todo`, 2026-06-12): `SkyMapState.TimeOffset` stacks on the base instant in the single `viewingTime` derivation in `SkyMapTab.Render`, so it drives sky color / LST / planets / horizon downstream with no rendering refactor. `SkyMapTab.HandleKey` re-points the arrows at the offset (Up/Down +-1h, Shift +-10m; Left/Right -+1d; PageUp/PageDown +-1w), adds `N` (`ComputeMidnightOffset`), `0` (offset reset), `T` (full reset) - redraw-only, never planner recompute. HUD offset chip via `SkyMapState.FormatOffset`; inspector exposes `mapTimeOffset`. 20 unit tests (`SkyMapTimeScrubTests`: FormatOffset, ComputeMidnightOffset, day->night sun-altitude propagation); verified live (inspector): Up x3 / `N` / `0`. Mount reticle stays wall-clock |
| [layout-engine](layout-engine.md) | **DONE -- Phases 0-3 merged to main (PR #34) + released; Phase 4 (single-panel tree) on the Session config form** (2026-06-19). **Theme + engine + shared widgets** (DIR.Lib 5.0): `UiTheme`/`UiPalette`/`UiMetrics`; `LayoutNode` tree (Stack/Dock/Grid/Overlay/Leaf + Fixed/Auto/Star) + `LayoutEngine.Measure`/`Arrange`; pixel painter (`ArrangeLayout`/`PaintLayout`/`RenderLayout` on `PixelWidgetBase`, auto-`RegisterClickable`) + Console.Lib `CellLayout` cell painter over the same arranged tree; surface-agnostic `PixelMenuWidget`/`MenuWidget` (first widget rendering one BuildTree on BOTH GPU + TUI). **Per-row tab adoption** via one shared `FormRowLayout` builder set: Planner target rows, Equipment (all 14 separate `RegisterClickable` pairs -> `InsetPillButton`/leaves + data-driven per-OTA `EquipmentPanelLayout` / section-driver, TODO.md:57), LiveSession (ModePill + steppers + polar config rows + toggles), Notifications Clear button -- all draw==hit. **Phase 4 (single-panel tree):** `SessionConfigLayout.Build` emits the whole Session config form as ONE `LayoutNode` tree (section headers + field rows + per-kind controls), arranged once into a scroll-offset bounds, off-panel nodes filtered, painted inside `PushClip` -- the `cursor += itemH` walk + header clip-hack are gone. 9 `SessionConfigLayoutTests` + 14 `EquipmentPanelLayoutTests`; inspector-verified geometry. **Released chain:** DIR.Lib 5.0 -> SdlVulkan.Renderer 6.4 + Console.Lib 3.0 on NuGet; TianWen pins bumped + merged. **Remaining/deferred:** LALR.CC layout DSL (the *tree* -- execution-order Phase 4 -- is done; the DSL front-end stays gated until the C# builder proves verbose); FitsViewer panel `LayoutNode` port + TUI consumption of the section list judged NOT warranted. |
| [rotator](rotator.md) | **NOT STARTED** (per-OTA field rotation; new `DeviceType.Rotator` + `IRotatorDriver` + ASCOM `IRotatorV4` / Alpaca / Fake drivers; mirrors the `CoverCalibrator` device-type wiring; novel logic is framing-on-center + post-flip re-rotate fanned out over `Setup.Telescopes`. Validates against `FakeRotatorDriver` + ASCOM/Alpaca simulators; real-hardware check deferred -- no rotator at hand) |
| [signal-handler-boilerplate](signal-handler-boilerplate.md) | **DONE -- Phases 1-3** (branch `refactor/signal-handler-boilerplate`; drafted 2026-07-03 after the two route-only passes PR #68 + #70). Three private instance helpers on `AppSignalHandler` -- `Notify(severity, message)` (all AppendNotification + redraw sites), guard helpers (`EnsureSessionIdle` / `TryGetConnected<T>` / `TryResolveIdleOtaFocuser`), and `RunTracked` over the upstreamed `DIR.Lib.BackgroundTaskTracker.RunGuarded` -- swept across the handlers and extended to the ones added since the draft (Flats mode, manual cover, comets). Bespoke error sites (Recompute, SkyMap slew/solve, DiscoverDevices, async device connects, StartPolarAlignment) deliberately left untouched. **Phase 5 shipped too**: the ctor's subscription groups split verbatim into per-concern `Subscribe*` methods across `.Planner/.SkyMap/.Equipment/.LiveSession/.Polar/.Flats.cs` partials (field-alias preambles keep bodies verbatim; ctor call order preserves SignalBus registration order); core file 2566 -> ~860 lines. **Dropped:** Tier 4 `Wire<T>` (by its own kill criterion — three cooler handlers already need three shapes). NOT a fluent DSL / MediatR / source generator (SignalBus stays deliberately lightweight). |
| [obstruction-first-light-oracle](obstruction-first-light-oracle.md) | **A + C DONE** (branch `feat/top-5-todo`, 2026-06-13; B follow-up): pivoted from catalog-floor-only to a **zenith calibration anchor** - the rough-focus zenith frame calibrates `detected/catalog-predicted` = transparency x detection efficiency. **A** (oracle): first scout vs `catalog(target, scout-limit, airmass-dimmed) x zenithEfficiency x OracleFactor`, shortfall -> existing `NudgeTestAsync`; catalog-floor fallback + narrowband/sparse/missing-DB guards. **C** (cloud gate): crushed zenith efficiency -> hold-and-re-gauge after rough focus. New `StarDetectionModel` / `CatalogStarCounter` / `NightSkyGauge` / `Session.Imaging.SkyGauge.cs`; 4 `SessionConfiguration` knobs; scout `maxStars` 200->1000. 16 unit + 2 functional tests (existing 11 scout + 41 full-session still green). **B** (transparency HUD from `EffectiveLimitMag`) deferred |
| [multi-source-previewer](multi-source-previewer.md) | **Phases 1-7 DONE** (2026-06-23): fold the standalone SER.Viewer prototype into `tianwen-fits` as a multi-source previewer (FITS+TIFF+SER, auto-switch to playback for SER). **P1-3** (merged, PR #52): SER.Lib wired as a sibling + pure `SerImageBridge` (SerColorId→SensorType+offset, raw→[0,1] into reused buffers); `IPreviewSource` (renderer previews it — `AstroImageDocument` the 1-frame impl, `SerPreviewSource` the N-frame impl, stats/WB computed once, per-frame = cheap raw upload); `.ser` open + auto-switch. **P3.5-6 + auto-WB** (branch `feat/multi-source-previewer-playback`): linear-default + lazy-trailer + cancel/supersede off-thread loads; off-thread frame-paced decode-ahead playback + transport bar; MHC debayer branch in `VkFitsImagePipeline` (CPU/GPU mirror); manual R/G/B WB sliders + gray-world Auto button (shared `ViewerState` → GPU WhiteBalance, also lights up the GUI viewer tab) with the two linear-mode/stat-scale pipeline fixes. **P7** (SER.Lib PR #2, awaiting rebase-merge): standalone `SER.Viewer` deleted; SER.Lib = library + tests (zero-dep, no version bump). Follow-up: a live astro-camera video stream is just another `IPreviewSource`. |
| [altaz-mount-support](altaz-mount-support.md) | **Phase 0 DONE — Phases 1-3 NOT STARTED** (2026-06-22, PRs #47 + #48): SkyWatcher AZ-GTi-class dual-mode mounts. **Phase 0 (safe):** user-selectable `?alignment=GermanPolar\|Polar\|AltAz` setting (default GermanPolar; AZ/EQ is *not* protocol-detectable, so it can't be auto-detected); `AltAz` is **report-only** — `GetAlignmentAsync` returns it + pier reads return `Unknown`, but coordinate slew/track/sync are **refused** (`EnsureEquatorialAlignment` throws) so an alt-az mount can never silently mis-point; the session's meridian-flip gate is now **GEM-only** (`Session.Imaging.cs` reads `AlignmentMode`, fork/AltAz skip the flip cycle). **Phases 1-3 (usable) NOT STARTED:** Az/Alt↔step transforms, `BeginSlewToTargetAsync` pier-gate bypass, dual-axis tracking, position reads (Phase 1 → visual/EAA/plate-solve); alt-az guiding (Phase 2); long-exposure alt-az **imaging** (Phase 3) is **blocked on rotator support** (field rotation; `IRotatorDriver` doesn't exist — see [rotator](rotator.md) + TODO.md). Recommendation in the plan: pursue Phase 1 only on demand, defer Phase 3 until a derotator lands. |
| [flat-frame-automation](flat-frame-automation.md) | **Phases 1-4 DONE** (P1-3 2026-07-01, **P4 2026-07-09**): automated **panel/calibrator + twilight sky** flats, an **on-demand surface + manual panel**, and a **GUI Flats mode**. **Phase 1** — pure `FlatExposureSolver` (`Imaging/Calibration/`, linear-panel-model: Capture/Adjust/Fail) + `Session.TakeFlatsAsync` (`Session.Flats.cs`): per-OTA close cover → calibrator on → per-filter converge → write `FrameType.Flat` → off; opt-in `TakeFlatsOnSessionEnd` after the observation loop, before `Finalise` warms cameras (setpoint temp). **Phase 2** — pure `SkyFlatExposureSolver` (re-metered per frame: Capture/Adjust/Wait/Stop, wrapping `FlatExposureSolver` with twilight-direction awareness) + `Session.TakeSkyFlatsAsync` (`Session.SkyFlats.cs`): opens covers, coarse solar-altitude window gate (`VSOP87a.Reduce(Sol)`), anti-solar zenith slew (`BeginSlewToZenithAsync`, west@dawn/east@dusk) with **tracking off** so stars average out. `FlatSource=TwilightSky` dispatches **dawn** at the end-of-session hook; `TakeSkyFlatsAtDusk` adds a **dusk** session-start hook (cooled first) as insurance against a clouded dawn. **Phase 3** — `ISession.RunFlatsOnlyAsync` (`Session.FlatsOnDemand.cs`): self-contained connect → cool → capture → finalise **outside** `RunAsync` (connects only the flat-relevant subset via the shared `ConnectTelescopeAsync` — mount only for sky, never the guider; focused `FinaliseFlatsAsync`), behind CLI `tianwen flats` (`FlatsSubCommand`) + `POST /api/v1/session/flats` (`FlatsRequestDto`, AOT-registered; mirrors `/session/start`), source/period via shared `FlatRunParsing`. A **manual** hand-switched panel is a **device** (`ManualCoverDevice`/`ManualCoverDriver`, a degenerate `ICoverDriver` mirroring `ManualFilterWheelDevice`: cover `NotPresent`, calibrator `Ready`-on-demand, no analog brightness), assigned to an OTA cover slot and captured through the **same** calibrator path — no `ManualPanel` source, no session branching; registered via `AddDeviceType` so it round-trips through `TryGetDeviceFromUri` (the manual filter wheel's matching gap is closed too). All paths share `ResolveFilterPositions`/`PrepareFilterForFlatsAsync`/`CaptureFlatFrameAsync`/`WriteFlatToFitsFileAsync`; identical output contract (`Flats/<date>/<filter>/Flat/`, grouped by `MasterGroupKey`, path cosmetic). `SessionPhase.Flats` + `FlatIlluminationSource` (Calibrator/TwilightSky) / `TwilightPeriod` enums; 22 solver + 7 flat-orchestration + 4 manual-cover-driver + 4 fake-discovery tests. **Phase 4** — GUI **`LiveSessionMode.Flats`** on the Live Session tab (mode-pill dropdown, like PolarAlign/Planetary): `FlatsBootstrapper` runs `RunFlatsOnlyAsync` with `ActiveSession` set but not `IsRunning` (keeps preview layout + mode pill; `FlatsCts`-gated); `LiveSessionTab.Flats.cs` side panel (source selector `FlatIlluminationChoice` + per-filter stepper + Start/Cancel, phase+status while running); live preview via `Session.Flats.cs` `PublishFlatPreview` + per-filter `_currentActivity`; equipment 💡 "+ Manual Light Panel" (`AssignManualCoverSignal`). New **session→UI user-prompt channel** (`ISession.PromptRequested` + `SessionPromptEventArgs.Respond` + `Session.RequestUserConfirmationAsync`, headless auto-proceed; GUI `PendingPrompt`/`RenderSessionPrompt`/`RespondSessionPromptSignal`) gated on the new **`ICoverDriver.CanControlBrightness`** (false only for `ManualCoverDriver`) — prompts case D (hand-switched panel) only; reused for a future dark-frame "cover the scope" prompt (`CoverStatus.NotPresent`). +3 prompt tests. **Deferred:** sky-flat live thumbnails, the dark-frame flow itself, a per-filter progress bar. |
| [device-simulator-ci](device-simulator-ci.md) | **DONE + LIVE-VALIDATED** (merged #72/#73; follow-up expansion 2026-07-04): on-demand CI tests that drive the real drivers against live simulators. New `TianWen.Lib.Tests.Simulators` project, env-gated via `SimulatorGate` (`TIANWEN_ALPACA_SIM` / `TIANWEN_ASCOM_CI`) so it skips clean with no sim. `AlpacaSimulatorTests` drives the production `AlpacaClient` + drivers against a live OmniSim over HTTP -- management API + the camera **ImageBytes** round-trip + telescope/focuser/filterwheel/covercalibrator/switch + camera gain/offset/readout/sensor metadata; devices resolved via the management API, not UDP. **The suite paid for itself on its first live run: caught 2 real Alpaca driver bugs** (mono camera couldn't connect; filter wheel never populated slots) + a follow-up stub audit (`Gains`/`Offsets` hardcoded empty, `LastExposureDuration` null, `ReadoutMode` a no-op -- all fixed). `AscomDeviceTests` moved here from `.Functional`, re-gated `Debugger.IsAttached` -> `TIANWEN_ASCOM_CI`, now mirrors the Alpaca focuser/filterwheel/covercalibrator coverage. `.github/workflows/simulators.yml`: `workflow_dispatch` (`-f suite=alpaca\|ascom\|both`) **+ a weekly `schedule` running the Alpaca leg only** as an unattended regression guard. Real-time settle waits go through `ITimeProvider` (`SystemTimeProvider.Instance`), not raw `Task.Delay`. |
| [serial-lib](serial-lib.md) | **NOT STARTED** (design captured, 2026-07-04). A `Serial.Lib` sibling repo (Lzip.Lib-style) that owns cross-platform, cancellable, timeout-honouring serial I/O — because .NET `SerialPort` async `BaseStream` reads spuriously abort (`ERROR_OPERATION_ABORTED`) on CH34x bridges and are blocking-on-a-thread underneath anyway (dotnet/runtime#28968). Interim fix shipped in `fix/gemini-flat-panel`: cancellable `ISerialConnection.SynchronousReads` (Task.Run over blocking `ReadByte` + `ReadTimeout` slices). P1 = extract that wrapper; P2 = native P/Invoke backend (Win32 overlapped-done-right / Linux termios); P3 = re-point TianWen. |
| [ascom-com-sta-message-pump](ascom-com-sta-message-pump.md) | **NOT STARTED** (finding + design, 2026-07-04). Vendor ASCOM COM drivers that busy-spin `Application.DoEvents()` on connect (Gemini FlatPanel + Focuser Pro confirmed via decompile; iOptron ×2, QHYFWRS232 suspected) hard-crash a headless connect (`0xC0000409`, no managed exception) — they assume a WinForms message pump. Mainstream drivers (OmniSim, ZWO/ASI/PlayerOne/QHYCCD) are clean. Fix: host ASCOM COM calls on a dedicated STA thread running a real message loop. The native Gemini serial driver sidesteps this for the panel. |
| [image-codecs-facade](image-codecs-facade.md) | **NOT STARTED** (design captured, 2026-07-05). Two new StbImageSharp-repo packages — `SharpAstro.Codecs.Abstractions` (`IImageDecoder` static-abstract sniff + `IDecodedImage`/`RasterImage` common type) + `SharpAstro.Codecs` (facade: explicit AOT registry + magic-byte dispatch) — so consumers reference one package (killing the family version-skew) and sniff/dispatch/`ToRgba8` live in one place instead of three ad-hoc copies (`Console.Lib/ImageDecoder`, `PngImage.ToRgba8`, tianwen `TryReadImageFile`). Name is `Codecs` not `Imaging` (already `TianWen.Lib.Imaging`). Facade must NOT dep DIR.Lib; `RgbaImage` adapter stays consumer-side. P1 abstractions → P2 PNG/JPEG facade → P3 Console.Lib migration → P4 TIFF/EXR/JXR/JXL → P5 tianwen delegates (retire read-path Magick.NET) → P6 FC.SDK. Open: static-abstract vs instance registry; `IDecodedImage` fidelity fields. |
| [comet-ephemeris](comet-ephemeris.md) | **Phases A-D DONE, C2c partial** (branch `feat/comet-ephemeris`, 2026-07-10): JPL comets as a dynamic ephemeris-computed catalog. **A (identity + math):** `Catalog.Comet` (`c` tag) + `ObjectType.Comet`; `CometDesignation` parse/pack/canonical — structured Base91 bit-field `CatalogIndex` (same mechanism as Tycho2/PSR/WDS: kind+year+half-month+order+fragment ≤48 bits), reconstructs from SBDB `prefix`+`pdes`, handles BC years + asteroid-style 2-letter half-months; `IsSolarSystemObject` now covers comets; `CometEphemeris` universal-variable (Stumpff) two-body Kepler propagation (one path for elliptic/parabolic/hyperbolic) + Earth from VSOP87a + light-time + M1/K1 total-magnitude law. Pinned against JPL Horizons: 12P (elliptic) + C/2023 A3 (near-parabolic) reproduce RA/Dec within 1 arcmin at epoch, 12P T-mag matches 15.037. (A first cut used a readable plain-7-bit-ASCII index `cC2024A1`, but the longest real designations — e.g. `C/2001 OG108`, 10 chars — overflow the 9-char ASCII ceiling; the structured bit-pack reaches 100% with `ToCanonical` still round-tripping to `C/2024 A1`.) **B (data layer):** `SbdbCometSource` (keyless bulk `sb-kind=c` fetch, pure `Parse` skip+count) + `SbdbJsonContext` (source-gen, AOT) + `CometRepository` cache `AppData/SmallBodies/comets.json` (weather-pattern TTL 7d + stale-offline fallback, `FetchedUtc` envelope, `ITimeProvider`-driven) + `TryGetPosition`. **Live-validated: 4068/4068 comets mapped** (100%). No separate MPC file (SBDB is MPC-fed). **C-D (DONE, inspector-validated):** sky-map dynamic markers + `com[e]t` (E) toggle + info-panel LIVE alt-az/rise-set + vmag **sparkline**; F3 sky-map search (designation + name); planner `TonightsBest` comet proposals + vmag-driven object bonus; `tianwen-mcp catalog.lookup` comet-aware + observability outlook; selection **paths** (body-appropriate window) + orbital-**event** markers (`SkyPathEventDetector`: station R/D, greatest-elongation, opposition, perihelion). Invariant: comets are NOT in `ICelestialObjectDB` (augment per-consumer). **Remaining:** C2c planner-tab search/autocomplete for comets + a dedicated vmag *chart* (the sky-map sparkline already covers the curve); #26 off-thread path/sparkline sampling. **Deferred:** per-object Horizons ephemeris + non-sidereal tracking rates, bright asteroids (`sb-kind=a`). |
| [soft-discovery](soft-discovery.md) | **PARTIAL** (2026-07-04, `fix/gemini-flat-panel`). DONE: per-source discovery parallelised + non-serial sources overlap the serial probe pass (`IDeviceSource.ConsumesSerialProbe`), probe reads made cancellable-synchronous (fixes the CH34x abort for all serial discovery), boot-aware serial probing (`ISerialProbe.Warmup` + `AssertControlLines`, pass-2-only) so the Gemini panel is auto-discovered; `device discover` ~30 s → ~25 s. NOT STARTED: the GC-style **soft** mode (verify-known-devices cheaply, skip the fan-out when nothing changed) + probe-budget tuning (the ~17 s serial phase) + the pinned-verify DTR-isolation gap + splitting OnStep serial-vs-mDNS discovery. |
| [ai-denoise-deconv](ai-denoise-deconv.md) | **NOT STARTED** (plan captured 2026-07-11). TianWen-trained NAFNet denoiser (`IDenoiseEnhancer`) + psf01-conditioned deconvolver (`INonStellarDeconvolver`) as a third backend tier (Auto: RC → TianWen → SAS). **License-clean by construction**: RC-Astro EULA §10 forbids oracle/distillation/paired-dataset use of its outputs, so ground truth is self-derived — Noise2Noise sub↔sub pairs + stack-as-truth eval for denoise, synthetic Moffat-PSF degradation (measured-distribution sweep) for deconv — from the ~20-24k recent (2024+) raw lights on D: (full disk survey — roots, per-era layout, camera table, hazards — captured separately in [astro-archive-survey.md](astro-archive-survey.md)). `tianwen dataset build` exports session-grid fp16 tiles with the *same* MTF/tile code the inference runner uses (zero train/inference skew); PyTorch offline on rented GPU (RunPod 4090, ~$15-50/run), ONNX opset-17 + provenance contract JSON asserted at load; customer machines are inference-only (no Python). Photometric-integrity gates (flux/centroid deltas) as release blockers — the science-safe claim RC/SAS don't make. Star removal explicitly out of scope. |
| [smart-framing](smart-framing.md) | **Phases 1-3 DONE, Phase 4 (sky-map group frame) DEFERRED** (2026-07-11): pin M8 with a wide-field profile → the planner auto-groups M20 into the same pointing ("M8 + M20", one scheduled observation at the combined-footprint centroid). Pure Lib core (`FramingGrouper` tangent-plane fit + greedy accretion, NOT quadratic: Dec-band binary search + grid-local neighbour discovery) + profile-persisted sensor specs auto-captured on first camera connect (`OTAData.CameraPixelSizeUm/SensorWidthPx/SensorHeightPx`, offline FOV via `PrimarySensorFovDeg`) + planner wiring (`ComputeFramingGroups` on every pin change, `CollapseForSchedule` at schedule build; no captured FOV → byte-identical schedule). Identity is index-based (cross-indices), no name matching. **Shipped with the SIMBAD merge v4 root-fix**: alias-only identifiers (all Messier numbers) now resolve through the cross-index table in `MergeSimbadRecords` (was: Sh2-25 landed as a standalone "Lagoon Nebula" duplicate because its only NGC-family id is "M 8"); LINQ-free per-record hot path; full 13-catalog SIMBAD + OpenNGC data refresh; both snapshots rebaked (AlgorithmVersion 4); external `lzip` retired for the managed `tools/lzip-util.ps1`. 11 grouper + 5 planner tests; 408-test catalog/framing sweep green; init stays on the snapshot fast path (1.86 s). |
| [web-multithreading](web-multithreading.md) | **NOT STARTED** (research captured, 2026-07-18). Forward-looking; the catalog-load freeze that motivated it is **already solved by AOT** (23 s→0.55 s init, 26 s→0.59 s sweep). Records what real browser parallelism would take. The constraint: real .NET threads (`WasmEnableThreads`) need `SharedArrayBuffer` → COOP/COEP headers → **GitHub Pages can't set them** (verified live). Options: (A) wasm-threads + a `coi-serviceworker` shim (shared heap, `Task.Run`/`BackgroundTaskTracker` parallelize almost free, but bigger download + COEP subresource discipline + Blazor main-thread-affinity marshaling); (B) plain message-passing Web Worker running a **second .NET runtime** (no sharing, header-free, works on Pages today, but ~2× runtime RAM + marshal flat arrays via Transferables + no first-class API); (C) GPU compute (header-free, but needs WebGPU — see web-webgpu). Blazor surface is thin + we're AOT/reflection-clean so dropping it is feasible, but it's not required for any threading path and the `WebGlCanvas`/`CanvasTextOverlay` components are chess-shared → **keep it**. **Recommendation: build none now**; trigger for (A) = real CPU-parallel browser work (in-browser stacking), for (C) = interactive sweep re-scoring. |
| [web-tycho2](web-tycho2.md) | **P1 DONE** (2026-07-18), P2-P4 measurement-gated. Bring the full ~2.5M-star Tycho-2 catalog to the web sky atlas (was HR-only ~8.6k; `Lightweight=true` strips `tyc2.bin.lz`). A **data-delivery problem, not compute/GPU**: render already solved (`DrawInstanced`, desktop proves the scale), the ~30 MB payload is the dominant blocker. **P1 (shipped):** `ICelestialObjectDB.TryLoadTycho2BulkFromCompressed(byte[])` injects the fetched-still-compressed bytes into a fresh DB (display-only: sets `_tycho2Data`/`_tycho2StreamCount`, skips the spatial index + cross-maps); `Planner.razor` lazy-fetches `tyc2.bin.lz` (same-origin static asset, CI-staged into wwwroot by pages.yml, gitignored) on **first atlas-open**, decodes+flattens off the first-paint path via the shared `SkyMapState.FillTycho2StarVertices`, and hands the buffer to `WebGlSkyMapPipeline.SubmitTycho2Stars` → render-thread swap over the HR seed (the desktop HIP→tyc2 analogue; switch, not overlay — no additive double-draw). Graceful 404 degrade to HR-only (dev server without the baked asset). Zoom-aware mag limit is the shared `SkyMapUbo` (already wired). Pinned by `Tycho2BulkInjectionTests`. **P2 (gated on the measured AOT decode being too slow):** multi-member bake (`LzipOptions.MemberSize`) + `WasmEnableThreads` + coi-serviceworker → `LzipDecoder.Parallel.For` parallelizes, zero new decode code. **P3:** IndexedDB decoded-snapshot cache. **P4 (deferred):** spatial tiling. Open: the AOT decode wall-time (gates P2) needs a published-build measurement; the double-compressed `.br` artifact-size hit is a payload-budget item. |
| [web-webgpu](web-webgpu.md) | **NOT STARTED** (research captured, 2026-07-18). Whether to add a WebGPU backend to `WebGl.Renderer` + reuse shaders + use **GPU compute** to parallelize the sweep without the CPU-thread/`SharedArrayBuffer` wall. Shader intake three-way: Vulkan=SPIR-V (compile), **WebGL2=GLSL source (browser compiles, no toolchain — why the web port was cheap)**, WebGPU=**WGSL only** (needs GLSL→WGSL transpile via naga/Tint, or WGSL twins). No compute shaders exist anywhere today; the tonight's-best sweep is the first plausible consumer (data-parallel, flatten candidates → dispatch → readback; header-free parallelism). Fallback WebGPU→WebGL2 is mandatory (support not universal early-2026) and the `Renderer<TSurface>` seam already supports a second backend (C# opcode layer is GL-agnostic; the JS `flush()`/`syncAtlas()` shim + shaders + `RegisterPipeline` raw-GLSL contract are the WebGL-specific parts). **Recommendation: defer**; trigger = interactive planner re-scoring (time-scrub) or a heavier browser GPU workload. If pursued: bounded branch-only spike (feature-detect `navigator.gpu` + one WGSL compute pass beside the CPU path + prove GLSL→WGSL on one pipeline, then measure). |

---

## serial-probe — DONE ~90%

- Phase 1 (plumbing / core types): **DONE** — `ISerialProbe`, `ISerialProbeService`, `SerialProbeService`, `SerialProbeMatch`, `ProbeExclusivity`, `ProbeFraming` under `src/TianWen.Lib/Devices/Discovery/`. `ProbeAllAsync` wired into `DeviceDiscovery.RunSerialProbesAsync` (`DeviceDiscovery.cs:164`).
- Phase 2 (logger scopes): **DONE** — scope instrumentation lives in `SerialProbeService` itself; per-source migration made per-loop scopes moot.
- Phase 3 (two-tier pinned-port discovery): **DONE** — `PinnedSerialPort`, `IPinnedSerialPortsProvider`, `ActiveProfilePinnedSerialPortsProvider` all present; `ISerialProbe.MatchesDeviceHosts` implemented; GUI composition root wires the active-profile provider.
- Phase 4 (migrate all sources): **DONE** — `SkywatcherSerialProbe`, `OnStepSerialProbe`, `MeadeSerialProbe`, `IOptronSerialProbe`, `QhyCfw3SerialProbe`, `QfocSerialProbe` all migrated; each source's `DiscoverAsync` reads `probeService.ResultsFor(...)` with no per-port open.
- Phase 5 (cleanup): **DONE** — `WaitForSerialPortEnumerationAsync` / `EnumerateAvailableSerialPorts` callers removed from individual sources.
- Tests: **DONE** — `SerialProbeServiceTests`, `SkywatcherSerialProbeTests`, `OnStepQuirkProbeTests`, `ActiveProfilePinnedSerialPortsProviderTests`, `SerialPortNamesTests`.
- Evolution: `ISerialProbe` uses `ProbeFraming` (ordering within baud group) instead of the simpler `Exclusivity`-only model in the plan — beneficial addition, not a shortfall.

## skymap-milkyway — DONE ~75%

- Phase 1 (shader + full-screen quad): **DONE** — `_milkyWayPipeline` in `VkSkyMapPipeline.cs`, alpha push-constant, sun-altitude fade, `ShowMilkyWay` toggle with `[S]` key, `TryLoadMilkyWayTexture` in `SkyMapTab.cs`.
- Phase 2 (texture pipeline + shipped file): **DONE** — `milkyway.bgra.lz` exists at `src/TianWen.UI.Gui/Resources/`, 8-byte header format + lzip compression via `MilkyWayTextureBaker.cs`.
- Phase 3 (bake from real data): **PARTIAL** — `tools/generate_milkyway.cs` is the .NET rewrite; `MilkyWayTextureBaker` + `MilkyWayBakerInputs` implement Tycho-2 luminance binning with Gaussian blur + brightness curve. Planck dust extinction path scaffolded (`DustOpacity`, `--dust-opacity`) but HEALPix reprojection step missing, so real-data bake has not yet replaced the analytical placeholder.
- Phase 4 (Planck HEALPix reader): **PARTIAL** — `tools/reproject_planck_dust.cs` exists, but no FITS download / `ang2pix` verified; dust input path in `MilkyWayBakerInputs.LoadAsync` reads a pre-converted float32 file, not raw FITS.
- Phase 5 (brightness/atmosphere integration): **NOT STARTED** — Bortle index modulation, HSV saturation slider, horizon fade absent; only the Phase 1 sun-altitude fade is present.

## skymap-gpu-overlays — PARTIAL ~35%

- Phase 1 (mosaic panels + sensor FOV to `LinePipeline`): **DONE** — `BuildFovLines` at `VkSkyMapTab.cs:726` replaces the old CPU `DrawFovQuadrilateral`; sensor FOV + mosaic panels loop into `_fovFloats` and are emitted via `WriteToRingBuffer`/`DrawLineBuffer` (`VkSkyMapTab.cs:164-183`).
- Phase 2 (planet dots to GPU): **NOT STARTED** — no planet instance buffer, no dedicated planet pipeline.
- Phase 3 (batch glyph rendering): **NOT STARTED** — no `ComputeLabelPositions` or `DrawGlyphAtBaseline`; `DrawConstellationNames` / `DrawGridLabels` still use per-label CPU text path.
- Phase 4 (kill CPU RA/Dec grid scan): **NOT STARTED** — `poleInView` branch still active at `OverlayEngine.cs:620-631`; overlay path doesn't iterate `AllObjectIndices`.
- Phase 5 (cache meridian line geometry): **PARTIAL** — `_meridianFloats` / `_horizonFloats` exist (`VkSkyMapTab.cs:29-30`) and are keyed via `_lastStaticGeomKey` (`VkSkyMapTab.cs:39`), which invalidates only on LST change. Plan's explicit `lstThreshold` sub-pixel guard not separately called out but equivalent behaviour from `_cachedLiveTime` 1s granularity.

## tui-live-session-parity — PARTIAL ~20%

- Phase 1 (extract renderer-agnostic draw helpers): **NOT STARTED** — `GuideGraphRenderer` class exists (`src/TianWen.UI.Abstractions/GuideGraphRenderer.cs`) but no generic `Render<TSurface>` static helper; `RenderCompactGuideGraph`, `RenderVCurveChart`, `RenderTimeline`, `RenderMiniSparkline` remain instance methods on `LiveSessionTab<TSurface>` (lines 473, 713, 946, 1151).
- Phase 2 (timeline band in TUI): **NOT STARTED** — no timeline `Panel` row in `TuiLiveSessionTab`.
- Phase 3 (compact guide graph in TUI): **NOT STARTED** — `_guideBar` is plain `TextBar` with RMS text only (`TuiLiveSessionTab.cs:51`).
- Phase 4 (V-curve overlay): **NOT STARTED** — no `VCurveRenderer` call in TUI.
- Phase 5 (preview mount section): **DONE** — `BuildPreviewRows` appends a mount `HeadingRow` block with RA/Dec/pier/HA (`TuiLiveSessionTab.cs:445`); consumes `liveState.PreviewMountState`.
- Phase 6 (clickable mini-viewer toolbar): **NOT STARTED** — `_previewToolbar` is text-only (`TuiLiveSessionTab.cs:54`).
- Phase 7 (ABORT button + modal): **PARTIAL** — keyboard abort flow + `ShowAbortConfirm` state handling exist (`TuiLiveSessionTab.cs:575, 718-730`), shown as status-bar hint; no red `Canvas` modal overlay or `ActionRow` button.
- Phase 8 (per-camera sparkline colors): **NOT STARTED** — `BuildSparkline` at line 600 is monochrome Unicode.
- Phase 9 (integration tests): **NOT STARTED**.

## first-light-resilience — PARTIAL (meta only)

- Meta-plan document (sequencing, non-goals, prior-art inventory): **DONE** as a coordination document.
- Sub-plan 1 (driver resilience): **NOT STARTED** — see below.
- Sub-plan 2 (FOV obstruction detection): **NOT STARTED** — correctly gated on driver-resilience PR-B1..B3.
- Sub-plan 3 (site horizon mask): **NOT STARTED** — explicitly deferred.
- Cross-cutting conventions (`ITimeProvider.SleepAsync`, Device/Phase logger scopes, `SessionConfiguration` XML docs): in place from prior work, but the new wrappers that would use them haven't been written.

## driver-resilience — DONE ~95%

Shipped on branch `driver-resilience` as 6 commits (PR-B1..B6). See
[`../architecture/driver-resilience.md`](../architecture/driver-resilience.md) for the full architecture
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

## fov-obstruction-detection — DONE ~95%

Shipped on branch `fov-obstruction-detection` after driver-resilience merged to main.

- Phase 1 (scout frame + compare): **DONE** — `ScoutResult`, `ScoutClassification`, `ScoutOutcome` in `src/TianWen.Lib/Sequencing/ScoutResult.cs`; `ScoutAndProbeAsync`, `ClassifyAgainstBaseline`, `TakeScoutFrameAsync`, `TryGetPreviousObservationBaseline` in `src/TianWen.Lib/Sequencing/Session.Imaging.Obstruction.cs`. Star-count classifier scales by `sqrt(exposure_ratio)` so a 10s scout vs. a 120s baseline compares correctly.
- Phase 2 (altitude-nudge disambiguation): **DONE** — `NudgeTestAsync` slews +N×half-FOV in declination, scouts again, and re-slews back in `finally` regardless of result. `ComputeWidestHalfFovDeg` derives nudge from camera pixel scale × focal length × NumX × BinX.
- Phase 3 (trajectory-aware wait): **DONE** — `EstimateObstructionClearTimeAsync` projects the target's natural altitude forward in 2-min steps until it reaches `current_alt + nudge_deg`, capped at 2 h lookahead. Returns `null` for setting targets.
- Phase 4 (integration with recovery loop): **DONE** — `RunObstructionScoutAsync` wraps the scout result + clear-time policy and returns `ScoutOutcome.Proceed` / `ScoutOutcome.Advance`. Wired into `ObservationLoopAsync` between `CenterOnTargetAsync` and `StartGuidingLoopAsync`. Transparency classification falls through to the existing `WaitForConditionRecoveryAsync`.
- Supporting config: **DONE** — `ScoutExposure`, `ObstructionStarCountRatioHealthy/Severe`, `ObstructionNudgeRadii`, `ObstructionClearFractionOfRemaining`, `SaveScoutFrames` added to `SessionConfiguration` with XML docs and sensible defaults.
- Tests: **DONE** — `SessionScoutClassifierTests` (6 unit tests for `ClassifyAgainstBaseline` + `ComputeWidestHalfFovDeg`) + `SessionScoutAndProbeTests` (5 functional tests covering first-observation, healthy, transparency-no-recovery, rising-target clear time, setting-target null clear time). All 1678 unit + 83 functional Session tests pass.

Not shipped: scout frames are not yet emitted via a `ScoutCompletedEventArgs` for the live-session UI (plan flagged as optional v1). `SaveScoutFrames` config key exists but no FITS write path yet (always discards, matching the false default).

## polar-alignment — DONE ~85%

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
- **Live site pressure/temperature.** DONE (`feat/top-5-todo`, 2026-06-12) via `SiteConditions.Resolve(IWeatherDriver?)` -> the 1010/10 hardcode is gone; live weather feeds refraction, else pressure derives from elevation. Unblocks polar-alignment refraction. (Values deliberately not profile-stored -- they vary.)

## catalog-binary-format — PARTIAL ~85%

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

## gpu-stretch-tests — DONE ~90%

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

## icc — DONE ~95%

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

## stacking — DONE ~85%

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

See `stacking.md` § "Phase 8 implementation status (2026-05-16)" for the
cold-start guide to the codebase: every file that holds the strategy machinery,
the test entry points, and the benchmark numbers worth remembering.

## altaz-mount-support — Phase 0 DONE (Phases 1-3 NOT STARTED)

SkyWatcher mounts like the AZ-GTi are dual-mode (equatorial on a wedge / alt-azimuth flat).
The driver is equatorial-only; this plan covers making alt-az *safe* (shipped) then *usable*
(deferred). See [altaz-mount-support.md](altaz-mount-support.md) for the full gap analysis.

- **Phase 0 (safe — make an alt-az mount un-mis-pointable): DONE** (2026-06-22, PRs #47 + #48).
  - `?alignment=GermanPolar|Polar|AltAz` device setting (`SkywatcherDevice` / `DeviceQueryKey.Alignment`),
    default `GermanPolar`. AZ vs EQ is **not protocol-detectable** (same model code, `:q CanAzEq`
    only means "supports both"), so it's a user setting mirroring GSServer — never auto-detected.
  - `AltAz` is **report-only**: `GetAlignmentAsync` returns it and pier reads return `Unknown`, but
    `BeginSlewRaDecAsync` / `SyncRaDecAsync` / `SetTrackingAsync(true)` are **refused** via
    `SkywatcherMountDriverBase.EnsureEquatorialAlignment` (throws `NotSupportedException`).
    `MaybeSyncToPoleAfterSiteSetAsync` is skipped for alt-az (home is the raw encoder zero — az 0
    / alt 0, not the equatorial pole).
  - Meridian flip is now **GEM-only**: `ImagingLoopAsync` reads `AlignmentMode.GermanPolar` and
    fork/AltAz skip the whole flip cycle (PR #47). Pinned by the `Polar`/`AltAz` `[Theory]` in
    `SessionObservationLoopTests` (`MeridianFlipCount == 0`).
  - Tests: `FakeSkywatcherMountDriverTests` (`GivenAltAzAlignmentThenReportedButCoordinateOpsRefused`,
    `GivenNoAlignmentQueryThenDefaultsToGermanPolar`) + the GEM-only flip `[Theory]`.
- **Phase 1 (correct GOTO + tracking + position — visual/EAA/plate-solve, no imaging): NOT STARTED.**
  Az/Alt↔encoder-step transforms, `BeginSlewToTargetAsync` pier-gate bypass for alt-az (an
  `IMountDriver`-layer change that also unblocks ASCOM/Alpaca alt-az mounts), dual-axis predictor
  tracking, and Az/Alt→RA/Dec position reads. Tracked in TODO.md.
- **Phase 2 (alt-az guiding): NOT STARTED**, gated on Phase 1 (field rotates continuously, so the
  fixed-angle calibration assumption breaks; likely short unguided subs or a different guide model).
- **Phase 3 (long-exposure alt-az imaging): BLOCKED** on field-rotation handling — needs a mechanical
  derotator (`IRotatorDriver` / `DeviceType.Rotator`, which doesn't exist — see the `rotator` plan +
  TODO.md) or software derotation in the stacker.

**Recommendation (from the plan):** Phase 0 makes the alt-az case safe rather than silently wrong;
most AZ-GTi *imagers* run EQ-on-a-wedge anyway (already works as `GermanPolar`). Pursue Phase 1 only
on demand; defer Phase 3 until rotator support lands.
