# TODO -- Infrastructure, Quality & Testing

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Flaky Tests

- [x] `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget` — fixed: cooperative time pump, `[Collection("Session")]` serialization, removed wall-clock timeouts

## Code Quality / Architecture

- [x] **Async transport layer — `ConnectSerialDevice` is async at heart now.** Done: `DeviceBase.ConnectSerialDeviceAsync` returns `ValueTask<ISerialConnection?>`; `IExternal.OpenSerialDeviceAsync` wraps the synchronous BCL `SerialPort.Open` in `Task.Run` so no driver thread blocks; `TcpSerialConnection.CreateAsync` awaits `TcpClient.ConnectAsync` cooperatively with a cancellable 3 s timeout; every override (`MeadeDevice` via base, `OnStepDevice`, `SkywatcherDevice`, `FakeDevice`, `IOptronDevice`) and every caller (`MeadeLX200ProtocolMountDriverBase`, `SgpMountDriverBase`, `SkywatcherMountDriverBase`, `QHYFocuserDriver`, `QHYSerialControlledFilterWheelDriver`, 5 device-source scanners) updated in one commit.
- [x] **Migrate remaining `appState.StatusMessage = …` sites to `appState.AppendNotification(when, sev, msg)`.** Swept `AppSignalHandler.cs` (site-recompute, Goto validation, discovery results, assign/connect/disconnect/force-disconnect result+failure, cooler setpoint, warm-and-disconnect, warm-and-cooler-off, cooler off, session start validation + finalizer phase + cancel/fail, preview/snapshot/plate-solve/jog result+failure) and `Program.cs` (site warning, warming-cameras prompt, shutdown initial-state). Kept pure transient progress hints as plain assignments: `Recomputing…`, `Discovering devices…`, `Building schedule…`, `Initialising session…`, `Plate solving…`, Sun-slew confirmation prompt, shutdown pending-count ticker, ESC-to-quit prompt.

- [ ] **Signal handler cleanup — route, don't implement.** Audit of `AppSignalHandler.cs` against the CLAUDE.md rule found these violations:
  - [ ] `StartSessionSignal` (~line 1230) — **violates** — inlines transform construction, schedule→observations copy loop, `config with { ... }` site+setpoint injection, factory init+create. Extract `SessionBootstrapper.BuildAndStartAsync(plannerState, sessionState, liveSessionState, profile, factory, tracker, ct)` or `LiveSessionActions`
  - [ ] `TakePreviewSignal` (~line 1385) — **violates** — full camera-capture sequence (gain, binning, start exposure, `while` loop polling `GetImageReadyAsync`). Extract `EquipmentActions.CapturePreviewAsync(camera, sig, timeProvider, ct)`
  - [ ] `ConnectDeviceSignal` (~line 911) — `foreach` over `eqState.DiscoveredDevices` with `DeviceBase.SameDevice` match. Extract `EquipmentActions.ResolveDeviceForConnect(hub, discoveredDevices, uri)`
  - [ ] `AssignDeviceSignal` (~line 830) — auto-disconnect of orphaned previous device inline (`GetDisconnectSafetyAsync` + branch + `hub.DisconnectAsync` + status). Extract `EquipmentActions.AutoDisconnectOrphanAsync(hub, prevSlotUri, expectedType, logger, ct)`
  - [ ] `SetCoolerSetpointSignal` (~line 1080) / `SetCoolerOffSignal` (~line 1123) — two-step temp+cooler sequences. Extract `EquipmentActions.SetCoolerSetpointAsync` / `SetCoolerOffAsync`
  - [ ] `UpdateProfileSignal` (~line 1143) + `AssignDeviceSignal` — conditional `FetchWeatherForecastAsync` duplicated. Extract shared `RefreshWeatherIfNeededAsync(prevWeatherUri, newWeatherUri, ct)`
  - [ ] `SaveSnapshotSignal` (~line 1466) — file-naming policy inline (`"Snapshot"` subfolder, date-stamped folder, `GetSafeFileName`). Extract `SnapshotPersistence.SaveAsync(image, otaIndex, external, timeProvider, ct)`
  - [ ] `JogFocuserSignal` (~line 1541) — read-pos + compute-target + `BeginMoveAsync`. Extract `EquipmentActions.JogFocuserAsync(focuser, steps, ct)`
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

## Upstream Extraction (to SharpAstro NuGet packages)

- [ ] Move `FileDialogHelper` to DIR.Lib — cross-platform native file picker (comdlg32/zenity/osascript), zero TianWen dependencies
- [ ] Move `Stat/` DSP suite to DIR.Lib — 12 files: FFT, DFT, 25+ window functions, Catmull-Rom splines, StatisticsHelper, AggregationMethod; all pure math with no astro imports (note: DFT/FFT missing namespace declarations)
- [ ] Port debayer algos out for FC.SDK.Raw to consume — `Image.Debayer.cs` / `DebayerAlgorithm.cs` / `DebayerAlgorithmExtensions.cs` are pure Bayer-mosaic operations and don't depend on TianWen-specific types beyond `Image`/`Channel`. FC.SDK.Raw currently stops at the raw `ushort[]` mosaic on `CanonRawFile.BayerMosaic` (by design — astronomical stacking only needs the mosaic), but downstream consumers that want a sensible default JPEG render have to roll their own demosaic. Extract to DIR.Lib (or a new `SharpAstro.Imaging`/`SharpAstro.Debayer` package) so both TianWen and FC.SDK.Raw consume the same implementation; keep the 5×5 BilinearMono as the default and the simple 2×2 bilinear as a fallback. As of FC.SDK.Raw 1.4 the parallel ushort-based `CanonDemosaic.Bilinear`/`Ahd` already exist for consumer raw-render use cases — TianWen's float-based copies are intentional duplication for the stretch-aware astronomical path.

