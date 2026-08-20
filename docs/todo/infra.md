# TODO -- Infrastructure, Quality & Testing

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Flaky Tests

- [ ] **TWIC0001 (and its shader-bake twin) warn on a fresh clone.** The staleness check is an MSBuild
  `Inputs`/`Outputs` mtime comparison, and a git checkout writes `icons.recipe` and `BakedIcons.g.cs`
  in directory order -- measured 19 ms apart on this box, recipe last -- so it fires on a clean tree
  whose table is byte-correct (`pwsh tools/bake-icons.ps1 -Verify` answers "matches its recipe"). A
  warning that cries wolf after every clone trains people to ignore the log; CI already runs the
  authoritative `-Verify`. Either drop the mtime target in favour of that, or give it a tolerance.

- [ ] `PlanetaryCaptureControllerTests.Auto_recenter_off_leaves_the_roi_window_fixed`: hit its 60s test timeout in 2 of 3 full-suite runs on 2026-07-03 (win-arm64 dev box under load); all 7 tests in the class pass in isolation in 10s. Suspected thread-pool starvation under `maxParallelThreads: 4` (the capture loop runs on `Task.Run` like the Session tests did before they were serialized). **Recurred 2026-07-06** (1 of 4 full-suite runs, same signature: timeout before/at first frame, 7/7 green in isolation in 7s). The originally-prescribed own-`[Collection]` fix is **moot, the class already sits in `[Collection("Session")]`**; the remaining suspects are the wall-clock poll loops (`5000×`/`600×` iterations of `Tick(); await Task.Delay(2)`, under contention each 2 ms delay stretches to ~15-30 ms timer granularity, and a starved capture `Task.Run` never sets `FramesReceived`) racing the `[Fact(Timeout = 60_000)]`. Proper fix: condition-based waits (wait for `FramesReceived >= N` with the timeout as the only bound, drop the fixed iteration counts) and/or pumping the capture loop off `FakeTimeProvider` instead of real 2 ms sleeps.
- [x] `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget`: fixed: cooperative time pump, `[Collection("Session")]` serialization, removed wall-clock timeouts

## CI

- [ ] **Set `enableCrossOsArchive: true` on the LFS cache steps** (deferred 2026-08-20; do it once the
  LFS budget resets in 2026-09). `actions/cache` segregates Windows entries from POSIX ones unless this
  is set, so a Windows job can never restore the `lfs-build-` entry the ubuntu `build` job saves -- not
  by key, not by the `restore-keys` prefix -- and cannot bootstrap one of its own, because a cache is
  saved only when the job succeeds. Measured on release run #1311: all six `publish-apps` legs computed
  the same key `lfs-build-5dcb5df07512d63d`, both Linux legs AND both macOS legs hit it, and only the two
  Windows legs reported `Cache not found`. macOS restoring a Linux-saved entry is what rules out a
  generic per-OS scoping story -- the split is specifically Windows, which is what the flag exists for.
  **Why deferred:** the flag changes the computed cache *version*, so enabling it invalidates every
  existing `lfs-build-*` and `lfs-tests-*` entry. While the budget is dry a miss means a live
  `git lfs pull` that gets refused, so turning it on today would take the currently-green `build`,
  `test-unit` and `test-functional` jobs red -- including the ~197 MB test-fixture pull. After the reset
  the same change costs one cheap re-save.
  **Not blocking:** `publish-apps` no longer reads that cache at all (it takes its files from the
  `lfs-payload` artifact), so this is about the remaining cache users and any future Windows job.

- [ ] **REVERT the n2n model out of plain git and back into LFS** (added 2026-08-19, expected to revert 2026-09,
  when the replacement model lands). `src/TianWen.AI.Imaging/models/*.onnx` carries an `!filter !diff !merge`
  exception in `.gitattributes` so `tianwen_denoise_osc_v19d.onnx` is stored as an ordinary 3.1 MB blob.
  **Why:** the repo's LFS budget is exhausted (2026-08-17, enforcement rather than fresh usage -- see
  `f1764364`), so CI cannot fetch a single new object and every run dies in `Fetch required LFS objects`.
  This file was the ONLY object missing from the runners' cache, verified by reproducing the workflow's
  own cache key across main: the cached set hashes `db37df0e2057d6cf` (32 files, at `237d7515`) and the
  wanted set `718e3fd94981c9ba` (33 files, from `69218529`), and this is the file that differs. Storing it
  as a blob therefore restores CI completely, because every remaining LFS object rides the cache.
  **To revert:** delete the `.gitattributes` block, then `git rm --cached` + `git add` the file so the
  general `*.onnx` rule takes it back. Note the 3.1 MB stays in history either way (removal does not
  reclaim it without a rewrite); it is 0.3% of a 976 MB `.git`, which is why that was accepted rather
  than worked around.
  **Do not** treat this as a template for other LFS objects: the catalogs (`*.lz`) and snapshots
  (`*.bin.gz`) are far larger, and unlike this file they are already cached, so they need no exception.

## Code Quality / Architecture

- [x] **Async transport layer: `ConnectSerialDevice` is async at heart now.** Done: `DeviceBase.ConnectSerialDeviceAsync` returns `ValueTask<ISerialConnection?>`; `IExternal.OpenSerialDeviceAsync` wraps the synchronous BCL `SerialPort.Open` in `Task.Run` so no driver thread blocks; `TcpSerialConnection.CreateAsync` awaits `TcpClient.ConnectAsync` cooperatively with a cancellable 3 s timeout; every override (`MeadeDevice` via base, `OnStepDevice`, `SkywatcherDevice`, `FakeDevice`, `IOptronDevice`) and every caller (`MeadeLX200ProtocolMountDriverBase`, `SgpMountDriverBase`, `SkywatcherMountDriverBase`, `QHYFocuserDriver`, `QHYSerialControlledFilterWheelDriver`, 5 device-source scanners) updated in one commit.
- [x] **Migrate remaining `appState.StatusMessage = …` sites to `appState.AppendNotification(when, sev, msg)`.** Swept `AppSignalHandler.cs` (site-recompute, Goto validation, discovery results, assign/connect/disconnect/force-disconnect result+failure, cooler setpoint, warm-and-disconnect, warm-and-cooler-off, cooler off, session start validation + finalizer phase + cancel/fail, preview/snapshot/plate-solve/jog result+failure) and `Program.cs` (site warning, warming-cameras prompt, shutdown initial-state). Kept pure transient progress hints as plain assignments: `Recomputing…`, `Discovering devices…`, `Building schedule…`, `Initialising session…`, `Plate solving…`, Sun-slew confirmation prompt, shutdown pending-count ticker, ESC-to-quit prompt.

- [ ] **`lock` standing-rule sweep** (rule in CLAUDE.md → Concurrency, 2026-07-03): every `lock` needs a justification comment at the lock site, must not be reachable from a rendering thread, and must use `System.Threading.Lock`, never `lock` on an `object`, a collection, or a StringBuilder. Compliant already: `FileLoggerProvider`, `FakeCameraDriver`, the fake serial devices, `LiveCameraFrameStream`. Remaining `object`-based sites (inventory 2026-07-03, none render-thread-reachable today): 5× `TianWen.AI.Imaging/Onnx/*` `_gate` (ONNX session single-flight), `HostedSession._targetLock`, `StreamingFrameStaging._gate`, `FileCredentialStore._gate`, `SyntheticStarFieldRenderer._noiseTilesLock`, `SerialProbeService` (locks on the `list`/`existing` collection instances + a local `verifyLock`), `RcAstroCli` (`lock (stderr)` on a StringBuilder), `Image.Histogram` parallel-reduction `lockObj`, tests `OnStepQuirkProbeTests._rxLock`. Convert mechanically + add the justification comment per site (or replace with a lock-free pattern where one fits, see `CircularBuffer<T>`).
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
- [ ] **OnStep follow-ups** (leftover from the OnStep commit series):
  - [ ] MoveAxis via `:Mn/:Ms/:Me/:Mw#` + `:Qe/Qw/Qn/Qs#` + `:RA/:RE` rates; enables direct jog buttons in GUI
  - [ ] Per-axis guide-rate setter via `:Rn#` (index 0–9) + `:GX90#` query; enables `CanSetGuideRates = true` on the OnStep override
  - [ ] Test `EquipmentActions.ReconcileAllProfilesAsync` with a fake `IExternal` that captures `AtomicWriteJsonAsync`; orchestration layer currently untested; unit tests only cover `ReconcileProfileData`
  - [ ] mDNS bind fallback, if port 5353 is owned by Bonjour/Avahi, bind to an ephemeral UDP port and accept unicast responses (currently silently returns empty results). Common on macOS
  - [ ] "Add unseen device" button in equipment tab: today WiFi OnStep mounts that don't advertise mDNS require hand-editing the profile JSON. Add a modal with host + port fields that constructs an `OnStepDevice` and injects it into discovery cache
  - [ ] Parse SRV records in `ParseMdnsResponse` to pick up non-default TCP ports. Currently assume 9999; some firmware advertises a different port via SRV
- [ ] Split `IDeviceSource<T>` discovery role from per-device driver role. Several drivers fuse both into one class and rely on a placeholder/"default root device" ctor so DI can construct the singleton:
  - `OpenPHD2GuiderDriver`: singleton ctor synthesizes a `MakeDefaultRootDevice(external.DefaultGuiderAddress)` just to satisfy `_guiderDevice`; only `_equipmentProfiles` is meaningful in the discovery role
  - `QHYDeviceSource` / `ZWODeviceSource` / `AscomDeviceIterator` etc. review for the same smell
  - Proper fix: separate `OpenPHD2DeviceSource : IDeviceSource<OpenPHD2GuiderDevice>` (no device field) from `OpenPHD2GuiderDriver : IGuider` (constructed only via `OpenPHD2GuiderDevice.NewInstanceFromDevice`). Mirror pattern across other dual-role classes
- [ ] Replace `IReadOnlyList<T>` in parameters with `ReadOnlySpan<T>`, return types with `ImmutableArray<T>`; gradual migration for better perf semantics and thread safety
- [ ] Abstract redraw flag propagation in TUI main loop; register `INeedsRedraw` state objects instead of listing `plannerState.NeedsRedraw || sessionState.NeedsRedraw || ...` manually
- [ ] Live Session tab: `RollingGraphWidget<TSurface>` extracted to DIR.Lib (reusable for guide graph, cooling graph, future charts)

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
- [ ] Free unmanaged resources and override finalizer in `External.Dispose` (`External.cs:85-91`)
- [ ] Actually ensure that FITS library writes async (`IExternal.cs:226`)
- [ ] Write an MCP server for TianWen (expose session status, device state, observation schedule). PARTIAL (verified 2026-06-02): `TianWen.AI.MCP` (`tianwen-mcp`) ships `FitsTools` (Header/Stats/FindStars/PlateSolve/Pixels), `CatalogTools` (Lookup), `LogTools` (Tail). Session-status / device-state / observation-schedule tools still TODO (planned `stack.*`/`profile.*`/`devices.*`/`app.*` categories are doc-only in `Program.cs`).

## Testing

- [ ] `ObjectType.IsStar()` helper method
- [ ] VDB has objects listed as `Be*`, but in HIP we only know stars (`*`) (`CelestialObjectDBTests.cs:73`)
- [x] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`) DONE (2026-06-02): `SolveFileAsync` falls back to `Image.TryReadFitsFile(...)` WCS when no `CatalogPlateSolver` is injected (`FakePlateSolver.cs:50-54`).
- [ ] See if fake mounts (`FakeMountDriver` and `FakeMeadeLX200ProtocolMountDriver`) can share a mount-specific base class
- [ ] GPU offscreen comp-test followups not yet done (per the GPU comp-test survey): **A** Bayer demosaic comp, **C** WCS grid overlay comp. (D `VkRenderer` primitives, F sky-map line tessellation, B histogram already shipped as `VkRendererPrimitiveTests` / `SkyMapLineTessellationTests` / `VkHistogramPipelineTests`.)

### External AI tools in the test suite (from the 2026-08-17 handover)

- [ ] **Env-gate the RC-Astro integration tests?** Proposed as `TIANWEN_RCASTRO_TESTS`, following the
  device-simulator suite's pattern, so a routine `dotnet test` stops invoking the real `rc-astro`
  binary (each call spikes the GPU through DirectML). **Undecided by the user -- do not do this
  unprompted.**
- [ ] **Unexplained: something launches PixInsight during a test run.** Nothing reproducible does it.
  Measured: `rc-astro` spawns **zero** child processes (both the license probe and a real `nxt` run),
  the RC test classes leave the tripwire untouched, and a full suite under a 200 ms process watcher
  saw nothing at all. PixInsight genuinely ran once, at 21:35:24, *during* a suite but not provably
  *because* of one. **Tripwire to reuse:** the `LastWriteTime` of
  `%APPDATA%\Pleiades\core-001-pxi.settings`. Closing this needs a concrete sighting (what, and when)
  rather than more speculative instrumentation.

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

## Dataset builder: make a re-measure cheap

- [ ] **Retain the session masters so re-deriving PSF stats does not mean re-registering.**
  **Evidence: two full 7h16m re-runs in two days** (2026-08-10 the star-detection duplicate fix,
  2026-08-11 the FWHM estimator fix), both of which re-registered 50 sessions purely to recompute a
  handful of numbers per star. `DatasetPsfStore` checkpoints *measured values*, which is exactly right
  for surviving an interrupted run and useless for surviving a change to the estimator that produced
  them. The measurement itself takes seconds; the registration takes the 7 hours.

  Why it currently cannot be short-circuited: the field-radius profile (`Bins[].Fwhm`, the only part
  P2's synthetic-PSF sweep actually needs) is measured by detecting stars on the **session master**,
  and the master exists only as the output of register + integrate into `outDir/_scratch`, which is
  wiped per session on purpose so peak disk is bounded by the largest single session.

  **Classify the change before choosing a mechanism**, because it is easy to build something that
  helps less than it appears to:

  | Change | What re-measuring needs |
  |---|---|
  | Registration or integration itself | Full re-register. Unavoidable, and correct. |
  | **Detection** (which stars, centroid, aperture sizing) | The master's PIXELS (field-radius half) and the subs' pixels (per-sub half) |
  | **A quantity derived from one star's radial profile** (the FWHM change) | Only the stored profile |

  Two candidate mechanisms:

  - **(A) Keep the 50 session masters.** ~108 MB each (3008x3008x3 float32), so ~5.4 GB, or ~2.7 GB at
    fp16. Covers the detection *and* derived classes for the field-radius half, and is nearly free to
    implement: write the master to a retained per-session path instead of only into scratch. Disk is
    abundant on this box and the masters are already computed. **Preferred**, because it covers the
    strictly larger class of change.
  - **(B) Persist the per-star radial profile** (`profileFlux` / `profileWeight` from
    `Image.AnalyseStar`, ~8-16 floats per star; ~219k sampled stars implies roughly 5-15 MB). Covers
    only the derived class, but covers it for **both** halves, and is small enough to commit-adjacent.
    A cheap complement to (A), not a substitute.

  **The per-sub half does not fully benefit either way, and say so up front.** `SubFwhm[]` comes from
  the analysis pass over each of the 5,984 subs, so re-deriving it needs a calibrate + detect sweep of
  the subs. That is far cheaper than register + integrate but not free (order 40 min), and retaining
  masters does nothing for it. Mechanism (B) is what makes that half cheap.

  Precedent for the shape: `TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz` stands in 2.1 MiB of
  star positions for ~9 GB of FITS, because the property under test was geometric. Same idea, applied
  to the dataset builder's own statistics.

- [ ] **`--regen-psf` oversells what it does; either rename it or make it mean its name.**
  `DatasetBuildRunner.RunAsync` returns early for a session that already has a PSF record **before**
  consulting `RegenPsfForExportedSessions`, so the flag only fills in *missing* records and cannot
  force a re-measure. The 2026-08-11 FWHM re-run therefore needed the store rotated aside by hand to
  make all 50 records "missing" before the flag would do anything. The doc comment is accurate but the
  name is not, and it mispredicted its own behaviour within a day of being written, which is evidence
  about the name rather than about the reader. Options: rename to `--fill-missing-psf`, or add a force
  path and let missing-record be the subset. Note that rotating the store is *independently* worth
  doing (it preserves the prior distribution for a before/after comparison, which an append-only
  last-wins force would bury), so whichever way this goes, keep rotation as the documented gesture.

## Licensing / release hygiene

- [ ] **Ship third-party notices with the release binaries.** The four AOT release assets
  (`tianwen`, `tianwen-server`, `tianwen-gui`, `tianwen-fits`) **static-link everything**, so each
  `.tar.gz` is a binary redistribution of its dependencies. MIT and BSD both require the copyright
  notice be reproduced in such a redistribution, and the assets carry no notices at all today.
  Concretely at least: `FITS.Lib` / CSharpFITS, whose BSD-style terms say "Redistributions in binary
  form must reproduce the above copyright notice ... in the documentation" (Thomas McGlynn, Samuel
  Carliles, Virtual Observatory India), the seven MIT SharpAstro siblings, SDL3 (zlib) and DotNext.
  `Codecs` is UNLICENSE so it needs nothing, and QHYCCD.SDK already separates QHYCCD's proprietary
  natives from its MIT wrapper in its own `license.txt`.

  Two ways: generate `THIRD-PARTY-NOTICES.txt` at publish time from the restored dependency graph, or
  hand-maintain it and accept the drift. Prefer generated. Then add it to the release upload globs in
  `.github/workflows/dotnet.yml` beside the binaries, and reference it from `NOTICE`, which today
  credits methods and data but says nothing about linked code.

  Worth stating so it is not re-investigated: **every sibling repo IS properly licensed.** An earlier
  pass claimed FITS.Lib, Codecs and QHYCCD.SDK had no licence file, which was wrong; the check globbed
  only `LICENSE*` and `COPYING*` and missed `license.txt`, `UNLICENSE` and `license.txt`
  respectively. All three also declare `PackageLicenseFile` in their csproj.

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
- [ ] **Keep `open-vs.ps1`'s "Siblings" folder in sync with `Directory.Build.props`'
      `UseLocalSiblings` set** (currently: DIR.Lib, Console.Lib, SdlVulkan.Renderer, Codecs
      family, QHYCCD.SDK, FITS.Lib, SER.Lib, Lzip.Lib, + transitive Fonts.Lib for
      `SharpAstro.Fonts.Tables.OpenTypeMath`). If a new sibling is added to the switch, add it here
      too or VS Go-To-Definition drops into the stale NuGet package instead of source.

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

## Upstream Extraction (to SharpAstro NuGet packages)

- [ ] Move `FileDialogHelper` to DIR.Lib: cross-platform native file picker (comdlg32/zenity/osascript), zero TianWen dependencies
- [ ] Move `Stat/` DSP suite to DIR.Lib: 12 files: FFT, DFT, 25+ window functions, Catmull-Rom splines, StatisticsHelper, AggregationMethod; all pure math with no astro imports (note: DFT/FFT missing namespace declarations)
- [ ] Port debayer algos out for FC.SDK.Raw to consume; `Image.Debayer.cs` / `DebayerAlgorithm.cs` / `DebayerAlgorithmExtensions.cs` are pure Bayer-mosaic operations and don't depend on TianWen-specific types beyond `Image`/`Channel`. FC.SDK.Raw currently stops at the raw `ushort[]` mosaic on `CanonRawFile.BayerMosaic` (by design, astronomical stacking only needs the mosaic), but downstream consumers that want a sensible default JPEG render have to roll their own demosaic. Extract to DIR.Lib (or a new `SharpAstro.Imaging`/`SharpAstro.Debayer` package) so both TianWen and FC.SDK.Raw consume the same implementation; keep the 5×5 BilinearMono as the default and the simple 2×2 bilinear as a fallback. As of FC.SDK.Raw 1.4 the parallel ushort-based `CanonDemosaic.Bilinear`/`Ahd` already exist for consumer raw-render use cases; TianWen's float-based copies are intentional duplication for the stretch-aware astronomical path.

