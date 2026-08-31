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
screenshot-poll-and-OCR**. **The inspector surfaces, the fake-device URI shapes and the cell-buffer
lessons are in
[`docs/architecture/unattended-ui-driving.md`](docs/architecture/unattended-ui-driving.md).** Three
pieces compose, and these are the parts that bite:

1. **A fake-device profile.** Fakes share the real URI shape with host `FakeDevice`
   (`Mount://FakeDevice/FakeMount1?latitude=…&longitude=…&port=SkyWatcher`) and only surface from
   discovery when `IncludeFake:true` -- the GUI auto-includes them when the active profile already
   references any fake URI (`ProfileData.ReferencesAnyFakeDevice`), otherwise Shift+Discover. Two query
   keys select behaviour: **`port=SkyWatcher`** picks `FakeSkywatcherMountDriver` (believed/true
   pointing seam + polar misalignment + worm PE, the variant that exercises meridian-flip and Dec-sense
   paths; omit it for the lightweight `FakeMountDriver`), and **`hasCover=false`** on a
   cover/calibrator picks the flap-less driver panel (absent = flip-flat).
   `ProfileData.SiteLatitude/Longitude` **must** match the mount URI's `latitude/longitude` (a split
   site throws "Could not calculate timezone"). Canonical wiring:
   `SessionTestHelper.CreateSessionAsync(mountPort:"SkyWatcher", latitude, longitude)`.
2. **Anchor the clock** with `TIANWEN_NOW` (see the TimeProvider section) to a real night at that site,
   so the planner computes visible targets and the session leaves `WaitingForDark` at once instead of
   stalling in daylight.
3. **Drive + observe via the DEBUG inspector, not screenshots.** A DEBUG build attaches
   `DebugInspector` (GUI, `sdl-ui-inspector` sidecar) or `ConsoleDebugInspector` (TUI,
   `Console.Lib.Inspector`), both compiled out of Release. Poll the `AppState` snapshot for coarse
   state; post any `*Signal` **by name** (`SignalFactories` is source-generated over every `*Signal`
   type by `DIR.Lib.SourceGenerators.SignalDirectoryGenerator`, so posting `StartSession` runs the
   whole `RunAsync` with no clicking); `describe_ui` gives clickable regions, `describe_layout` the
   FULL arranged `DIR.Lib.Layout` tree. `StartSession` needs >=1 pinned target
   (`PlannerState.Proposals.Length > 0`), and planner pins persist per-profile, so pin once.

Four rules that decide whether what you read means anything:

- **Ground truth for fine telemetry is the Debug log, not the inspector snapshot.** `AppState` reads
  `LiveSessionState`, which can lag during the guide loop; per-frame guide stats (errDec/corrDec/RMS),
  HA and pier side come from `%LOCALAPPDATA%/TianWen/Logs/<date>/GUI_*.log`.
- **Use `render_liveness`, not a screenshot, to decide IF the render thread is stuck.** Every inspector
  command runs ON the render thread, so a `ping` that round-trips proves the loop is pumping and a
  connected-but-silent probe means it is blocked -- and screenshot/describe block exactly when it is.
  A dead device is distinguishable from a wedge: `VK_ERROR_DEVICE_LOST` is terminal and logs event 115
  instead of entering swapchain recovery (event 110, which reads like a workload problem), and event
  501 names the selected GPU.
- **`validation_report` with zero messages is evidence only when `active` is true** (the DEBUG +
  `SDLVK_VALIDATION=1` gate AND `layerAvailable`) -- a host with no Khronos layer installed used to
  answer `enabled: true` with zero messages, indistinguishable from a clean run.
- **A terminal reads back as TEXT, which is the one thing a GPU surface cannot offer.** `screen` /
  `row` / `cell` report the **front** cell buffer -- what was actually emitted, not a parallel model
  that can drift -- and `cell` adds the resolved pen, which is how a colour bug is caught at all (a
  glyph drawn `#000000` on `#000000` is invisible on screen yet identical to a correct one in a text
  dump). One gotcha: the modifier parameter is **`mods`** (`"Ctrl"`, `"ctrl+shift"`), not a `ctrl`
  boolean, and the verb echoes what it resolved. Diagnose repaints from the once-a-second
  `TUI paint: N frames, M cells (K opaque)` log line, never from the screen (~1 cell/tick at rest).

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
(`RA`/`DEC` is what the *mount reported*, and the two agree only on a synced mount, which nothing in
the header states). `WCS.FromHeader` and `Image.Fits.ParseTargetCoords` read CRVAL ->
OBJCTRA/OBJCTDEC -> RA/DEC, in that order, and must not diverge. It is load-bearing because the
pair-lock anchor pool is the brightest catalog stars that *project inside the frame from the hint*, so
a hint off by most of a field fills the pool with stars the image does not contain and the seed never
reaches consensus (a 2.4 deg unsynced mount fell through to ASTAP, and a wider search radius does not
help -- coverage was never the problem).

**A solver-built WCS answers in DETECTED-CENTROID coordinates -- never subtract 1 from `SkyToPixel`.**
`AttachCDMatrix` derives the CD matrix from the affine that maps projected pixels onto detected
centroids, so the emitted WCS needs no 1-based-to-0-based conversion; applying one injects a constant
(+0.91, +0.89) px bias and spends a third of the acceptance gate's tolerance.

**Frozen real-field regressions: `TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz`** -- STAR
LISTS, not FITS, from 24 real Vela pointings / 96 frames / 78k catalog stars with the gate-verified
WCS as oracle, driven by `VelaMosaicFieldTests`. **Three of the four bugs it found would have passed a
synthetic suite**, because a synthetic field is built from a transform the test already knows. The
dataset rationale, both measurements above, and what real density covers that synthetic fields cannot:
[docs/plans/plate-solver-performance.md](docs/plans/plate-solver-performance.md).

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

- **The HORIZON test keys on HOUR ANGLE, not pier side** (`HA > 0` IS descending, since altitude is
  maximal at upper transit), so it needs no alignment mode and holds for fork and AltAz mounts too.
  **The MERIDIAN test is the opposite -- an RA-AXIS test where the pointing state is load-bearing:**
  the same HA puts the axis in two places, so `Evaluate` reads the offset as `Normal ? -HA : HA`
  (`Unknown` = the HA approximation). Reading HA alone stopped every rig ~30 min after a flip.
- **`IMountDriver.GetAxisAngleAsync` is the MECHANICAL tier and WINS when present** (SkyWatcher only,
  null elsewhere): `|angle| - 90` deg is how far the counterweight is above horizontal in either
  state, read with no clock, site or sync. Fallback, never cross-check; `MountLimitVerdict.Basis`
  says which tier answered, because a user who mistakes the estimate for a mechanical limit sets the
  threshold wrong. Inside `MountState` the driver's null is NaN (`PollDriverReadAsync` is
  `where T : struct`, which excludes `Nullable<T>`).
- **Only a MEASURED pointing state may drive it -- or one the SESSION verified.**
  `MountLimits.TrustedPointingState` hands `Evaluate` `Unknown` for a `Computed` driver (LX200 base,
  SGP, `FakeMountDriver`), whose HA-derived answer reads as post-flip west of the meridian whatever
  the mount did. Its **three-argument overload** takes `Session._verifiedPointingState` instead --
  latched where a goto landed, moved only by an image-confirmed flip -- which is strictly better than
  `Unknown`. It is the MIRROR case that needs it: a flipped rig pointed EAST swings back toward the
  pier, and the hour-angle tier reads that as clear. **`MountLimitWatcher` has no session and no
  latch, so it keeps the two-argument form; do not unify the call sites.** The flip gate keeps the
  computed answer.
- **Warn and act are a threshold plus a non-negative EXTRA**, never two absolute numbers: the limits
  run in opposite directions (HA rises toward its limit, altitude falls toward its own), so an
  absolute pair can be edited into acting before it warns -- differently for each.
  `MeridianActionDeg = Warn + max(0, Extra)` and `HorizonWarnDeg = Action + max(0, Extra)` make
  warn-before-action hold by construction both ways.
- **`alreadyActed` is a latch and must downgrade to `Warn`, never clear.** The check runs on a poll
  loop, so without it a park is re-commanded every tick and the park slew restarts forever, never
  arriving; clearing instead of downgrading stops telling the user they are still in the limit.
- **The meridian limit is in MINUTES and is the ULTIMATE CLAMP on the flip.** It shares an axis with
  `MeridianFlipEarliestMinutesAfter`/`LatestMinutesAfter` so it shares their unit -- it was degrees
  once and the defaults read as the same numbers while differing 4x. Horizon stays in degrees.
  `MountLimitConfiguration.ClampFlipLatestMinutes` is applied INSIDE `MeridianFlipDecision` so no
  caller can forget it. **The dependency direction is load-bearing:** how long to keep imaging is a
  preference, where the tube meets the pier is a fact, and the fact caps the preference. Deriving the
  limit from the flip instead would let a preference walk a safety bound into the pier; unclamped,
  the two race and the limit wins, stopping the mount as it was about to flip.
- **It is the TUBE that collides, not the counterweight** (tracking past the meridian swings the
  counterweight UP, the tube DOWN toward the pier), so the threshold approximates a three-variable
  envelope -- optics length x declination -- and must be set for the worst case the rig images.
- **Config lives on `ProfileData.MountLimits`** (nullable = disabled), projected onto `Setup` by
  `SessionFactory`, never onto the per-run `SessionConfiguration` -- it must hold for a manual slew
  with no session. **Enforcement is in `PollDeviceStatesAsync`, not the imaging tick**: a limit
  evaluated between exposures would watch a mount drive into a pier during a goto. The altitude is
  **geometric** (`SiteContext.AltitudeDegrees`) -- a tripod leg is not lifted by refraction.
  Breaching routes to `ImageLoopNextAction.LimitReached`, NOT `DeviceUnrecoverable`: nothing is
  broken. Tracking stops for a Park response too, before the park slew.
- **Parking is opt-in for both limits**, because a park is MOTION across a path nothing has checked --
  a mount stopped at 8 deg altitude may be a hand's width from a tripod leg. `Finalise` already parks
  at session end, so the unattended-dawn case needs no limit to slew.
- **A mount that stops tracking without being asked is a LIMIT EVENT, not a fault**
  (`MountLimitKind.DriverEnforced`): `Session.DetectDriverEnforcedStop` latches it and ends the run
  instead of `EnsureTrackingAsync` fighting the driver's own stop. Gated on not-slewing (a Synta goto
  is "running, not tracking"), debounced over two polls, every session-side stop raises
  `_mountStopCommanded` first, and the poll reads `IsSlewing` BEFORE `IsTracking` (on SkyWatcher the
  former resumes the latter). **The imaging `while` leaves on the first "not tracking" read, so an
  undecided exit polls the detector again.** An RA pulse on a STOPPED SkyWatcher axis runs it in
  constant-speed mode (`_raPulseOnStoppedAxis` masks it); `LimitReached` stops the guider.
- **SkyWatcher chooses its axis solution per goto** from the target's HA (east -> through the pole),
  decided once in `BeginSlewRaDecAsync` and kept for refinement; a sync keeps the half the Dec encoder
  is in, and `GetSideOfPierAsync`/`StepsToRa` REPORT from that encoder. Before this every target took
  the straight solution and a "flip" re-slewed to identical steps.
- **Two test traps:** `default(PointingState)` is `Normal`, the state in which the meridian test is
  SILENT, so an unconfigured mock passes with enforcement deleted; and a test must place the mount by
  SYNC, not slew (a slew only begins, and these tests run no clock pump), with tracking switched ON
  explicitly (a fresh test session has never initialised a mount).
- **The verdict is telemetry:** `ISessionTelemetry.MountLimitVerdict` -> `MountLimitDto` (nullable,
  older nodes read `Clear`) -> mirror -> `LiveSessionState` (holder-boxed) -> Home card (the Flip
  column doubles as the limit countdown) -> both feeds, on CLASS transitions only. The editor is
  `PanelSection.MountLimits` on the profile panel, plus a "Meridian Flip" config group whose deadline
  shows the limit's clamp as a `ConfigFieldDescriptor.Caveat`.

**`MountLimitWatcher` (`Sequencing/`) is the enforcement half with no session running.** The profile
placement above makes the config APPLY to a manual slew; this is what ENFORCES it. Host-agnostic (only
`IDeviceHub`/`IDeviceDiscovery`, no ASP.NET), it matches a connected mount by the hub's own identity
rule (`Uri.DeviceKey` -- whole-URI equality skipped profiles whose mount query had drifted) against
every discovered profile's `Mount` each 5 s, rather than any single "active profile" (no such uniform
concept exists across GUI/server/CLI), and skips any mount a session already leases. Driven as a
`BackgroundService` in `tianwen-server`, and from `tianwen-gui`'s `Program.cs` through its
`BackgroundTaskTracker` (the GUI runs a bare `ServiceCollection`, so nothing starts hosted services).

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
  conditioning-plane dial was measured and rejected). Design + measurements:
  [`docs/plans/osc-narrowband-denoiser.md`](docs/plans/osc-narrowband-denoiser.md) section 1o.
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

### Hosting API (`TianWen.Hosting` + `TianWen.Server`)

Headless REST + WebSocket API plus an ASCOM Alpaca device plane, on one ASP.NET Core host. Two API
layers: **native v1** (`/api/v1/`, multi-OTA, camelCase, POST for mutations) is the session plane, and
the **ninaAPI v2 shim** (`/v2/api/`, single-OTA -> OTA[0], PascalCase, GET for everything). Run:
`dotnet run --project TianWen.Server` or `tianwen-server [--port 1888]`. **Endpoint inventory, the
Alpaca plane, the enhance endpoint and the full native-AOT rules:
[`docs/architecture/hosting-api.md`](docs/architecture/hosting-api.md).** What bites:

1. **A pushed schedule beats the target queue.** `POST /session/schedule` preserves per-filter plans,
   the planner's altitude-optimised `Start` and `AcrossMeridian`; `PendingTarget` carries none of those
   and `/session/start` stamps `Start = now` on whatever it drains (schedule first, queue as fallback).
   Never route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** A session answers a
   prompt itself only while *nothing* is subscribed, which is what keeps unattended runs from blocking
   on a step nobody will perform. `EventBroadcaster` is a subscriber, so it restores the guarantee: no
   WebSocket client attached -> answer immediately with
   `SessionPromptEventArgs.DefaultIfUnanswerable`; one attached -> hold indefinitely, with no timer.
   The only bound is liveness. **Any new subscriber on a headless path owes the same.**
3. **The JSON contract uses numeric enums** (no `JsonStringEnumConverter` on `HostingJsonContext`), so
   a `required` enum on a request DTO is hostile to hand-written callers -- default it.
4. **Previews go through the shared stretch, never a private one.** `PreviewEncoder` runs
   `StretchSolver` + `Image.RenderStretchedRgba`, the same pipeline as the GPU viewer and the TUI. The
   shim once divided by `Image.MaxValue` and called it an auto-stretch, which renders a linear sub
   near-black. It also only ever *reads* the session frame, because `LastCapturedImages` pins a
   recycled camera buffer.
5. **The Alpaca plane is a DEVICE plane and cannot become the session plane** (Alpaca has no vocabulary
   for phase, schedule, prompts, autofocus or flats, and no Guider device type). Ownership there is the
   hub lease, not an Alpaca policy: actuation and `Connected=false` answer `0x40B`, reads and
   `Connected=true` always pass -- never make the plane read-only during a session. Device numbers come
   from the **ACTIVE PROFILE, in profile order**, never from discovery.
6. **AOT is not verified by `dotnet build`.** The trim/AOT warnings only surface on `dotnet publish -r
   <rid>`, so verify an endpoint change by *publishing* `TianWen.Server` and curling the binary. Three
   standing rules: RDG must stay enabled in the **`TianWen.Hosting` library** (that is where the `Map*`
   call sites are), both JSON contexts stay registered via `ConfigureHttpJsonOptions` (this is what
   makes request-body binding AOT-safe), and **never reintroduce a `ResponseEnvelope<object>` or an
   anonymous-type payload** -- register a concrete DTO.

### Remote Rigs (mirror another node's session "as if local")

[`docs/plans/remote-profile.md`](docs/plans/remote-profile.md) is complete P1-P5. A GUI can bind
another TianWen node (`tianwen-server`) and render its session through the same tabs that render a
local one, via **`TianWen.Hosting.Contracts`** (wire DTOs + the shared `HostingJsonContext`) and
**`TianWen.RemoteClient`** (`TianWenNodeClient` REST, `TianWenEventStream` WebSocket,
`RemoteSessionMirror`).

**The overlay model is the whole design: selecting a rig changes what you look at, never what this
node owns.** A remote connect starts a read-only HTTP mirror (no lease, no hardware touched); local
opens drivers. The single-session invariant is per NODE. `RemoteRigBinding` persists on a stable
`NodeId`, **never** an address -- the address resolves per connect from the LAN peer table with the
stored `LastAddress` as only a hint, so a DHCP-lease change reconnects on its own.

**One `LiveSessionState` per view context**: **Active** (what renders), **Local** (this node's own
hardware -- every quit/park/disconnect path belongs here, the only capturable one), **All** (poll +
redraw). Reaching for Active where Local is meant is how a remote view ends up parking the local
mount.

**`ISession`/`ISessionTelemetry` split**: telemetry is the wire-crossable *read* surface, `Setup`
stays local (live driver instances). `RemoteSessionMirror` implements telemetry, which is why the
Live Session and Guider tabs render a remote rig with no knowledge it is remote.

**Two wire traps:** never put `required` on a nullable wire property (`WhenWritingNull` omits it,
making the payload undeserializable by its own contract); a non-finite double is a bodiless 500 for
the WHOLE endpoint (one NaN altitude kills `/state`) -- route through `ForWire`, derived from
`NumberHandling` so the two cannot drift.

**Polling is authoritative; the WebSocket is a latency hint, not truth** -- one reference-write DTO
swap, no field tearing. `NodeResult<T>` carries a status code because **404 is not unreachable** (the
node answering "no session"), so `LastContactUtc` stamps there too. The outstanding prompt rides on
`/session/state`, not only the event stream, so a late-attaching client can still unblock a rig.

**Every request has a time budget** (state poll 5 s, preview 30 s, control 10 s) behind a 60 s
`HttpClient` backstop -- a switched-off rig black-holes packets rather than refusing. Budget expiry
and caller cancellation both surface as `OperationCanceledException` meaning opposite things: keep
`when (...)` filters on the **original** token, never the linked one.

**Profile switching is gated** (`ProfileSwitchGate`): refuses while connected/running, or where
drivers would strand in the hub.

**The Home tab** (`Ctrl+H`, first in `TabOrder`) is the multi-rig dashboard: phase / progress /
cooling / flip / guide RMS / HFD / notification + an outstanding-prompt badge (a prompt blocks a rig
*indefinitely* and was otherwise visible only on the selected rig). The TUI renders the same tree, so
a card change lands on both surfaces or neither. Four rules:

- **It is a read-only PROJECTION, structurally** -- `HomeBoard.BuildCards` draws only from the
  `ImmutableArray<RigCard>` snapshot, never `RemoteRigRegistry` or a live `LiveSessionState`. A card
  click changes which rig you look at; nothing on it connects, commands, or takes a lease, and
  previews stay OFF by default.
- **A prompt's age is the raising node's truth** (`RaisedUtc`, nullable) -- never substitute "when
  this client first saw it", which resets on restart.
- **`GET /api/v1/session/profile`** reports which profile a node runs, cached per connection -- the
  LAN beacon is not a second home for it.
- **A dark rig is polled less often** (doubling to a 30 s cap); each mirror owns its own poll loop, so
  one offline node cannot stall the others, and a 404 resets the backoff (an idle rig is a healthy
  rig).

**Sidebar icon convention.** Every tab glyph is a bare codepoint with no variation selector (VS16
emoji render inconsistently), written as backslash-U escapes. **Adding a tab touches six places:**
the `GuiTab` enum, `TabOrder`, `TabChrome`, the Ctrl+letter map, two `VkGuiRenderer` switches, and
`GuiTabNavigationTests.TabOrder_IsTheSidebarLayoutOrder` (pins the order, will go red by design).

### Colour Theme (`GuiTheme`, four states incl. Night)

`GuiTheme` (`TianWen.UI.Abstractions/GuiTheme.cs`) owns the one palette every surface paints with;
`UiThemeState` is **System / Light / Dark / Night**, and `GuiTheme.Apply(state, desktopIsDark)`
resolves + swaps it in as a single reference write. `Palette` is one reference read, never torn. The
source XML comments carry the full rationale (including the scotopic-sensitivity numbers); read them
before changing a colour. Full design + phasing: [docs/plans/colour-theme.md](docs/plans/colour-theme.md).

- **Anything that CACHES a projection of the palette owes `GuiTheme.PaletteGeneration` in its cache
  key.** `Apply` bumps the generation only when the resolved palette actually moves. This is the
  frozen-snapshot bug in another costume: the planner chart renders to a GPU texture keyed on the data
  it draws, a theme switch changes none of that data, so the chart kept painting the old palette after
  F12 while every other surface moved. `Apply`'s `bool` return ("did it change") cannot fix a cache
  (the consumer may not have been asked), but a generation is comparable later by anyone.
- **Night is not a darker Dark, and is deliberately unreachable from `System`.** F12 toggles it
  (SharpCap's night-vision gesture). Blue is **zero** throughout and green is spent only to buy hue
  separation, because red is the only cheap channel for dark adaptation; red-on-black caps at 5.25:1,
  so the whole text ladder fits under that ceiling; anything that must be READ uses `BodyText`, and
  `DimText` is reserved for chrome nobody needs to read. Two derived colours had leaked blue into
  Night and had to be fixed; derive new ones from the palette, never from a literal.
- **Judge Night at night.** A sky-map screenshot taken at 5pm shows its daylight tint, not the theme's;
  anchor the clock with `TIANWEN_NOW` before concluding a Night colour is wrong.

98 raw colour literals remain of an original 317, all categorical or two-trace series by design.

### Desktop Shell: File Types, the Single-Instance Hand-off, and the MSIX Store Lane

`tianwen-fits` ships to the Microsoft Store as **Astro Photo Viewer** (the executable keeps its
`tianwen-fits` name), which is what makes the file associations worth having and also what creates the
problem underneath: once the shell opens `.fits` with us, every double-click is a fresh AOT process
with its own Vulkan device and font atlas, when what the user wanted was the file to appear in the
window already open. **The layering arguments, the two CI lanes, why the Store rather than a signed
installer, and the activation bug that shipped:
[`docs/architecture/desktop-shell.md`](docs/architecture/desktop-shell.md)**; packaging lives in
`packaging/windows/msix/` with its own README. The rules:

- **The gate is folder-scoped, and the pipe IS the lock.** `InstanceGate` (SharpAstro.AppShell) claims
  a channel built from a scope plus a normalised folder, so there is one primary *per folder* -- no
  enumeration, no registry of live instances. `--new-window` and `TIANWEN_FITS_SINGLE_INSTANCE=0` opt
  out; a bare launch never hands off.
- **Failure is never fatal.** Every failed path opens the document in this process instead. A stray
  window is a poor outcome; a double-click that does nothing is an unacceptable one.
- **Re-binding on a folder change is required, not optional.** The folder is not fixed for the life of
  the process (the open dialog and a drag-drop both rescan), so `PumpInstanceGate` releases the old
  channel and claims the new one, holding none if it is already taken. A gate still answering for a
  folder the window no longer shows is worse than having no gate.
- **Activation is `sdlWindow.Activate()`** -- AppShell's extension on `IActivatableWindow`, never a
  local copy of it, and never either obvious spelling. Raising alone moves focus WITHOUT un-minimising
  (so keystrokes go to a window parked off-screen at -21333,-21333), and restoring first
  un-*maximises*, **which shipped to the Store as "opening a second file un-maximises my window"**. It
  restores ONLY if the window is minimised; the compound state needs no special case.
- **Two silent MSIX traps.** A package with no `resources.pri` resolves NO qualified resource and the
  only symptom is the icon coming out at the wrong SIZE. And `-AllowUnsigned` cannot install a package
  carrying a Store identity (0x80073D2C) -- to test locally, sign with a certificate whose subject
  matches the manifest Publisher.
- **The toolkit owns the translation, not the app.** `SdlVulkanWindow : IActivatableWindow` lives in
  SdlVulkan.Renderer (7.23+) so no application writes an adapter; the concepts live in
  SharpAstro.AppShell; each host's `Program.cs` carries only policy (the scope, whether `--new-window`
  applies). Do NOT state the rule twice -- a dependency-free convenience copy on `SdlVulkanWindow` is
  the same two-copies-of-a-rule failure that caused the activation bug, moved up a layer.
- For an UNPACKAGED install `FileAssociationRegistrar` still does the registering. Neither route can do
  better on Windows 10/11: they make the app a *candidate*, and the user assigns the default in
  Settings.

### Image Pipeline & Buffer Lifecycle

Camera → `ChannelBuffer` → `Image` → consumer → `image.Release()` → camera recycles. See
`ChannelBuffer` XML doc for ownership semantics.

**Who owns a frame is stated in ONE place: the `<remarks>` on `Image`** (P0 of
[docs/plans/frame-lifecycle.md](docs/plans/frame-lifecycle.md)), and every producer's own doc names
the convention it hands out and points back there. The vocabulary is **own / borrow / consume**:
`Release()` spends ownership, `TryLease` is the borrow, `Adopt*` and `*Into*` consume. Four
conventions coexist -- driver-owned (1), self-owned (2), pool-owned (3), consumed-input (4). A fifth,
**identity-or-copy-decided-at-runtime, was not a convention but the absence of one** and P1 retired
all fourteen of its guards. **Never derive "may I release this?" from a `ReferenceEquals`**:
ownership is a property of the handoff, and the two coincide only while one thread owns the whole
chain. Getting it wrong is silent -- it corrupts a stack rather than throwing.

**The retirement generalises, so use it rather than re-deriving it:** in every case the answer was
already in hand one branch earlier (`Blend < 1f`, `channels == 1`, `options.IsNoOp`, the assignment
that replaced an accumulator), so ask THAT; and where a producer offers no such predicate, make it
**consume** its input instead (which is why `RawLightDecoder` has no guard at all). Reference checks
that survive are asking a DIFFERENT question -- an enhancer declining a plate, a display-identity "is
this a new frame to upload?", the flat preview's slot swap -- and must not be mechanically converted.
- Never hold an `Image` from `GetImageAsync` longer than needed; it pins the camera buffer
- **`Image`'s primary ctor takes `ImmutableArray<Channel>`** (2026-07-06): per-channel
  `Filter`/min/max live on each `Channel` (via `Image.GetChannel`), image-wide
  `MaxValue`/`MinValue` are the derived extrema, and a camera's ref-counted buffer travels ON
  its channel (`Channel.Buffer`, harvested by the ctor). The raw `float[][,]` signature survives
  as a delegating legacy overload (stamps image-wide values on every channel). Never
  re-introduce an attach-after-construct buffer step (`WithChannelBuffers` is gone), and a
  rewrap that shares arrays (e.g. `ScaleFloatValuesToUnitInPlace`) must set `Buffer = null`;
  release responsibility stays with the original image (double-release guard, pinned by
  `ImageChannelCtorTests`).
- Viewers never CPU-debayer: the raw RGGB mosaic is uploaded as-is and the GPU shader debayers
  (`LiveFramePreviewSource.AcceptFrame`, `AstroImageDocument`). CPU `DebayerAsync` is for batch
  paths (stacking, planetary master, tests). `Image.DebayerIntoAsync` (the write-into-caller-buffers
  variant) currently has **zero callers**: wire it or delete it, don't cite it as the viewer path.
- `Array2DPool` is for scratch only: camera buffers use `ChannelBuffer`/`_freeBuffers`
- **A buffer nobody released is findable in DEBUG**: `ChannelBufferLeakTracker` holds a weak-referenced
  table of unreleased `ChannelBuffer`s attributed to their producing site by caller info (no finalizer,
  no strong reference -- either would change the GC behaviour it measures), and
  `StackingPipeline.RunAsync` warns at `[end]` when anything is outstanding. It is compiled out of
  Release, which is what the main CI leg builds, so `dotnet.yml`'s `test-unit` runs a second **DEBUG
  leg** selecting `--filter "Category=DebugOnly"`; a DEBUG-gated suite joins it by carrying that trait,
  and the step reads the executed count back from the TRX and **fails at zero**, or a renamed trait is
  a green no-op.
- The recycle loop is complete for DAL (ZWO/QHY), Fake, Alpaca and ASCOM; Canon wraps its RAW decode
  output (no recycle, deliberate). Coverage matrix + the by-design consumer copies:
  [docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).
- **`Channel.MaxValue`/`Image.MaxValue` is the peak pixel actually OBSERVED in that frame**, not the
  sensor's saturation level; it varies frame to frame with scene brightness, seeing and hot pixels.
  The fixed value travels separately as the optional `ImageMeta.SensorFullScaleAdu` (from
  `ICameraDriver.MaxADU` at the `GetImageAsync` choke point, or a FITS `SATURATE` card on read).
  **Two "full scale" numbers exist and must not be conflated:** the FITS/BITPIX *container* width
  (`BitDepthEx.UnsignedFullScale` = 65535 for Int16), the right divisor for **N.I.N.A.-recorded** files
  because N.I.N.A. multiplies on recording; and the *native ADC* resolution (`AdcResolution`, 16383 for
  the ASI533MC Pro), which is what the vendor SDK hands TianWen, because it does **not** left-shift on
  capture. Never infer the SDK's delivered scale from third-party capture files, and never route a
  native ADC depth through `BitDepthEx.FromValue` (it silently falls back to the container width).
- **`Image.UnitScaleDivisor` is the single source of truth for [0,1] normalisation**
  (`SensorFullScaleAdu` when known, clamped to never go below the observed peak, else `MaxValue`).
  A private `1/MaxValue` in any normalisation path diverges the moment `SensorFullScaleAdu` is
  present; `TiffRoundTripTests` is the guard. The measurements, the N.I.N.A. combing evidence and the
  rescale rules: [docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).

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

`ParseImageMetaFromHeader`, called by BOTH `Image.TryReadFitsFile` (pixels) and
`Image.TryReadFitsHeader` (headers only, what the calibration scan walks). They used to be separate
copies of the same ~35-card parse ending in identical `new ImageMeta(...)` blocks, and had drifted
into three dead locals and two defects: a `PIXSCALE` parsed and dropped by one path and unparsed by
the other, and a fallback list reading `{ EXPTIME, EXPTIME, 0 }` that made `EXPOSURE` dead everywhere
-- so a frame carrying only that card read as a **zero-second exposure**, which `MasterGroupKey` then
used to choose its dark. **A card added to one read path is a bug in the other**;
`FitsPixelScaleTests.TheTwoReadPathsAgreeOnEveryMetadataField` compares the whole record so the next
divergence fails on its own. Full write-up:
[docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).

- **A declared pixel scale beats `FOCALLEN`, which is only ever a hint** (it is whatever a human typed
  into a capture profile; on the 10P set it read 205 mm for a 202.5 mm rig, and the solver recovered
  202.4 mm from the stars alone). `Image.GetImageDim` prefers `ImageMeta.DeclaredPixelScale`
  (`PIXSCALE`, else `SCALE`), falls back to pixel size x binning x focal length, and returns `null`
  rather than guess when it has neither.
- **`DeclaredPixelScale` and `DerivedPixelScale` are in different conventions.** The declared one is
  the ACTUAL image scale and already includes binning; the derived one is per unbinned photosite.
  Collapsing them into one property double-counts `BinX` on a binned frame.
- **A light carries the guiding quality of ITS OWN exposure** (`ImageMeta.Guiding`, `GuidingStats`;
  `GUIDERMS` / `GUIRMSRA` / `GUIRMSDE` / `GUIDEPK` / `GUIDEN`, arcsec). `GuideStatistics.OverExposure`
  reduces `Session.GuideSamples` over `[ExposureStartTime, +ExposureDuration]` and **never a rolling
  session average** -- that answers "how is the rig doing tonight", which is a different question and
  is actively misleading stamped on a sub taken during the other hour. Three rules: **settling/dither
  samples inside the window are INCLUDED** (a live guiding display excludes them because a dither is a
  commanded move, but if the guider had not settled while the shutter was open the sub IS smeared, and
  filtering makes the worst frames report the cleanest numbers); **null is not zero**, so an unguided
  rig writes NO cards rather than `GUIDERMS = 0`, which would claim perfect guiding; and `GUIDEPK`
  earns its keep because RMS hides the single gust that trails one sub, which is the defect it is
  worst at describing. Nothing else in the wild writes these cards -- a survey of the reference archive
  found zero guiding keywords across N.I.N.A.- and SharpCap-authored lights -- so they are ours.
  The session stamps `ICameraDriver.GuideStats` just before `GetImageAsync`, since the statistic is
  only complete once the shutter closes and that call is the one place an `ImageMeta` is built.
  Pinned by `GuideStatisticsTests` + an end-to-end `SessionImagingTests` case.

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

**Magick.NET is no longer a dependency anywhere in this repo** (no PackageReference in any
project incl. tests; remaining "Magick" strings are historical comments). Float32 TIFF I/O goes
through `SharpAstro.Tiff.TiffWriter`/`TiffReader` (`Image.Export.cs` / `Image.Import.cs`); imports
route through the SharpAstro codecs facade (extension-based, no Magick fallback; CR2/CR3 →
FC.SDK.Raw, FITS → FITS.Lib).

**These types are NOT in DIR.Lib, though they used to be.** DIR.Lib 3.0 extracted the codec layer
into the `Codecs` repo's own packages and 4.0 dropped the dependencies outright, so the namespaces
TianWen imports are `SharpAstro.Png` / `.Jpeg` / `.Tiff` / `.Color.Icc` / `.Jxr` / `.Exr` / `.Exif` /
`.Codecs`, pinned as one family through `$(SharpAstroCodecsVersion)` (see the sibling table above,
which lists the same family). A `DIR.Lib.Tiff.*` or `DIR.Lib.Color.*` reference anywhere is a stale
name, not a second implementation.

**The on-disk convention is `[0, 1]` file values, always** -- it predates the facade swap and must
not regress, because libtiff-HDRI readers (ImageMagick-based) and scientific tools (`tifffile`,
PixInsight, ImageJ) disagree about what a float TIFF's pixel values mean, and `[0, 1]` is the one
range both read correctly. Rationale, the `SMinSampleValue`/`SMaxSampleValue`/`Q16HdriQuantumMax`
mechanics and the round-trip guards:
[docs/plans/image-codecs-facade.md](docs/plans/image-codecs-facade.md).

**The codec surface these paths rely on** (managed PNG/TIFF encode + decode incl. 16-bit, cICP,
`iCCP`, the bundled `IccProfiles.SRgbV4`, host-order byte swapping and multi-page chains) is
inventoried in [docs/plans/image-codecs-facade.md](docs/plans/image-codecs-facade.md); the current
pins live in `src/Directory.Packages.props`.

### FITS Viewer Widget (`ImageRendererBase<TSurface>`)

The renderer-agnostic viewer (shared by `tianwen-fits` and the GUI 🪐 tab via the `VkImageRenderer`
concretion) is a `partial class` split **by concern** across files -- `ImageRendererBase.cs` holds the
abstract GPU seam + `Render` orchestration + shared fields/colours + the text helpers, and one file each
for `.Layout` (`ComputeLayout`/placement), `.Toolbar`, `.FileList`, `.Overlays` (grid + star + object +
WCS annotation), `.Histogram`, `.InfoPanel` (incl. WB + wavelet sliders), `.StatusBar`, `.Transport`
(SER scrub) and `.Input`. Add a new concern as a new partial; don't grow the core file back into a
monolith. The whole chrome is arranged from ONE layout pass rooted at `ContentRegion` (see the
`.Layout.cs` banner) -- never hand-place chrome at `(0,0,Width,...)`.

**One slider widget, and it lives in DIR.Lib now** (`PixelWidgetBase`, upstreamed out of TianWen) --
the WB sliders, wavelet-layer sliders and SER transport scrub all reuse `DrawTrackSlider(...)` /
`TrackFrac(RectF32, px)`. A new track-style control calls these; never re-triplicate the
bar/fill/handle/clamp math. Details: [docs/architecture/widgets-and-controls.md](docs/architecture/widgets-and-controls.md).

**GPU resource lifetime has its own doc, and the incidents behind every rule are in it:
[`docs/architecture/viewer-gpu-lifetime.md`](docs/architecture/viewer-gpu-lifetime.md).** Four rules:
never call `UploadDocumentTextures` from a render callback -- textures upload in `PrepareFrame`,
between frames only (a Store-viewer `VK_ERROR_DEVICE_LOST` regression, pinned by
`ANewDocumentIsUploadedBeforeTheLayerPassThatSamplesIt`); never destroy a Vulkan object a frame may
have bound or write a shared descriptor set from an upload path (`VulkanContext.DeferDestroy` +
one-sampler-set-per-frame-in-flight since SdlVulkan.Renderer 7.28); a window resize is a distinct
GPU-lifetime path from a document swap (drive maximize/restore, not just file loads); and the cached
image layer samples in TEXTURE space, so divide UVs by the CAPACITY, not the requested size (pinned
by `TheBlitSamplesInTextureSpaceWhenTheTargetIsLargerThanTheLayer`). **Run the viewer under
`SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` and read `validation_report`** whenever this area is
touched.

**One viewer (no mini viewer).** The Live Session preview, polar-align and guide-cam all host this same
full viewer configured chromeless (`ViewerState.HideChrome` drops the toolbar/status rows). The feed is
`LiveFramePreviewSource : IPreviewSource` (`TianWen.UI.Abstractions`): it normalises each camera frame to
`[0,1]` and keeps a subsampled median/MAD stretch-stats scan (NOT the heavy
`AstroImageDocument.AdoptImageAsync` per frame), with `AcceptFrame(image, freezeStats)` doing the freeze
(`ViewerState.FreezeStretchStats`, set from polar phase; one-shot recompute on the off->on edge). Its
`ComputeStretchUniforms` delegates to the shared static `AstroImageDocument.ComputeStretchUniforms`. A
document-less live source has no `document.Wcs`, so `ImageRendererBase.OverrideWcs` supplies the WCS for
the GPU grid + `WcsAnnotation` overlay. Embedded hosts call `SetSurfaceSize(w,h)` each frame (the GPU
projection dims, NOT `Resize`/`OnResize`, since they share the host renderer's surface) and draw any
reticle/rings on top after `Render` returns. **`LiveFramePreviewSource.PerChannelBackground` must be
non-empty + channel-sized** -- `ComputePostStretchBackground` indexes `[0]` unconditionally (an empty
array crashed the GUI; pinned by `LiveFramePreviewSourceTests`).

### The star field is culled on TWO axes, and both are load-bearing

`StarMagnitudeIndex` + `StarChunkIndex` (`TianWen.UI.Abstractions`) are the one implementation, shared
by `VkSkyMapPipeline` and `WebGlSkyMapPipeline`: group the buffer by sky region, sort brightest-first
within each region and index it into a 0.5-mag prefix table, then draw only the regions the view cone
reaches and only their prefix. No per-frame CPU pass, no re-upload. Measurements, the WebGL2
`firstInstance` workaround and the two cull details that bite when you touch them (the allocation-free
struct-comparer sort, the ~8-degree cone radius that makes a tight-cull assertion answer "nothing is
visible"): [`docs/plans/web-tycho2.md`](docs/plans/web-tycho2.md).

- **Neither axis covers the other.** Magnitude bounds a WIDE field (~3% of Tycho-2 at 60 degrees) and
  stops bounding anything as the effective limit climbs with zoom (81% at V<=12); the cone bounds a
  deep zoom and does nothing at full sky. Shipping only the magnitude half leaves a one-degree field
  submitting ~2M instances for a patch of sky that holds almost none of them.
- **Submitting the whole ~2.5M-star Tycho-2 buffer every frame is not merely slow.** On the desktop
  the unbounded form **TDR'd an Adreno X1-85**, which is why `VkSkyMapPipeline` has culled for a while;
  the WebGL pipeline was written without it and a trace of a real drag showed the GPU process 59% busy
  with **944 of 1287 frames dropped**. Fixed by sharing the arithmetic, not reimplementing it.
- **A limit past the last bin must clamp to "everything", never wrap to zero** -- the effective limit
  climbs with zoom and readily exceeds the table's 15-magnitude span, and zero there blanks the star
  field at exactly the zoom the user cares about. Pinned by a `[Theory]`.

### A quantized cache key must not derive its grid from a continuous input

The object-overlay candidate cache exists twice (`SkyMapTab.BuildOverlayKey` on CPU/browser,
`OverlayGatherKey` on the desktop GPU path) and **both** quantized the view centre into `FOV/8` cells
using the **raw** FOV while separately bucketing the FOV itself. So during a zoom the grid rescaled
continuously and the rounded centre moved on **every event** with the centre held still: the
quantization quantized nothing. **Take the step from the BUCKETED value.** Measured 69 gathers against
8 over one pinch, while a pure pan cost 3 -- the asymmetry is the tell, because the cache was designed
for pan and tested by panning.

- **Assert the gather COUNT, never the output.** A stale-keyed rebuild draws the byte-identical frame,
  so nothing observable separates a cache that holds from one that misses per event. Hence
  `SkyMapTab.PrimOverlayGathers`, the same reasoning as `SkyMapState.PlanetCacheRebuilds`.
- **A bound like `gathers <= 12` passes on `gathers == 0`.** `ShowObjectOverlay` is off by default, so
  the first version of the E2E turned nothing on and passed with the fix reverted. Such a test needs
  `gathers > 0` beside the bound, and must be seen to FAIL with the fix removed.
- Why it presented as jank in the browser and as a never-settling background walk on the desktop:
  [`docs/plans/web-showcase.md`](docs/plans/web-showcase.md).

### The web host paints per event, so continuous gestures must coalesce onto rAF

There is no render loop in the browser build: every input handler ends in a synchronous full repaint,
which is right for a one-shot event and waste for a **continuous** gesture (measured: 1096 of 1535
move-driven repaints, 71%, superseded inside their own 16.67 ms window). `RequestRenderCoalesced()`
marks dirty and schedules one repaint via `wwwroot/raf-pump.js`, and is for `OnPointerMove` / `OnWheel`
/ `OnPinch` **only**. **Clear the flag BEFORE painting, and on the schedule-failure path** -- a latched
flag is a permanently frozen canvas. **A trackpad pinch is `ctrl`+`wheel`, a different path from the
touchscreen pinch** (Blazor `@onwheel` vs the canvas touch bridge), so it is never covered by anything
done to the bridge, and it is the densest gesture the app sees. Details:
[`docs/plans/web-host-carve-out.md`](docs/plans/web-host-carve-out.md).

### Sky Map / FITS Viewer GLSL (pre-baked SPIR-V, no runtime shaderc)

TianWen.UI.Shared's Vulkan shaders (`VkFitsImagePipeline`, `VkSkyMapPipeline`) are authored as GLSL
450 **files** under `src/TianWen.UI.Shared/Shaders/*.vert|*.frag` and **pre-baked to SPIR-V at
build-host time** (`Shaders/spirv/*.spv`, committed + embedded) by `tools/BakeShaders`. The pipelines
load the embedded `.spv` at runtime (`LoadShaderModule`); there is **no runtime shaderc**. This was
forced when SdlVulkan.Renderer 6.23 dropped the transitive `Vortice.ShaderCompiler` it used to
provide, and is required for **Android** (shaderc ships no android RID) + trims AOT / first-frame cost.
Mirrors SdlVkR's own `tools/BakeShaders`.

- **Edit a shader → re-bake → commit the `.spv`.** After changing any `Shaders/*.vert|*.frag`, run
  `dotnet run --project tools/BakeShaders -c Release -- src/TianWen.UI.Shared/Shaders` and commit
  `Shaders/spirv/*.spv`. The build emits **warning TWSH0001** when a source is newer than its baked
  `.spv` (never fails), so a forgotten re-bake is caught.
- **ASCII only.** shaderc's lexer rejects non-ASCII bytes even inside `//` comments (a stray em dash
  reports as "unexpected end of file"). BakeShaders warns on non-ASCII; keep shader files ASCII.
- The stereographic-projection GLSL (`stereoProject`) is currently **inlined** into
  `skymap_star.vert` / `skymap_line.vert` / `skymap_overlay.vert` (it was a shared C# const
  substituted at runtime via a `PROJECTION_PLACEHOLDER` token; the bake inlined it). Restoring a single
  source (a BakeShaders placeholder / `#include` step) is a deferred cleanup.

`Image.StretchValue()` is the single source of truth for the scalar stretch math (normalize → subtract
pedestal → rescale → MTF). Don't reimplement it.

### Stretch Pipeline: CPU/GPU Mirror

The stretch pipeline runs in two parallel implementations that must produce visually equivalent
output for the same `StretchUniforms`:
- **GPU**: `TianWen.UI.Shared/Shaders/image.frag` (loaded by `VkFitsImagePipeline` from the baked
  `Shaders/spirv/image.frag.spv`): `stretchChannel` (per-channel) + the Luma branch that mirrors
  `StretchLumaPixelCpu`; used by the live FITS viewer.
- **CPU**: `Image.StretchChannelCpu`, `Image.StretchLumaPixelCpu`, `Image.ApplyHdr`,
  `Image.ApplyCurveLut`, `Image.ApplyBoost`, `Image.RenderStretchedRgba`; used by
  `ConsoleImageRenderer` (TUI Sixel) and tests (`StretchTests_NewPipeline`). Never use the GPU.

Pipeline order in both: pedestal subtract → bg neutralization → WB → shadow/rescale → MTF →
luma blend → curves (LUT or boost) → HDR knee → normalize → clamp. Per-channel for
Linked/Unlinked, luma-Y'/Y for Luma. In Luma mode the producer always populates BOTH
`StretchUniforms.LumaStretch` (scalar Luma MTF params) AND per-channel `Shadows/Midtones/Rescale`
(linked branch params) so the shader can blend between them via `LumaBlend`.

**The subject in full, with the measurements: [`docs/architecture/stretch-pipeline.md`](docs/architecture/stretch-pipeline.md)**
(and `stacking-render-pipeline.md` sections 5-6 for the solve + per-pixel order). The rules:

- **Wire a new stage into BOTH the GLSL shader AND the CPU helpers.** A stage that only exists in
  GLSL is a regression for the tests and the TUI. `StretchTests_NewPipeline` is the end-to-end guard
  (TIFF + JPEG per case into the temp output dir; assert per-channel means stay inside
  `(epsilon, max-epsilon)`, which is what catches a channel collapse).
- **`AstroImageDocument.ComputeStretchUniforms` is the SINGLE producer of `StretchUniforms`**, and it
  scales per-channel stats by WB before deriving shadows/midtones/rescale so the post-WB norm and
  shadow share one coordinate space (as does `ConvergeStretchFactor`).
- **`Linked` and `Unlinked` mean what they mean in PixInsight, and the difference lives ENTIRELY in
  the uniforms** -- neither the GLSL nor `StretchChannelCpu` branches on the mode, so `StretchSolver`
  is the only place the distinction exists and the only place it can silently collapse. Linked writes
  ONE curve into all three slots (from the mean of the per-channel WB-applied medians and MADs) so a
  white balance survives as colour; Unlinked writes each channel's own auto-normalised curve, which
  absorbs the calibration and neutralises the background -- that is what the mode is FOR.
  **Never re-derive a per-channel curve in the Linked branch.**
- **`StretchMode.Auto` is a UI intent, resolved before any `StretchUniforms` is built, never a shader
  mode.** `mode.ResolveAuto(isColour, calibrationActive)` (`ViewerActions`) is the one resolver;
  `ViewerActions.DefaultStretchMode` is the single VIEWER default (= Auto), while
  `MasterPreviewRenderer` / `PreviewEncoder` render **Linked explicitly**. A test or renderer that
  needs a fixed curve passes an explicit mode, never Auto.
- **Background neutralisation is solved for a neutral POST-WB background, so its gains depend on the
  calibration**, and **anything caching them owes the WB in its cache key**. They print at F4 (affine
  about 1.0, so F2 shows three 1.00s).
- **The SPCC / Calibrate toggle gates the RENDER, not the measurement** (`applyColorCalibration`), W
  is a **toggle**, and an AI enhance **INHERITS** the WB triple (`InheritColorCalibration`) rather
  than re-fitting SPCC on deconvolved pixels; background neutralisation is re-solved per document.
- **The manual WB is a SEPARATE multiplier from the auto calibration** (`shaderWhiteBalance` = auto x
  manual; only the auto half scales the stats), the sliders show the composed EFFECTIVE triple via
  `StretchSolver.ComposeWhiteBalance`, and their travel is their own `[0.25, 4]` constant, never
  `GrayWorldWhiteBalance`'s estimator clamp. **WB is applied in the `StretchMode.None` linear path**
  too (a SER opens linear), mono excepted.
- **Luma weights live in `StretchUniforms.LumaWeights`** (default Rec.709, `SensorMatched` resolved
  from the sensor QE x CFA integral); never hardcode Rec.709 in a consumer. Post-stretch normalize
  rides on `NormalizeScale` (default 1.0 = no-op).

### Layout DSL (`DIR.Lib.Layout`)

GUI/TUI panels are built from a surface-agnostic declarative layout engine in DIR.Lib (`DIR.Lib.Layout`):
author a tree of immutable `Layout.Node` records, `Layout.Engine.Arrange` measures + arranges it, and
`PixelWidgetBase.PaintLayout` draws + binds clicks **from the same arranged rect** (draw == hit by
construction; no second hit-rect arithmetic that can drift). The engine + DSL reference lives in
**DIR.Lib's README**; the engine features TianWen leans on, the five traps in full, the TUI row contract
and the pointer-cursor rule are in
[`docs/architecture/widgets-and-controls.md`](docs/architecture/widgets-and-controls.md) -- read it
before any layout work. The short form:

- **Build trees with the `Layout.Builder` DSL, never `new Layout.Node.X { }` initializers or `cursor += h`
  placement.** Factories: `Layout.Builder.VStack/HStack/Text/Box/Fill/Spacer/Grid/Overlay/Split/Dock(...)`.
  Chrome via fluent **instance methods** on `Layout.Node`: `.WFixed/.WStar/.RowH/.ColW/.Stretch/.Bg/.Pad/
  .Clickable/.WithGap`. Each is a pure `this with { ... }` transform emitting the same records.
- **Alias, don't import.** Keep `using DIR.Lib;` and add a per-project `global using Layout =
  DIR.Lib.Layout;` (or csproj `<Using ... Alias="Layout"/>`); write the qualified `Layout.Node` /
  `Layout.Builder`. Do NOT `using DIR.Lib.Layout;`: it drops the collision-prone barewords (`Node`,
  `Content`, `Size<T>`) into scope. A consumer owning its own `Layout` type must rename it (PTV did:
  `Layout` -> `ElementGrid`).
- **Conditional background:** `.Bg(color)` always sets a value, so for a nullable bg build the base then
  `if (cond) n = n.Bg(color);`, never `.Bg(default)` (paints transparent, not null).
- **Interactive sub-widgets** (charts, sky map) emit a `Layout.Builder.Fill(key: "...")` leaf and draw
  into its rect via the `drawFill` callback of `PaintLayout`. **A text field is NOT one of them** -- it
  is `Layout.Builder.TextInput(state, fontSize)`, see below.
- **Responsive sizing is `Sizing.Star(weight, min, max)` + `.CollapseBelow(u)` + `WrapH`/`WrapV`**, and
  orientation is a plain C# branch (the tree is rebuilt per frame). Canonical consumer:
  `PlannerTab.BuildFrameLayout`, pinned by `PlannerTabLayoutTests`.
- **Five silent traps, all found on the Home board** (measured detail in the doc above and in
  [`docs/plans/remote-profile.md`](docs/plans/remote-profile.md)): (1) `.RowH(h)` also sets
  `Width = Star` and silently eats a preceding `.WFixed(w)` -- fixed on both axes is
  `.WFixed(w).HFixed(h)`; (2) a `Stack` places children at the cross-axis START, so centring needs
  `.CrossCenter()`, never container padding or spacer sandwiches; (3) the default `Width` of a `Node` is
  `Auto`, so a container whose children are all Star arranges to nothing -- state `.WStar()`; (4) never
  pair `.CollapseBelow(u)` with a Star *minimum* on the same node, and a child that must survive takes
  NO threshold rather than a small one (the engine prunes every under-threshold child in one pass);
  (5) an icon inks the full square it DECLARES, so size a mark to the text it sits beside.
- **A mark is a `Layout.Content.Icon`, never a symbol character in a `Text` run** -- an `Icon` names a
  meaning that each surface constructs what it can draw for, whereas a glyph asks whichever face the
  host resolved and draws .notdef where it does not.
- **`.PadX(u)` / `.Pad(across, down)` for a FIXED-height bar**: padded symmetrically, a bar with no
  vertical room to give away loses its icon to a stub while text overflows and goes on looking correct.
- **`PushClip(x, y, w, h)` / `PopClip()` on the widget base**, never `Renderer.PushClip` with a
  hand-built `RectInt` (that struct takes `(LowerRight, UpperLeft)`, the opposite order to every other
  rect a widget states). Clips NEST and NARROW since DIR.Lib 7.27.
- **TUI list and tree rows are trees too, never formatted strings** (Console.Lib 4.10): a row implements
  `IRowLayout.BuildRow(in RowContext)`, states its own pen (foreground AND background), and puts an
  inline button in a `.Clickable(...)` node resolved via `ScrollableList.DispatchRowHit` -- never a
  column range computed beside the drawing code, and never width arithmetic where a min-clamped Star
  says it. Adding a capability adds a **field to `RowContext`**, never an overload.
- Engine geometry is headless-testable (stub `Layout.IMeasureContext`); the offline `RgbaImageRenderer`
  honours clipping since DIR.Lib 7.25, so a headless render agrees with the app about what was drawn.

### UI Primitives: the cursor, a text field, and who holds focus

Full reasoning:
[`docs/architecture/widgets-and-controls.md`](docs/architecture/widgets-and-controls.md) (cursor) and
[`docs/plans/automatic-text-input.md`](docs/plans/automatic-text-input.md) (field, focus, key routing).
The rules that bite:

- **The pointer's appearance is a property of a REGION, never a host predicate.** Declare it beside the
  click (`RegisterClickable(..., cursor:)` / `.Clickable(hit, onClick, cursor)` / `.WithCursor(kind)`);
  the host only asks `guiRenderer.CursorAt(x, y) ?? CursorKind.Default`. A region that states nothing is
  **transparent** to the query (`null` means nobody had a view, NOT Default), so a row inherits the
  cursor of its card. A geometric predicate needs one term per overlay and every overlay added later
  silently invalidates it. The `CursorKind` -> SDL mapping lives in SdlVulkan.Renderer, not in an app.
- **HOVER needs a z-order answer too, and it is `ViewerState.OverlayOwnsPointer`** -- unlike a click
  (paint order IS hit-test order), hover is decided at PAINT time, before the overlay above has
  registered anything. Add an overlay to that ONE property, never to a call site.
- **A text field is a declaration**: `Layout.Builder.TextInput(state, fontSize)` and nothing else.
  `PaintLayout` draws it via `TextInputRenderer`, registers the `TextInputHit` and states
  `CursorKind.Text`; `CellLayout` paints the same leaf on a terminal. Click-to-focus,
  blur-on-outside-click, Tab cycling (visual order, derived from paint order) and the I-beam all follow.
  `fontSize` is in DESIGN units -- the painter crosses `ctx.FontScale`, so a pre-scaled size applies DPI
  twice. Intrinsic width comes from the placeholder, never the live text.
- **Focus is global and that is fine, but it is not settable.** `DIR.Lib.TextInputFocus` owns the
  transition; the host binds `FocusChanged` ONCE (SDL `StartTextInput`/`StopTextInput`, the web
  `CanvasTextOverlay`) and nothing else knows those calls exist. `GuiAppState.ActiveTextInput` is a
  read-only forward to `Current`. `Focus` is idempotent (a declarative UI asks every frame);
  `BlurIfUnpainted(painted)` runs after each frame and the **caller supplies what was painted**
  (`VkGuiRenderer.PaintedTextInputs()` unions chrome + active tab -- asking one surface when the frame
  draws two blurs a field that is on screen).
- **`TextInputInteraction` (DIR.Lib) reads the focused field from `ctx.Focus.Current`**, never a
  parameter beside it; takes `KeyContext.TabFields` as a callback rather than an `IPixelWidget` (that
  interface was the one thing keeping it off a terminal); and **swallows every key while a field is
  focused**, which is what makes a field a field.

### Per-Window Widget State: `DpiScale` / `FontPath` / `EmojiFontPath` are properties, not parameters

A value **constant for the whole window** that would otherwise be threaded identically through every
widget `Render`/helper signature is a `virtual` property on `PixelWidgetBase<TSurface>` (DIR.Lib), owned
per widget instance. The host sets it once (startup + resize; the web per frame; a terminal keeps the
defaults); a composite chrome widget propagates to its children by overriding the setter; DIR.Lib's
`RenderLayout`/`ArrangeLayout`/`PaintLayout` resolve `?? DpiScale` / `?? FontPath` so a call omits them,
with `dpiScale: 1f` as the explicit device-px escape hatch and a `PixelMeasureContext` overload for the
two cases a scalar cannot express (a per-axis scale, a cell-authored tree on a pixel surface) -- build
that context ONCE and pass the same instance to Arrange and Paint, or text is drawn at a size it was
never measured at. An empty/absent font makes the text helpers no-op rather than throw.

**Do NOT reintroduce these as `Render`/helper parameters.** Per-window *constant* -> property; per-call
*derived* value stays a parameter. `fontSize` is the canonical parameter (it varies per region and is
computed inside each tab) and is NEVER a property; the two static non-widget renderers
(`AltitudeChartRenderer`, `SkyMapRenderer`) keep their font parameters and are fed the values of the
caller. Breakdown: [`docs/plans/dpi-scale.md`](docs/plans/dpi-scale.md).

**Which FACE they get is one decision in one place: `BundledFonts.Resolve()`**, which returns
`(Text, Emoji, Fallback)` **together** for all three hosts. **A direct `FontResolver.` call in
production code is a regression** (tests are exempt -- a layout test wants a deterministic
always-present face). Resolving a SUBSET is the bug it prevents: the viewer had faces but no
`FontFallback`, so it could not ask `CanRender` and gated marks on file existence, and every missing
glyph was then found visually, per glyph, by a human looking at the toolbar. Bundled first, because a
bundled face is the only one whose COVERAGE is known -- a system face lacking a codepoint draws NOTHING,
indistinguishable from a broken control. Why the shape holds, the `Lazy<FontSet>` cache, and what is
still outstanding:
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
