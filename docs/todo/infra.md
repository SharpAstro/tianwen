# TODO -- Infrastructure, Quality & Testing

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Flaky Tests

- [ ] `PlanetaryCaptureControllerTests.Auto_recenter_off_leaves_the_roi_window_fixed` — hit its 60s test timeout in 2 of 3 full-suite runs on 2026-07-03 (win-arm64 dev box under load); all 7 tests in the class pass in isolation in 10s. Suspected thread-pool starvation under `maxParallelThreads: 4` (the capture loop runs on `Task.Run` like the Session tests did before they were serialized). If it recurs, give the planetary capture tests their own `[Collection]` the way `Session*Tests` share `[Collection("Session")]`.
- [x] `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget` — fixed: cooperative time pump, `[Collection("Session")]` serialization, removed wall-clock timeouts

## Code Quality / Architecture

- [x] **Async transport layer — `ConnectSerialDevice` is async at heart now.** Done: `DeviceBase.ConnectSerialDeviceAsync` returns `ValueTask<ISerialConnection?>`; `IExternal.OpenSerialDeviceAsync` wraps the synchronous BCL `SerialPort.Open` in `Task.Run` so no driver thread blocks; `TcpSerialConnection.CreateAsync` awaits `TcpClient.ConnectAsync` cooperatively with a cancellable 3 s timeout; every override (`MeadeDevice` via base, `OnStepDevice`, `SkywatcherDevice`, `FakeDevice`, `IOptronDevice`) and every caller (`MeadeLX200ProtocolMountDriverBase`, `SgpMountDriverBase`, `SkywatcherMountDriverBase`, `QHYFocuserDriver`, `QHYSerialControlledFilterWheelDriver`, 5 device-source scanners) updated in one commit.
- [x] **Migrate remaining `appState.StatusMessage = …` sites to `appState.AppendNotification(when, sev, msg)`.** Swept `AppSignalHandler.cs` (site-recompute, Goto validation, discovery results, assign/connect/disconnect/force-disconnect result+failure, cooler setpoint, warm-and-disconnect, warm-and-cooler-off, cooler off, session start validation + finalizer phase + cancel/fail, preview/snapshot/plate-solve/jog result+failure) and `Program.cs` (site warning, warming-cameras prompt, shutdown initial-state). Kept pure transient progress hints as plain assignments: `Recomputing…`, `Discovering devices…`, `Building schedule…`, `Initialising session…`, `Plate solving…`, Sun-slew confirmation prompt, shutdown pending-count ticker, ESC-to-quit prompt.

- [ ] **`lock` standing-rule sweep** (rule in CLAUDE.md → Concurrency, 2026-07-03): every `lock` needs a justification comment at the lock site, must not be reachable from a rendering thread, and must use `System.Threading.Lock` — never `lock` on an `object`, a collection, or a StringBuilder. Compliant already: `FileLoggerProvider`, `FakeCameraDriver`, the fake serial devices, `LiveCameraFrameStream`. Remaining `object`-based sites (inventory 2026-07-03, none render-thread-reachable today): 5× `TianWen.AI.Imaging/Onnx/*` `_gate` (ONNX session single-flight), `HostedSession._targetLock`, `StreamingFrameStaging._gate`, `FileCredentialStore._gate`, `SyntheticStarFieldRenderer._noiseTilesLock`, `SerialProbeService` (locks on the `list`/`existing` collection instances + a local `verifyLock`), `RcAstroCli` (`lock (stderr)` on a StringBuilder), `Image.Histogram` parallel-reduction `lockObj`, tests `OnStepQuirkProbeTests._rxLock`. Convert mechanically + add the justification comment per site (or replace with a lock-free pattern where one fits — see `CircularBuffer<T>`).
- [x] **Signal handler cleanup — route, don't implement.** (Completed 2026-07-03 across two passes; `AppSignalHandler.cs` 2,991 → 2,519 lines.) The original audit below listed six handlers; a follow-up sweep found the audit itself was incomplete — the two biggest handlers (`StartPolarAlignmentSignal`, `SkyMapSolveSyncSignal`) and the whole TextInput-callback block were never enumerated. All now resolved.
  - Part 1 (the originally-audited six):
    - [x] `StartSessionSignal` — extracted to `SessionBootstrapper.BuildAndStartAsync` (container-free: caller resolves `ISessionFactory`); the lambda keeps the three preconditions + one call. Biggest single win (255 → 30 lines).
    - [x] `TakePreviewSignal` — capture sequence had already been extracted (`LiveSessionActions.CaptureCameraPreviewAsync` + `CameraExposureActions.StampDenormAsync`); the remaining inline device-resolution block is now `EquipmentActions.ResolveOtaCaptureDevices`.
    - [x] `ConnectDeviceSignal` — resolve loop extracted to `EquipmentActions.ResolveDeviceForConnect(hub, discoveredDevices, uri)`. The mount site-reconcile follow-up routes through the existing `ReconcileSiteOnMountConnectAsync` and only reflects the outcome into profile/planner state, which is routing.
    - [x] `AssignDeviceSignal` — target-switch extracted to `EquipmentActions.ApplyAssignment`; orphan handling to `EquipmentActions.AutoDisconnectOrphanAsync` (returns `OrphanDisconnectOutcome` + safety; the lambda maps outcomes to notifications).
    - [x] `SetCoolerSetpointSignal` / `SetCoolerOffSignal` — `EquipmentActions.SetCoolerSetpointAsync` / `SetCoolerOffAsync` (immediate counterparts to the ramped `WarmAndCoolerOffAsync`).
    - [x] `UpdateProfileSignal` + `AssignDeviceSignal` weather refresh — reviewed, no change: each site is one conditional + one call to the existing `FetchWeatherForecastAsync` (that IS routing); a shared wrapper would add indirection without removing logic.
    - [x] `SaveSnapshotSignal` / `JogFocuserSignal` — already routed via `LiveSessionActions.SaveSnapshotAsync` / `JogFocuserAsync` (fixed in an earlier pass; audit entries were stale).
  - Part 2 (the handlers the original audit missed):
    - [x] `StartPolarAlignmentSignal` (~270 lines) — the capture-source building (guider + main-camera branches, device resolution, frame-published callbacks) → `PolarAlignmentActions.BuildCaptureSource` (returns source + activeGuider + error); the site/weather build → `PolarAlignmentActions.BuildSite`. Lambda keeps preconditions + `tracker.Run(RunAsync)`.
    - [x] `SkyMapSolveSyncSignal` — the inline per-OTA device-resolution block → `EquipmentActions.ResolveOtaCaptureDevices` (the rest already routes to `MountActions.SolveAndSyncAsync`).
    - [x] `PlateSolvePreviewSignal` — search-origin derivation + solve + result-to-message mapping → `LiveSessionActions.SolvePreviewFrameAsync`.
    - [x] `SkyMapSlewToObjectSignal` — largely already routing (two `MountActions` calls); the two-click Sun-slew confirmation state machine → testable `GuiAppState.GateSunSlew`.
    - [x] TextInput commit callbacks — `saveSite` parse/validate → `EquipmentActions.TryParseSite`, mount push → `EquipmentActions.PushSiteToMountIfProfileWinsAsync`; `StringSettingInput.OnCommit` masked-secret/URI decision → `EquipmentActions.CommitDeviceSetting`. (`ProfileName`/`GuiderFL`/`saveOta` left as-is: already thin — single helper call or single-field set + save.)
  - Pinned by `RouteOnlyExtractionTests` (`TryParseSite`, `CommitDeviceSetting`, `GateSunSlew`).
- [ ] **OnStep follow-ups** (leftover from the OnStep commit series):
  - [ ] MoveAxis via `:Mn/:Ms/:Me/:Mw#` + `:Qe/Qw/Qn/Qs#` + `:RA/:RE` rates — enables direct jog buttons in GUI
  - [ ] Per-axis guide-rate setter via `:Rn#` (index 0–9) + `:GX90#` query — enables `CanSetGuideRates = true` on the OnStep override
  - [ ] Test `EquipmentActions.ReconcileAllProfilesAsync` with a fake `IExternal` that captures `AtomicWriteJsonAsync` — orchestration layer currently untested; unit tests only cover `ReconcileProfileData`
  - [ ] mDNS bind fallback — if port 5353 is owned by Bonjour/Avahi, bind to an ephemeral UDP port and accept unicast responses (currently silently returns empty results). Common on macOS
  - [ ] "Add unseen device" button in equipment tab — today WiFi OnStep mounts that don't advertise mDNS require hand-editing the profile JSON. Add a modal with host + port fields that constructs an `OnStepDevice` and injects it into discovery cache
  - [ ] Parse SRV records in `ParseMdnsResponse` to pick up non-default TCP ports. Currently assume 9999; some firmware advertises a different port via SRV
- [ ] Split `IDeviceSource<T>` discovery role from per-device driver role. Several drivers fuse both into one class and rely on a placeholder/"default root device" ctor so DI can construct the singleton:
  - `OpenPHD2GuiderDriver` — singleton ctor synthesizes a `MakeDefaultRootDevice(external.DefaultGuiderAddress)` just to satisfy `_guiderDevice`; only `_equipmentProfiles` is meaningful in the discovery role
  - `QHYDeviceSource` / `ZWODeviceSource` / `AscomDeviceIterator` etc. — review for the same smell
  - Proper fix: separate `OpenPHD2DeviceSource : IDeviceSource<OpenPHD2GuiderDevice>` (no device field) from `OpenPHD2GuiderDriver : IGuider` (constructed only via `OpenPHD2GuiderDevice.NewInstanceFromDevice`). Mirror pattern across other dual-role classes
- [ ] Replace `IReadOnlyList<T>` in parameters with `ReadOnlySpan<T>`, return types with `ImmutableArray<T>` — gradual migration for better perf semantics and thread safety
- [ ] Abstract redraw flag propagation in TUI main loop — register `INeedsRedraw` state objects instead of listing `plannerState.NeedsRedraw || sessionState.NeedsRedraw || ...` manually
- [ ] Live Session tab: `RollingGraphWidget<TSurface>` extracted to DIR.Lib (reusable for guide graph, cooling graph, future charts)

## External / Infrastructure

- [ ] Free unmanaged resources and override finalizer in `External.Dispose` (`External.cs:85-91`)
- [ ] Actually ensure that FITS library writes async (`IExternal.cs:226`)
- [ ] Write an MCP server for TianWen (expose session status, device state, observation schedule). PARTIAL (verified 2026-06-02): `TianWen.AI.MCP` (`tianwen-mcp`) ships `FitsTools` (Header/Stats/FindStars/PlateSolve/Pixels), `CatalogTools` (Lookup), `LogTools` (Tail). Session-status / device-state / observation-schedule tools still TODO (planned `stack.*`/`profile.*`/`devices.*`/`app.*` categories are doc-only in `Program.cs`).

## Testing

- [ ] `ObjectType.IsStar()` helper method
- [ ] VDB has objects listed as `Be*`, but in HIP we only know stars (`*`) (`CelestialObjectDBTests.cs:73`)
- [x] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`) DONE (2026-06-02): `SolveFileAsync` falls back to `Image.TryReadFitsFile(...)` WCS when no `CatalogPlateSolver` is injected (`FakePlateSolver.cs:50-54`).
- [ ] See if fake mounts (`FakeMountDriver` and `FakeMeadeLX200ProtocolMountDriver`) can share a mount-specific base class
- [ ] GPU offscreen comp-test followups not yet done (per the GPU comp-test survey): **A** Bayer demosaic comp, **C** WCS grid overlay comp. (D `VkRenderer` primitives, F sky-map line tessellation, B histogram already shipped as `VkRendererPrimitiveTests` / `SkyMapLineTessellationTests` / `VkHistogramPipelineTests`.)

## Statistics

- [x] Find a faster way to multiply all values in an array/span (`StatisticsHelper.cs:167`)
      Replaced manual `Vector<T>` loops in `StatisticsHelper`, `VectorMath`, `Image`, and DSP
      classes with `System.Numerics.Tensors` (`TensorPrimitives`) — SIMD-accelerated one-liners.
- [x] Run star detection and use the mask to exclude stars from background estimation.
      `ScanBackgroundRegion` accepts optional `BitMatrix? starMask`, re-scanned with
      48×48 squares after detection. Star mask reused from `StarList.StarMask`.

## Build / dev environment (local siblings)

- [x] **NuGet graph-restore source-key alignment — standardized on `nuget.org`** (DONE 2026-07-04,
      re-diagnosed + fixed properly). With all sibling repos cloned, `UseLocalSiblings`
      project-references them, so a restore builds a graph spanning `../DIR.Lib`, `../Codecs`,
      `../FITS.Lib`, `../SER.Lib`, … and MSBuild merges *their* `nuget.config`s into one settings
      object. `packageSourceMapping` matches by source **key**, so a key mismatch across the merged
      configs makes the winning mapping point at a source that didn't survive the merge → NU1100
      "PackageSourceMapping is enabled … not considered" for FC.SDK / FC.SDK.Raw / ZWOptical.SDK /
      TianWen.DAL / SharpAstro.LALR.CC. **The correct key is `nuget.org`** — proven to be the
      NuGet fresh-install default (an empty user config auto-writes `<add key="nuget.org"
      value="https://api.nuget.org/v3/index.json" protocolVersion="3" />`). The earlier note here had
      the premise **inverted** (it claimed the user-wide key was `nuget.org` and briefly flipped
      `src/NuGet.config` to `api.nuget.org`); in fact the user-wide config on the arm64 box had drifted
      to a non-standard `api.nuget.org` key **and** a `packageSourceMapping` routing `*` there — that
      mapping was the real root cause. Fix: renamed the `api.nuget.org` **key** → `nuget.org` (URL
      unchanged) in the user-wide config **and** `FITS.Lib` / `SER.Lib` / `zwo-sdk-nuget` configs, and
      kept `src/NuGet.config` on `nuget.org`. `TianWen.DAL` was already on `nuget.org`. After that,
      `dotnet build TianWen.Lib` restores clean. CI is unaffected (fresh runners = `nuget.org` default).
      `RestoreConfigFile` in `Directory.Build.props` does **not** help — it only applies to tianwen
      projects, not the sibling projects in the graph.
- [x] **`TianWen.DAL/NuGet.config`** — now maps `*`→`nuget.org` (fixed in the sibling repo). Was an
      empty `<packageSourceMapping><clear/></packageSourceMapping>` that mapped nothing. NB: TianWen.DAL
      is consumed by tianwen as a **package**, not a project ref, so its config was never actually in
      tianwen's restore graph — the graph-poisoning configs were `FITS.Lib` / `SER.Lib` (project refs)
      plus the user-wide config, all now on `nuget.org`.
- [ ] **Keep `open-vs.ps1`'s "Siblings" folder in sync with `Directory.Build.props`'
      `UseLocalSiblings` set** (currently: DIR.Lib, Console.Lib, SdlVulkan.Renderer, Codecs
      family, QHYCCD.SDK, FITS.Lib, SER.Lib, Lzip.Lib, + transitive Fonts.Lib for
      `SharpAstro.Fonts.Tables.OpenTypeMath`). If a new sibling is added to the switch, add it here
      too or VS Go-To-Definition drops into the stale NuGet package instead of source.

## Upstream Extraction (to SharpAstro NuGet packages)

- [ ] Move `FileDialogHelper` to DIR.Lib — cross-platform native file picker (comdlg32/zenity/osascript), zero TianWen dependencies
- [ ] Move `Stat/` DSP suite to DIR.Lib — 12 files: FFT, DFT, 25+ window functions, Catmull-Rom splines, StatisticsHelper, AggregationMethod; all pure math with no astro imports (note: DFT/FFT missing namespace declarations)
- [ ] Port debayer algos out for FC.SDK.Raw to consume — `Image.Debayer.cs` / `DebayerAlgorithm.cs` / `DebayerAlgorithmExtensions.cs` are pure Bayer-mosaic operations and don't depend on TianWen-specific types beyond `Image`/`Channel`. FC.SDK.Raw currently stops at the raw `ushort[]` mosaic on `CanonRawFile.BayerMosaic` (by design — astronomical stacking only needs the mosaic), but downstream consumers that want a sensible default JPEG render have to roll their own demosaic. Extract to DIR.Lib (or a new `SharpAstro.Imaging`/`SharpAstro.Debayer` package) so both TianWen and FC.SDK.Raw consume the same implementation; keep the 5×5 BilinearMono as the default and the simple 2×2 bilinear as a fallback. As of FC.SDK.Raw 1.4 the parallel ushort-based `CanonDemosaic.Bilinear`/`Ahd` already exist for consumer raw-render use cases — TianWen's float-based copies are intentional duplication for the stretch-aware astronomical path.

