# TODO -- Infrastructure, Quality & Testing

**The open items are GitHub issues** labelled [`area:infra`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Ainfra) since 2026-09-24, when this file was migrated. What is left here is the DONE archive, kept for the measurements and reasons it records. Never add an open `- [ ]` here: open an issue.

## Flaky Tests

- [x] `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget`: fixed: cooperative time pump, `[Collection("Session")]` serialization, removed wall-clock timeouts

## CI

- [x] **REVERT the n2n model out of plain git and back into LFS.** Done 2026-09-06, on exactly the occasion
  it was written for: the replacement checkpoint landed (`tianwen_denoise_osc_e2wide_s2.onnx`) and the LFS
  budget is back, so the `.gitattributes` exemption is gone and `*.onnx` is an LFS object again.
  **The revert is three coordinated edits, not one.** The exemption block, `git rm --cached` + `git add`
  so the filter takes the file, and **`*.onnx` back into `lfs-payload.list` in `dotnet.yml`**: the publish
  matrix deliberately never touches LFS, so a new object left out of that artifact reaches every release
  asset as a ~130-byte pointer stub in `models/`, which `ModelResolver` refuses at runtime while the build
  stays green. The `Verify LFS objects materialised` step in `publish-apps` covered `*.onnx` throughout,
  which is what made the flip safe to perform rather than merely plausible.
  **Why it was there** (2026-08-19 to 2026-09-06): the repo's LFS budget was exhausted (2026-08-17,
  enforcement rather than fresh usage -- see `f1764364`), so CI could not fetch a single new object and
  every run died in `Fetch required LFS objects`. That file was the ONLY object missing from the runners'
  cache, verified by reproducing the workflow's own cache key across main: the cached set hashes
  `db37df0e2057d6cf` (32 files, at `237d7515`) and the wanted set `718e3fd94981c9ba` (33 files, from
  `69218529`), and it was the file that differed. Storing it as a blob restored CI completely, because
  every remaining LFS object rode the cache. The 3.1 MB stays in history either way (removal does not
  reclaim it without a rewrite); it is 0.3% of a 976 MB `.git`, which is why that was accepted rather
  than worked around.
  **Do not** treat that exemption as a template if the budget ever runs dry again: the catalogs (`*.lz`)
  and snapshots (`*.bin.gz`) are far larger, and unlike this file they are already cached, so they would
  need no exception.

## Code Quality / Architecture

- [x] **Async transport layer: `ConnectSerialDevice` is async at heart now.** Done: `DeviceBase.ConnectSerialDeviceAsync` returns `ValueTask<ISerialConnection?>`; `IExternal.OpenSerialDeviceAsync` wraps the synchronous BCL `SerialPort.Open` in `Task.Run` so no driver thread blocks; `TcpSerialConnection.CreateAsync` awaits `TcpClient.ConnectAsync` cooperatively with a cancellable 3 s timeout; every override (`MeadeDevice` via base, `OnStepDevice`, `SkywatcherDevice`, `FakeDevice`, `IOptronDevice`) and every caller (`MeadeLX200ProtocolMountDriverBase`, `SgpMountDriverBase`, `SkywatcherMountDriverBase`, `QHYFocuserDriver`, `QHYSerialControlledFilterWheelDriver`, 5 device-source scanners) updated in one commit.
- [x] **Migrate remaining `appState.StatusMessage = …` sites to `appState.AppendNotification(when, sev, msg)`.** Swept `AppSignalHandler.cs` (site-recompute, Goto validation, discovery results, assign/connect/disconnect/force-disconnect result+failure, cooler setpoint, warm-and-disconnect, warm-and-cooler-off, cooler off, session start validation + finalizer phase + cancel/fail, preview/snapshot/plate-solve/jog result+failure) and `Program.cs` (site warning, warming-cameras prompt, shutdown initial-state). Kept pure transient progress hints as plain assignments: `Recomputing…`, `Discovering devices…`, `Building schedule…`, `Initialising session…`, `Plate solving…`, Sun-slew confirmation prompt, shutdown pending-count ticker, ESC-to-quit prompt.

- [x] **`lock` standing-rule sweep** (rule in CLAUDE.md → Concurrency, 2026-07-03; finished 2026-09-24, #353): every `lock` needs a justification comment at the lock site, must not be reachable from a rendering thread, and must use `System.Threading.Lock`, never `lock` on an `object`, a collection, or a StringBuilder. **No `lock` in `src/` targets anything but a `System.Threading.Lock` now**, and every `Lock` carries its justification. Already compliant: `FakeCameraDriver`, the fake serial devices, `LiveCameraFrameStream`, the 5× `TianWen.AI.Imaging/Onnx/*` `_gate` fields (ONNX session single-flight) + `N2nDenoiser._gate`, `HostedSession._targetLock`, `StreamingFrameStaging.StreamingFrameReader._gate`, `FileCredentialStore._gate`, `SyntheticStarFieldRenderer._noiseTilesLock` (converted 2026-09-18, #304), `AscomHostJob`/`AscomHostProcess._gate`, `StartupTrace._anchorGate`. The last object locks went LOCK-FREE rather than changing type: `SerialProbeService` (per-probe results are an `ImmutableArray` replaced through `ConcurrentDictionary.AddOrUpdate`, so `ResultsFor` no longer copies and the pass-drop check no longer locks each list; the pinned-port verify set is a `ConcurrentDictionary`), `RcAstroCli` (stderr into a `ConcurrentQueue<string>` joined after exit, as `ExternalProcessPlateSolverBase` does), and the test/probe collectors `CometRepositoryTests`, `ObjectPictureCacheTests`, `SharpenPipelineDebayerTests` and eight `TianWen.UI.Web.E2E` probes (`ConcurrentQueue`). `OnStepQuirkProbeTests._rxLock` became a `Lock` (the drain records a count and clears as one step). `Image.Histogram`'s `lockObj` was already gone (one result slot per row strip, then a serial argmin, replaced it). Justification comments added to `FileLoggerProvider`, `FakeCameraDriver`, the three fake serial devices and `N2nDenoiser`. One clause is still unmet, tracked below.
- [x] **Signal handler cleanup: route, don't implement.** (Completed 2026-07-03 across two passes; `AppSignalHandler.cs` 2,991 → 2,519 lines.) The original audit below listed six handlers; a follow-up sweep found the audit itself was incomplete; the two biggest handlers (`StartPolarAlignmentSignal`, `SkyMapSolveSyncSignal`) and the whole TextInput-callback block were never enumerated. All now resolved.
  - Part 1 (the originally-audited six):
    - [x] `StartSessionSignal`: extracted to `SessionBootstrapper.BuildAndStartAsync` (container-free: caller resolves `ISessionFactory`); the lambda keeps the three preconditions + one call. Biggest single win (255 → 30 lines).
    - [x] `TakePreviewSignal`: capture sequence had already been extracted (`LiveSessionActions.CaptureCameraPreviewAsync` + `CameraExposureActions.StampDenormAsync`); the remaining inline device-resolution block is now `EquipmentActions.ResolveOtaCaptureDevices`.
    - [x] `ConnectDeviceSignal`: resolve loop extracted to `EquipmentActions.ResolveDeviceForConnect(hub, discoveredDevices, uri)`. The mount site-reconcile follow-up routes through the existing `ReconcileSiteOnMountConnectAsync` and only reflects the outcome into profile/planner state, which is routing.
    - [x] `AssignDeviceSignal`: target-switch extracted to `EquipmentActions.ApplyAssignment`; orphan handling to `EquipmentActions.AutoDisconnectOrphanAsync` (returns `OrphanDisconnectOutcome` + safety; the lambda maps outcomes to notifications).
    - [x] `SetCoolerSetpointSignal` / `SetCoolerOffSignal`: `EquipmentActions.SetCoolerSetpointAsync` / `SetCoolerOffAsync` (immediate counterparts to the ramped `WarmAndCoolerOffAsync`).
    - [x] `UpdateProfileSignal` + `AssignDeviceSignal` weather refresh; reviewed, no change: each site is one conditional + one call to the existing `FetchWeatherForecastAsync` (that IS routing); a shared wrapper would add indirection without removing logic.
    - [x] `SaveSnapshotSignal` / `JogFocuserSignal`: already routed via `LiveSessionActions.SaveSnapshotAsync` / `JogFocuserAsync` (fixed in an earlier pass; audit entries were stale).
  - Part 2 (the handlers the original audit missed):
    - [x] `StartPolarAlignmentSignal` (~270 lines): the capture-source building (guider + main-camera branches, device resolution, frame-published callbacks) → `PolarAlignmentActions.BuildCaptureSource` (returns source + activeGuider + error); the site/weather build → `PolarAlignmentActions.BuildSite`. Lambda keeps preconditions + `tracker.Run(RunAsync)`.
    - [x] `SkyMapSolveSyncSignal`: the inline per-OTA device-resolution block → `EquipmentActions.ResolveOtaCaptureDevices` (the rest already routes to `MountActions.SolveAndSyncAsync`).
    - [x] `PlateSolvePreviewSignal`: search-origin derivation + solve + result-to-message mapping → `LiveSessionActions.SolvePreviewFrameAsync`.
    - [x] `SkyMapSlewToObjectSignal`, largely already routing (two `MountActions` calls); the two-click Sun-slew confirmation state machine → testable `GuiAppState.GateSunSlew`.
    - [x] TextInput commit callbacks: `saveSite` parse/validate → `EquipmentActions.TryParseSite`, mount push → `EquipmentActions.PushSiteToMountIfProfileWinsAsync`; `StringSettingInput.OnCommit` masked-secret/URI decision → `EquipmentActions.CommitDeviceSetting`. (`ProfileName`/`GuiderFL`/`saveOta` left as-is: already thin, single helper call or single-field set + save.)
  - Pinned by `RouteOnlyExtractionTests` (`TryParseSite`, `CommitDeviceSetting`, `GateSunSlew`).
- [x] **Signal-handler boilerplate reduction** -- DONE (Phases 1-3 + 5, branch `refactor/signal-handler-boilerplate`): `Notify` / guard helpers (`EnsureSessionIdle` / `TryGetConnected<T>` / `TryResolveIdleOtaFocuser`) / `RunTracked` (over the upstreamed `DIR.Lib.BackgroundTaskTracker.RunGuarded`) swept across the handlers and extended to the ones added since the draft (Flats/manual-cover/comets); the ctor's subscription groups then split verbatim into per-concern `Subscribe*` partials (`.Planner/.SkyMap/.Equipment/.LiveSession/.Polar/.Flats.cs`, call order = registration order). Bespoke error sites left untouched; Tier 4 `Wire<T>` dropped by its own kill criterion. Core file ~2687 -> ~860 lines. [docs/plans/signal-handler-boilerplate.md](../plans/signal-handler-boilerplate.md).

## External / Infrastructure

- [x] **Hosting API returned a bodiless 500 on any unpopulated `double` (NaN is not valid JSON).**
      FIXED 2026-07-26. Found by the mandatory AOT publish smoke test during the
      `TianWen.Hosting.Contracts` split (pre-existing, unrelated to it): `GET
      /v2/api/equipment/camera/info` with no camera connected threw `ArgumentException: .NET number
      values ... cannot be written as valid JSON` out of `Utf8JsonWriter.WriteNumberValue(Double)`, and
      because serialization runs while the response is already streaming, Kestrel emits a **bodiless
      500 for the whole endpoint**. The audit found it was **not** nina-only: native v1's
      `OtaCameraStateDto.FocuserTemperature` is NaN by default whenever no focuser is fitted, so
      `/api/v1/session/state` -- the endpoint the whole remote-mirror path depends on -- would have
      500'd on an ordinary single-OTA session. Also unguarded: HFD/FWHM before the first measured
      frame, guide RMS before the first folded sample, a synthesized target's coordinates, and
      `NinaMountInfoDto` RA/Dec before the first poll.
      Fix: **policy-driven**, not a hardcoded coercion. `JsonNumber` in `TianWen.Hosting.Contracts`
      exposes `WireAllowsNonFinite`, **derived from `HostingJsonContext.Default.Options.NumberHandling`**
      so it cannot drift from the contract it describes, and `ForWire(value, fallback = 0)` substitutes
      only while the contract is strict. Applied at every wire boundary that copies a domain double
      (replacing the private `MountStateDto.NanToZero`); `Disconnected` sentinels use `JsonNumber.Unknown`
      so a pre-built DTO obeys the same policy instead of hardcoding the coercion where it cannot be
      re-decided. Flip the contract to `AllowNamedFloatingPointLiterals` and all ~30 call sites preserve
      NaN with no edit -- verified by doing exactly that and observing the payload serialize with NaN
      intact. The policy stays OFF because named literals emit the non-standard `"NaN"` token real nina
      clients do not parse; 0 matches what N.I.N.A. itself reports for an unavailable reading.
      Pinned by `HostingWireNumberTests`: the policy value itself (a deliberate tripwire on flipping it),
      that both contexts agree on `NumberHandling`, and all-NaN sources through the real projections and
      real contexts -- asserted policy-aware, so a legitimate flip does not fail for the wrong reason.
      Verified to fail when a guard is removed, and re-checked against the published AOT binary: all five
      nina `*/info` endpoints plus 12 other GETs answer 200.

## Testing

- [x] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`) DONE (2026-06-02): `SolveFileAsync` falls back to `Image.TryReadFitsFile(...)` WCS when no `CatalogPlateSolver` is injected (`FakePlateSolver.cs:50-54`).

### External AI tools in the test suite (from the 2026-08-17 handover)

Method note worth keeping, since it cost real time: **for process-launch forensics on Windows without
admin, a vendor's app-data write time is a better tripwire than process polling**, because it catches
a launch that happened while nobody was watching. A 200 ms `Win32_Process` poll that captures
`ParentProcessId` is the right tool only when you can watch live, and Prefetch needs admin.

### Two gotchas recorded from the same handover

- **A `dev/null/` directory at the repo root is git-lfs hooks written under
  `core.hooksPath=/dev/null`.** Go's `filepath.IsAbs` says false for that path on Windows, so it
  lands relative to the worktree. The real hooks in `.git/hooks` were intact and the artifact was
  deleted; if it reappears, some tool is invoking git with that config.
- **`FormattableString.Invariant` rejects concatenated interpolated strings.** `$"a" + $"b"` is a
  `string`, not a `FormattableString`. Hit twice in one day; write one long interpolation instead.

## Statistics

- [x] Find a faster way to multiply all values in an array/span (`StatisticsHelper.cs:167`)
      Replaced manual `Vector<T>` loops in `StatisticsHelper`, `VectorMath`, `Image`, and DSP
      classes with `System.Numerics.Tensors` (`TensorPrimitives`). SIMD-accelerated one-liners.
- [x] Run star detection and use the mask to exclude stars from background estimation.
      `ScanBackgroundRegion` accepts optional `BitMatrix? starMask`, re-scanned with
      48×48 squares after detection. Star mask reused from `StarList.StarMask`.

## Build / dev environment (local siblings)

- [x] **NuGet graph-restore source-key alignment: standardized on `nuget.org`** (DONE 2026-07-04,
      re-diagnosed + fixed properly). With all sibling repos cloned, `UseLocalSiblings`
      project-references them, so a restore builds a graph spanning `../DIR.Lib`, `../Codecs`,
      `../FITS.Lib`, `../SER.Lib`, … and MSBuild merges *their* `nuget.config`s into one settings
      object. `packageSourceMapping` matches by source **key**, so a key mismatch across the merged
      configs makes the winning mapping point at a source that didn't survive the merge → NU1100
      "PackageSourceMapping is enabled … not considered" for FC.SDK / FC.SDK.Raw / ZWOptical.SDK /
      TianWen.DAL / SharpAstro.LALR.CC. **The correct key is `nuget.org`**; proven to be the
      NuGet fresh-install default (an empty user config auto-writes `<add key="nuget.org"
      value="https://api.nuget.org/v3/index.json" protocolVersion="3" />`). The earlier note here had
      the premise **inverted** (it claimed the user-wide key was `nuget.org` and briefly flipped
      `src/NuGet.config` to `api.nuget.org`); in fact the user-wide config on the arm64 box had drifted
      to a non-standard `api.nuget.org` key **and** a `packageSourceMapping` routing `*` there, that
      mapping was the real root cause. Fix: renamed the `api.nuget.org` **key** → `nuget.org` (URL
      unchanged) in the user-wide config **and** `FITS.Lib` / `SER.Lib` / `zwo-sdk-nuget` configs, and
      kept `src/NuGet.config` on `nuget.org`. `TianWen.DAL` was already on `nuget.org`. After that,
      `dotnet build TianWen.Lib` restores clean. CI is unaffected (fresh runners = `nuget.org` default).
      `RestoreConfigFile` in `Directory.Build.props` does **not** help; it only applies to tianwen
      projects, not the sibling projects in the graph.
- [x] **`TianWen.DAL/NuGet.config`**, now maps `*`→`nuget.org` (fixed in the sibling repo). Was an
      empty `<packageSourceMapping><clear/></packageSourceMapping>` that mapped nothing. NB: TianWen.DAL
      is consumed by tianwen as a **package**, not a project ref, so its config was never actually in
      tianwen's restore graph; the graph-poisoning configs were `FITS.Lib` / `SER.Lib` (project refs)
      plus the user-wide config, all now on `nuget.org`.

- [x] **Harden `Planetary/PlanetaryCaptureControllerTests` off the wall clock** (2026-08-07). Was six
      spin loops of up to 5000 iterations, each doing a real `await Task.Delay(2)`, plus one of 600,
      while the capture loop rendered synthetic frames flat out on a background task. That budget
      measured the wrong thing twice: it burned ~10 s of wall clock in the good case, and under a
      loaded suite `Task.Delay(2)` stretched toward 10 ms so one test spent 50-75 s and then failed
      its own iteration bound. Fixed with a frame-arrival seam --
      `PlanetaryCaptureController.WaitForNextFrameAsync` (internal, one `TaskCompletionSource`
      completed per fully-processed frame) and a `PumpAsync` helper that ticks the render thread in
      lock-step with the producer on a FRAME budget. `FakeTimeProviderWrapper.SleepAsync` advances
      fake time synchronously, so planet drift / stack depth / the ROI chase are all deterministic
      functions of the frame count -- the wall clock was never the right axis. Class went 9 s -> 4 s
      run alone, and no longer degrades under load. The terminal signal is deliberately **sticky**
      (completed in place, never re-armed) so a stopped or faulted producer surfaces as a failed
      predicate instead of a `[Fact]` timeout; pinned by
      `Pumping_past_the_end_of_a_capture_bounds_out_instead_of_hanging`, verified to hang without it.

## `stack` has no `--masters-dir`, so a fresh `-o` rebuilds every calibration master

`StackingPipeline` puts the cache at `Path.Combine(outputDir, "masters")` with no override. Iterating
with a different `-o` per run therefore rebuilds bias/dark/flat masters from scratch every time --
about 7 GB of reads on the 10P set, which pinned the disk at 100% on a 16 GB box and cost minutes per
run. Four output dirs meant four identical copies of the same masters, ~800 MB each.

`MasterCache`'s own doc describes a **shared** directory "not per-run", and that is what the dataset
builder gets; `stack` does not. A `--masters-dir` flag (defaulting to the current behaviour) would let
comparison runs share one cache, which is exactly the workflow that provoked this: the same lights
stacked star-aligned and comet-aligned into separate directories.
