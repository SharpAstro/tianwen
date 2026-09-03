# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powershell
- Use CRLF line endings for `.cs` and `.csproj` files
- **Exit codes 127 and 13x from GUI / CLI / Server processes mean the .NET process crashed**, not
  "command not found" or "shell killed it". Always read the stderr log (e.g. `gui-stderr.log`) for
  the actual .NET exception + stack trace before drawing conclusions from the exit code.

## Project Tracking Docs

Canonical project state lives in these markdown files; read the relevant ones before starting non-trivial work:

| File | Purpose |
|------|---------|
| `docs/plans/summary.md` | Current status of every plan in `docs/plans/` (DONE / PARTIAL / NOT STARTED) cross-checked against the codebase |
| `docs/plans/*.md` | Per-feature implementation plans with phasing tables |
| `docs/architecture/*.md` | Architecture deep-dives: the subject in full, where a section below keeps only the rules (e.g. `image-pipeline.md`, `stacking-render-pipeline.md`, `stretch-pipeline.md`, `viewer-gpu-lifetime.md`, `widgets-and-controls.md`, `hosting-api.md`, `unattended-ui-driving.md`, `desktop-shell.md`, `driver-resilience.md`, `sibling-builds-and-releases.md`) |
| `TODO.md` | Active / high-priority task list (repo root) |
| `docs/todo/*.md` | Full backlog + done-archive + unsorted inbox, split by area |
| `docs/todo/hardware-validation.md` | The bench queue: every check only a real device or night can answer, indexed by GEAR; one home per item (plans keep the *why* + a pointer, never a second checkbox) |
| `docs/known-limitations.md` | Root causes of limitations/bugs (the *why*); read before "fixing" a suspected bug |

## Custom Skills

Available in `.claude/skills/<name>/SKILL.md`: auto-invocable when the request matches the skill's description, or explicitly via `/<name>`.

| Skill | Purpose |
|-------|---------|
| `release-lib` | Release a SharpAstro sibling library to NuGet with full dependency chain |
| `release-tianwen` | Cut a TianWen binary release (workflow_dispatch + GitHub Release with .tar.gz assets) |
| `sibling-status` | Git status + version across all SharpAstro repos |
| `check-ci` | GitHub Actions CI status across all repos |
| `bump-version` | Bump TianWen's version: the one `<VersionMajorMinor>` in `src/Directory.Build.props` |
| `run-gui` / `run-tui` / `run-fits` | Build and launch the GUI / CLI TUI / FITS viewer with stderr redirect |
| `test-run` | Run a suite so a failure is always identifiable (TRX + no truncation), and hunt flakes |
| `test-filter` | Run tests matching a name pattern |
| `test-image-diff` | Diff test-output PNGs across run folders to flag visual regressions |
| `test-output-prune` | Delete old `yyyyMMdd` test-output folders, keeping the N most recent |
| `stack` | Run `tianwen stack` against a folder of FITS lights + calibration |
| `digitize-filter` | Digitise a vendor filter chart into `FilterCurveDatabase` (three chart families, the validation gates, the matcher re-check) |
| `tick-todo` | Mark a TODO item done and update CLAUDE.md, PLAN files, and memory |

## Project Overview

TianWen is a .NET 10 library for astronomical device management, image processing, and astrometry.
Supports cameras, mounts, focusers, filter wheels, cover/calibrators, and guiders via ASCOM, Alpaca (HTTP),
ZWO, QHYCCD, Meade LX200, Skywatcher, OnStep (serial + WiFi/mDNS), iOptron SkyGuider Pro, Gemini FlatPanel
Lite (native serial cover/calibrator), Gemini Focuser Pro (native serial focuser, a rebadged myFocuserPro2),
PHD2, and a built-in guider. Published as `TianWen.Lib` on NuGet, plus AOT-published binaries, of
which **four are release assets** (`tianwen` CLI, `tianwen-server` headless, `tianwen-gui`,
`tianwen-fits`) and two are built but not shipped as `.tar.gz` (`tianwen-mcp`, `tianwen-ascomhost`).

Repository: https://github.com/SharpAstro/tianwen

## Solution Structure

```
src/
├── TianWen.slnx                   # Solution file (XML format)
├── Directory.Build.props          # Auto-detect sibling repos (ProjectReference vs PackageReference)
├── Directory.Packages.props       # Centralized package version management
├── TianWen.Lib/                   # Core library (net10.0)
├── TianWen.Lib.SourceGenerators/  # Roslyn generators for Lib (DispatchInterfaceGenerator)
├── TianWen.Lib.Tests/             # Unit tests (xUnit v3)
├── TianWen.Lib.Tests.Functional/  # Functional/integration tests (Session loops with FakeTimeProvider)
├── TianWen.Lib.Tests.Simulators/  # On-demand tests vs LIVE Alpaca/ASCOM simulators (gated; skip by default)
├── TianWen.Cli/                   # CLI (AOT-published → `tianwen`)
├── TianWen.Hosting.Contracts/     # Wire DTOs + the shared HostingJsonContext (host AND client reference it)
├── TianWen.Hosting/               # ASP.NET Core Minimal API (REST + WebSocket + Alpaca device plane)
├── TianWen.Server/                # Headless server (AOT-published → `tianwen-server`)
├── TianWen.RemoteClient/          # Client for a remote node (TianWenNodeClient / EventStream / SessionMirror)
├── TianWen.AscomHost/             # Windows-only out-of-proc host for in-proc COM ASCOM drivers
├── TianWen.UI.Abstractions/       # Widget system, layout, state, shared types
├── TianWen.UI.Shared/             # SDL→InputKey mapping, Vulkan FITS pipeline, VkSkyMapPipeline
├── TianWen.UI.Gui/                # N.I.N.A.-style integrated GUI (AOT-published → `tianwen-gui`)
├── TianWen.UI.FitsViewer/         # Standalone FITS viewer (AOT-published → `tianwen-fits`)
├── TianWen.UI.Web/                # WebAssembly showcase build (WebGl renderer)
├── TianWen.UI.Web.E2E/            # Playwright end-to-end tests for the web build
├── TianWen.UI.Benchmarks/         # BenchmarkDotNet performance tests
├── TianWen.AI/                    # ORT facade (EP resolver + session-options helpers)
├── TianWen.AI.Imaging/            # Image ↔ tensor bridge + concrete enhancer wrappers
└── TianWen.AI.MCP/                # MCP (Model Context Protocol) stdio server (AOT-published → `tianwen-mcp`)
```

Six projects set `PublishAot` + an `<AssemblyName>` short lower-case name: `tianwen`,
`tianwen-server`, `tianwen-fits`, `tianwen-gui`, `tianwen-mcp`, `tianwen-ascomhost`. **Only the
first four are packaged as release assets** by `.github/workflows/dotnet.yml`; adding a binary to
the release means adding it to the upload/glob steps there as well as setting the properties here.

## Build & Test Commands

```bash
# All commands run from src/
dotnet build
dotnet test
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
```

## SharpAstro Sibling Libraries

TianWen depends on in-house libraries published to nuget.org under the **SharpAstro** org. Their
source repos live as siblings under the same parent directory (`../`). csproj layout varies, not
every sibling uses `src/<Lib>/<Lib>.csproj`.

| Package | Source Repo | csproj path | Auto-detect |
|---------|-------------|-------------|:-----------:|
| `DIR.Lib` | `../DIR.Lib` | `src/DIR.Lib/DIR.Lib.csproj` | ✅ |
| `SdlVulkan.Renderer` | `../SdlVulkan.Renderer` | `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` | ✅ |
| `Console.Lib` | `../Console.Lib` | `src/Console.Lib/Console.Lib.csproj` | ✅ |
| `FITS.Lib` | `../FITS.Lib` | `CSharpFITS/CSharpFITS.csproj` (package name is `FITS.Lib`) | ✅ |
| `FC.SDK` | `../FC.SDK` | `src/FC.SDK/FC.SDK.csproj` | ❌ |
| `ZWOptical.SDK` | `../zwo-sdk-nuget` | `ZWOptical.SDK.csproj` (repo root) | ❌ |
| `QHYCCD.SDK` | `../QHYCCD.SDK` | `QHYCCD.SDK.csproj` (repo root) | ✅ |
| `SharpAstro.Fonts` | `../Fonts.Lib` | `src/SharpAstro.Fonts/SharpAstro.Fonts.csproj` | transitive |
| `SER.Lib` | `../SER.Lib` | `src/SER.Lib/SER.Lib.csproj` | ✅ |
| `Lzip.Lib` | `../Lzip.Lib` | `src/Lzip.Lib/Lzip.Lib.csproj` | ✅ |
| `LAN.Lib` | `../LAN.Lib` | `src/LAN.Lib/LAN.Lib.csproj` | ✅ |
| `WebGl.Renderer` | `../WebGl.Renderer` | `src/WebGl.Renderer/WebGl.Renderer.csproj` | ✅ |
| `SharpAstro.AppShell` | `../AppShell` | `src/SharpAstro.AppShell/SharpAstro.AppShell.csproj` | ✅ |
| `TianWen.DAL` | `../TianWen.DAL` | - | ❌ |

**Auto-detection** (`Directory.Build.props`): a **single** property `UseLocalSiblings` gates them all.
The build switches to ProjectReference when **every** sibling working copy exists; `DIR.Lib`,
`Console.Lib`, `SdlVulkan.Renderer`, `WebGl.Renderer`, the `Codecs`-repo codec family (`SharpAstro.Tiff`,
`SharpAstro.Exif`, `SharpAstro.Png`, `SharpAstro.Color.Icc`, `SharpAstro.Jxr`,
`SharpAstro.Jpeg.IccInjector`, `SharpAstro.Exr`, `SharpAstro.Codecs`), `QHYCCD.SDK`, `FITS.Lib`,
`SER.Lib`, `Lzip.Lib`, and `LAN.Lib`; otherwise it falls
through to PackageReference. Override: `dotnet build -p:UseLocalSiblings=false`. CI always uses
PackageReference. `Fonts.Lib` is transitive via DIR.Lib's own `UseLocalFontsLib` switch. There is **no**
per-library switch anymore, so a missing checkout of *any* listed sibling flips the whole set back to
packages (all-or-nothing), which is fine on a dev box that has them all.

**The history behind the rules below -- the CPM drift, the web projects in CI, the `open-vs.ps1` /
`Exists(...)` divergence and the release traps -- is in
[`docs/architecture/sibling-builds-and-releases.md`](docs/architecture/sibling-builds-and-releases.md).**
The rules:

- **No CPM opt-outs left in `src/`**, and a new one needs a real technical justification, not "this
  project is not in the solution" (being outside a solution never had any bearing on CPM).
- **A sibling gated on `UseLocalSiblings` must also be in that property's own `Exists(...)` list**, and
  `open-vs.ps1`'s project list must match the same conjunction -- nothing enforces either, and a
  generated solution with unresolvable entries loads with them silently unloaded.
- **`TianWen.UI.Web` is IN `TianWen.slnx`; only `.E2E` stays out** (`IsTestProject` + Playwright, so a
  solution-wide `dotnet test` would sweep a suite needing a browser) and `dotnet.yml` compiles it. The
  web host consumes `UI.Abstractions` from `.razor`, which no `--include=*.cs` grep sees and no
  out-of-solution project compiles: a rename passed both and broke CI. Run E2E explicitly:
  `dotnet test TianWen.UI.Web.E2E`.

For libraries without auto-detection (`FC.SDK`, `ZWOptical.SDK`, `TianWen.DAL`),
prefer to extend the `UseLocalSiblings` switch in
`Directory.Build.props` + add a conditional `ProjectReference` in the consuming `.csproj`
rather than reaching for local nupkg feeds. When that's not viable (e.g. cross-team release
cadence forces a version bump), commit + push + wait for NuGet publish; **do not** create
local nupkg feeds or run `dotnet pack` to short-circuit the release dance, since CI builds
will still pull from nuget.org and a local-only nupkg will mask version-skew bugs.

### Releasing a sibling, and TianWen's own version

**The mechanism is org-wide and documented once, in the imported `../.github/CLAUDE.md` ("Versioning")
plus `../.github/docs/dotnet-ci-pattern.md` (the org root's `.github` clone, one level up from this
repo, NOT this repo's own `.github/`): a release in ANY SharpAstro repo is editing
`<VersionMajorMinor>` in that repo's `Directory.Build.props` and nothing else.** Do not restate it
here. TianWen uses the same shape (`src/Directory.Build.props`; `/bump-version` edits the one line), so
**a version literal in a csproj or the workflow is a regression** -- delete it and let it derive.

Three traps that doc omits, plus this repo's five-job read-back and the latent 1.0.0 pack it closed:
[`docs/architecture/sibling-builds-and-releases.md`](docs/architecture/sibling-builds-and-releases.md).
In short: `DOTNET_NOLOGO: 1` must be in the workflow `env:` (the version is read off msbuild stdout);
release notes live in `CHANGELOG.md`, never beside the number; a test step that rebuilds a
`GeneratePackageOnBuild` project without `-p:Version` publishes a stray `X.Y.0` package that
`--skip-duplicate` then hides; **a new CI job that builds or publishes needs both halves of the
`VERSION_PREFIX` hand-off** (`$GITHUB_ENV` is per-job, so `needs: build` + the job-level `env:`);
and `LALR.CC` is deliberately exempt from the shared shape, so leave it alone.

## Key Technologies

| Area | Technology |
|------|-----------|
| DI / Logging | Microsoft.Extensions.* |
| CLI | System.CommandLine v2 + Pastel |
| Testing | xUnit v3 + Shouldly + NSubstitute |
| Imaging | SharpAstro codecs facade (`SharpAstro.Tiff`/`.Png`/`.Exr`/...), FITS.Lib (Magick.NET removed) |
| UI / GPU | SDL3 + Vulkan (SdlVulkan.Renderer) |
| Hosting | ASP.NET Core Minimal API, SharpAstro.Jpeg (preview encode) |
| Astronomy | ASCOM, ZWOptical.SDK, QHYCCD.SDK, IAU SOFA (C# port) |

## Testing Conventions

- **xUnit v3** with `[Fact]` / `[Theory]` + `[InlineData]`; **Shouldly** for assertions; **NSubstitute** for mocks
- Test data: embedded resources in `Data/` subdirectories
- **Never use reflection in tests**: add an `internal` property/method instead (test project has `InternalsVisibleTo`)
- **Avoid duplication**: extract shared setup to helpers (e.g., `SessionTestHelper`)

### Device-Simulator Integration Tests (on-demand)

`TianWen.Lib.Tests.Simulators` drives the **real** device drivers against **live simulators** --
separate from the fast unit (`TianWen.Lib.Tests`) and fake-device functional
(`TianWen.Lib.Tests.Functional`) suites so neither depends on an external process. Every test is
opt-in via `SimulatorGate` and **skips (never fails) with no simulator present**, so a bare
`dotnet test` stays green:
- **Alpaca** (`AlpacaSimulatorTests`, cross-platform HTTP): set `TIANWEN_ALPACA_SIM` to a running
  ASCOM Alpaca "OmniSim" base URL (e.g. `http://localhost:11111`). Resolves devices via the
  management API (NOT UDP discovery -- unreliable on runners) + direct-addressed `AlpacaDevice`s,
  then exercises the production `AlpacaClient`/drivers incl. the camera **ImageBytes** round-trip
  (the path `AlpacaImageBytesTests` only byte-pinned).
- **ASCOM** (`AscomDeviceTests`, Windows COM): set `TIANWEN_ASCOM_CI` with the ASCOM Platform +
  `ASCOM.Simulator.*` installed. (Moved here from `.Functional`; re-gated off `Debugger.IsAttached`.)

Kept off the push/PR path (an OmniSim download / a full Platform install is too heavy for every push),
so `.github/workflows/simulators.yml` has two entry points: `workflow_dispatch`
(`gh workflow run simulators.yml [-f suite=alpaca|ascom|both]`) and a **weekly `schedule`** running the
**Alpaca leg only** as an unattended regression guard. The PR `dotnet.yml` loop only *compiles* the
project. Real-time settle waits go through a real `SystemTimeProvider` (never a fake clock -- its
auto-advancing `SleepAsync` would busy-spin), so the "no raw `Task.Delay`" rule holds even for genuine
wall-clock waits. The shared `catalogs` job, and what the suite caught on its first run:
[docs/plans/device-simulator-ci.md](docs/plans/device-simulator-ci.md).

### Test Collections & Parallelism

Tests grouped into `[Collection("X")]` by functional area. **Any test that drives a `Session` belongs in
`[Collection("Session")]` -- the rule is about what a test DOES, not what it is CALLED**, and they run
sequentially so several sessions' concurrent `Task.Run` + `FakeTimeProvider` timer callbacks cannot
starve the pool. It used to be written as "all `Session*Tests`", and three classes drove real sessions
from outside every collection because their names did not match: `DeviceOwnershipTests`,
`SessionFaultCounterTests` and `SessionScoutClassifierTests`. If it calls
`SessionTestHelper.CreateSessionAsync`, it is a session test.

**A fake-clock `SleepAsync` must throw on a cancelled token, exactly as the real one does, and a guider's
`StopCaptureAsync` must not return until its loop has exited.** `FakeTimeProviderWrapper.SleepAsync`
used to `Advance` and return whatever the token said, so a cancelled background loop ran on to its next
natural exit; and `FakeGuider` / `BuiltInGuiderDriver.StopCaptureAsync` cancelled their capture loop and
returned at once (cancelling is synchronous, the exit is not). Every target start is "stop guiding,
slew, start guiding", so the next loop began on the guide camera while the previous one was still
mid-frame: two consumers of one camera, one guide frame released twice (`ChannelBuffer: more releases
than refs`), the new loop's `GuideLoop` nulled by the old one's finally, and the session never saw its
first exposure complete. That is what `DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was -- **a
race, not starvation** (6 of 9 failures in isolation on a quiet box, 0 of 10 after the fix). It was
called starvation for a day because every measurement had been taken under load; instrumenting the fake
clock (fake time traversed, per thread) is what settled it.

**No wall-clock `CancellationTokenSource` timeouts** in session tests; use `[Fact(Timeout = ...)]`
(inner timeouts cause flakes). **A test that drives a whole run needs that bound**: a wedged run hangs
rather than fails, and an unbounded hang is a five-minute `--blame-hang` timeout plus a multi-GB dump
instead of one red test.

**Less parallelism is faster here, and the config only counts if it is copied to the output.** All three
test projects carry an `xunit.runner.json` (`maxParallelThreads: 4`; Simulators pins 1 +
`parallelizeTestCollections: false`) **and** a matching
`<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`. `TianWen.Lib.Tests`
had neither for a long time while this file claimed otherwise, so xUnit silently defaulted to the core
count and thrashed the box; adding both cut the suite from 8m45-12m to 7m46 **and made it green**:
contention was dominating. Never diagnose a slow suite by re-running it repeatedly: one run with a TRX
logger, then rank durations.

`SessionTestHelper` defaults to `FakeMountDriver`; pass `mountPort: "LX200"` or `"SkyWatcher"` only for
protocol-specific tests.

**Cooperative time pump pattern** for tests that run session loops via `Task.Run`:
```csharp
ctx.External.ExternalTimePump = true;
var loopTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(...));
var pumpIncrement = TimeSpan.FromSeconds(5);
var pumped = TimeSpan.Zero;
while (pumped < TimeSpan.FromHours(4) && !loopTask.IsCompleted && !ct.IsCancellationRequested)
{
    ctx.External.Advance(pumpIncrement);
    pumped += pumpIncrement;
    await Task.Delay(1, ct);
}
```
**Never** use `SleepAsync(subExposure)` in a pump loop; it advances fake time even when the `Task.Run`
hasn't been scheduled yet, causing targets to "set" before imaging starts. `Advance` fires timers
synchronously; `Task.Delay(1)` yields to the thread pool.

### Driving the GUI and the TUI unattended

Drive a full `RunAsync` session against simulated hardware with **no human in the loop and no
screenshot-poll-and-OCR**. **Every mechanism -- the fake-device URI shapes incl. `port=SkyWatcher` /
`hasCover=false`, the DEBUG inspector surfaces, the cell-buffer contract -- is in
[`docs/architecture/unattended-ui-driving.md`](docs/architecture/unattended-ui-driving.md).** The rules
that bite even after reading it:

- **`ProfileData.SiteLatitude/Longitude` must match the mount URI's `latitude/longitude`** (a split
  site throws "Could not calculate timezone"). Canonical wiring:
  `SessionTestHelper.CreateSessionAsync(mountPort:"SkyWatcher", latitude, longitude)`.
- **Anchor the clock with `TIANWEN_NOW`** to a real night at that site, or the session stalls in
  daylight instead of leaving `WaitingForDark`.
- **`StartSession` needs >=1 pinned target** (`PlannerState.Proposals.Length > 0`); planner pins
  persist per-profile, so pin once.
- **Ground truth for fine telemetry is the Debug log, not the inspector snapshot.** `AppState` reads
  `LiveSessionState`, which can lag during the guide loop; per-frame guide stats, HA and pier side come
  from `%LOCALAPPDATA%/TianWen/Logs/<date>/GUI_*.log`.
- **Use `render_liveness`, not a screenshot, to decide IF the render thread is stuck** -- every
  inspector command runs ON that thread, so screenshot/describe block exactly when it is.
- **`validation_report` with zero messages is evidence only when `active` is true** (the DEBUG +
  `SDLVK_VALIDATION=1` gate AND `layerAvailable`) -- a host with no Khronos layer installed used to
  answer `enabled: true` with zero messages, indistinguishable from a clean run.
- **A terminal reads back as TEXT, which is the one thing a GPU surface cannot offer.** `screen` /
  `row` / `cell` report the **front** cell buffer, and `cell` adds the resolved pen, which is how a
  colour bug is caught at all (`#000000` on `#000000` is invisible on screen yet identical to a correct
  one in a text dump). One gotcha: the modifier parameter is **`mods`** (`"ctrl+shift"`), not a `ctrl`
  boolean.

## Coding Style

Enforced via `src/.editorconfig` (it sits beside the solution, not at the repo root):
- 4 spaces, CRLF line endings, block-scoped namespaces (`namespace Foo { }`, not file-scoped)
- Primary constructors preferred for DI
- No implicit `new(...)`, always `new SomeType()`
- Expression-bodied: properties yes, methods/constructors no
- Interfaces prefixed with `I`; PascalCase types/properties/methods; `_camelCase` private fields

## Architecture

### Logger, TimeProvider, and SleepAsync

- `ILogger` and `ITimeProvider` resolved from `IServiceProvider` (not from `IExternal`)
- **`ITimeProvider.SleepAsync`** must be used instead of `Task.Delay(duration, timeProvider, ct)`.
  `FakeTimeProvider`'s `SleepAsync` auto-advances fake time; `Task.Delay` with `FakeTimeProvider`
  hangs waiting for external advancement. All code should be testable.
- `LoggerCatchExtensions` provides `ILogger.Catch/CatchAsync` for best-effort fallbacks

**`TIANWEN_NOW` startup clock anchor (dev/test):** set the `TIANWEN_NOW` env var to an ISO-8601
timestamp (ideally with an explicit offset, e.g. `2026-06-21T22:00:00+10:00`; no offset = machine-local)
to anchor the *entire* system clock to a simulated instant that then advances at real-time rate. This
lets you run a real night at the configured site while the machine clock says daytime, with **no fake-time
pump**. Single wiring point: the `ITimeProvider` registration in `AddExternal`
(`ExternalServiceCollectionExtensions.cs`) wraps `TimeProvider.System` in an `OffsetTimeProvider` when
`StartupTimeOverride.TryGet` returns an offset. Because planner, session loop, fake mount/camera, and
mount-reported UTC all resolve the clock from DI, they jump together. `StartupTimeOverride` (`Devices/`)
freezes the offset once at process start; the GUI logs a WARNING (`SIMULATED CLOCK ACTIVE`) when active.
Absent/unparseable → real system clock (previous behaviour). Pinned by `StartupTimeOverrideTests`.

### Device Management

URI-addressed: `DeviceBase` (URI identity), `IDeviceSource<T>` (driver backends),
`ICombinedDeviceManager` (coordinates sources), `IDeviceUriRegistry` (URI → instance map).
Each subclass reads query keys (`?key=value`) defined in `DeviceQueryKey`. See class XML doc comments
for supported keys. Full driver hierarchy (ASCOM / Alpaca / ZWO / QHY / native-serial subgraphs):
[docs/architecture/device-architecture.md](docs/architecture/device-architecture.md).

**A profile scan never probes a COM port, and a port that will not TAKE bytes is given up, not retried.**
`DiscoverOnlyDeviceType(type)` runs the serial probe pass only when a source for that type consumes it (it
used to run for `Profile` -- at GUI start-up on the main thread and from `MountLimitWatcher` every 5 s).
`SerialConnectionBase` bounds every write twice (port `WriteTimeout` + task deadline) and `SerialConnection`
bounds the close, because a Windows Bluetooth SPP listener port (`bthmodem.sys`, created for any paired
device advertising SPP) accepts an open and then never completes a write, and `SerialStream` ignores its
token. Only a write the driver never completed raises `ISerialConnection.HasAbandonedIo`, on which the pass
drops the port for the rest of the discovery; a READ timeout never does -- a device at the wrong baud or
awaiting another protocol completes the write and stays silent, and still gets every probe and baud. Found
and measured live 2026-08-30: [docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md),
"Live verification".

### Device Ownership (the hub lease)

A run that is driving hardware **claims it from the hub**, and nothing else may disconnect or command a
claimed device. `IDeviceHub.TryAcquireLease` / `DeviceLeaseSet.Acquire` (all-or-nothing over a rig) /
`DeviceOwnershipGate.Evaluate` (the one shared verdict + `Describe()` message, mirroring
`ProfileSwitchGate`). `Session.RunAsync` and `RunFlatsOnlyAsync` claim `Setup.DeviceUris()` for the
whole run, released in the `finally` so a claim survives `Finalise` (parking + warming is exactly when a
stray disconnect hurts most).

- **Reads are never leased.** Telemetry, status and previews stay free for every observer; watching a
  rig must cost it nothing. A lease only refuses *taking the driver away* and *commanding it*.
- **Never guard hardware access on a UI flag.** The guards used to be five ad-hoc
  `LiveSessionState.IsRunning` checks, every one wrong the same way: `IsRunning` is **false during a flat
  run** (which is why `HasActiveRun` exists), so mid-flat-run the focuser could be jogged, the mount
  pulsed and slewed, and a planetary capture started on the camera being metered. A UI flag also cannot
  work for the hosted API or the Alpaca plane, which never see one. Ask `DeviceOwnershipGate`; in the
  GUI that is `EnsureDeviceControllable(uri)`.
- **Enforcement is asymmetric, deliberately.** Disconnect has one choke point, so `DisconnectAsync`
  throws `DeviceLeasedException` unless `force: true`; a caller that skips the gate gets an exception,
  not a stolen driver. Actuation has no choke point short of proxying every driver (an interception layer
  on the imaging hot path), so actuation call sites ask the gate. Both evaluate the same rule.
- **`force: true` is for process shutdown only.** Note that GUI "Force Off" does **not** force past
  ownership: it means "skip the warm-up", which is what the user confirmed; consenting to a cold
  disconnect is not consenting to kill the night.
- **Escalation is explicit:** stop the run (abort the session / cancel the flat run) and the lease frees.
  There is no override on the actuation path by design.
- `GetDisconnectSafetyAsync` is a **hardware**-safety check (cooler on / mid-exposure) and returns `Safe`
  for anything that is not a camera; it is not, and never was, an ownership check. Ask the gate first.

Pinned by `DeviceOwnershipTests`, including three that drive a real `Session`/flat run end to end.

### Alpaca Backend (ASCOM Remote / Alpaca HTTP)

`AddAlpaca()` is a **fully functional** device source (camera, telescope, focuser, filter wheel,
switch, cover-calibrator) over the ASCOM Alpaca REST API; wired into CLI / Server / GUI alongside
`AddAscom()`. It is the primary cross-platform path for a headless Linux / Raspberry Pi host, where
the Windows-only native ASCOM COM bridge is unavailable.

**Camera image transfer goes through the binary `application/imagebytes` protocol, NOT the legacy
JSON `imagearray`.** JSON encodes every pixel as a decimal-ASCII integer (an order of magnitude
slower for full frames); ImageBytes sends a 44-byte little-endian `ArrayMetadataV1` header followed
by raw pixels. `AlpacaImageBytes.DecodeChannel` is the pure decoder;
`AlpacaClient.GetImageArrayBytesAsync` negotiates it via `Accept: application/imagebytes,
application/json` and verifies the response `Content-Type`. **Wire-order gotcha:** ImageBytes is laid
out `[Dimension1 = Width(X), Dimension2 = Height(Y)]` row-major, i.e. column-major in image terms, so
the flat index of `(x, y)` is `y + x*Height`; `DecodeChannel` transposes that into `Channel`'s `[y, x]`
layout. `AlpacaCameraDriver` downloads + decodes **once** when the server first reports `imageready`,
populating `ImageData` / `ChannelBuffer`, and `StartExposureAsync` clears them so the next frame
re-downloads. **The HTTP round-trip is validated against a live OmniSim** by
`AlpacaSimulatorTests.Camera_ExposesAndDownloadsViaImageBytes`; the decoder stays separately byte-pinned
by `AlpacaImageBytesTests`.

### Device Secrets (Credential Store)

Secrets (API keys) are **not** stored on the device URI or in the profile JSON. `ICredentialStore`
(`TianWen.Lib/Devices/`) holds them keyed `{deviceId}/{settingKey}` (e.g. `openweathermap/apiKey`);
keyed by **device, not URI**, so the secret survives the URI being replaced on a provider switch /
re-discovery (the bug it fixes: OWM's `?apiKey=` used to be wiped on every re-assign) and is shared
across profiles (enter once).

- **Windows**: `WindowsCredentialStore`. Credential Manager (Generic credentials) via
  `LibraryImport` (source-gen marshalling, AOT-clean; visible in Control Panel → Credential Manager).
  The `CREDENTIAL` struct keeps string fields as `IntPtr` (hand-marshalled) so it stays blittable.
- **Non-Windows**: `FileCredentialStore`; owner-only (`0600`) file per secret under `AppData/Secrets`.
  A libsecret / macOS-Keychain backend can drop in later behind the same interface.
- OS-selected in `AddExternal`. Tests exercise `FileCredentialStore` over a temp dir (the Windows
  vault is not unit-tested; it would write to the real per-user store).

A masked `DeviceSettingDescriptor` (`Mask: true`) routes its edit to the store, never the URI
(`AppSignalHandler`'s `StringSettingInput.OnCommit`; it re-fetches weather afterwards). A leftover
`?apiKey=` on a URI is ignored; the driver only reads the store. **Deferred:** a per-profile
override of the shared per-device key (would need an active-profile-id provider at driver-creation
time, since `NewInstanceFromDevice(sp)` has no profile context).

### Plate Solving

`IPlateSolverFactory` selects in priority order:
- `CatalogPlateSolver`: built-in, ~6 matched stars, no external dep, used by polar alignment refine loop
- `AstapPlateSolver`: wraps `astap_cli`; needs ~44 stars
- `AstrometryNetPlateSolver`: wraps `solve-field`; slower fallback

**`CatalogPlateSolver` requires Tycho-2 to be loaded.** The solver self-inits the
`ICelestialObjectDB` at the top of `SolveImageAsync` via the idempotent `InitDBAsync`
fast path (`_isInitialized`), so any caller (CLI, hosted API, tests) works without
remembering to init upstream. First call pays the Tycho-2 bulk-decode cost (~500 ms
typical); subsequent calls are free.

**A header hint comes from `OBJCTRA`/`OBJCTDEC` first, and `RA`/`DEC` is NOT the frame centre**
(`RA`/`DEC` is what the *mount reported*, agreeing with the frame only on a synced mount).
`WCS.FromHeader` and `Image.Fits.ParseTargetCoords` read CRVAL -> OBJCTRA/OBJCTDEC -> RA/DEC in that
order and must not diverge: the pair-lock anchor pool is the brightest catalog stars projecting inside
the frame from the hint, so a hint off by most of a field fills it with stars the image does not
contain and the seed never reaches consensus.

**A solver-built WCS answers in DETECTED-CENTROID coordinates -- never subtract 1 from `SkyToPixel`.**
The emitted WCS needs no 1-based-to-0-based conversion; applying one injects a constant (+0.91, +0.89)
px bias and spends a third of the acceptance gate's tolerance.

**`MinSampledFwhmPx` (2.0) re-detects at full resolution when a bin's median `StarFWHM` lands under
it** -- binning is proposed by the plate scale and vetoed by the measured star width, since a scale
gate cannot see seeing. It only ever un-does a bin (one wasted pass on the cheap raster); never infer
the unbinned width by multiplying back (measured FWHM floors near 1.2 px non-linearly).

**A remembered parity (`SolveHintCache`) is a hypothesis BUDGET for the other half, never a skip, and
is keyed on `(Telescope, Instrument, RowOrder, Bin)`, the LIGHT PATH** -- an OAG guide camera is the
opposite parity to the main camera on the same rig at the same instant, so per-rig or per-camera keying
is wrong. Skipping the doubted parity inverts the goal: a frame that seeds on neither then runs both in
SERIES instead of parallel. The parity rides on `SolveAttempt.IsStd` because the acceptance gate can
overturn the pick and the cache must learn the half that actually answered.

**The quad seed (`CatalogPlateSolver.TrySeedByQuadMatch`) runs BEFORE the parity race, in ONE parity,
and answers where the frame IS -- never a solution.** It matches the detected field to the catalog on
the five scale-free quad ratios and hands the race its origin, scale and parity belief; the pair-lock
still seeds at full fidelity and the acceptance gate still decides, so a wrong quad seed costs a pass,
never a wrong WCS. Three rules: the catalog cut is by DENSITY over the area the query box actually
COVERS of the window (a too-deep cut re-points the neighbours that define a quad); a relocation placed
AFTER the race saves nothing by construction; and a matcher that tests `Dist1` in pixels beside five
ratios rejects correct catalog quads (right for stacking, wrong here). Pinned by `RealFrameSolveTests`
and `QuadCatalogMatchTests`.

**Frozen real-field regressions: `TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz`** -- STAR
LISTS, not FITS, from 24 real Vela pointings / 96 frames / 78k catalog stars, driven by
`VelaMosaicFieldTests`. **Three of the four bugs it found would have passed a synthetic suite**,
because a synthetic field is built from a transform the test already knows.

Every measurement above, the dataset rationale, phase C's numbers and the quad-seed/parity-cache
design in full: [docs/plans/plate-solver-performance.md](docs/plans/plate-solver-performance.md).

**DI registration uses a factory lambda** (`AstrometryServiceCollectionExtensions.cs`):

```csharp
.AddSingleton<IPlateSolver>(sp => new CatalogPlateSolver(
    sp.GetRequiredService<ICelestialObjectDB>(),
    sp.GetRequiredService<ILogger<CatalogPlateSolver>>()))
```

The short form `AddSingleton<IPlateSolver, CatalogPlateSolver>()` does NOT work for any
ctor with a non-generic `ILogger` parameter; `Microsoft.Extensions.Logging` only
registers `ILogger<T>` (open generic) and `ILoggerFactory`, never `ILogger` directly.
A ctor `(Foo, ILogger? logger = null)` therefore silently gets `logger = null` from DI,
and `_logger?.LogDebug(...)` lines never fire, which is exactly how the
"`CatalogPlateSolver` fails on drizzle outputs from `tianwen solve`" bug hid for weeks.
**Rule:** ctor params should be `ILogger<TSelf> logger` for direct DI resolution, or
use a factory lambda when a non-generic `ILogger` ctor parameter must be preserved
(e.g. so the same class can be manually constructed by another component that already
holds an `ILogger`).

### Comet & Small-Body Ephemeris (`TianWen.Lib.Astrometry.Comets`)

JPL comets are a **dynamic, ephemeris-computed catalog**: a comet's RA/Dec AND brightness are
functions of time, computed locally from cached orbital elements, alongside the VSOP87 planets.
`Catalog.Comet` + `ObjectType.Comet`, covered by `CatalogIndex.IsSolarSystemObject` so the sky-map
live-position path applies for free. One keyless bulk SBDB fetch (~4000 comets) IS the database,
cached to `AppData/SmallBodies/comets.json`; position and magnitude are then pure local computation,
offline. Consumed by the sky map, the planner and `tianwen-mcp catalog.lookup`.

**Design, math, the bake, and the shipped operational invariants (with the measurements behind them):
[`docs/plans/comet-ephemeris.md`](docs/plans/comet-ephemeris.md).** Read it before touching this area.
Four rules that bite before you get there:

- **Comets are NOT in `ICelestialObjectDB`** (immutable after init). Every consumer augments at its
  own layer from `ICometRepository`. Never try to inject them into the DB.
- **A failed per-object Horizons fetch must be remembered** (`ApparitionRetryCooldown`).
  `RequestCurrentApparition` runs per drawn marker per frame, so without it an endpoint that can never
  answer is retried forever: 45 requests / 50 s in one four-minute session, measured on the deployed
  web build.
- **JPL sends no CORS headers from EITHER comet host, so the browser bakes BOTH** (SBDB *and*
  Horizons, which is a different host reached from a different class -- missing it was that retry
  storm). Nothing detects the host, deliberately.
- **The path and sparkline caches key on `(index, time-BUCKET)` and must hit regardless of sample
  count** -- an all-failed-to-solve empty result still caches, or it re-samples ~49 ephemerides every
  frame.

### Planner Pin Identity (a pinned planet is not its position)

**A proposal must always produce exactly one row in the planner list, and object identity for a
solar-system body is its `CatalogIndex` -- never the `Target` value.** Both halves are load-bearing; the
bug they fix was "Venus is in a proposal but doesn't appear in the planner so I can't remove it".

- **`Target` is a positional record**, so its equality includes RA/Dec -- and for a planet, the Moon, or
  a comet those are *ephemeris values resolved at an instant*, not identity. Venus pinned at last
  night's `AstroDark` is a different `Target` value from tonight's Venus. Match through
  **`PlannerActions.IsSameObject`** (index-equality gated on `CatalogIndex.IsSolarSystemObject`, exact
  record equality otherwise), used by `FindProposalIndex` (the removal path), the proposal->score
  resolve, and `AddProposal`'s duplicate check. Do **not** widen it to all catalogued objects: mosaic
  panels share an index and differ only by their offset centre.
- **`GetFilteredTargets` never drops a proposal.** The pinned section is a *projection* of `Proposals`
  (so `PinnedCount == Proposals.Length`, which is also what the N-1 `HandoffSliders` indexing assumes),
  not a filtered subset of it. `ResolveProposalScore` prefers tonight's list -> search results -> the
  score cache and, failing all three, **synthesizes** a row from the proposal itself. This is what makes
  the failure class impossible rather than merely unlikely: the row IS the unpin affordance (the `[-]`
  button and the keyboard toggle both act on it), so a proposal that resolves to nothing and is dropped
  is a pin the user can neither see nor remove, while it keeps being re-saved and re-scheduled.
- **Two independent ways in, both now closed.** `ComputeTonightsBestAsync` rebuilds `ScoredTargets`
  from tonight's list alone and **the scheduler never sweeps planets** (they arrive only via search /
  `CommitSuggestion` / a sky-map pin), so any full recompute orphaned a pinned planet. And
  **solar-system bodies are stored in the object DB with `double.NaN` coordinates**, so
  `PlannerPersistence.MatchTarget`'s DB fallback rebuilt a restored pin at NaN/NaN; it prefers the saved
  proposal's own RA/Dec whenever the catalog's is not a number, and a comet -- never in the DB by design
  -- restores from the proposal directly instead of being discarded.

Pinned by `PlannerSolarSystemPinTests`.

### Smart Framing (planner co-framing groups)

Pinning M8 with a wide-field profile auto-groups M20 into the same pointing: the planner derives the
sensor FOV from the profile and collapses co-framable targets into one scheduled observation
("M8 + M20") at the combined-footprint centroid. Plan + invariants:
[`docs/plans/smart-framing.md`](docs/plans/smart-framing.md).

- **Pure core in Lib** (`FramingGrouper` + `FramingPlanner`, `TianWen.Lib/Sequencing/`): tangent-plane
  fit, greedy nearest-accretion, RA-seam wrap. NOT quadratic -- Dec-sorted band binary-search per seed;
  neighbour discovery is grid-local (`DeepSkyCoordinateGrid` FOV-footprint cells only, never a catalog
  scan). Identity is index-based via `ObservationScheduler.MarkCrossIndicesSeen` (cross-indices), no
  name comparison; discovered companions are limited to NAMED non-star DSOs.
- **Sensor specs persist in the profile JSON** (`OTAData.CameraPixelSizeUm/SensorWidthPx/SensorHeightPx`),
  auto-captured on first camera connect (`EquipmentActions.CaptureSensorSpecs`, idempotent) -- NOT on
  the camera URI (re-discovery replaces URIs). Offline FOV: `ProfileData.PrimarySensorFovDeg`
  (`SensorFovExtensions`). No captured specs -> `FramingGroups` empty -> schedule byte-identical.
- **Wiring:** `PlannerActions.ComputeFramingGroups` runs from `RecomputeHandoffSliders` (every pin
  change, BEFORE its pinnedCount<2 early-return -- one pin still discovers neighbours) and
  `BuildSchedule` (which collapses via `FramingPlanner.CollapseForSchedule`);
  `AppSignalHandler.RefreshSensorFovAndFraming` pushes profile FOV on planner init / recompute /
  sensor capture. Sky-map group-frame rendering is deferred.

**SIMBAD merge v4 (catalog identity root-fix, shipped with this):** Messier numbers exist only as
cross-index aliases of NGC entries, so `MergeSimbadRecords`' bare `TryLookupByIndexDirect` filter
dropped SIMBAD records whose only main-catalog identifier is an M-number (Sh2-25 = "M 8" landed as a
standalone "Lagoon Nebula" duplicate). `ResolveToDirectIndex` follows the cross-index table now, and
the `bestMatches` computation is deliberately LINQ-free (per-record hot path). **Any change to the
merge logic requires bumping `SimbadMergeSnapshot.AlgorithmVersion` + re-running
`tools/precompute-simbad-merge.ps1`** (and `precompute-hd-hip-cross.ps1` when any `*.gs.gz` input
changed) -- the embedded snapshot's hash guard covers inputs + version, not code. Catalog refresh:
`Get-SimbadCatalogs.ps1` + `Copy-OpenNGC.ps1` (in `Astrometry/Catalogs/`) re-fetch sources and the
build's preprocess target regenerates `*.gs.gz`; all lzip I/O goes through the managed
`tools/lzip-util.ps1`, so there is **no external `lzip` binary anywhere**.

### Session

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator. **Single-mount /
multi-OTA invariant**: `Setup.Telescopes` is plural for dual-/triple-saddle rigs, but there is exactly
one `Setup.Mount`. All OTAs share pointing and the current target. Multi-OTA buys parallel capture
(per-OTA camera/filter wheel/focuser) and per-OTA focus/filter/baseline state. Any future "branch"
or "re-order" logic must operate on the OTA set as a single unit.

`RunAsync` workflow: `InitialisationAsync` → wait for twilight → `CoolCamerasToSetpointAsync` →
`InitialRoughFocusAsync` → `AutoFocusAllTelescopesAsync` → `CalibrateGuiderAsync` → `ObservationLoopAsync`.
See the class XML doc + the relevant `docs/plans/*.md` for details on each phase.

**Session failure surfacing (`ISession.FailureReason`):** when a run ends `SessionPhase.Failed`, the
session carries a plain-language, user-actionable reason (which device to check, what to do), surfaced
verbatim by the GUI notification feed, the hosted `/state` endpoint (`SessionStateDto.FailureReason`)
and the CLI. Throw `SessionFailedException(userMessage, inner)` for failures with a clear user
explanation (the inner exception carries the technical cause to the log); anything unhandled falls to
the generic catch ("Unexpected error: …"). Init device connects go through `ConnectOrFailAsync`
(`Session.Lifecycle.cs`), which names the device + telescope and is **deliberately fail-fast** -- a
device that cannot connect at init makes the night pointless (a flip-flat we cannot open leaves the OTA
blind), so fail there rather than discover it at dawn. The END-of-session flat block is the opposite:
best-effort, so a flats failure after a successful night never flips the session to Failed. Pinned by
`SessionFailureReasonTests`.

**Guider calibration pier-side invariant:** `CalibrateGuiderAsync` (`Session.Lifecycle.cs`) slews to
HA **−0.5h** (30 min *east* of the meridian, target still approaching transit) before calibrating, NOT
west. `HA = LST − RA`, so HA < 0 = east = *before* crossing. East keeps the GEM on its pre-flip pier
side for the whole calibration, so the learned Dec guide sense matches the side rising targets are
imaged on. Calibrating west (HA > 0) is past the flip boundary on the opposite pier side → inverted Dec
sense + ambiguous flip-edge → Dec runaway. Hemisphere-independent (only apparent left/right mirrors in
the south); pinned by a both-hemisphere `[Theory]` in `SessionLifecycleTests`.

`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (default 3 min,
covering slew + center + guider settle) before slewing to each target, via `WaitForScheduledStartAsync`
(`Session.Timing.cs`), so the scheduler's altitude-optimised slot times are honored -- on the same mount
clock (`GetMountUtcNowAsync`) as the loop condition. Same-Start / past-Start schedules (the hosted API
stamping `Start = now`, legacy callers, existing tests) short-circuit the wait and advance linearly.
Late starts proceed unclamped (the full `Duration` still runs); a lead-adjusted start beyond session end
skips the observation cleanly.

**Meridian-flip oscillation invariant:** `MeridianFlipDecision.DecideFlipAction` must be gated so the
imaging loop can never re-issue a flip it already performed. Two backstops, in order: `if (hasFlipped)
return Continue` (a per-observation flag set after a successful flip in `Session.Imaging.cs`), then
`if (pierSideChanged) return AlreadyFlipped`. The HA-zone switch only reaches `CommandFlip` when
`!alreadyOnCorrectSide`, where `alreadyOnCorrectSide` compares the current pier side against
`DestinationSideOfPierAsync(target)`. **Load-bearing on SkyWatcher**, whose pier side is the Dec encoder
(the MECHANICAL state, which tracking never changes), so an unflipped GEM tracking west keeps reporting
the state it was slewed in and a naive "flip when HA > 0" is trivially true forever -> stuck `Slewing`,
zero exposures. **Never re-introduce an HA-only flip check**; gate on the *destination* side + the
`hasFlipped` memory. Pinned by `MeridianFlipDecisionTests` + a `mountPort:"SkyWatcher"` loop test.

**Whether a flip HAPPENED is read off the IMAGE wherever the pointing state is `Computed`** (LX200 base,
SGP: derived from HA, so it turns over as the POINTING crosses whether or not the tube moved -- the
flip-SUCCESS twin of the trap above). `WCS.RotationDeg` measures it against the recentre's own solve,
which already happens; `MeridianFlipVerification.FromSolves` judges. **The likelier failure is
`AlreadyFlipped`, not the commanded flip**: such a mount reports the flipped side at the crossing, so the
loop skips the slew and images on upside down with the guider's Dec inverted; a field that did not turn
now makes the session COMMAND the flip. `Inconclusive` falls back to the mount's report, or every rig on
a coordinates-only solver fails every flip. `FakeMountDriver` has a mechanical tube state only MOTION
changes (**a sync must not touch it**) and `FakeCameraDriver` rolls off that, never off the report.
[docs/plans/meridian-flip-verification.md](docs/plans/meridian-flip-verification.md)

**Mount safety limits are NOT the meridian flip.** `MountLimits.Evaluate` (`Sequencing/MountLimits.cs`,
pure, beside `MeridianFlipDecision`) is the mechanical bound -- where the TUBE meets the pier or the
ground -- while a flip is a *scheduling* choice about a target still imaged from the other side. A rig
can have one, both or neither. Ported from GSServer's `CheckAxisLimits`; the derivations, phasing, live
verification and the wider GSServer sweep are in
[docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md) and
[docs/plans/gss-parity-audit.md](docs/plans/gss-parity-audit.md). The rules that bite:

- **The HORIZON test keys on HOUR ANGLE, not pier side** (`HA > 0` IS descending); **the MERIDIAN test
  is the opposite, an RA-AXIS test where the pointing state is load-bearing** (`Evaluate` reads the
  offset as `Normal ? -HA : HA`). Reading HA alone stopped every rig ~30 min after a flip.
- **`IMountDriver.GetAxisAngleAsync` is the MECHANICAL tier and WINS when present** (SkyWatcher only):
  fallback, never cross-check; `MountLimitVerdict.Basis` says which tier answered.
- **Only a MEASURED pointing state may drive it -- or one the SESSION verified.**
  `MountLimits.TrustedPointingState` hands `Evaluate` `Unknown` for a `Computed` driver; its
  three-argument overload takes `Session._verifiedPointingState` instead (latched, image-confirmed).
  `MountLimitWatcher` has no session and no latch, so it keeps the two-argument form.
- **Warn and act are a threshold plus a non-negative EXTRA**, never two absolute numbers (the two
  limits run in opposite directions), so warn-before-action holds by construction both ways.
- **`alreadyActed` is a latch and must downgrade to `Warn`, never clear**, or a park is re-commanded
  every poll tick and the slew restarts forever.
- **The meridian limit is in MINUTES and is the ULTIMATE CLAMP on the flip** (shares its unit with
  `MeridianFlipEarliestMinutesAfter`/`LatestMinutesAfter`, applied INSIDE `MeridianFlipDecision`).
  Horizon stays in degrees. Deriving the limit from the flip instead would let a preference walk a
  safety bound into the pier.
- **It is the TUBE that collides, not the counterweight**, so the threshold approximates a
  three-variable envelope (optics length x declination) set for the worst case the rig images.
- **Config lives on `ProfileData.MountLimits`**, projected onto `Setup`, never the per-run
  `SessionConfiguration` (must hold for a manual slew with no session). **Enforcement is in
  `PollDeviceStatesAsync`, not the imaging tick.** Breaching routes to `ImageLoopNextAction.LimitReached`,
  NOT `DeviceUnrecoverable`.
- **Parking is opt-in for both limits**: a park is MOTION across a path nothing has checked.
- **A mount that stops tracking without being asked is a LIMIT EVENT, not a fault**
  (`Session.DetectDriverEnforcedStop`), gated on not-slewing and debounced over two polls; an RA pulse
  on a STOPPED SkyWatcher axis runs constant-speed (`_raPulseOnStoppedAxis` masks it).
- **Two test traps:** `default(PointingState)` is `Normal`, which is SILENT for the meridian test, so
  an unconfigured mock passes with enforcement deleted; a test must place the mount by SYNC, not slew.
- **The verdict is telemetry** all the way to the Home card's Flip column, on CLASS transitions only.

**`MountLimitWatcher` (`Sequencing/`) is the enforcement half with no session running**: host-agnostic,
matches a connected mount by the hub's identity rule against every discovered profile's `Mount` each
5 s, skips a mount a session already leases. Driven as a `BackgroundService` in `tianwen-server` and
from `tianwen-gui`'s `Program.cs` (the GUI runs a bare `ServiceCollection`, so nothing else starts it).

Full derivations, the GSServer sweep and live verification: both docs linked above.

**A guide pulse is TWO methods, and picking the wrong one is silent.** `StartPulseGuideAsync`
(`IMountDriver` / `ICameraDriver` / `IPulseGuideTarget`) is the primitive: it commands the hardware
and RETURNS, with `IsPulseGuidingAsync` required to be true by then. `PulseGuideAsync`
(`PulseGuideTargetExtensions`, internal to the guider) is the composite: start AND wait, which is
what a caller almost always means. **Awaiting a start is not waiting for the pulse** -- reach for the
composite, and keep the primitive only for a caller doing something else meanwhile, which today
means driving the other axis. It stays off the public driver interfaces because the Alpaca plane and
the planetary recenter nudge genuinely want start-and-return.

**Every driver honours the primitive, SkyWatcher included.** Synta boards have no "pulse for N ms",
so the driver holds the duration in a background task split from the caller at *commanded*. Two
rules ride on it. **The in-flight count rises BEFORE the first write and falls only when the hold
ends** (GSS #109): a caller must never observe "no pulse running" for a pulse already issued, and it
is a counter so an overlapping RA+Dec pair clears only when both finish. And **a failed restore has
no caller to throw to**, so it parks in `_pendingPulseFault` and is re-thrown from the next
`StartPulseGuideAsync` *and from `IsPulseGuidingAsync`* -- a read that throws on purpose, so the
fault lands in the guide frame that caused it. Ordering makes that deterministic: the hold parks the
fault BEFORE lowering the count, and `IsPulseGuidingAsync` checks the fault BEFORE reading it.
Rationale in [docs/plans/gss-parity-audit.md](docs/plans/gss-parity-audit.md).

**A test for the non-blocking starter needs `ExternalTimePump` and a `[Fact(Timeout=…)]`**: under an
auto-advancing clock the hold can finish before the assertion runs, so the test passes against a
blocking driver too -- and the regression does not fail, it HANGS, because a blocking driver awaits
its own hold, which parks in the pumped clock's sleep waiting for an advance that comes after the
starter returns.
**No-astro-dark night-window fallback:** `SessionEndTimeAsync` (`Session.Timing.cs`) derives the dark
window via `ObservationScheduler.CalculateNightWindow`, which has a fallback chain (astronomical −18° →
amateur-astro −15° → nautical −12° → polar-night 24h). It must **never** demand `EventTimes(...).Count == 1`
for astronomical twilight: at high-summer mid-latitudes (e.g. 50.9°N at solstice the sun bottoms ~−15.7°)
the sun never reaches −18°, and the old strict read threw, killing the session at a site that simply has
no astro-dark. Pinned by a no-dark German-solstice test in `SessionLifecycleTests`.

**Focus-drift refocus trigger (trend, not single-frame):** the imaging loop compares
`FocusDriftDetector.EstimateTrendHfd` -- a least-squares fit of median HFD over the last
`SessionConfiguration.FocusDriftSampleSize` frames (default 30; only samples comparable to the
baseline participate -- same exposure + gain, enough stars -- and below `FocusDriftMinSamples` of them
it falls back to the newest frame's raw HFD) -- against the per-target baseline at
`FocusDriftThreshold` (the NINA `AutofocusAfterHFRIncreaseTrigger` analogue), so one bloated frame
(wind gust, passing haze) cannot trigger a spurious refocus. Two invariants: **the LSQ divisor is the
INCLUDED-sample count, not the window length** (dividing by the window length biases slope and
intercept whenever a sample is skipped -- the bug in the original inline implementation); and **the
history window is cleared on a drift-triggered refocus and on target change**, so the fit never sees
frames from a different focus position (a stale high-HFD window fitted against the fresh post-refocus
baseline re-triggers immediately -- refocus oscillation). The window is a `CircularBuffer<T>`, the
lock-free most-recent-N ring (torn-free `Snapshot`; the GUI render thread polls `Session.GuideSamples`
off the same type every frame). Pinned by `FocusDriftDetectorTests` + `CircularBufferTests`.

### Driver Resilience on the Hot Path

All driver calls reachable from the session hot path go through `Session.ResilientInvokeAsync(...)`,
a thin wrapper over `ResilientCall.InvokeAsync` with `OnDriverReconnect` as the fault callback. See
[`docs/architecture/driver-resilience.md`](docs/architecture/driver-resilience.md).

- **Never introduce a raw `await driver.X(...)` on the session hot path.** Grep PRs for regressions.
- **Pick the preset:** `IdempotentRead` (status/position polls, 3 attempts, exponential backoff +
  inter-retry reconnect), `NonIdempotentAction` (slew/exposure/dither, 1 attempt, pre-reconnect only),
  `AbsoluteMove` (focuser/filter-wheel, 2 attempts, target is absolute so re-issue is safe).
- **Telemetry polls go through `PollDriverReadAsync` / `PollDriverReadAsyncIf`** (capability-gated).
  These count consecutive per-driver failures and fire a one-shot proactive reconnect at threshold.
- **Escalation:** every reconnect bumps `_driverFaultCounts[driver]`; successful frames decay it.
  When any driver crosses `SessionConfiguration.DeviceFaultEscalationThreshold` (default 5),
  `ImagingLoopAsync` returns `ImageLoopNextAction.DeviceUnrecoverable`.
- **`CatchAsync` is still correct** for best-effort predicate decisions (`IsSlewingAsync`,
  `IsTrackingAsync`), FITS header reads, and finaliser steps.

**Sending a command is not the same as it having taken effect, and the distinction only matters for
the ones that fail BACKWARD.** Most driver commands fail forward: a lost slew is not a slew, a lost
guide correction is re-issued next frame, so best-effort plus a log is right. A command that returns
hardware to a SAFE state is the opposite -- it leaves the device running in a mode the driver
believes it has already cancelled, and nothing downstream can tell. In `SkywatcherMountDriverBase`
those are exactly three (`:I1` restoring the sidereal step period, `:K1`/`:K2` stopping an axis a
pulse started), and they go through `SendCommandVerifiedAsync`: classify the ack (`=` accepted /
`!X` refused / **null = no answer, a timeout and a different fact**), retry three times, then throw
`SkywatcherDriverException`. **Retrying is half the fix** -- a serial hiccup must not end the night,
so only an exhausted budget is a fault. Before this only a failed *write* surfaced; a refusal
reached `LogWarning` and a timeout reached nothing, so an unrestored `:I1` tracked RA at up to 2x
sidereal for the rest of the night and showed up solely as trailed subframes. **Do not widen this to
every command** (a recoverable hiccup would become a stopped guider), and when adding a serial
driver, ask which of its commands fail backward -- that set, and only that set, is worth a fault.
No new plumbing surfaces it: a throw from `StartPulseGuideAsync` becomes a `GuidingErrorEvent`, which the
session drains, logs and answers by restarting the guider. Pinned by `SkywatcherPulseRestoreTests`;
rationale in [docs/plans/gss-parity-audit.md](docs/plans/gss-parity-audit.md) Finding 3.

### Backlash Auto-Tuning

Every successful AutoFocus opportunistically infers per-direction backlash from the verification
exposure (no separate measurement routine, based on the cloudynights "no need to measure backlash,
just overshoot enough" approach). The hyperbola fit predicts HFD at `bestPos`; the verification frame
measures actual HFD at the mechanical position the focuser landed on. Inverting the fit
(`Hyperbola.StepsToFocus`) gives the lag, and `B = currentOvershoot + lag`. Per-focuser EWMA (α=0.3)
sized to `B × 1.5`. State persists to `Profiles/BacklashHistory/<focuserDeviceId>.json` and rounded
values mirror back to the focuser URI's `focuserBacklashIn`/`focuserBacklashOut` query keys at
session-end. Wire-up: `BacklashEstimator`, `BacklashHistoryPersistence`, `Session.Focus`,
`EquipmentActions.SaveBacklashEstimatesIfChangedAsync`.

### Polar Alignment

`PolarAlignmentSession` (`TianWen.Lib/Sequencing/PolarAlignment/`) is a SharpCap-style two-frame
plate-solve routine that runs **outside** of `Session.RunAsync` against a manually-connected mount.
See `docs/plans/polar-alignment.md` for the math/algorithm.

### Flat-Frame Acquisition (automation)

`Session.TakeFlatsAsync` (`Session.Flats.cs`) is the automated end-of-session flat block: it runs in
`RunAsync` after `ObservationLoopAsync` on **normal completion only** (abort/exception skips to
`Finalise`) and **before** `Finalise` warms the cameras -- so flats are taken at the imaging setpoint
temperature -- gated on the opt-in `SessionConfiguration.TakeFlatsOnSessionEnd`. The same routines are
reachable on-demand outside a session via `ISession.RunFlatsOnlyAsync` -> CLI `tianwen flats` /
`POST /api/v1/session/flats` (source/period strings map through the shared `FlatRunParsing`, one parser
for CLI + API, mirroring `EnhanceOptions.TryParse`). **Capture flows, the exposure solvers, the
cover-capability model, the GUI mode and the tests are all in
[`docs/plans/flat-frame-automation.md`](docs/plans/flat-frame-automation.md).** What bites before you
open it:

- **`SessionConfiguration.FlatSource` has exactly two values**, `Calibrator` (default) and
  `TwilightSky` -- **a manual hand-switched panel is NOT a third one**, it is a `ManualCoverDevice`
  captured through the **same** `Calibrator` path (one path for every `ICoverDriver`, device kind
  invisible to `TakeFlatsAsync`). A motorised cover with no panel, or no flat device at all, is
  skipped with a warning.
- **Auto-exposure is a pure solver, and the two paths differ in how often it is asked.** The panel
  path converges once per filter (metering frames discarded); the sky path re-meters EVERY frame
  (`SkyFlatExposureSolver.Decide`: `Capture` / `Adjust` / `Wait` / `Stop`) because sky brightness
  ramps.
- **Sky flats point near the zenith tilted anti-solar and turn tracking OFF** (field drifts, stars
  average out, no dither slews); covers are **opened**, the opposite of the panel path. Two
  independently-gated hooks run both windows in one night: dawn at end-of-session, dusk at session
  start before wait-for-dark (`TakeSkyFlatsAtDusk`, pre-AutoFocus, a known focus-match tradeoff
  accepted for the cloud insurance).
- **Output contract, identical for all sources:** `IMAGETYP/FRAMETYP=Flat` under
  `Flats/<date>/<filter>/Flat/` -- the path is **cosmetic**, `MasterFrameBuilder` matches by FITS
  headers (`MasterGroupKey`). **Never make flat-master matching depend on the path.**
- **`RunFlatsOnlyAsync` connects a subset** (camera/focuser/filter wheel/cover, mount only for
  sky-flats, **never the guider**); `FinaliseFlatsAsync` is its focused `Finalise` counterpart.
- **The GUI surface is a MODE on the Live Session tab, not a tab** (`LiveSessionMode.Flats`).
  `FlatsBootstrapper` sets `LiveSessionState.ActiveSession` **without** `IsRunning`, which is exactly
  why hardware guards must ask `DeviceOwnershipGate` and never a UI flag (see Device Ownership
  above).
- **Session->UI user-prompt channel** (`ISession.PromptRequested`). **With no subscriber the session
  answers `SessionConfiguration.UnattendedPromptResponse`, which defaults to `Decline`** -- proceeding
  would assert a physical act nobody performed, and blocking forever leaves the rig exposed at dawn.
  Operator-invoked runs opt into `Proceed`; the flat routine prompts only on a
  present-but-`!CanControlBrightness` calibrator.
- **Native Gemini FlatPanel Lite driver** (`AddGemini()`): an ASCOM-free serial `ICoverDriver` for a
  driver-controlled panel with no flap. Wire spec + its two silent traps (probe-time DTR,
  `SerialPort.IsOpen` not being a liveness signal):
  [docs/architecture/gemini-flatpanel-lite-protocol.md](docs/architecture/gemini-flatpanel-lite-protocol.md).

### Deep-Sky Stacking + Enhance Pipeline (`TianWen.Lib.Imaging.Stacking`)

`StackingPipeline.RunAsync` (CLI `tianwen stack`) is the deep-sky integration pipeline:
scan DataRoot -> build bias/dark/flat masters -> per light group register (star-quad match)
-> integrate (strategy auto-picked: Bayer drizzle on RGGB with >= `DrizzleOptions.MinFrameCount`,
else AHD + sigma-clip rejection) -> `MasterPostProcessor.WriteMasterAsync` (plate-solve, SPCC
WB, FITS + autocrop + optional enhance + previews). Sibling of, but **completely separate
from**, the Planetary stacker below. **Full flowcharts, the render model, the two opt-in display
stages and the parity notes:
[`docs/architecture/stacking-render-pipeline.md`](docs/architecture/stacking-render-pipeline.md).**

**Output contract is by data type -- do not regress it:**
- **Linear (canonical)**: FITS, full-frame `master_<slug>.fits` AND cropped `_autocrop.fits`;
  `--output-format exr` mirrors both. Full-frame linear pixels live only here.
- **Display / stretched (ALWAYS autocropped)**: the PNG quick-look and `--split-plates` TIFFs.
  `MasterPostProcessor`, NOT the CLI, renders ONLY the autocrop -- the rendered image is its own
  stats source, so WB / bg-neut can never be poisoned by partial-coverage / NaN-ring edges.

**Comet / moving-target integration (`stack --comet [designation]`)** registers on the BODY (comet
sharp, stars trail); the rate derives from the frames (`OBJECT` + site + exposure epochs ->
topocentric JPL Horizons track fitted through the reference WCS), `--comet-rate dx,dy` is the offline
override. **Read [docs/plans/comet-integration.md](docs/plans/comet-integration.md) before touching
this** -- it carries the design, every measurement, and thirteen traps that break the model SILENTLY
(pooled amplitude, a starless-plate nucleus, a whole-pixel model centre, a slope fitted after the
pedestal, five epoch/NaN/calibrator preconditions). Four that reach beyond the feature:

- **Registration is the ONE place the pipeline plate-solves anything but the finished master** -- the
  rate is needed *while* integrating, too late once the master itself solves.
- **The star layer SUBTRACTS the body; it does not exclude or reject it,** and the model MUST come
  from star-removed plates. Kappa-sigma cannot substitute (the body sits in a third of the frames,
  inflating the very sigma meant to catch it), and a model differenced from stars-still-in plates
  smears every star into a dark streak.
- **A NaN in a rejection sample column disabled rejection everywhere, in every rejector** (NaN
  comparisons are all false), so canvas edges had never been rejected -- fixed via
  `PixelRejection.MarkAbsent`. Not comet-specific: it affected every stack.
- **Judge these layers at 1:1, never by a band median** -- edge bars, correlated-noise texture and
  trail streaks all hid behind a clean radial profile; use p0.5/min for streaks, and never compare
  differently-integrated layers against each other.
**Provenance skip (never re-ingest our own outputs).** The scan drops any TianWen-produced FITS
(`STACK_N > 0` OR a TianWen `SWCREATE`, gated by `--include-integrations`) so a processed image parked
alongside the lights is never re-stacked as a fresh sub. Markers, the ghost-master failure mode and the
`ScanSummary` reporting:
[docs/architecture/stacking-render-pipeline.md](docs/architecture/stacking-render-pipeline.md).

**A CAPTURED frame that is not a light says so in `IMAGETYP`, and never relies on the skip above.**
`SessionConfiguration.SaveIntermediates` (default OFF, one switch, `Session.IO.cs`'s
`WriteIntermediateFrameToFitsFileAsync` the one write path) keeps the frames a session takes to
*measure* something and would otherwise release unseen, under
`<output>/Intermediates/<date>/<filter>/<frame type>/[group/]`:

- **`FrameType.Focus`** -- every auto-focus V-curve rung plus the verification exposure, grouped one
  folder per run (`ota<n>_<runStart>/`, a directory rather than a filename convention because our
  timestamp format contains underscores). This is a real defocus ladder for the deconvolver corpus;
  [docs/plans/ai-denoise-deconv.md](docs/plans/ai-denoise-deconv.md) 2.1b carries the measurements
  that say why the archive could not supply one.
- **`FrameType.Scout`** -- the FOV-obstruction probe and nudge-test frames, kept whatever the star
  count (a zero-star scout is the interesting one), which is what answers "why did it think the field
  was blocked?" the morning after.

**Each kind gets its OWN frame type rather than one `Intermediate`,** because path is cosmetic here as
everywhere and headers are truth: collapse them and the only way to tell an AF rung from a scout is
the folder. Exclusion from stacking is by frame type -- the scan and the dataset builder both select
`Light`, so these drop out by the same mechanism that excludes darks, NOT by the provenance heuristic
(authorship) or the folder. A scout is the one that most needs this: it is in focus and points where
the lights point, differing only in exposure, so nothing about the pixels would stop a scan ingesting
it. **Never widen a consumer's filter to admit `Focus` or `Scout`.**

Deliberately NOT covered, so the switch can never fill a disk: condition-recovery test exposures
(unbounded while cloud lasts), the rough-focus sweep, plate-solve frames and flat-metering frames.
Each is one `WriteIntermediateFrameToFitsFileAsync` call away if it earns its keep.

**`--enhance`** runs `SharpenPipeline` on the master ONCE, writing `_sharpened.fits` (never
overwriting the linear masters); deblurrer-aware (RC-Astro present -> BlurX-first PixInsight-OSC
flow, no stellar-sharpen; none -> SAS-shaped remove/sharpen/deconvolve). `--split-plates` is the
SAME AI pass exporting the kept stars/starless plates as edit-ready TIFFs -- NO second enhance run.

**Render model: WB once, per-plate self-stretch (the PixInsight OSC order).** ONE SPCC white balance
on the enhanced master; each plate then computes its OWN background-neutralisation + MTF from its
own pixels -- sharing only WB is load-bearing, since grafting the master's bg-neut onto a plate whose
background differs double-corrects it into a colour cast (the original `--split-plates` regression).

**Three colour defects fixed on the SWAN/10P sets, measured in
[docs/plans/comet-integration.md](docs/plans/comet-integration.md) (colour section):**

- **SPCC's clip test reads the frame's OBSERVED peak from the pixels, never `MaxValue`** -- a
  rewrapped `MaxValue = 1.0` is a display convention, not a saturation level (10P dropped 545 of 545
  stars before this fix).
- **SPCC's matcher claims each catalogue star ONCE, brightest detection first** -- a deep master
  out-detects Tycho-2, so an unclaimed nearest-neighbour probe measures a random distance and hands a
  faint neighbour a B-V that isn't its own.
- **The stacking normaliser anchors every frame on its PEDESTAL (`Image.Pedestal`), never a pixel
  statistic** -- a per-channel MINIMUM floor let one hot pixel swing a whole frame's gain (red
  wandered x3.7). Absolute normalised levels quoted before 2026-08-27 are in the old units.

**SPCC is BROADBAND-ONLY; a narrowband master has no colour path at all.** Do not extend it by
swapping a narrow passband over the existing Pickles SEDs -- a spectral *type average* cannot tell
Ha absorption from emission over 3 nm. **Narrowband SPCC is BLOCKED on data, not maths** (needs
per-star Gaia DR3 `xp_sampled` spectra, ADR-3). **Naive HOO is rank-deficient** (`G = B = OIII`
renders uniformly teal by construction, not by bug). Algorithms + thirteen ADRs:
[docs/plans/narrowband-colour.md](docs/plans/narrowband-colour.md).

**The filter-curve matcher must never answer with a brand, nor a MORE SPECIFIC product.**
`FilterCurveDatabase` matches by token overlap over 183 curves; three gates -- a two-token
(BRAND+CHANNEL) key must be covered in FULL, an unmatched token absent from every other filter name
refuses the match (by document frequency, not a stop-list), and a two-sided token difference refuses
it (a ONE-sided difference still resolves, e.g. `Baader R CCD 31mm`). Re-run
`ReportKnownLightPollutionFilters` after every added curve -- it captures a new curve's own siblings
too. Seven standalone light-pollution curves are ours, digitised via `tools/digitize-filter-curve/`
(the **`digitize-filter`** skill); the `L-eNhance` tri-line (Hb 486.1) trap, gate table and coverage:
[docs/known-limitations.md](docs/known-limitations.md).

**Zero-pedestal render (do not regress).** Shadows derive from the pedestal-SUBTRACTED median -- a
no-op on raw masters, but an enhanced (GraXpert-flattened) master needs
`MasterPreviewRenderer.WithZeroPedestal` or subtracting the floor explodes or blacks out a drizzle
frame.

**Unified display render** (`MasterPreviewRenderer` + `StretchSolver`, CPU-only, in `TianWen.Lib`) is
driven in-pipeline by `MasterPostProcessor`. **The CLI renders nothing** -- it only sets
`RenderPreviewPng`, writes EXR, and prints the SPCC summary; the viewer forwards to the same
`StretchSolver`, keeping it the single producer.

**Two opt-in DISPLAY stages** (`--saturation`/`--contrast-boost` via `Image.MaskedBoost`, and
`--output-format uhdr`) touch **only the display raster**, never the linear masters or split-plate
TIFFs. **Never apply the mask primitives to a LINEAR master** (the luminance mask degenerates to ~0
everywhere). Both stages' invariants + tests: the architecture doc above.

**Stellar-sharpen is opt-in** (default OFF) and **hard-skipped when a deblurrer is live** -- BlurX
already tightens stars, and the SAS sharpener turns tight cores into square white blocks. RC-vs-SAS
roles: [docs/plans/rc-astro-enhancers.md](docs/plans/rc-astro-enhancers.md).

**CLI flags + viewer Enhance action.** `--ai-backend auto|rc|sas|n2n` + tuning flags parse through
the shared **`EnhanceOptions.TryParse`** (also used by the server endpoint) into an immutable
`EnhanceOptions` -- no mutable settings singleton, so parallel enhances cannot tear.
`tianwen-fits`'s interactive Enhance action runs off the render thread via
`ViewerController._enhanceTask`; the GUI has no document-viewer tab yet.

**Server enhance endpoint.** `POST /api/v1/image/enhance` + `GET .../status`, single-flight, tied to
`ApplicationStopping` not the request:
[docs/architecture/hosting-api.md](docs/architecture/hosting-api.md).

### Planetary Lucky-Imaging Stack (`TianWen.Lib.Imaging.Planetary`)

A CPU-first planetary stacker, **completely separate** from the deep-sky `Imaging.Stacking` pipeline
(star-quad align + sigma-clip rejection don't apply to a featureless disk). Plan + status:
[`docs/plans/planetary-stacking.md`](docs/plans/planetary-stacking.md); the live-capture path in detail
(drivers, controls, the fake's noise model, the breadcrumb trail):
[`docs/plans/live-planetary-capture.md`](docs/plans/live-planetary-capture.md).

- **Batch** (`LuckyImagingStacker`, CLI `tianwen planetary-stack`): grade frames by sharpness
  (`IFrameQualityEstimator`, Laplacian default) -> keep the best N% -> disk-COM + phase-correlation
  global align (`GlobalAligner`) -> feature-driven alignment points + per-AP displacement-mesh warp ->
  per-AP quality-weighted split-CFA integrate -> **Bayer drizzle** (forward-scatter raw CFA through the
  AP mesh) -> demosaic-once -> 6-level **wavelet sharpen** (`WaveletSharpen`, a-trous;
  `PlanetaryDefault`/`Bandpass`/`Combo` presets).
- **Live (`RollingWindowStacker`)**: the streaming counterpart of `StackGlobalAsync`, over a
  **frame-capped** sliding window (`MaxWindowFrames`, default 500 -- a dense capture would otherwise
  pull the whole capture into a 5-min window and make every update a full batch stack). O(pixels)
  `add`/`evict`: eviction re-folds a frame's cached contribution with a **negated weight** (the
  accumulate kernel is linear, so +w then -w cancels exactly). The hot path is **align-bound**
  (~85-89%), so `GlobalAligner` caches the reference tile's forward FFT once.
- **`PlanetaryMaster`** is the single shared "accumulators -> master" finalize (normalize + CFA-merge +
  MHC demosaic), so the batch and live masters can never drift.
- **Live capture, three rules that bite.** (1) **Camera ADU frames normalise to [0,1] at the stream
  boundary** (`LiveCameraFrameStream.DeepCopy`), the convention the SER bridge also follows, so the
  coverage-normalised master is display-ready -- an un-normalised ADU master clamps to white. (2) A
  colour (RGGB) sensor's video frame is a 1-channel **Bayer mosaic**, and the stream layout is derived
  from the ACTUAL frame (1ch+RGGB -> SplitCfa -> per-photosite stack -> single demosaic -> colour
  master), **NOT** the camera's `SensorType`. (3) Exposure / gain / ROI size / ROI pan are live-tunable
  during capture, and **no driver call crosses onto the render thread**: the render thread stages the
  change and the capture loop drains + applies it. Planetary preview defaults to **linear**
  (`StretchMode.None`).
- **Live-capture drivers + the COM recenter loop are SHIPPED**: `FakeCameraDriver` (synthetic drifting
  disk, full ROI-jog), `CanonCameraDriver` (FC.SDK Live View incl. the 5x/10x EVF-zoom regime and its
  pannable crop), and `PlanetaryRecenterController.Decide` (pure per-axis-deadband damped ROI jog, plus
  a coarse mount nudge on an edge-blocked axis via `MountActions.PulseGuideArcsecAsync`).
  `DALCameraDriver` (ZWO/QHY native raw video) is Phase D, not implemented. **Read the plan doc before
  touching the Canon path** -- it is a list of five things that fail SILENTLY. Auto-recenter defaults ON
  (ROI-only, zero mount disturbance); mount jog is opt-in OFF and its **sign is uncalibrated**.
- **Benchmarks/profiling**: `PlanetaryStackBenchmarks` / `PlanetaryMasterBenchmarks`, and
  `dotnet run --project TianWen.UI.Benchmarks -- profile planetary [--frames N]` for a per-stage
  breakdown plus a tight loop for `dotnet-trace`.

### AI Image Enhancement: SETI Astro (ONNX) + RC-Astro (CLI)

`SharpenPipeline` (`TianWen.Lib/Imaging/Enhancement/`) orchestrates role-typed enhancers
(`IStarRemover` / `IStellarSharpener` / `INonStellarDeconvolver` / `IDenoiseEnhancer` /
`IGradientCorrector`) over an immutable `SharpenStep[]` program. Three backends implement those roles:

- **SETI Astro (SAS Pro AI4)** -- plain ONNX loaded in-proc via ONNX Runtime
  (`TianWen.AI.Imaging/Onnx/*`, `AddTianWenAi()`). Models under `%LOCALAPPDATA%\TianWen\models`
  (`tools/tianwen-ai-models-fetch.ps1`).
- **In-house N2N denoiser** (`N2nDenoiser`; OSC-only, throws on mono), whose weights ship **in this
  repo** at `src/TianWen.AI.Imaging/models/` as a **plain git blob, not an LFS object** --
  `.gitattributes` exempts that directory from the repo-wide `*.onnx` rule, so a checkout has the real
  weights with or without git-lfs (`ModelResolver` still refuses a pointer stub, so the failure mode
  stays a logged skip, not an ORT protobuf error). **Three ways in, deliberately tiered:**
  `--ai-backend n2n` selects it per enhance for the denoise role; **Auto rescues with it** when the SAS
  AI4 weights are absent and the input is OSC at the default variant (it replaces a crash, never a
  measured backend's result -- with SAS weights present Auto is byte-for-byte the old path); and
  `AddTianWenN2nDenoiser` makes it the `IDenoiseEnhancer` unconditionally. It is deliberately **not**
  Auto's preferred denoiser, never having been compared against AI4 on the enhance pipeline's own job.
  The user-facing strength dial is a **blend**, with the graph's own `strength` pinned to 1.0 (the
  conditioning-plane dial was measured and rejected). **The net works in the exporter's MTF-stretched
  domain, not in linear units**: every training tile was stored after
  `ChunkedNafnetRunner.ApplyInputStretch`, so `N2nLinearRunner` applies that same call to the whole
  frame, runs, and inverts with `MtfUnstretch` before blending; the boundary contract stays linear
  in, linear out. For two weeks it fed the frame verbatim on the belief the tiles were linear, 100x
  below the training band, which removed a tenth of the noise and cut every star's peak by 30 percent
  at every brightness while no metric looked. A parity fixture that runs the same bytes on both sides
  cannot see a domain error; verify the domain a runner hands a graph against the domain the training
  bytes are in. Design + measurements:
  [`docs/plans/osc-narrowband-denoiser.md`](docs/plans/osc-narrowband-denoiser.md) section 1o and
  [`docs/plans/denoiser-training.md`](docs/plans/denoiser-training.md) H0 / section 9.
- **RC-Astro (BlurX / NoiseX / StarXTerminator)** -- `AddRcAstroAi()`. Its `.onnx` files are
  **encrypted at rest** (the license forbids extracting the weights), so they are driven through the
  `rc-astro` CLI's `--json` NDJSON protocol, **never** loaded into ORT: `RcAstroEnhancerBase` writes the
  plate to a temp FITS (BITPIX=-32), runs the product, parses the event stream and reads the result
  back. RC normalises to [0,1] internally, so no rescaling. Role mapping: sxt -> `IStarRemover`, nxt ->
  `IDenoiseEnhancer` (noise-adaptive `--dn`), bxt -> `INonStellarDeconvolver` (on the starless plate,
  auto-PSF). Details: [`docs/plans/rc-astro-enhancers.md`](docs/plans/rc-astro-enhancers.md).

**Selection is RC-preferred, deferred, and license-gated.** `AddRcAstroAi()` calls `AddTianWenAi()`
then `Replace`s the three RC-servable roles with **`DeferredEnhancer` proxies**: the RC-vs-SAS choice
AND its blocking license probe run on the FIRST `EnhanceAsync`, never at DI registration/resolution --
so composing a service collection (or resolving `SharpenPipeline`) spawns **no** `rc-astro` process. RC
wins only when the CLI is present (`RcAstroCli.LocateExecutable`: `RC_ASTRO_CLI` env -> documented
per-OS default install dir -> PATH; RC-Astro writes **no** registry footprint, so no
Uninstall/App-Paths probe) AND the product is licensed (cached); else the SAS ONNX enhancer is used.
`IStellarSharpener` / `IGradientCorrector` stay SAS (no CLI equivalent).

**A vendor's weights are read WHERE THE VENDOR PUT THEM, never only where a dev script copied them.**
`ModelResolver` probes its three model directories, then GraXpert's own cache (auto-detected, no
override -- the version subdir is not knowable ahead of time, so a search directory structurally
cannot reach it) for `graxpert_bge.onnx`. A packaged install cannot run the repo-relative dev script
that used to be the only bridge, which is exactly how the Store build shipped an Enhance failure
against 207 MB of GraXpert weights already on disk. **Never make a shipped capability depend on a
script only a checkout can run.** Mechanism + the SAS-Pro sibling pattern:
[docs/plans/ai-enhancement.md](docs/plans/ai-enhancement.md).

### Classical Background Extraction (`TianWen.Lib.Imaging.BackgroundExtraction`)

`ClassicalBackgroundExtractor` is the AI-free gradient corrector: a robust iterative degree-2 polynomial
on a block-mean working grid, with an optional inpainted low-pass surface on its residual
(`SurfaceRefinement`), applied in LINEAR with the model's median added back per plane. It is both
`IBackgroundExtractor` (headless, options per call) and `IGradientCorrector`, and `AddTianWenAi()`
registers `FallbackGradientCorrector` for the pipeline role: GraXpert when `graxpert_bge.onnx` resolves,
this otherwise, so a machine without GraXpert still flattens. Design, the reference review it came from
and the measurements: [docs/plans/background-extraction.md](docs/plans/background-extraction.md). The
rules that bite:

- **Every threshold is in noise units of the WORKING grid**, a block mean of `Downsample^2` pixels, so
  "2 sigma" is two sigma of a noise four times smaller than the frame's. A "dim plateau" at 1.5
  frame-sigma is 6 working-sigma and is rejected without any polygon.
- **The polynomial stage iterates to convergence; the surface stage runs ONCE.** The surface's
  residual is a high-pass, a smooth feature of scale s leaks about (sigma_blur/s)^2 of its amplitude into
  it, and on a deep master that beats the block-mean noise at the peak of an ordinary dome: an iterated
  sigma rejection carved the peak out and the harmonic hole-fill widened the hole every pass (7.1e-4 RMS
  model error against 1.1e-4 single-pass). Never close that loop.
- **Stars are not structure, and neither is a star's blur shadow.** Structure seeds exclude COMPACT
  pixels (a one-working-pixel high-pass a star fails and a nebula passes), and compact is the positive
  high-pass core plus its eight neighbours only; flagging the negative side too cost a third of the grid.
- **A one-channel `SensorType.RGGB` input is a mosaic and is fitted per photosite colour** (split, four
  planes at half the factor, merge). One plane on a mosaic removes the average gradient and leaves each
  colour's own behind.
- **The level is per plane and the pedestal field is untouched.** One scalar level would equalise the
  channels, which is background neutralisation and a separate step; accumulating the level onto the
  pedestal is what forced `WithZeroPedestal` on GraXpert-flattened masters.
- **Neither structure threshold is a tuning knob, measured over 118 real masters, and the switch that
  matters is `SurfaceRefinement`.** `StructureThresholdSigma` (3) is safe anywhere in 2 to 6 and even
  deleting the mask moves a real model by 0.03 sigma at p95, because it was tuned on a synthetic field
  far denser in bright stars than a deep master; `SurfaceStructureThresholdSigma` (10) is INERT across
  5/10/20/40 (identical on every metric) since a real surface's high-pass residual never reaches five
  sigma, though it is wired and binds at 1.5. Turning `SurfaceRefinement` ON moves the model 0.40 sigma
  RMS at p50 and drops the kept fraction 0.795 to 0.581 against a median gradient of 2.32 sigma, so it
  stays off: a flexible surface hollows a frame-filling nebula. Pinned by
  `ClassicalBackgroundExtractorTests`.
- **`tianwen dataset gradient-report` is the measurement** (`DatasetGradientReport`, append-only
  `stats/gradient-masters.jsonl` + a rewritten `stats/gradient-report.md` per bake, detached via
  `tools/run-gradient-report.ps1`): per master the model's amplitude in the plane's own sigma, shape,
  brightening direction, and the horizon and Moon geometry from one plate solve. **A TianWen master's
  canvas ring is EXACT ZERO where no frame covered it** (0.3 percent of a frame at the median) and must
  be masked to NaN before fitting, or the fit chases the edge. What it found:
  [docs/plans/gradient-remover-training.md](docs/plans/gradient-remover-training.md) H1.

### Hosting API (`TianWen.Hosting` + `TianWen.Server`)

Headless REST + WebSocket API plus an ASCOM Alpaca device plane on one ASP.NET Core host: **native v1**
(`/api/v1/`, multi-OTA, camelCase, POST mutations) is the session plane; the **ninaAPI v2 shim**
(`/v2/api/`, OTA[0], PascalCase, GET) is compatibility. Run `dotnet run --project TianWen.Server` or
`tianwen-server [--port 1888]`. **Endpoint inventory, the Alpaca plane, the enhance endpoint and the
full native-AOT rules, and the reasoning behind each rule below:
[`docs/architecture/hosting-api.md`](docs/architecture/hosting-api.md).** What bites:

1. **A pushed schedule beats the target queue.** `POST /session/schedule` preserves per-filter plans,
   the planner's `Start` and `AcrossMeridian`; `PendingTarget` carries none and `/session/start` stamps
   `Start = now`. Never route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** `EventBroadcaster`
   restores the guarantee (no WebSocket client -> `SessionPromptEventArgs.DefaultIfUnanswerable` at
   once; one attached -> hold with no timer, liveness is the only bound). **Any new subscriber on a
   headless path owes the same.**
3. **Numeric enums on the wire** (no `JsonStringEnumConverter` on `HostingJsonContext`): a `required`
   enum on a request DTO is hostile to hand-written callers, default it.
4. **Previews go through the shared stretch, never a private one.** `PreviewEncoder` runs
   `StretchSolver` + `Image.RenderStretchedRgba`, the same pipeline as the GPU viewer and the TUI. The
   shim once divided by `Image.MaxValue` and called it an auto-stretch, which renders a linear sub
   near-black. It also only ever READS the session frame; `LastCapturedImages` pins a recycled buffer.
5. **The Alpaca plane is a DEVICE plane and cannot become the session plane.** Ownership there is the
   hub lease, not an Alpaca policy: actuation and `Connected=false` answer `0x40B`, reads and
   `Connected=true` always pass; never make the plane read-only during a session. Device numbers come
   from the **ACTIVE PROFILE, in profile order**, never from discovery.
6. **AOT is verified by `dotnet publish -r <rid>`, not `dotnet build`.** RDG stays enabled in the
   **`TianWen.Hosting` library**, both JSON contexts stay registered via `ConfigureHttpJsonOptions`,
   and **never reintroduce a `ResponseEnvelope<object>` or an anonymous-type payload**.

### Remote Rigs (mirror another node's session "as if local")

[`docs/plans/remote-profile.md`](docs/plans/remote-profile.md) is complete P1-P5 and holds the design,
the Home-tab decisions and every measurement; the pieces are `TianWen.Hosting.Contracts` (wire DTOs +
`HostingJsonContext`) and `TianWen.RemoteClient` (`TianWenNodeClient`, `TianWenEventStream`,
`RemoteSessionMirror`). The rules:

- **The overlay model is the whole design: selecting a rig changes what you look at, never what this
  node owns.** A remote connect is a read-only HTTP mirror (no lease, no hardware); the single-session
  invariant is per NODE; `RemoteRigBinding` persists on a stable `NodeId`, **never** an address
  (`LastAddress` is only a hint, so a DHCP-lease change reconnects on its own).
- **One `LiveSessionState` per view context**: **Active** (renders), **Local** (this node's own
  hardware -- every quit/park/disconnect path belongs here), **All** (poll + redraw). Reaching for
  Active where Local is meant parks the local mount from a remote view.
- **`ISession`/`ISessionTelemetry` split**: telemetry is the wire-crossable read surface, `Setup` stays
  local; `RemoteSessionMirror` implements telemetry, so the Live Session and Guider tabs render a
  remote rig with no knowledge it is remote.
- **Two wire traps:** never `required` on a nullable wire property (`WhenWritingNull` omits it); a
  non-finite double is a bodiless 500 for the WHOLE endpoint, so route through `ForWire` (derived from
  `NumberHandling`).
- **Polling is authoritative; the WebSocket is a latency hint.** `NodeResult<T>` carries a status code
  because **404 is not unreachable**; `LastContactUtc` stamps there too; the outstanding prompt rides
  on `/session/state`.
- **Every request has a time budget** (state 5 s, preview 30 s, control 10 s; 60 s `HttpClient`
  backstop, a dark rig black-holes packets). Budget expiry and caller cancellation both surface as
  `OperationCanceledException` meaning opposite things: keep `when (...)` filters on the **original**
  token, never the linked one.
- **Profile switching is gated** (`ProfileSwitchGate`) while connected/running or where drivers would
  strand in the hub.
- **The Home tab** (`Ctrl+H`) is a read-only PROJECTION: `HomeBoard.BuildCards` draws only from the
  `ImmutableArray<RigCard>` snapshot, never `RemoteRigRegistry` or a live state; a prompt's age is the
  raising node's `RaisedUtc`; `GET /api/v1/session/profile` (not the LAN beacon) reports a node's
  profile; a dark rig is polled less often (doubling to a 30 s cap, per-mirror loop, a 404 resets it).

**Sidebar icon convention.** Every tab glyph is a bare codepoint with no variation selector (VS16
emoji render inconsistently), written as backslash-U escapes. **Adding a tab touches six places:**
the `GuiTab` enum, `TabOrder`, `TabChrome`, the Ctrl+letter map, two `VkGuiRenderer` switches, and
`GuiTabNavigationTests.TabOrder_IsTheSidebarLayoutOrder` (pins the order, will go red by design).

### Colour Theme (`GuiTheme`, four states incl. Night)

`GuiTheme` (`TianWen.UI.Abstractions/GuiTheme.cs`) owns the one palette; `UiThemeState` is **System /
Light / Dark / Night**, `GuiTheme.Apply(state, desktopIsDark)` swaps it in as one reference write and
`Palette` is one reference read. The source XML comments carry the rationale (scotopic numbers
included); design + phasing: [docs/plans/colour-theme.md](docs/plans/colour-theme.md).

- **Anything that CACHES a projection of the palette owes `GuiTheme.PaletteGeneration` in its cache
  key** (the planner chart's GPU texture kept the old palette after F12; `Apply`'s `bool` return
  cannot fix a cache the consumer never asked).
- **Night is not a darker Dark and is unreachable from `System`** (F12 toggles it): blue is **zero**,
  green only buys hue separation, red-on-black caps at 5.25:1, so anything READ uses `BodyText` and
  `DimText` is chrome only. Derive new colours from the palette, never a literal.
- **Judge Night at night**: anchor the clock with `TIANWEN_NOW` before concluding a Night colour is
  wrong.

98 raw colour literals remain of an original 317, all categorical or two-trace series by design.

### Desktop Shell: File Types, the Single-Instance Hand-off, and the MSIX Store Lane

`tianwen-fits` ships to the Microsoft Store as **Astro Photo Viewer** (the exe keeps its name), which
is what makes the file associations worth having and what makes every double-click a fresh AOT process
unless the file is handed to the window already open. **Layering, the two CI lanes, why the Store
rather than a signed installer, the activation bug that shipped and both MSIX traps:
[`docs/architecture/desktop-shell.md`](docs/architecture/desktop-shell.md)**; packaging in
`packaging/windows/msix/` (own README); thumbnails in
[docs/plans/explorer-thumbnails.md](docs/plans/explorer-thumbnails.md). The rules:

- **The gate is folder-scoped and the pipe IS the lock** (`InstanceGate`, SharpAstro.AppShell; one
  primary per normalised folder, no registry of instances). `--new-window` and
  `TIANWEN_FITS_SINGLE_INSTANCE=0` opt out; a bare launch never hands off.
- **Failure is never fatal**: every failed path opens the document in this process.
- **Re-bind on a folder change** (`PumpInstanceGate` releases the old channel, claims the new one,
  holds none if taken); the open dialog and a drag-drop both rescan.
- **Activation is `sdlWindow.Activate()`**, AppShell's `IActivatableWindow` extension, never a local
  copy: raise alone leaves a minimised window off-screen, restore-first un-maximises (shipped to the
  Store as "opening a second file un-maximises my window"). It restores ONLY if minimised.
- **Two silent MSIX traps:** no `resources.pri` = icons at the wrong SIZE and nothing else;
  `-AllowUnsigned` cannot install a Store identity (0x80073D2C), sign locally with a certificate whose
  subject matches the manifest Publisher.
- **The toolkit owns the translation**: `SdlVulkanWindow : IActivatableWindow` in SdlVulkan.Renderer
  (7.23+), concepts in AppShell, each `Program.cs` only policy. Do NOT add a convenience copy on
  `SdlVulkanWindow`; two copies of one rule is what caused the activation bug.
- An UNPACKAGED install registers via `FileAssociationRegistrar`; both routes only make the app a
  candidate, the user assigns the default in Settings.
- **Explorer thumbnails are `tianwen-thumb.dll`** (`TianWen.Shell.Thumbnails`, NativeAOT COM, INSIDE
  the viewer's publish tree; imaging in `TianWen.Lib`'s `ThumbnailRenderer`). A packaged handler runs
  only in the shell's surrogate, so it gets a STREAM (`IInitializeWithStream`, container sniffed from
  the first bytes, one class for five types); **an embedded `ILLink.Substitutions.xml` applies only to
  its own assembly**, so the catalog strip is the `TianWen.Lib.EmbeddedCatalogs` feature switch
  (59.6 MB to 3 MB, pinned by `EmbeddedCatalogFeatureSwitchTests`); the CLSID is written in three
  places and never changes (`build-msix.ps1` checks); caching is the shell's (`thumbcache_*.db`), so
  the handler is stateless.

### Image Pipeline & Buffer Lifecycle

Camera → `ChannelBuffer` → `Image` → consumer → `image.Release()` → camera recycles. **The ownership
vocabulary (own / borrow / consume), the four conventions, the P1 retirement of fourteen runtime guards
and the DEBUG leak leg: [docs/plans/frame-lifecycle.md](docs/plans/frame-lifecycle.md). Driver coverage
matrix, the two full-scale numbers and the header parse:
[docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).** The rules:

- **Who owns a frame is stated ONCE, in the `<remarks>` on `Image`**; `Release()` spends ownership,
  `TryLease` borrows, `Adopt*` / `*Into*` consume. **Never derive "may I release this?" from a
  `ReferenceEquals`**: the answer was always in hand one branch earlier (`Blend < 1f`,
  `channels == 1`, `options.IsNoOp`), else make the producer CONSUME its input (`RawLightDecoder`).
  Getting it wrong corrupts a stack silently. Reference checks asking a DIFFERENT question (an
  enhancer declining a plate, a display-identity "new frame to upload?", the flat preview's slot swap)
  stay.
- Never hold an `Image` from `GetImageAsync` longer than needed; it pins the camera buffer.
- **`Image`'s primary ctor takes `ImmutableArray<Channel>`**: per-channel `Filter`/min/max on each
  `Channel`, the ref-counted buffer travels ON its channel (`Channel.Buffer`). Never re-introduce an
  attach-after-construct step (`WithChannelBuffers` is gone); a rewrap sharing arrays
  (`ScaleFloatValuesToUnitInPlace`) sets `Buffer = null` (pinned by `ImageChannelCtorTests`).
- Viewers never CPU-debayer; the GPU shader debayers the raw mosaic (`LiveFramePreviewSource.AcceptFrame`,
  `AstroImageDocument`). CPU `DebayerAsync` is for batch paths. `Image.DebayerIntoAsync` has **zero
  callers**: wire it or delete it.
- `Array2DPool` is scratch only; camera buffers use `ChannelBuffer`/`_freeBuffers`.
- **A buffer nobody released is findable in DEBUG** (`ChannelBufferLeakTracker`, weak-referenced, no
  finalizer); `dotnet.yml`'s `test-unit` runs a DEBUG leg on `--filter "Category=DebugOnly"` and
  **fails at zero executed**, so a renamed trait is caught. A DEBUG-gated suite joins by that trait.
- The recycle loop is complete for DAL (ZWO/QHY), Fake, Alpaca and ASCOM; Canon wraps its RAW decode
  output (no recycle, deliberate).
- **`Image.MaxValue` is the peak pixel OBSERVED in that frame**, not saturation; the fixed value is
  `ImageMeta.SensorFullScaleAdu` (`ICameraDriver.MaxADU` at `GetImageAsync`, or a FITS `SATURATE`
  card). **Two "full scale" numbers exist and must not be conflated**: the BITPIX container width
  (`BitDepthEx.UnsignedFullScale`, 65535 for Int16, right for N.I.N.A. files because N.I.N.A.
  multiplies on recording) and the native ADC resolution (`AdcResolution`, 16383 for the ASI533MC Pro,
  what the SDK hands TianWen). Never infer the SDK's scale from third-party files, and never route a
  native ADC depth through `BitDepthEx.FromValue` (it falls back to the container width).
- **`Image.UnitScaleDivisor` is the single source of truth for [0,1] normalisation**; a private
  `1/MaxValue` diverges the moment `SensorFullScaleAdu` is present (`TiffRoundTripTests` guards).

### The image is not necessarily in HDU 0

`Fits.ReadFirstImageHdu()` / `ReadFirstImageHduHeaderOnly()` (`FitsHduExtensions`) walk to the
first HDU that carries an image, and **every reader of an image file uses them** --
`Image.TryReadFitsFile`, `Image.TryReadFitsHeader`, `MasterCache.ReadFingerprint`,
`IntegrationFitsWriter.IsTianWenMaster`. A bare `ReadHDU()` on the read path is a regression.

- **A tile-compressed (`.fz`) image can never be in HDU 0.** It is a binary table, which is only
  legal as an extension, so an fpack file always opens with an empty primary (`NAXIS = 0`) and
  carries the pixels in HDU 1. FITS.Lib 5.0 surfaces that extension as an `ImageHDU` with the
  header translated back to the image's own `BITPIX`/`NAXIS`/`NAXISn`, so nothing downstream
  knows the difference -- but a reader that stops at HDU 0 finds `Axes == null` and rejects the
  file. Ordinary multi-extension FITS from other capture software has the same shape by choice,
  and was equally unreadable before the walk.
- **`WCS.FromFits` deliberately keeps its single `ReadHDU`.** A plate solver's `.wcs` output is a
  header with `NAXIS = 0` and no data at all; walking past it to find an image would find none
  and return null, which silently breaks plate solving. Reading that first header IS the point
  there.
- **`.fz` is matched on `.fz` alone**, in `Image.Import.cs`, `AstroImageDocument`
  (`SupportedExtensions` + `FileDialogFilters` + the `OpenAsync` dispatch),
  `FitsFolderFrameSource.FitsExtensions` and `FileAssociationRegistrar`.
  `Path.GetExtension("x.fit.fz")` returns `.fz`, so a `.fit.fz` entry would be dead code.

### A FITS header becomes an `ImageMeta` in exactly ONE place

`ParseImageMetaFromHeader`, called by BOTH `Image.TryReadFitsFile` and `Image.TryReadFitsHeader`. Two
copies once drifted into a `PIXSCALE` one path dropped and an `EXPOSURE` fallback reading
`{ EXPTIME, EXPTIME, 0 }`, so a frame with only that card was a **zero-second exposure** and
`MasterGroupKey` chose its dark by it. **A card added to one read path is a bug in the other**;
`FitsPixelScaleTests.TheTwoReadPathsAgreeOnEveryMetadataField` fails on the next divergence. Write-up,
pixel-scale precedence and the guiding cards:
[docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).

- **A declared pixel scale beats `FOCALLEN`, which is only a hint** (`Image.GetImageDim`:
  `ImageMeta.DeclaredPixelScale` from `PIXSCALE`/`SCALE`, else pixel size x binning x focal length,
  else `null`, never a guess).
- **`DeclaredPixelScale` and `DerivedPixelScale` are in different conventions.** The declared one
  already includes binning; the derived one is per unbinned photosite: collapsing them double-counts
  `BinX`.
- **A light carries the guiding quality of ITS OWN exposure** (`ImageMeta.Guiding`;
  `GUIDERMS`/`GUIRMSRA`/`GUIRMSDE`/`GUIDEPK`/`GUIDEN`, arcsec, ours alone): `GuideStatistics.OverExposure`
  reduces `Session.GuideSamples` over the exposure window, **never a rolling session average**; null is
  not zero (an unguided rig writes NO cards); `GUIDEPK` catches the single gust RMS hides. Stamped via
  `ICameraDriver.GuideStats` just before `GetImageAsync`. Pinned by `GuideStatisticsTests` +
  `SessionImagingTests`.

### Image Mutability: Almost-Immutable with In-Place Escape Hatches

`Image` is logically immutable (no public setter, `GetChannelSpan -> ReadOnlySpan<float>`). Full
design + ownership vocabulary (own/borrow/consume):
[docs/plans/frame-lifecycle.md](docs/plans/frame-lifecycle.md),
[docs/plans/viewer-memory-footprint.md](docs/plans/viewer-memory-footprint.md). Four things
deliberately mutate `data[c]` or its planes in place; any new caller must respect the same rule:

- **`Image.ScaleFloatValuesToUnitInPlace()`** (internal): rescales to `[0, 1]` reusing the
  underlying arrays -- the original instance's `MaxValue` is stale after the call.
- **`Calibrator.Apply(Image light)`** CONSUMES the light regardless of configuration -- the one
  deliberate exception to "ownership transfer is visible in the name" (an established domain verb),
  pinned by `CalibratorOwnershipTests`.
- **`AstroImageDocument.AdoptImageAsync(Image, ...)`** is the public ownership-transfer factory
  (internally calls `ScaleFloatValuesToUnitInPlace`) -- caller must not retain `image` after. Use
  `OpenAsync(filePath, ...)` when the source `Image` is shared. **Any new mutating public API
  should follow the same `Adopt*` naming**, never a neutral `CreateFrom*`.
- **Plane RESIDENCY** (`TryEvictFloatPlanes` / `Image.ResidentPlanes()`) is a third, deliberately
  INVISIBLE mutation -- unlike the two above it is not opt-in and the caller keeps using the image,
  because an evicted plane rebuilds from the retained raster on next read. Derived from the single
  `_planes` array with one interlocked publish (never a separate flag, which a reader could catch
  mid-update), so two threads reading `Image` -- public package surface, documented as immutable --
  never tear. Costs +8.7% to +20.3% on bilinear resample loops; resolve residency ONCE per
  operation, never per-sample. Pinned by `ImagePlaneResidencyConcurrencyTests`.

**Eviction is NOT release.** `Release()` spends OWNERSHIP (back to camera/pool, never touch again);
`TryEvictFloatPlanes` is reversible and the image stays usable -- the two words are one apart and
opposite in implication, which is the likely way to write an inverted guard. **Every read must go
through the `Planes` accessor**: three call sites that didn't (`GetChannelArray`, the subpixel
sampler, `ScaleFloatValuesToUnitInPlace`) silently read the evicted 0x0 stub -- a FITS write of an
evicted image emitted nothing and the in-place rescale threw on `plane[0, 0]`.

**Test fixtures must not share `Image` instances across tests.** `SharedTestData` caches the
extracted temp file path, not an `Image` -- two parallel collections sharing one cached `Image`
through `AdoptImageAsync` produced a "1 ms / 0 stars" `FindStarsAsync` flake.

### Float TIFF Convention (`SharpAstro.Tiff` I/O; Magick.NET fully removed)

Magick.NET is gone from every project; float TIFF I/O is `SharpAstro.Tiff.TiffWriter`/`TiffReader`
(`Image.Export.cs` / `Image.Import.cs`) and imports route through the SharpAstro codecs facade (CR2/CR3
→ FC.SDK.Raw, FITS → FITS.Lib). **The codec types are NOT in DIR.Lib** (3.0 extracted them to the
`Codecs` repo, 4.0 dropped the dependency): the namespaces are `SharpAstro.Png` / `.Jpeg` / `.Tiff` /
`.Color.Icc` / `.Jxr` / `.Exr` / `.Exif` / `.Codecs`, pinned as one family via
`$(SharpAstroCodecsVersion)`; a `DIR.Lib.Tiff.*` or `DIR.Lib.Color.*` reference is a stale name.

**The on-disk convention is `[0, 1]` file values, always**, because libtiff-HDRI readers and
scientific tools (`tifffile`, PixInsight, ImageJ) disagree on float TIFF values and `[0, 1]` is the one
range both read. Rationale, the `SMinSampleValue`/`SMaxSampleValue`/`Q16HdriQuantumMax` mechanics, the
round-trip guards and the codec surface inventory (16-bit, cICP, `iCCP`, `IccProfiles.SRgbV4`):
[docs/plans/image-codecs-facade.md](docs/plans/image-codecs-facade.md).

### FITS Viewer Widget (`ImageRendererBase<TSurface>`)

The renderer-agnostic viewer (`tianwen-fits` and the GUI 🪐 tab via `VkImageRenderer`) is a `partial
class` split by concern (`.Layout`, `.Toolbar`, `.FileList`, `.Overlays`, `.Histogram`, `.InfoPanel`,
`.StatusBar`, `.Transport`, `.Input`); add a concern as a new partial, never grow the core file back
into a monolith. All chrome is arranged from ONE layout pass rooted at `ContentRegion`; never
hand-place chrome at `(0,0,Width,...)`. One slider (`DrawTrackSlider` / `TrackFrac`, DIR.Lib's
`PixelWidgetBase`) serves WB, wavelet and SER scrub; never re-triplicate it. Details:
[docs/architecture/widgets-and-controls.md](docs/architecture/widgets-and-controls.md).

**GPU resource lifetime, with the incidents behind every rule:
[`docs/architecture/viewer-gpu-lifetime.md`](docs/architecture/viewer-gpu-lifetime.md).** Never call
`UploadDocumentTextures` from a render callback (textures upload in `PrepareFrame`; a Store
`VK_ERROR_DEVICE_LOST`, pinned by `ANewDocumentIsUploadedBeforeTheLayerPassThatSamplesIt`); never
destroy a bound Vulkan object or write a shared descriptor set from an upload path
(`VulkanContext.DeferDestroy`, one sampler set per frame in flight since SdlVulkan.Renderer 7.28); a
window resize is its own GPU-lifetime path (drive maximize/restore); the cached image layer samples in
TEXTURE space, so divide UVs by the CAPACITY
(`TheBlitSamplesInTextureSpaceWhenTheTargetIsLargerThanTheLayer`). **Run under `SDLVK_VALIDATION=1
SDLVK_SYNC_VALIDATION=1` and read `validation_report`** whenever this area is touched.

**One viewer, no mini viewer.** Live Session preview, polar-align and guide-cam host this viewer
chromeless (`ViewerState.HideChrome`), fed by `LiveFramePreviewSource : IPreviewSource` (normalises to
`[0,1]`, subsampled median/MAD stats, `AcceptFrame(image, freezeStats)` for
`ViewerState.FreezeStretchStats`, delegates to the shared `AstroImageDocument.ComputeStretchUniforms`;
`ImageRendererBase.OverrideWcs` supplies the WCS). Embedded hosts call `SetSurfaceSize(w,h)` each
frame, not `Resize`. **`LiveFramePreviewSource.PerChannelBackground` must be non-empty and
channel-sized** (`ComputePostStretchBackground` indexes `[0]`; an empty array crashed the GUI;
`LiveFramePreviewSourceTests`).

### The star field is culled on TWO axes, and both are load-bearing

`StarMagnitudeIndex` + `StarChunkIndex` (`TianWen.UI.Abstractions`) are the one implementation for
`VkSkyMapPipeline` and `WebGlSkyMapPipeline`: sky-region chunks, brightest-first within each, a 0.5-mag
prefix table; draw only the regions the view cone reaches and only their prefix. **Neither axis covers
the other** (magnitude bounds a wide field, ~3% of Tycho-2 at 60 degrees but 81% at V<=12; the cone
bounds a deep zoom and nothing at full sky). **Submitting the whole ~2.5M-star buffer TDR'd an Adreno
X1-85** and dropped 944 of 1287 frames in the browser. **A limit past the last bin clamps to
"everything", never wraps to zero** (pinned by a `[Theory]`). Measurements, the WebGL2 `firstInstance`
workaround and the two cull details that bite: [`docs/plans/web-tycho2.md`](docs/plans/web-tycho2.md).

### A quantized cache key must not derive its grid from a continuous input

Both overlay caches (`SkyMapTab.BuildOverlayKey`, `OverlayGatherKey`) once quantized the view centre
into `FOV/8` cells from the RAW FOV while bucketing the FOV separately, so a zoom re-gathered on every
event (69 gathers against 8 over one pinch; a pan cost 3, and that asymmetry is the tell). **Take the
step from the BUCKETED value.** **Assert the gather COUNT, never the output**
(`SkyMapTab.PrimOverlayGathers`, like `SkyMapState.PlanetCacheRebuilds`): a stale-keyed rebuild draws
the identical frame. **`gathers <= 12` passes on `gathers == 0`** (`ShowObjectOverlay` is off by
default): pair the bound with `gathers > 0` and see it FAIL with the fix removed. Why it read as jank
in the browser and a never-settling walk on the desktop:
[`docs/plans/web-showcase.md`](docs/plans/web-showcase.md).

### The web host paints per event, so continuous gestures must coalesce onto rAF

The browser build has no render loop: every input handler repaints synchronously (71% of move-driven
repaints were superseded inside their own 16.67 ms). `RequestRenderCoalesced()` (via
`wwwroot/raf-pump.js`) is for `OnPointerMove` / `OnWheel` / `OnPinch` **only**. **Clear the dirty flag
BEFORE painting and on the schedule-failure path**, or the canvas freezes for good. **A trackpad pinch
is `ctrl`+`wheel`** (Blazor `@onwheel`), a different path from the touch bridge and the densest gesture
the app sees. Details: [`docs/plans/web-host-carve-out.md`](docs/plans/web-host-carve-out.md).

### Sky Map / FITS Viewer GLSL (pre-baked SPIR-V, no runtime shaderc)

TianWen.UI.Shared's shaders are GLSL 450 files under `src/TianWen.UI.Shared/Shaders/*.vert|*.frag`,
**pre-baked to SPIR-V** (`Shaders/spirv/*.spv`, committed + embedded, loaded via `LoadShaderModule`) by
`tools/BakeShaders`; there is **no runtime shaderc** (SdlVulkan.Renderer 6.23 dropped
`Vortice.ShaderCompiler`; shaderc ships no android RID). Two rules: **edit a shader → re-bake → commit
the `.spv`** (`dotnet run --project tools/BakeShaders -c Release -- src/TianWen.UI.Shared/Shaders`;
**warning TWSH0001** flags a source newer than its `.spv` and never fails); **ASCII only**, shaderc's
lexer rejects non-ASCII bytes even inside comments. The `stereoProject` GLSL is inlined into the three
`skymap_*.vert` files; restoring a single source is a deferred cleanup (docs/todo/ui.md).

`Image.StretchValue()` is the single source of truth for the scalar stretch math (normalize → subtract
pedestal → rescale → MTF). Don't reimplement it.

### Stretch Pipeline: CPU/GPU Mirror

Two implementations must produce visually equivalent output for the same `StretchUniforms`: **GPU**
`Shaders/image.frag` (`stretchChannel` + the Luma branch mirroring `StretchLumaPixelCpu`) for the live
viewer; **CPU** `Image.StretchChannelCpu` / `StretchLumaPixelCpu` / `ApplyHdr` / `ApplyCurveLut` /
`ApplyBoost` / `RenderStretchedRgba` for `ConsoleImageRenderer` (TUI Sixel) and tests. Order in both:
pedestal subtract → bg neutralization → WB → shadow/rescale → MTF → luma blend → curves → HDR knee →
normalize → clamp; in Luma mode the producer populates BOTH `StretchUniforms.LumaStretch` and the
per-channel `Shadows/Midtones/Rescale` so the shader blends via `LumaBlend`. **The subject in full, with
the measurements: [`docs/architecture/stretch-pipeline.md`](docs/architecture/stretch-pipeline.md)**
(and `stacking-render-pipeline.md` sections 5-6). The rules:

- **Wire a new stage into BOTH the GLSL and the CPU helpers**; `StretchTests_NewPipeline` is the
  end-to-end guard (per-channel means inside `(epsilon, max-epsilon)` catch a channel collapse).
- **`AstroImageDocument.ComputeStretchUniforms` is the SINGLE producer of `StretchUniforms`**, scaling
  per-channel stats by WB before deriving shadows/midtones/rescale (as does `ConvergeStretchFactor`).
- **`Linked`/`Unlinked` mean what they mean in PixInsight, and the difference lives ENTIRELY in the
  uniforms** (`StretchSolver` is the only place it exists): Linked writes ONE curve into all three
  slots so a white balance survives as colour; Unlinked writes each channel's own curve and
  neutralises the background. **Never re-derive a per-channel curve in the Linked branch.**
- **`StretchMode.Auto` is a UI intent, resolved before any `StretchUniforms` is built, never a shader
  mode**: `mode.ResolveAuto(isColour, calibrationActive)` (`StretchModeExtensions`, TianWen.Lib, shared
  with `ThumbnailRenderer`) is the one resolver; `ViewerActions.DefaultStretchMode` is the VIEWER
  default (= Auto), `MasterPreviewRenderer` / `PreviewEncoder` render Linked explicitly; a fixed-curve
  test passes an explicit mode.
- **Background neutralisation is solved POST-WB, so anything caching its gains owes the WB in its
  cache key** (they print at F4).
- **The SPCC / Calibrate toggle gates the RENDER, not the measurement** (`applyColorCalibration`); an
  AI enhance INHERITS the WB triple (`InheritColorCalibration`) rather than re-fitting; background
  neutralisation is re-solved per document.
- **The manual WB is a SEPARATE multiplier from the auto calibration** (`shaderWhiteBalance` = auto x
  manual, only the auto half scales the stats; sliders show `StretchSolver.ComposeWhiteBalance`, travel
  is its own `[0.25, 4]`, never `GrayWorldWhiteBalance`'s clamp). WB applies in the `StretchMode.None`
  linear path too, mono excepted.
- **Luma weights live in `StretchUniforms.LumaWeights`** (Rec.709 default, `SensorMatched` from QE x
  CFA); never hardcode Rec.709. Post-stretch normalize is `NormalizeScale` (1.0 = no-op).

### Layout DSL (`DIR.Lib.Layout`)

GUI/TUI panels are immutable `Layout.Node` trees: `Layout.Engine.Arrange` measures,
`PixelWidgetBase.PaintLayout` draws and binds clicks **from the same arranged rect** (draw == hit by
construction). Engine + DSL reference: DIR.Lib's README; the engine features TianWen leans on, the five
traps in full, the alias and conditional-background rules, the TUI row contract and the pointer-cursor
rule: [`docs/architecture/widgets-and-controls.md`](docs/architecture/widgets-and-controls.md), read it
before any layout work. The short form:

- **Build trees with `Layout.Builder`** (`VStack/HStack/Text/Box/Fill/Spacer/Grid/Overlay/Split/Dock`)
  and the fluent `Layout.Node` methods (`.WFixed/.WStar/.RowH/.ColW/.Stretch/.Bg/.Pad/.Clickable/
  .WithGap`), never `new Layout.Node.X { }` or `cursor += h`.
- **Alias, don't import**: `global using Layout = DIR.Lib.Layout;` and the qualified `Layout.Node`;
  `using DIR.Lib.Layout;` drops the `Node`/`Content`/`Size<T>` barewords into scope. A consumer owning
  its own `Layout` type renames it (PTV: `ElementGrid`).
- **Conditional background**: `.Bg(color)` always sets a value, so `if (cond) n = n.Bg(color);`, never
  `.Bg(default)`.
- **Interactive sub-widgets** emit `Layout.Builder.Fill(key: "...")` and draw via `drawFill`; **a text
  field is NOT one**, it is `Layout.Builder.TextInput(state, fontSize)` (see below).
- **Responsive sizing is `Sizing.Star(weight, min, max)` + `.CollapseBelow(u)` + `WrapH`/`WrapV`**;
  orientation is a plain C# branch (canonical: `PlannerTab.BuildFrameLayout`, `PlannerTabLayoutTests`).
- **Five silent traps** (all found on the Home board): `.RowH(h)` sets `Width = Star` and eats a
  preceding `.WFixed(w)` (fixed on both axes is `.WFixed(w).HFixed(h)`); a `Stack` places children at
  the cross-axis START (`.CrossCenter()`, never padding); a `Node`'s default `Width` is `Auto`, so
  all-Star children arrange to nothing (state `.WStar()`); never pair `.CollapseBelow(u)` with a Star
  minimum, and a child that must survive takes NO threshold; an icon inks the full square it DECLARES.
- **A mark is a `Layout.Content.Icon`, never a symbol character in a `Text` run** (a glyph draws
  .notdef where the face lacks it).
- **`.PadX(u)` / `.Pad(across, down)` for a FIXED-height bar**, or the icon becomes a stub while the
  text overflows and goes on looking correct.
- **`PushClip(x, y, w, h)` / `PopClip()` on the widget base**, never `Renderer.PushClip` with a
  hand-built `RectInt` (its `(LowerRight, UpperLeft)` order). Clips nest and narrow since DIR.Lib 7.27.
- **TUI rows are trees too** (Console.Lib 4.10): `IRowLayout.BuildRow(in RowContext)`, own pen, inline
  buttons via `.Clickable(...)` resolved by `ScrollableList.DispatchRowHit`; a new capability is a
  **field on `RowContext`**, never an overload.
- Engine geometry is headless-testable (stub `Layout.IMeasureContext`); `RgbaImageRenderer` honours
  clipping since DIR.Lib 7.25.

### UI Primitives: the cursor, a text field, and who holds focus

Full reasoning: [`docs/architecture/widgets-and-controls.md`](docs/architecture/widgets-and-controls.md)
(cursor) and [`docs/plans/automatic-text-input.md`](docs/plans/automatic-text-input.md) (field, focus,
key routing). The rules that bite:

- **The pointer's appearance is a property of a REGION, never a host predicate**: declare it beside the
  click (`RegisterClickable(..., cursor:)` / `.Clickable(hit, onClick, cursor)` / `.WithCursor(kind)`);
  the host asks `guiRenderer.CursorAt(x, y) ?? CursorKind.Default`; a region stating nothing is
  transparent (`null`, not Default), so a row inherits its card's cursor. The `CursorKind` -> SDL
  mapping lives in SdlVulkan.Renderer.
- **HOVER needs a z-order answer, `ViewerState.OverlayOwnsPointer`**, because hover is decided at PAINT
  time; add an overlay to that ONE property, never a call site.
- **A text field is a declaration**, `Layout.Builder.TextInput(state, fontSize)` and nothing else
  (`TextInputRenderer`, `TextInputHit`, `CursorKind.Text`; `CellLayout` on a terminal). `fontSize` is in
  DESIGN units (the painter crosses `ctx.FontScale`); intrinsic width comes from the placeholder.
- **Focus is global but not settable**: `DIR.Lib.TextInputFocus` owns the transition, the host binds
  `FocusChanged` ONCE (SDL `StartTextInput`/`StopTextInput`, web `CanvasTextOverlay`); `Focus` is
  idempotent; `BlurIfUnpainted(painted)` takes what the caller painted
  (`VkGuiRenderer.PaintedTextInputs()` unions chrome + active tab).
- **`TextInputInteraction` reads `ctx.Focus.Current`**, takes `KeyContext.TabFields` as a callback, and
  **swallows every key while a field is focused**.

### Per-Window Widget State: `DpiScale` / `FontPath` / `EmojiFontPath` are properties, not parameters

A value constant for the whole window is a `virtual` property on `PixelWidgetBase<TSurface>` (DIR.Lib):
set once by the host, propagated by a composite widget overriding the setter, resolved by
`RenderLayout`/`ArrangeLayout`/`PaintLayout` as `?? DpiScale` / `?? FontPath`; `dpiScale: 1f` is the
device-px escape hatch and a `PixelMeasureContext` overload covers a per-axis scale or a cell-authored
tree (build it ONCE and pass the same instance to Arrange and Paint). **Do NOT reintroduce these as
`Render`/helper parameters**: per-window constant -> property, per-call derived -> parameter; `fontSize`
is NEVER a property (`AltitudeChartRenderer`, `SkyMapRenderer` keep theirs). Breakdown:
[`docs/plans/dpi-scale.md`](docs/plans/dpi-scale.md).

**Which FACE they get is one decision, `BundledFonts.Resolve()`**, returning `(Text, Emoji, Fallback)`
together for all three hosts; **a direct `FontResolver.` call in production code is a regression**
(tests exempt). Resolving a subset is the bug it prevents (the viewer had faces but no
`FontFallback`, could not ask `CanRender`, and every missing glyph was found by eye); bundled first
because only a bundled face has known COVERAGE. The `Lazy<FontSet>` cache and what is outstanding:
[`docs/plans/font-roles-and-icon-baking.md`](docs/plans/font-roles-and-icon-baking.md).

### Signal Handler Pattern: Route, Don't Implement

The lightweight `SignalBus` is our alternative to MediatR/MVVM. `AppSignalHandler.cs` subscribe
lambdas must **route only**: take signal payload, call one or two helpers, reflect results back into
UI state. No loops over domain state, no direct persistence, no URI manipulation, no multi-step
business logic.

Where business logic goes:
- **Pure profile/equipment transformations** → `EquipmentActions` in `TianWen.UI.Abstractions`
- **Device-model operations** (URI reconciliation, discovery) → extension methods in `TianWen.Lib/Devices/*Extensions.cs`
- **Persistence** → dedicated helpers (`PlannerPersistence`, `SessionPersistence`, `Profile.SaveAsync`)

**Red flag**: a `foreach` or multi-step `if`/`await`/`save` chain inside a subscribe lambda; extract it.

### Shared UI State: `ImmutableArray<T>`, not `List<T>`

Any collection on shared UI state (`PlannerState`, `LiveSessionState`, `EquipmentTabState`,
`GuiAppState`) that can be touched by **both** the render thread and a background task must be
`ImmutableArray<T>` with atomic replacement. Writers build the new array (or use `array.Add(x)`,
`.RemoveAt(i)`, `.SetItem(i, x)`, `.Sort(cmp)`, all return new instances) and assign in one
reference update. Readers snapshot the property into a local. Pattern match on `.Length`, not
`.Count` (`ImmutableArray<T>` only exposes `Count` via explicit `IReadOnlyCollection<T>`).

`List<T>` here **will** produce `InvalidOperationException: Collection was modified` under load.
`Dictionary<K, V>` has the same hazard.

### Background-Task State in `AppSignalHandler`

State that gates background tasks is mutated from two threads even when the source code looks
single-threaded: `bus.Subscribe<T>(async sig => ...)` runs the synchronous prefix on the UI thread,
but every continuation after `await` runs on a thread pool thread. Crashes show as
`IndexOutOfRangeException` inside `HashSet<T>.Add` / `Dictionary<K, V>` internals.

| Use case | Wrong | Right |
|---|---|---|
| Per-key in-flight set | `HashSet<TKey>` + `Add`/`Remove` | `ConcurrentDictionary<TKey, byte>` + `TryAdd`/`TryRemove` |
| Per-key value buffers | `Dictionary<TKey, T>` | `ConcurrentDictionary<TKey, T>` (T also thread-safe if mutated) |
| Single-flag in-flight gate | `bool _busy` | `int _busy` + `Interlocked.CompareExchange(ref _busy, 1, 0)` |
| Ring buffer / accumulator | unguarded `_ring`/`_count`/`_head` (or `lock` around them) | lock-free `CircularBuffer<T>` (ImmutableArray + CAS replace); readers take `Snapshot`, not lazy `IEnumerable` |
| Large `record struct` cross-thread | unguarded auto-property `set` | private field + `lock` (struct writes > pointer-size aren't atomic) |

**Telemetry-poll-only state** can stay non-concurrent if it is genuinely only written from the
per-frame poll method. Mark it clearly so a future edit doesn't move the write into a continuation.
Canonical example: `AppSignalHandler.PollCameraTelemetry` and `EquipmentTabState.PendingTransitions`.

### Concurrency

- `SemaphoreSlim` / `DotNext.Threading` for resource locking
- `CancellationToken` propagated throughout
- `ValueTask` for allocation-free async paths
- **Never use `.GetAwaiter().GetResult()`**: make the method `async` and `await`
- **Prefer a lock-free hand-off over `lock {}` blocks.** For producer/consumer hand-off (a background
  task feeding a render or poll loop), return the result *through* the `Task<T>` and let the consumer
  poll it: `if (_task is { IsCompleted: true } t) { _task = null; if (t.IsCompletedSuccessfully && t.Result is { } x) use(x); }`.
  The Task is the synchronisation primitive, so no shared mutable field crosses threads; in a
  synchronous loop where you cannot `await`, that poll is the stand-in for `await _task`. For a single
  grab-and-clear reference, use `Interlocked.Exchange`. (Canonical example: `SkyMapTab`'s async Milky
  Way load, mirroring `TryApplyPendingStarBuild`.)
- **There is no `WhenAll` for `ValueTask`** -- not in the BCL, not in this org, and not in DotNext
  (it had a tuple `WhenAll` and dropped it after 4.x; 6.1.0 ships no combinators). Do not reach for
  `.AsTask()` reflexively: **start both, then await both**. Calling an async method runs it to its
  first await, so `var a = XAsync(); var b = YAsync(); await a; await b;` has both already in flight
  and allocates nothing. **Wrap it `try { await a; } finally { await b; }`** whenever abandoning the
  second one matters -- two bare awaits drop `b` if `a` faults, leaving an unobserved `ValueTask`
  and whatever `b` was cleaning up unfinished. Canonical use:
  `PulseGuideTargetExtensions.PulseGuideAsync`, where `b` is the pulse on the other mount axis and
  dropping it leaves that axis running.
- **Never build the value for a `CompareExchange` inside the call.** An argument is evaluated before
  the call it is passed to, so `Interlocked.CompareExchange(ref _task, Task.Run(Work), null)` starts
  `Work` on **every** racing caller, not just the CAS winner. The losers return the winner's task and
  look correct while their own copy runs on. In `FilterCurveDatabase.LoadAsync` that appended a second
  copy of every curve (180 filters became 360). Publish a `TaskCompletionSource` placeholder first, do
  the work behind it, and raise any "ready" flag only once the data is there -- a flag set by the CAS
  winner *before* the work runs answers true over empty state.
- **Standing rule for `lock () {}`** (any lock, anywhere): (1) it needs a strong justification as a
  comment at the lock site -- why a Task hand-off / `Interlocked` / ImmutableArray-CAS swap does not
  fit; (2) the locked path should not be reachable from a rendering thread (a contended lock there is a
  frame stall -- hand the render thread an immutable snapshot instead); (3) if the lock stays, it must
  be `System.Threading.Lock` (C# 13), never `lock` on an `object`, a collection or any other reachable
  instance (faster, self-documenting, compiler-enforced). Remaining `object`-based sites are inventoried
  in [docs/todo/infra.md](docs/todo/infra.md). For a most-recent-N window polled by readers (guide
  samples, frame metrics), prefer the lock-free `CircularBuffer<T>` (`TianWen.Lib/Sequencing`):
  ImmutableArray + CAS replace, torn-free `Snapshot` reads, O(capacity) appends -- right when producers
  are low-rate (per exposure) and pollers high-rate (per frame).

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, `Span<T>` / `ReadOnlySpan<T>`
- **Immutability with controlled mutability**: types immutable by default; private mutable state with read-only views
- **Correct abstraction levels**: pure math/data in `TianWen.Lib`, UI state in `TianWen.UI.Abstractions`,
  Vulkan-specific rendering in `TianWen.UI.Shared` / `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **No code duplication**: reuse single sources of truth (e.g., `Image.StretchValue()`)
- **Directory walks go through `FileEnumeration` (`TianWen.Lib/IO`), never the `SearchOption`
  overloads of `Directory.EnumerateFiles`/`GetFiles`.** Those run with the legacy defaults: they ENTER
  every reparse point (the organized archive's `targets/` junction farm was scanned once per link, and a
  scratch junction into `D:\Astro-Pics` turned a scratch walk into an archive walk), abort the whole walk
  on the first unreadable directory, and match `*.fits` case-sensitively on Linux. `FileEnumeration` sits on
  `FileSystemEnumerable<T>` (the directory index, no per-file open: a million tiles in about a second),
  skips reparse points, ignores inaccessible directories, keeps hidden files, uses a 64 KiB buffer and
  matches extensions as an ordinal-ignore-case name SUFFIX (`.fits.gz` is one extension). Results are
  unordered; sort with `StringComparer.OrdinalIgnoreCase` where determinism matters. Pinned by
  `FileEnumerationTests`, including a real junction that must be neither listed nor entered.

## Package Management

Centralized in `Directory.Packages.props`: version numbers go there, not in individual `.csproj` files.

## Runtime Data (AppData)

`%LOCALAPPDATA%/TianWen/`. The whole set, because there is **no** single choke point that creates these:
`IExternal.CreateSubDirectoryInAppDataFolder(name)` covers four of them, `Planner`/`Session` are built
from `AppDataFolder` directly, `Profiles`/`Logs` off `SharedStaticData.CommonDataRoot`, and `models` +
`lan-node-id.txt` are resolved by their own owners. Add a directory here when you add one there.
```
TianWen/
├── Logs/<date>/        # <appName>_<timestamp>.log per process: GUI_*, FitsViewer_*, ... (FileLoggerProvider)
├── Profiles/           # Per-profile data (*.json + NeuralGuider/*.ngm + BacklashHistory/*.json)
├── Planner/            # Pinned targets: <profileId>/<date>.json, remote rigs under rigs/<bindingId>/
├── Session/            # Session-setup state, <profileId>.json (SessionPersistence)
├── Guider/             # Guider frames dumped for plate solving (guider_*.fits + .ini)
├── Weather/            # OpenMeteo / OpenWeatherMap forecast cache
├── SmallBodies/        # JPL SBDB comet cache: comets.json + apparitions.json
├── models/             # AI ONNX models (ModelResolver; also probes SASpro's own models dir)
├── Secrets/            # Non-Windows only: 0600 file per device secret (Windows uses Credential Manager)
└── lan-node-id.txt     # tianwen-server's stable LAN NodeId, the key remote-rig bindings persist against
```
