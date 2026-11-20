# CLAUDE.md

Guidance for Claude Code in this repository. **This file holds the rules that bite; the reasons, measurements and
incidents live in the doc each section names.** Every section here has its former text, in full, in that doc under
"Moved from CLAUDE.md, 2026-11-15" unless the doc already held it. Open the doc before changing what a rule names. A comment or doc that cites a `CLAUDE.md` section by a name not found here
means a former section: `docs/architecture/claude-md-sections.md` says where each one's text is now.

- Use extended thinking when analyzing bugs, designing architecture or refactoring.
- `python`, not `python3`; `pwsh`, not `powershell`.
- Line endings are LF everywhere (`.gitattributes` `* text=auto eol=lf`, `.editorconfig`). Never hand-convert a file; a file that shows as
  modified with an empty diff is a stale stat entry.
- **Exit codes 127 and 13x from GUI / CLI / Server processes mean the .NET process crashed.** Read the stderr log
  (`gui-stderr.log`) for the exception before concluding anything.

## Project Tracking

Full rules: `docs/architecture/project-tracking.md`.

- **The backlog is GitHub issues**, never a file. Labels `area:*`, `bench`, `needs-triage`, `from-todo`; priority is the
  organisation's Priority issue field, never a label. Never write an open `- [ ]` into `TODO.md` or `docs/todo/`.
- **Close an item through the PR that does it** (`Closes #n`).
- **Issue and plan link both ways, to the section:** the issue links a heading anchor on `main`, the plan names the issue
  at that section. A plan with open work has a milestone of the same name; a PR carries the milestone of the plan it
  advances. `tools/plan-issue-report.py --strict` (the `plan-report` skill and workflow) checks it.
- **A doc never cites a commit by hash** (PRs squash-merge). Cite the commit subject in quotes, a tag, or a PR or issue.

| File | Purpose |
|------|---------|
| `docs/plans/summary.md` | Status of every plan |
| `docs/plans/*.md` | Per-feature plans: the design and the why |
| `docs/architecture/*.md` | One deep-dive per subject (`ls docs/architecture/` is the index) |
| `TODO.md`, `docs/todo/*.md` | The DONE archive only |
| `docs/known-limitations.md` | Root causes; read before "fixing" a suspected bug |
| `CHANGELOG.md` | One section per `MAJOR.MINOR`, added in the same commit as a version bump |

Skills are in `.claude/skills/<name>/SKILL.md` and listed by the session. The `chrome-review` agent runs in CI on every
non-draft PR that touches a `.cs` or `.razor` file of the five UI projects (`.github/workflows/chrome-review.yml`; a
push that leaves the UI and chrome-test diff unchanged is not reviewed again) and fails the check on a
high-confidence finding; run `/chrome-review` before marking a PR ready to see it first.

## Project Overview

TianWen is a .NET 10 library for astronomical device management, image processing and astrometry: cameras, mounts,
focusers, filter wheels, covers, power boxes and guiders over ASCOM, Alpaca, vendor SDKs and native serial protocols.
Published as `TianWen.Lib` on NuGet plus AOT binaries. Repository: https://github.com/SharpAstro/tianwen

```
src/
├── TianWen.slnx, Directory.Build.props; Directory.Packages.props is at the REPO ROOT
├── TianWen.Lib/ (core)   TianWen.Devices.Native/ (ZWO + QHYCCD drivers)   TianWen.Lib.SourceGenerators/
├── TianWen.Lib.Tests/ (unit)   .Tests.Functional/ (Session loops, fake time)   .Tests.Gpu/ (real GPU, own process)
├── TianWen.Lib.Tests.Simulators/ (live Alpaca/ASCOM simulators; gated, skip by default)
├── TianWen.Cli/ (`tianwen`)   TianWen.Server/ (`tianwen-server`)   TianWen.AscomHost/ (`tianwen-ascomhost`)
├── TianWen.Hosting.Contracts/ (wire DTOs)   TianWen.Hosting/ (REST + WebSocket + Alpaca)   TianWen.RemoteClient/
├── TianWen.UI.Abstractions/ (widgets, layout, state)   TianWen.UI.Shared/ (Vulkan pipelines)
├── TianWen.UI.Gui/ (`tianwen-gui`)   TianWen.UI.FitsViewer/ (`tianwen-fits`)
├── TianWen.UI.Web/ (WebAssembly)   .Web.E2E/ (Playwright)   TianWen.UI.Benchmarks/
└── TianWen.AI/ (ORT facade)   TianWen.AI.Imaging/   TianWen.AI.MCP/ (`tianwen-mcp`)
```

- Seven projects set `PublishAot` (the six named above and `TianWen.Shell.Thumbnails`, the Explorer thumbnail handler);
  **only `tianwen`, `tianwen-server`, `tianwen-gui`, `tianwen-fits` are release assets**
  (`.github/workflows/dotnet.yml`; a new one needs its upload steps there).
- **A program another one starts ships beside it** (`src/ExeBeside.targets`, `<ExeBesideThis>`); never find a sibling's
  build output by walking up to the solution.

## Build & Test

```bash
# from src/
dotnet build
dotnet test
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
```

**Tests run on Microsoft.Testing.Platform, not VSTest** (opt-in and the SDK floor in `global.json`). `--filter` is
unchanged; `--output Detailed`, `--report-trx --report-trx-filename x.trx`, `--crashdump`, `--hangdump --hangdump-timeout
5min` and `--minimum-expected-tests N` replace the VSTest `--logger` and `--blame-*` options, which now fail the run. The
dump and TRX options exist on `TianWen.Lib.Tests` and `.Functional` only. Details: `docs/architecture/testing.md`.

Packages are versioned centrally in `Directory.Packages.props`, never in a `.csproj`.

## Sibling Libraries and Releases

Full rules: `docs/architecture/sibling-builds-and-releases.md`; the org-wide versioning rule is in the imported org
`CLAUDE.md` (a release is one edit of `<VersionMajorMinor>`; a version literal in a csproj or workflow is a regression).

- In-house libraries (`DIR.Lib`, `SdlVulkan.Renderer`, `Console.Lib`, `FITS.Lib`, `FC.SDK`, `ZWOptical.SDK`, `QHYCCD.SDK`,
  `Fonts.Lib`, `SER.Lib`, `Lzip.Lib`, `Serial.Lib`, `LAN.Lib`, `Node.Lib`, `WebGl.Renderer`, `AppShell`, `TianWen.DAL`,
  `Codecs`) are sibling clones at `../<repo>`.
- **One property, `UseLocalSiblings`, switches all of them to ProjectReference, and only when EVERY sibling folder exists
  under its repo's current name.** `dotnet msbuild src/TianWen.Lib/TianWen.Lib.csproj -getProperty:UseLocalSiblings`;
  override with `-p:UseLocalSiblings=false`. CI always uses packages.
- A sibling gated on it must be in its `Exists(...)` list and in `open-vs.ps1`'s project list.
- No CPM opt-outs in `src/`. Never a local nupkg feed or `dotnet pack` to skip a release: commit, push, wait for NuGet.
- `TianWen.UI.Web` is in `TianWen.slnx`; only `.E2E` stays out (run it explicitly).
- A new CI job that builds or publishes needs both halves of the `VERSION_PREFIX` hand-off and `DOTNET_NOLOGO: 1`.

## Testing Conventions

Full rules: `docs/architecture/testing.md`, `docs/architecture/session-test-harness.md`.

- xUnit v3, Shouldly, NSubstitute. Test data is embedded under `Data/`. **No reflection in tests**: add an `internal`
  member.
- **A temporary folder comes from `TempFolders`**, never a bare `Directory.CreateTempSubdirectory`. Kept output and
  `TempFolders.Root` stay under the ORIGINAL temp (`TestRunTemp.Root`), never `GetTempPath()` after the redirect.
  `TIANWEN_KEEP_TEST_OUTPUT=1` keeps a dated output folder.
- **A test that drives a real GPU goes in `TianWen.Lib.Tests.Gpu`**, `[Collection(GpuCollection.Name)]`, its fixture from
  `OffscreenGpuFixtureBase`.
- **A thread stress test runs off CI** (`StressTestGate.SkipOnCi`) on threads of its own (`LongRunning`).
- **A wall-clock latency bar is opt-in** (`TIANWEN_TIMING_TESTS=1`, `TimingTestGate`, `TimingCollection`) and never widened.
- **A test of 30 s or more on CI is `[Trait("Category", "Heavy")]`**, tagged by measurement, never by guess.
- **A shared fixture plus an assertion about a FIRST write is an order dependency**: establish the precondition in the test.
- **Any test that drives a `Session` is `[Collection("Session")]`** (it calls `SessionTestHelper.CreateSessionAsync`).
- **No wall-clock timeout inside a test**: `[Fact(Timeout = ...)]` plus `TestContext.Current.CancellationToken`. A
  `Timeout` on a synchronous test does nothing. Never set `parallelAlgorithm: aggressive`.
- A fake-clock `SleepAsync` throws on a cancelled token; a guider's `StopCaptureAsync` returns only once its loop exited.
- Every test project carries `xunit.runner.json` AND copies it to the output. Diagnose a slow suite from one TRX, not reruns.
- **Drive a session loop with `FakeTimeProviderWrapper.PumpUntilCompletedAsync` and its progress probe**; never choose a
  step, never `SleepAsync(subExposure)` in a pump loop.
- `TianWen.Lib.Tests.Simulators` is opt-in (`SimulatorGate`; `TIANWEN_ALPACA_SIM`, `TIANWEN_ASCOM_CI`) and skips, never
  fails, with no simulator: `docs/plans/device-simulator-ci.md`.

### Driving the GUI and the TUI unattended

Every mechanism: `docs/architecture/unattended-ui-driving.md`.

- **An agent starts an app local-only**: through `tools/start-app.ps1`, or with `TIANWEN_LOCAL_ONLY=1`.
- **A Debug app is found by the inspector sidecar's `list_instances`**, never a port read off a log. Release has no inspector.
- `ProfileData.SiteLatitude/Longitude` must match the mount URI's. Anchor the clock with `TIANWEN_NOW` to a real night
  at that site. `StartSession` needs at least one pinned target.
- **Ground truth for telemetry is the NODE's log** (`%LOCALAPPDATA%/TianWen/Logs/<date>/Server_*.log`), not the GUI mirror.
- Use `render_liveness`, not a screenshot, to decide whether the render thread is stuck. `validation_report` with zero
  messages is evidence only when `active` is true.
- A terminal reads back as text (`screen` / `row` / `cell`); the modifier parameter is `mods`.
- A `tianwen-fits` behaviour test goes through `ViewerE2E` at DPI 1 and 1.5; a new host step goes into
  `StandaloneViewerHost`, never `Program.cs`.

## Coding Style

`src/.editorconfig` enforces it. 4 spaces, LF. File-scoped namespaces in new files; match the file you edit; never churn
one form to the other. Primary constructors for DI. Target-typed `new(...)` where the type is apparent. Expression bodies
for properties and one-line methods. `I` prefix, PascalCase, `_camelCase` private fields.

## Architecture

General coding rules in full: `docs/architecture/coding-rules.md`.

### Clock and logging

- `ILogger` and `ITimeProvider` come from `IServiceProvider`. A ctor with a non-generic `ILogger` silently gets `null`
  from DI: take `ILogger<TSelf>`.
- **`ITimeProvider.SleepAsync`, never `Task.Delay`.** **A deadline is `ITimeProvider.CancelAfter(delay)`**, never
  `new CancellationTokenSource(delay)` (`NoWallClockDeadlineTests`).
- **`TIANWEN_NOW`** (ISO-8601, ideally with an offset) anchors the whole process clock to a simulated instant that then
  runs at real rate (`StartupTimeOverride`, wired once in `AddExternal`).

### Devices

Full rules: `docs/architecture/device-architecture.md`, `docs/architecture/device-ownership.md`, and one protocol doc per native serial
driver (a new native driver ships its doc in its first commit).

- Devices are URI-addressed (`DeviceBase`, `IDeviceSource<T>`, `IDeviceDiscovery`, `IDeviceHub`); query keys in
  `DeviceQueryKey`.
- **One driver per device; a driver's connect is all or nothing.** A connected device that does not answer is a transient
  THROW.
- **Every vendor SDK call through `_deviceInfo` goes inside `using var sdk = EnterSdk();`.**
- **Vendor native binaries live in `TianWen.Devices.Native`; only `tianwen-server` references it.**
- **A profile scan never probes a COM port**; a port that will not take bytes is given up (`HasAbandonedIo`); a Bluetooth
  port that cannot be an instrument is not probed (`SerialProbeExclusion`).
- **Every read on an `ISerialConnection` ends at its `ReplyTimeout`**; raise it for a reply that waits on motion, never
  make it infinite. Serial I/O is Serial.Lib's; closing is asynchronous (`TryCloseAsync`, `CloseInBackground`).
- **A run claims its devices from the hub** (`TryAcquireLease`, `DeviceLeaseSet`, `DeviceOwnershipGate`); every new kind
  of run owes the same. Read `Driver` only after `ConnectAsync(hub)`. Reads are never leased.
- **Never guard hardware access on a UI flag**: ask `DeviceOwnershipGate` and show its refusal. `DisconnectAsync(force:
  true)` is for process shutdown only.
- **Stopping the rig is one order, `RigShutdown`; quitting is one rule, `AppQuit`.** Never queue a camera warm-up beside
  a run's cancel.
- **Alpaca** (`AddAlpaca()`) is the cross-platform device source. Camera frames use binary `application/imagebytes`,
  whose wire order is column-major in image terms (`AlpacaImageBytes.DecodeChannel` transposes).
- **Secrets are never on a device URI or in the profile JSON**: `ICredentialStore`, keyed `{deviceId}/{settingKey}`. A
  masked `DeviceSettingDescriptor` routes to the store through `DeviceSettingHelper.Commit`. The file store is used on
  any OS under `TIANWEN_DATA_ROOT`.

### Plate Solving

Full rules: `docs/plans/plate-solver-performance.md`.

- **A `WCS` is in detected-centroid coordinates, 0-based, everywhere in memory.** Never subtract 1 from `SkyToPixel`,
  never write CRPIX by hand (`WriteToHeader` / `FromHeader` are the only conversions). On screen, a frame coordinate lands at
  `origin + (x + 0.5) * zoom` through `WcsAnnotationLayer.ImageToScreen` / `ScreenToImage` and nothing else.
- **A header hint is `OBJCTRA`/`OBJCTDEC` before `RA`/`DEC`** (CRVAL first); `WCS.FromHeader` and
  `Image.Fits.ParseTargetCoords` must agree.
- A remembered parity (`SolveHintCache`) is a hypothesis budget, never a skip. The quad seed answers where the frame is,
  never a solution; the scale ladder (`ScaleLadder`, for a frame that states no scale) answers a scale, never a
  solution. `MinSampledFwhmPx` only un-does a bin.
- **An external tool never outlives the call that started it** (`BoundedProcess.WaitForExitOrKillAsync`).

### Sky objects, planner, atlas

- **Comets, meteor showers: never in `ICelestialObjectDB`.** Each consumer augments at its own layer
  (`ICometRepository`; `docs/plans/comet-ephemeris.md`, `docs/plans/yearly-planner.md` section E).
  - A failed Horizons fetch is remembered (`ApparitionRetryCooldown`). JPL sends no CORS headers: the browser bakes both
    hosts. **A request timeout is not a cancellation**: filter on `ct.IsCancellationRequested`.
  - A shower's peak is the Sun reaching its solar longitude (`MeteorShowerCalendar.PeakNear`); its identity is the
    `CatalogIndex`; a night is judged at `MeteorShowerNights.NightInstant`; the atlas draws and hit-tests through
    `SkyMapState.ShouldDrawShowerRadiant`.
- **Planner pins** (`docs/architecture/planner-pin-identity.md`): **a proposal always produces exactly one row, and a
  solar-system body's identity is its `CatalogIndex`, never the `Target` value.** Match through
  `PlannerActions.IsSameObject`; `GetFilteredTargets` never drops a proposal.
- **A Wikipedia link is the article the bake verified** (`ICelestialObjectDB.TryGetArticle`); an unverified object gets
  no link. A link opens only where the host wired `InputRouter.OpenUrl`. `docs/plans/object-imagery.md`.
- **Atlas hover and click come from one resolver** (`SkyMapSearchActions.TryResolveHit`); only a changed answer asks for
  a frame (`HoverFrameRequests`); a cell is walked through `IRaDecIndex.EnumerateCell`; layer gates go through
  `OverlayEngine.PassesLayerFilter`. `docs/plans/in-app-sky-atlas.md`.
- **Smart framing** (`FramingGrouper`, `FramingPlanner`; `docs/plans/smart-framing.md`): sensor specs live in the profile
  JSON, never the camera URI. **A change to the SIMBAD merge logic bumps `SimbadMergeSnapshot.AlgorithmVersion` and
  re-runs `tools/precompute-simbad-merge.ps1`.** `OpenNgcCorrections` is the one place a wrong row is fixed. No external
  `lzip` binary (`tools/lzip-util.ps1`).
- **Night calendar** (`docs/plans/night-calendar.md`): one multi-day forecast serves the planner band and the calendar;
  every verdict goes through `NightVerdict.For`; every Open-Meteo hourly array is nullable per element; a night is its
  evening date; one widget on every host.

### Session

Full rules: `docs/architecture/session-invariants.md`, `docs/architecture/driver-resilience.md`,
`docs/plans/mount-safety-limits.md`, `docs/plans/gss-parity-audit.md`.

`Session.RunAsync`: initialise, wait for twilight, cool, rough focus, autofocus, calibrate guider, observation loop.

- **Single mount, many OTAs**: branch or re-order logic treats the OTA set as one unit.
- A failed run carries `ISession.FailureReason` (`SessionFailedException`). Init connects are fail-fast.
- A mount that cannot goto is pointed by `PointWithoutGotoAsync` only.
- **A goto or Home never unparks a mount** (`MountGoto.IsParkedAsync`); a run may, behind
  `SessionConfiguration.UnparkMount`, at its first move. Going home is `DeviceOperations.GoHomeAsync`.
- **Guider calibration slews to HA -0.5h, EAST of the meridian.**
- The loop waits for `ScheduledObservation.Start - ScheduledStartLeadTime` on the MOUNT clock.
- **Never re-introduce an HA-only meridian flip check** (`MeridianFlipDecision.DecideFlipAction`). Where the pointing
  state is `Computed`, whether a flip happened is read off the image (`MeridianFlipVerification.FromSolves`).
- **No astro-dark is a fallback, never a throw** (`ObservationScheduler.CalculateNightWindow`).
- **Focus-drift refocus is a trend** (`FocusDriftDetector.EstimateTrendHfd`): the LSQ divisor is the included-sample
  count, the window clears on a drift refocus and a target change, one baseline per `AcquisitionSetting`.
- **A light judged cloud as it lands is a suspicion, never a reject** (`MAYBE_CLOUD_`, `LiveCloudJudge`,
  `Session.RecordLatestLightCloud`).
- **Mount limits are not the meridian flip** (`MountLimits.Evaluate`): horizon keys on hour angle; only a measured or
  session-verified pointing state may drive the meridian test; `alreadyActed` latches; config is on
  `ProfileData.MountLimits`; a limit routes to `ImageLoopNextAction.LimitReached`; a mount that stops tracking unasked is
  a limit event. `default(PointingState)` is `Normal`: place a test mount by sync. `MountLimitWatcher` enforces with no
  session running.
- **A guide pulse is two methods**: `StartPulseGuideAsync` starts and returns; `PulseGuideAsync` starts and waits. A
  test of the non-blocking starter needs `ExternalTimePump` and a `[Fact(Timeout=...)]`.
- **Every hot-path driver call goes through `Session.ResilientInvokeAsync`** with a preset (`IdempotentRead`,
  `NonIdempotentAction`, `AbsoluteMove`). A lost exposure throws nothing: `DALCameraDriver` gives it up itself.
- **A command that fails backward needs verified retry** (`SendCommandVerifiedAsync`); do not widen it to every command.
- Canon: every announced object goes through one queue; mirror lockup is per exposure.
- Backlash is inferred from each autofocus (`BacklashEstimator`). Polar alignment is one routine, `PolarAlignmentRun`,
  ended by cancellation (`docs/plans/polar-alignment.md`).
- **Flats** (`docs/plans/flat-frame-automation.md`): `FlatSource` has two values; flat matching is by FITS headers, never
  the path; the GUI surface is a mode on the Live Session tab; with no prompt subscriber the session answers `Decline`.

### Deep-Sky Stacking (`TianWen.Lib.Imaging.Stacking`)

Full rules: `docs/architecture/stacking-render-pipeline.md`; comets: `docs/plans/comet-integration.md`. Separate from
the planetary stacker.

- **Output:** linear is FITS (full frame and `_autocrop`); display outputs are always the autocrop, rendered by
  `MasterPostProcessor`. A written master is [0, 1] with true labels (`ScaleFloatValuesToUnitCeiling` into new planes).
- **Never take a whole-frame statistic over a CFA mosaic.** A NaN in a rejection column disables rejection
  (`PixelRejection.MarkAbsent`).
- **A bad-pixel mask judges a photosite against its same-colour neighbours** (`BadPixelDetection`); no known defect is a
  star (`FrameRegistration.DetectAsync(knownBad:)`). The flat recipe is in `InputSetFingerprint`.
- Never re-ingest our own outputs. A light's `CALSTAT` is a read guard.
- **Colour planes are placed on green per frame through `Image.ChannelOffsets`**, never measured elsewhere.
- Calibration is grouped by temperature run; a flat run is its folder. The archive scan is one function,
  `SessionDiscovery.ScanAsync`.
- A captured non-light says so in `IMAGETYP`; never widen a consumer's filter to admit one.
- **Render model:** one SPCC white balance, then each plate self-stretches. An enhance solves it on the linear master
  first and applies a broadband fit into the pixels (`ColourCalibration.Applied`, `WBAPPLD`). SPCC is broadband-only as a
  model, not a gate. The filter-curve matcher never answers with a brand.

### Planetary (`TianWen.Lib.Imaging.Planetary`)

Full rules, measurements and what was measured and NOT adopted: `docs/plans/planetary-restoration.md` ("Rules in full"),
`docs/plans/planetary-stacking.md`, `docs/plans/live-planetary-capture.md`. **Read them before changing a default.**

- One finalize (`PlanetaryMaster`), one best stack (`PlanetaryBestStack`), one AUTO (`PlanetaryAuto`), one sharpening
  (`PlanetarySharpening`), one capture loop (`PlanetaryCapture`), for every host.
- Live capture normalises ADU to [0,1] at the stream boundary and takes its layout from the actual frame; no driver call
  on the render thread.
- **The corpus is read, never written** (`SerReader.CropTo`; writes go through `ScratchSpace`).
- Orientation comes from `PhysicalEphemeris`; Saturn's rings are drawn from the ephemeris; a disk's centre and scale come
  from `PlanetaryLimbFit`; each colour is placed by its own limb fit.
- **The stack's defaults are the measured best** (`--legacy` is the old recipe); no step may grow faster than linearly
  in frames.
- **Sharpening:** nothing is drawn outside the limb; the derived gains are the truth's and are never multiplied; anything
  past them is the strength option; learned gains and deconvolution are options; the window is the frame mirrored.
- **A stack is scored through `PlanetaryMetrics`**; a real capture is judged against its own `_stack`/`_post`
  (`planetary judge`), never a display picture. Frame quality is read in the mid bands; on 8-bit frames plain
  correlation, never phase correlation.
- A recenter nudge asks `DeviceOwnershipGate` first. Read the plan before touching the Canon path.

### AI Enhancement, colour composition, background extraction

Full rules: `docs/plans/ai-enhancement.md`, `docs/plans/rc-astro-enhancers.md`, `docs/plans/narrowband-colour.md`,
`docs/plans/background-extraction.md`.

- `SharpenPipeline` runs role-typed enhancers; RC-Astro is preferred, deferred and licence-gated (`DeferredEnhancer`).
  **The program is shaped by what SERVES** (`CapabilitiesFor`, `CanonicalProgram`); a gate asks
  `IEnhancerAvailability.Serves`, never `is null`.
- **The SETI Astro tier is removed**: never open or derive from its model files (`EnhanceBackend` 2 stays unassigned).
- TianWen's deconvolver serves only when asked (`--ai-backend tianwen` and a stated kernel). A graph exported through
  torch.export clears `allowzero` on every Reshape. **Never pass a device index to DirectML.**
- **The canvas ring is the pipeline's** (`SanitiseForEnhance`, `Image.WithRingFrom`); a gradient corrector holds it out
  of its fit.
- **A TianWen model loads only against its contract** (`<stem>.contract.json`, `ModelContract`); a retrain gets a new
  file name. A new LFS file type the apps ship goes into `APP_LFS_INCLUDE` in `dotnet.yml`.
- No star remover of ours ships: it runs only on weights a caller names (`OnnxTianWenStarRemover`), never through
  `N2nLinearRunner`. Core protection is applied in `NarrowbandCombination.SplitStarsAsync`, and after it, for our
  remover only, the dig-fill (`DigFill.Apply`).
- RC-Astro is driven through its CLI's NDJSON protocol, never ORT. A vendor's weights are read where the vendor put them.
  **Never make a shipped capability depend on a script only a checkout can run.**
- **Mono colour composition:** each step is one verb on one routine; `image combine` is `ColourComposition.RunAsync`
  calling those routines and nothing between. A step writes on its input's scale. Stars are never denoised.
- **Classical background extraction** (`ClassicalBackgroundExtractor`, chosen by `GradientBackend`): thresholds are in
  noise units of the working grid (a block mean of `Downsample^2` pixels); the polynomial iterates, the surface runs once; a one-channel RGGB input is fitted per
  photosite colour; the level is per plane; a master's zero canvas ring is masked to NaN before fitting.

### Hosting API and remote rigs

Full rules: `docs/architecture/hosting-api.md`, `docs/plans/hardware-in-the-server.md`, `docs/plans/remote-profile.md`.

Native v1 (`/api/v1/`) is the session plane; ninaAPI v2 (`/v2/api/`) is a shim; Alpaca is a device plane.

- **A pushed schedule beats the target queue**: never route a real schedule through `/targets`.
- **Subscribing to `PromptRequested` takes over the unattended answer.** Liveness is the client's presence beat, never
  its socket. A broadcast only queues.
- **Numeric enums on the wire**; default a `required` enum on a request DTO.
- **Previews go through `PreviewEncoder`**, lease the frame, and answer `If-None-Match` first; every frame route reads
  `NodeFrames`.
- The Alpaca plane cannot become the session plane; its device numbers are the active profile's order. Actuation routes
  ask the lease (`ActuationGate`, 409).
- **AOT is verified by `dotnet publish -r <rid>`.** RDG stays enabled in the `TianWen.Hosting` library and both JSON
  contexts stay registered; never a `ResponseEnvelope<object>` or an anonymous-type payload.
- **A run is the node's, never a request's** (`IHostedSession.TryStartAsync`, `TryAbort`, `INodeRun`); a request that
  lasts ends at `ApplicationStopping`.
- `new SessionConfiguration()` is the defaults; `default(...)` is zeros. A new field goes into `SessionConfigApiDto` and
  its round-trip test.
- **A slow operation is a job** (`NodeJobs.StartOrJoin`, 202); a job on a device holds the device.
- The node is found on its socket behind one lock (`NodeSocket`, `NodeLock`); a client uses `NodeTransport` and starts
  the keeper through `LocalNodeLauncher`. A new place that commands a cooler owes `IDeviceHub.SetCoolerIntent`; one
  cooling ramp, `CameraCoolingRamp`.
- A device is read through `DeviceHubReadingExtensions`; the node never reads a device a run holds.
- The node writes and reads a profile through `NodeProfiles` (a stale revision is a 412).
- **Over TCP, seeing is free and a command needs control** (`NodeAccessGate`); a new route goes in its surface's group.
- The node keeps the computer awake while it holds hardware (`NodeKeepsAwake`, a power request object).
- **Local-only is one decision per process, `LocalOnlyMode`**; a new network bind asks it (`LocalOnlyModeSourceTests`).
- **Remote rigs:** selecting a rig changes what you look at, never what this node owns. One `LiveSessionState` per view
  context; a handler that drives a rig resolves its node with `CommandTargetOrSay`.
- **Wire traps:** never `required` on a nullable wire property; an unknown crosses as null (`JsonNumber.OrNull`), never
  0 or a non-finite double; a serialised property with a default is `set`, never `init`.
- Polling is authoritative, the WebSocket a hint; every broadcast event is one the mirror handles; every request has a
  time budget, filtered on the ORIGINAL token. A member added to `LiveSessionState` goes into `MirrorParityTests`.
- Control of a rig is its connection's (`NodeGrants`, the credential store). A run's notes are worded once
  (`SessionNotes`); whether a rig answers is one rule, `ISessionTelemetry.Contact`.

### Images and files

Full rules: `docs/architecture/image-pipeline.md`, `docs/plans/frame-lifecycle.md`,
`docs/plans/viewer-memory-footprint.md`, `docs/plans/image-codecs-facade.md`, `docs/plans/sensor-active-area.md`.

- **Who owns a frame is stated once, in the `<remarks>` on `Image`.** Never hold an image from `GetImageAsync` longer
  than needed. A preview of someone else's frame leases it; never `AdoptImageAsync`, never a bare read.
- `Image` is logically immutable; a new mutating API is named `Adopt*`, never a neutral `CreateFrom*`. Test fixtures
  must not share `Image` instances across tests. **Eviction is not release**; every read goes
  through `Planes`; a reload-backed frame is awaited with `AstroImageDocument.EnsurePlanesAsync` off the render thread.
- **Every histogram goes through `Image.Traverse`**; parallel row bands through `Image.TraverseInBands` only.
- A demosaic the viewer offers has its own branch in `image.frag`; resolve `DebayerAlgorithm.Auto` first; every gradient
  compares two samples of the same colour; a tap past the edge mirrors.
- A driver's frame is in ADU that agree with `BitDepth` and `MaxADU`. **`Image.MaxValue` is the peak observed, not
  saturation**; `Image.UnitScaleDivisor` is the one source for [0,1].
- **Every reader walks to the first HDU holding an image** (`ReadFirstImageHdu()`); a file is opened through
  `Image.OpenFits`; `.fz` is matched on `.fz` alone. `WCS.FromFits` keeps its single `ReadHDU`.
- **A FITS header becomes an `ImageMeta` in one place, `ParseImageMetaFromHeader`.** A declared pixel scale beats
  `FOCALLEN`. `ImageMeta.PixelSizeX` is the unbinned photosite; the `XPIXSZ` card includes binning. A light's guiding
  quality is its own exposure's (`ExposureGuideTally`), never a session average.
- **A Canon raw is cropped to `CanonRawFile.ActiveArea` on import**; ask `ActiveArea.CfaPattern` after cropping. Other
  cameras record their areas (`DATASEC`, `BIASSEC`; `FitsSection`).
- **Float TIFF values are [0, 1] on disk.** Codec types are in the `SharpAstro.*` codec packages, not DIR.Lib.
- **Directory walks go through `FileEnumeration`**, never the `SearchOption` overloads.
- **An embedded data file is asked of `DataFiles.Source` by NAME** (`IDataFileSource`, #2161), never
  `GetManifestResourceStream`: a trimmed consumer switches a group off (`TianWen.Lib.EmbeddedData.<group>`) and its host
  supplies the file instead. A reader awaits `OpenAsync`; one that cannot wait takes `OpenIfAtHand` and owes a load
  step its host awaits first. A file nobody has is named, or done without. `DataFileSourceTests`.
- **AppData files have more than one process on them**: `IExternal.AtomicWriteJsonAsync` / `TryReadJsonAsync` or
  `SharedFile`, never a bare `FileStream`; a file every host adds to goes through `UpdateJsonAsync`. Add a new directory
  to the tree in `docs/architecture/runtime-data.md`.

### Viewer, rendering, stretch

Full rules: `docs/architecture/widgets-and-controls.md`, `docs/architecture/viewer-gpu-lifetime.md`,
`docs/architecture/stretch-pipeline.md`, `docs/plans/hdr-display.md`, `docs/plans/colour-theme.md`,
`docs/architecture/desktop-shell.md`.

- The Enhance popover's half is chosen by `ViewerState.EnhancesAsPlanet`. `LiveFramePreviewSource` (per exposure) and
  `LiveStackPreviewSource` (video rate) are not interchangeable.
- **GPU lifetime:** never call `UploadDocumentTextures` outside `PrepareFrame`; never destroy a bound Vulkan object
  (`VulkanContext.DeferDestroy`). Run under `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` when touching this.
- Auto-crop prefers the master's coverage plane; a crop is a clip every draw path owes. Absence is border-reachable;
  `Image.FillInteriorHolesInPlace` is the only hole filler.
- Read the catalogue through `ImageRendererBase.LoadedCatalog`. **The docked info strip reports; settings live in
  toolbar popovers.** A popover is a `Layout.Builder.Popover` node plus a `PopoverState`.
- **A light's grade is judged in one place, `LightGrading.Verdict`** (Lib); a reject is the `BAD_<CAUSE>_` rename;
  `MAYBE_CLOUD_` is a suspicion the stacker leaves out unless `--include-maybe-cloud`. The cloud terms, thresholds and
  baselines: `docs/architecture/widgets-and-controls.md`, "FITS Viewer Widget (from CLAUDE.md)".
- **Stretch: wire a new stage into BOTH `image.frag` and the CPU helpers** (`StretchTests_NewPipeline`).
  `Image.StretchValue()` is the one scalar stretch. `StretchMode.Auto` and `.Planetary` are UI intents resolved before
  any `StretchUniforms`, by every renderer. Background neutralisation is solved post-WB. Luma weights come from
  `StretchUniforms.LumaWeights`.
- **Shaders are pre-baked SPIR-V**: edit, re-bake (`dotnet run --project tools/BakeShaders -c Release --
  src/TianWen.UI.Shared/Shaders`), commit the `.spv` and `sources.sha256`. ASCII only.
- **The star field is culled on two axes** (`StarMagnitudeIndex`, `StarChunkIndex`) and capped in total
  (`BudgetedMagnitudeLimit`); `docs/plans/web-tycho2.md`.
- **A quantized cache key takes its step from the BUCKETED value**; assert the gather count with a lower bound too
  (`docs/plans/web-showcase.md`).
- **The web host paints per event**: continuous gestures coalesce through `RequestRenderCoalesced()`; a delayed step
  waits at least 1 ms and loops. `docs/plans/web-host-carve-out.md`.
- **Theme** (`GuiTheme`): a cache of palette colours keys on `GuiTheme.PaletteGeneration`; Night has zero blue and is
  judged at night; the accent is the colour of an action (`GoButtonBg`); a TUI row asks `TuiRowPalette`.
- **Desktop shell:** the single-instance gate is `InstanceGate`; activation is `sdlWindow.Activate()`; a thumbnail
  provider reads nothing in `Initialize`; every file in a macOS bundle's `Contents/MacOS` is signed.

### Layout and input (`DIR.Lib.Layout`)

Full rules and the five traps: `docs/architecture/widgets-and-controls.md`; direction: `docs/plans/dir-lib-10.md`. Read
it before layout work.

- Build trees with `Layout.Builder` and the fluent methods; `global using Layout = DIR.Lib.Layout;`.
- A text field is `Layout.Builder.TextInput(state, fontSize)`; a choice, checkbox and double-click are declarations; a
  mark is a `Layout.Content.Icon`; a tooltip is `.WithTooltip`; a control's key is `.WithShortcut`.
- **A box is the engine's measurement of its content**, never a sum of constants. **A declared node takes design units**
  (`DeclaredLayoutTakesDesignUnitsTests`).
- **Every host routes press, move and release through `DIR.Lib.InputRouter`** and calls `AfterPaint()`; a press on a
  region is consumed there. No fourth key router. The pointer's appearance is a region's property.
- **Focus has one owner per window** (`WindowUiSettings.Focus`). `TextInputInteraction` swallows every key while a field
  is focused, so a binding that must survive one is a `.WithShortcut`, not a case in the host's key switch.
- `DpiScale` / `FontPath` / `EmojiFontPath` are widget properties, never parameters; never read `Ui.DpiScale` in a
  widget. Faces come from `BundledFonts.Resolve()`.
- TUI rows are trees (`IRowLayout.BuildRow(in RowContext)`); a new capability is a field on `RowContext`.

### State, threads, code quality

Full rules: `docs/architecture/coding-rules.md`.

- **Signal handlers route, they do not implement**: logic goes to `EquipmentActions`, `*Extensions.cs` or a persistence
  helper.
- **Shared UI state collections are `ImmutableArray<T>` with atomic replacement**, never `List<T>` or `Dictionary`.
- State touched after an `await` in `AppSignalHandler` is cross-thread: `ConcurrentDictionary`, `Interlocked`,
  `CircularBuffer<T>`.
- **Never `.GetAwaiter().GetResult()`.** Prefer a Task hand-off or `Interlocked` to a lock; a `lock` needs a justifying
  comment, must be unreachable from a render thread, and is `System.Threading.Lock`.
- No `WhenAll` for `ValueTask`: start both, then `try { await a; } finally { await b; }`.
- **Never build the value for a `CompareExchange` inside the call.**
- **No null-forgiving `!` in production code**; state the invariant (`[MemberNotNullWhen]`, a pattern bind, `?? throw`).
- **Never copy an array through a cast `Clone()`** (`NoCastArrayCloneTests`).
- **No JSON type holds a nullable struct whose last property can be null** (`JsonStreamReadTrapTests`).
- Pure math in `TianWen.Lib`, UI state in `TianWen.UI.Abstractions`, GPU calls only in `TianWen.UI.Shared` / `.Gui`.
