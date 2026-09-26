# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powershell
- Line endings are LF everywhere, in the working copy as well as the repo (`.gitattributes`
  `* text=auto eol=lf`, `.editorconfig`; settled 2026-09-11). Git always stored LF, so never
  hand-convert a file to quiet a diff -- a file that shows as modified with an empty diff is a
  stale stat entry, not a change.
- **Exit codes 127 and 13x from GUI / CLI / Server processes mean the .NET process crashed**, not
  "command not found" or "shell killed it". Always read the stderr log (e.g. `gui-stderr.log`) for
  the actual .NET exception + stack trace before drawing conclusions from the exit code.

## Project Tracking Docs

**The backlog is GitHub issues, not a file** (since 2026-09-24, when `TODO.md` and `docs/todo/*.md` were
migrated, 345 open items). Labels: `area:astrometry` / `drivers` / `guider` / `imaging` / `infra` /
`sequencing` / `ui`; `bench` (only a real device or night can answer it); `needs-triage` (the old inbox);
`priority:high` / `priority:next`; `from-todo` marks the migration. **Close an item through the PR that
does it** (`Closes #n`), so it cannot drift from the code, which is the whole reason for the move: the
same task used to be kept in `TODO.md`, an area file, a plan and a tracking issue, and a PR landing
updated at most one of them. **Never write a new open `- [ ]` into `TODO.md` or `docs/todo/`**; open an
issue (`gh issue create --label area:...`). A plan keeps the design and the *why*, and names its issue.
**The link runs both ways, down to the paragraph** (user, 2026-09-25):
- An issue for a planned feature links the plan's SECTION: a heading anchor on `main`, such as
  `docs/plans/planetary-stacking.md#e-de-rotation-winjupos-style`.
- The plan names the issue at that section.
- A bold-lead paragraph cannot be linked, so give it a heading first.
- A plan's open item with no issue is untracked work, whatever its status table says.
- A plan with open work has a **milestone** of the same name, holding its issues. A new issue joins its plan's milestone, and **a PR carries the milestone of the plan it advances** (`gh pr create --milestone <plan>`). `tools/plan-issue-report.py` (the `plan-report` skill) checks all of it.

Canonical project state otherwise lives in these markdown files; read the relevant ones before starting
non-trivial work:

| File | Purpose |
|------|---------|
| `docs/plans/summary.md` | Current status of every plan in `docs/plans/` (DONE / PARTIAL / NOT STARTED) cross-checked against the codebase |
| `docs/plans/*.md` | Per-feature implementation plans with phasing tables |
| `docs/architecture/*.md` | Architecture deep-dives, one file per subject, where a section below keeps only the rules that bite (`ls docs/architecture/` is the index) |
| `TODO.md`, `docs/todo/*.md` | The DONE archive, by area (the open items are issues now; see above). Kept because a done entry often records the measurement or the reason behind a decision |
| Issues labelled `bench` | The bench queue (was `docs/todo/hardware-validation.md`): every check only a real device or night can answer, one issue each, its gear in the body; a plan keeps the *why* and a pointer, never a second checkbox |
| `docs/known-limitations.md` | Root causes of limitations/bugs (the *why*); read before "fixing" a suspected bug |
| `CHANGELOG.md` | Released version history, newest first, one section per `MAJOR.MINOR`. A version bump adds its entry **in the same commit** (`/bump-version` step 6), so a number can never ship without its note. The GitHub Release lists the commits; this says what the release was FOR and what breaks |

**A doc never cites a commit by hash.** Every PR merges by rebase, so a hash written into a plan or
TODO before its PR lands is rewritten on landing, and the 2026-04-22 LFS migration rewrote everything
before it; sixteen dangling hashes were found in the docs on 2026-09-06. Cite the commit SUBJECT in
quotes, a release tag (`v6.3.1352`), or a PR or issue number (`#227`), all of which survive both.

## Custom Skills

Available in `.claude/skills/<name>/SKILL.md`: auto-invocable when the request matches the skill's description, or explicitly via `/<name>`.

| Skill | Purpose |
|-------|---------|
| `release-lib` | Release a SharpAstro sibling library to NuGet with full dependency chain |
| `release-tianwen` | Cut a TianWen binary release (workflow_dispatch + GitHub Release with .tar.gz assets) |
| `sibling-status` | Git status + version across all SharpAstro repos |
| `check-ci` | GitHub Actions CI status across all repos |
| `bump-version` | Bump TianWen's version: the one `<VersionMajorMinor>` in `src/Directory.Build.props` |
| `run-gui` / `run-tui` / `run-fits` | Build and launch the GUI / CLI TUI / FITS viewer DETACHED via `tools/start-app.ps1` (pid + redirected stdout/stderr paths; survives a reaped background shell) |
| `test-run` | Run a suite so a failure is always identifiable (TRX + no truncation), and hunt flakes |
| `test-filter` | Run tests matching a name pattern |
| `test-image-diff` | Diff test-output PNGs across run folders to flag visual regressions |
| `test-output-prune` | Delete old `yyyyMMdd` test-output folders, keeping the N most recent |
| `stack` | Run `tianwen stack` against a folder of FITS lights + calibration |
| `digitize-filter` | Digitise a vendor filter chart into `FilterCurveDatabase` (three chart families, the validation gates, the matcher re-check) |
| `curate-session` | File a capture session into `Astro-Organized` so a bake can use it: the four archive tiers, backfilling a filter identity by measurement, checking a calibration set against the pixels, and what to stop and report rather than guess |
| `dataset-gallery` | Build a browsable gallery of a bake's session masters (enhanced beside raw) and publish it as an Artifact |
| `plan-report` | Check docs/plans against the issues and the per-plan milestones (`tools/plan-issue-report.py`, no model involved) and publish the report as an Artifact |
| `tick-todo` | Close a backlog ISSUE (preferably through its PR) and update CLAUDE.md, the plan files and memory |

## Project Overview

TianWen is a .NET 10 library for astronomical device management, image processing, and astrometry.
Supports cameras, mounts, focusers, filter wheels, cover/calibrators, and guiders via ASCOM, Alpaca (HTTP),
ZWO, QHYCCD, Player One, ToupTek (and the ten rebadged families on its SDK: Altair, Omegon, OGMA, MallinCam,
...; only ToupTek verified), Meade LX200, Skywatcher, OnStep (serial + WiFi/mDNS), iOptron SkyGuider Pro, Gemini FlatPanel
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
├── (Directory.Packages.props)     # Centralized package versions: at the REPO ROOT, not in src/
├── TianWen.Lib/                   # Core library (net10.0)
├── TianWen.Devices.Native/        # ZWO + QHYCCD drivers, split out so Lib carries no vendor natives
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
├── TianWen.UI.Shared/             # Vulkan FITS pipeline, VkSkyMap pipeline + tab (the SDL→InputKey map is SdlVulkan.Renderer's `SdlInputMapping`)
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
**A program another one starts ships BESIDE it, in a checkout as in a release** (`src/ExeBeside.targets`,
P1 of `hardware-in-the-server.md`): `tianwen-server` is built and published into `tianwen-gui`'s and
`tianwen`'s own output (and the functional tests'), `tianwen-ascomhost` into the server's. List a new
one there as `<ExeBesideThis>`; never find a sibling's build output by walking up to the solution. A file
both outputs carry must be the same file, or the build fails naming it.

## Build & Test Commands

```bash
# All commands run from src/
dotnet build
dotnet test
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
```

**Tests run on Microsoft.Testing.Platform (MTP), not VSTest.** xunit.v3 4.x dropped the VSTest
bridge and the .NET 10 SDK refuses it outright ("Testing with VSTest target is no longer
supported"), so the opt-in lives in `global.json` -- this repo's ONLY one, and it pins no SDK
version, which is why the org rule against pinning one is untouched. Each test project is an
`Exe`, and `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio` / `coverlet.collector` are gone
(nothing ever collected coverage; `Microsoft.Testing.Extensions.CodeCoverage` is the MTP
equivalent if it is ever wanted). What changes at a call site:

| VSTest | Microsoft.Testing.Platform |
|---|---|
| `--logger "console;verbosity=detailed"` | `--output Detailed` |
| `--logger "trx;LogFileName=x.trx"` | `--report-trx --report-trx-filename x.trx` |
| `--blame-crash` | `--crashdump` |
| `--blame-hang --blame-hang-timeout 5min` | `--hangdump --hangdump-timeout 5min` |
| `Sequence_*.xml` (written on crash) | `<app>_<hash>_crash.sequence.log`, TSV, written AS THE RUN GOES and deleted when the run exits cleanly (so a crash, a hang AND a kill leave one, and a green run leaves none) |
| reading `<Counters total=` back out of the TRX to catch a filter that matched nothing | `--minimum-expected-tests N` |

**`--filter` survived unchanged, VSTest syntax and all**, as did `xunit.runner.json` and the
`[Collection]` / parallelism rules below. A stale `--logger` or `--blame-*` fails the run with an
unknown option rather than collecting less, which is the good outcome. The dump and TRX options
come from `Microsoft.Testing.Extensions.CrashDump` / `.HangDump` / `.TrxReport`, referenced by
`TianWen.Lib.Tests` and `.Functional` only, so they do not exist on the other two suites.

## SharpAstro Sibling Libraries

TianWen depends on in-house libraries published to nuget.org under the **SharpAstro** org.
Siblings, each at `../<repo>` (csproj layout varies; the full table with paths and the auto-detect
column: `docs/architecture/sibling-builds-and-releases.md`): `DIR.Lib`, `SdlVulkan.Renderer`,
`Console.Lib`, `FITS.Lib` (`../FITS.Lib`, csproj `CSharpFITS/CSharpFITS.csproj`), `FC.SDK` (no
auto-detect), `ZWOptical.SDK` (`../ZWOptical.SDK`, csproj at the repo root),
`QHYCCD.SDK` (csproj at the repo root), `SharpAstro.Fonts` (`../Fonts.Lib`, transitive), `SER.Lib`,
`Lzip.Lib`, `LAN.Lib`, `WebGl.Renderer`, `SharpAstro.AppShell` (`../AppShell`), `TianWen.DAL` (csproj at
the repo root).

**Auto-detection** (`Directory.Build.props`): a **single** property `UseLocalSiblings` gates them all.
The build switches to ProjectReference when **every** sibling working copy exists; `DIR.Lib`,
`Console.Lib`, `SdlVulkan.Renderer`, `WebGl.Renderer`, the `Codecs`-repo codec family (`SharpAstro.Tiff`,
`SharpAstro.Exif`, `SharpAstro.Png`, `SharpAstro.Color.Icc`, `SharpAstro.Jxr`,
`SharpAstro.Jpeg.IccInjector`, `SharpAstro.Exr`, `SharpAstro.Codecs`), `QHYCCD.SDK`, `TianWen.DAL`,
`ZWOptical.SDK`, `FITS.Lib`, `SER.Lib`, `Lzip.Lib`, `LAN.Lib` and `SharpAstro.AppShell`; otherwise it falls
through to PackageReference. **A sibling's FOLDER must carry its repo's current name**: the check is a
path, so a clone still under a renamed repo's old name (`zwo-sdk-nuget` for `ZWOptical.SDK`, found
2026-09-19) is a missing sibling, and one missing sibling silently puts EVERY library back on the
published package. `dotnet msbuild src/TianWen.Lib/TianWen.Lib.csproj -getProperty:UseLocalSiblings`
answers `true` or nothing. Override: `dotnet build -p:UseLocalSiblings=false`. CI always uses
PackageReference. `Fonts.Lib` is transitive via DIR.Lib's own `UseLocalFontsLib` switch. There is **no**
per-library switch anymore, so a missing checkout of *any* listed sibling flips the whole set back to
packages (all-or-nothing), which is fine on a dev box that has them all.

**The history behind the rules below -- the CPM drift, the web projects in CI, the `open-vs.ps1` /
`Exists(...)` divergence and the release traps -- is in
`docs/architecture/sibling-builds-and-releases.md`.**
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

For a library without auto-detection (`FC.SDK`, the one left),
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
`docs/architecture/sibling-builds-and-releases.md`.
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
- **A shared fixture plus an assertion about a FIRST write is an order dependency**, whether or not
  today's order satisfies it. `IClassFixture<T>` lives for the whole class, so its state carries from
  one test to the next: establish the precondition in the test (write a known-different shape, then
  the one under test) rather than inheriting whatever the previous test left. Test order is not a
  contract and it moved under us: `StretchUboChangeDetectionTests` passed for as long as an ALTERING
  test happened to run immediately before the one that needed it, and the xunit 3.2.2 to 4.x upgrade
  reordered the class and turned it red in Release only.

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
`docs/plans/device-simulator-ci.md`.

### Test Collections & Parallelism

Tests grouped into `[Collection("X")]` by functional area. **Any test that drives a `Session` belongs in
`[Collection("Session")]` -- the rule is about what a test DOES, not what it is CALLED**, and they run
sequentially so several sessions' concurrent `Task.Run` + `FakeTimeProvider` timer callbacks cannot
starve the pool. It used to be written as "all `Session*Tests`", and three classes drove real sessions
from outside every collection because their names did not match: `DeviceOwnershipTests`,
`SessionFaultCounterTests` and `SessionScoutClassifierTests`. If it calls
`SessionTestHelper.CreateSessionAsync`, it is a session test.

**A fake-clock `SleepAsync` must throw on a cancelled token, and a guider's `StopCaptureAsync` must not
return until its loop has exited**, or the next target's guide loop starts on a camera the previous one
hasn't released yet -- `DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was this, a race misdiagnosed
as starvation for a day. Full story: `docs/architecture/session-test-harness.md`.

**No wall-clock `CancellationTokenSource` timeouts** in session tests; use `[Fact(Timeout = ...)]`
(inner timeouts cause flakes). **A test that drives a whole run needs that bound**: a wedged run hangs
rather than fails, and an unbounded hang is a five-minute `--hangdump` timeout plus a multi-GB dump
instead of one red test.

**That bound is best-effort, not a guarantee, and xunit says so**: `IFactAttribute.Timeout`'s own
documentation reads "using this with parallelization turned on will result in undefined behavior.
Timeout is only supported when parallelization is disabled, either globally or with a
parallelization-disabled test collection". `[Collection("Session")]` serialises its OWN tests but
still runs alongside other collections, and neither `TianWen.Lib.Tests` nor `.Functional` sets
`parallelizeTestCollections: false` (only `.Simulators` does), so every session bound sits in that
undefined zone. Keep writing the bound, since it is the best available and it costs nothing, but do
not read a hang that outlived one as impossible, and do not quote it as the reason a past hang was
nameable without checking whether that test carried one at the time. Making it
reliable means `parallelizeTestCollections: false` on the two suites, which is a measurement, not a
one-line edit: less parallelism has twice come out FASTER here (see below).

**A `Timeout` on a SYNCHRONOUS test does nothing at all** and the analyzer now says so
(`xUnit1069`): the framework can fail the test but cannot interrupt a body that never awaits. 36
such attributes were decoration and were removed in 9.0; if you add one, the test must reference
`TestContext.Current.CancellationToken` for it to mean anything.

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

**Use the cooperative time pump** (`FakeTimeProviderWrapper.PumpUntilCompletedAsync`) for a session loop
run via `Task.Run`, and **always pass the progress probe** -- the budget bounds a STALL, not the run,
because a `PeriodicTimer` tick coalesces and registers no waiter, so an unobserved advance is budget
spent for nothing (measured 33-50 minutes of budget for one 30-minute observation). Pattern, the
measurements and why a naive `while (pumped < budget) { Advance(); }` loop is wrong:
`docs/architecture/session-test-harness.md`.

**Never** use `SleepAsync(subExposure)` in a pump loop; it advances fake time even when the `Task.Run`
hasn't been scheduled yet, causing targets to "set" before imaging starts. `Advance` fires timers
synchronously; `Task.Delay(1)` yields to the thread pool.

### Driving the GUI and the TUI unattended

Drive a full `RunAsync` session against simulated hardware with **no human in the loop and no
screenshot-poll-and-OCR**. **Every mechanism -- the fake-device URI shapes incl. `port=SkyWatcher` /
`hasCover=false`, the DEBUG inspector surfaces, the cell-buffer contract -- is in
`docs/architecture/unattended-ui-driving.md`.** The rules that bite even after reading it:

- **`ProfileData.SiteLatitude/Longitude` must match the mount URI's `latitude/longitude`** (a split
  site throws "Could not calculate timezone"); canonical wiring
  `SessionTestHelper.CreateSessionAsync(mountPort:"SkyWatcher", latitude, longitude)`.
- **Anchor the clock with `TIANWEN_NOW`** to a real night at that site, or the session stalls in
  daylight instead of leaving `WaitingForDark`. **`StartSession` needs >=1 pinned target**
  (`PlannerState.Proposals.Length > 0`); planner pins persist per-profile, so pin once.
- **Ground truth for fine telemetry is the Debug log, not the inspector snapshot.** `AppState` reads
  `LiveSessionState`, which can lag during the guide loop; per-frame guide stats, HA and pier side come
  from `%LOCALAPPDATA%/TianWen/Logs/<date>/GUI_*.log`.
- **Use `render_liveness`, not a screenshot, to decide IF the render thread is stuck** -- every
  inspector command runs ON that thread. **`validation_report` with zero messages is evidence only when
  `active` is true** (the DEBUG + `SDLVK_VALIDATION=1` gate AND `layerAvailable`); a host with no
  Khronos layer used to answer `enabled: true` with zero messages.
- **A terminal reads back as TEXT, the one thing a GPU surface cannot offer.** `screen` / `row` /
  `cell` report the **front** cell buffer, and `cell` adds the resolved pen (`#000000` on `#000000` is
  invisible on screen yet identical to a correct one in a text dump). The modifier parameter is
  **`mods`** (`"ctrl+shift"`), not a `ctrl` boolean.
- **`tianwen-fits` also runs end to end WITHOUT its window, in CI**: `ViewerE2E` constructs the same
  `StandaloneViewerHost` `Program.cs` runs (router, toolbar policy, between-frames steps) over a CPU
  surface and real files. A viewer behaviour test goes through it at DPI 1 AND 1.5, and **a new host step
  goes into `StandaloneViewerHost`, never back into `Program.cs`**, where no test can reach it.

## Coding Style

Enforced via `src/.editorconfig` (it sits beside the solution, not at the repo root):
- 4 spaces, LF line endings. Namespaces are FILE-scoped (`namespace Foo;`) in three files of four
  (1,253 / 417 on 2026-09-11; `TianWen.Lib` 648 / 79, only `TianWen.UI.Abstractions` leans the other
  way at 62 / 106): write a new file file-scoped, match the file you are in when editing, and never
  churn a file from one form to the other. This line used to say the opposite.
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
`docs/architecture/device-architecture.md`.

**Every native serial protocol has ONE architecture document** (LX200, OnStep, Skywatcher, SGP and
both Gemini devices, indexed under "Native serial protocols" in that file), and a new native driver
ships with its document in its first commit: OnStep went without one for five months and its wire
notes survived only in a commit message. Each maps every driver member to the wire, including what
it does when the device is connected and does not answer, which must be a transient THROW (#810).

**A vendor's native binaries reach an app through the REFERENCE GRAPH, and nothing downstream can
filter them out.** The ZWO and QHYCCD drivers therefore live in `TianWen.Devices.Native`, not in
`TianWen.Lib`: an SDK project marks its natives `CopyToOutputDirectory` deliberately (a
`runtimes/<rid>/native` layout is a NuGet mechanism a `ProjectReference` does not honour), MSBuild
propagates that to every transitive consumer, and trimming cannot undo it because it reasons about
MANAGED reachability while a native library is an opaque blob a `DllImport` may resolve by name at run
time. Not calling `AddZWO()` changes nothing; only not referencing does. The viewer shipped 8.2 MB of
camera, focuser and filter-wheel drivers to the Store this way. **Namespaces stayed
`TianWen.Lib.Devices.*` / `TianWen.Lib.Extensions` on purpose** (a deployment split, not an API
redesign), so the assembly name and the namespace root differ; the drivers stay `internal` and see
Lib's internals through `InternalsVisibleTo` rather than the DAL abstraction being promoted to public
API. An app that drives hardware references the project; `tianwen-fits` must NOT, and a new consumer
that only reads files must not either.

**A profile scan never probes a COM port, and a port that will not TAKE bytes is given up, not retried.**
`DiscoverOnlyDeviceType(type)` runs the serial probe pass only when a source for that type consumes it (it
used to run for `Profile` -- at GUI start-up on the main thread and from `MountLimitWatcher` every 5 s).
`SerialConnectionBase` bounds every write twice (port `WriteTimeout` + task deadline) and `SerialConnection`
bounds the close, because a Windows Bluetooth SPP listener port (`bthmodem.sys`, created for any paired
device advertising SPP) accepts an open and then never completes a write, and `SerialStream` ignores its
token. Only a write the driver never completed raises `ISerialConnection.HasAbandonedIo`, on which the pass
drops the port for the rest of the discovery; a READ timeout never does -- a device at the wrong baud or
awaiting another protocol completes the write and stays silent, and still gets every probe and baud. Found
and measured live 2026-08-30: `docs/plans/mount-safety-limits.md`,
"Live verification".

### Device Ownership (the hub lease)

A run that is driving hardware **claims it from the hub**, and nothing else may disconnect or command a
claimed device. `IDeviceHub.TryAcquireLease` / `DeviceLeaseSet.Acquire` (all-or-nothing over a rig) /
`DeviceOwnershipGate.Evaluate` (the one shared verdict + `Describe()` message, mirroring
`ProfileSwitchGate`). `Session.RunAsync` and `RunFlatsOnlyAsync` claim `Setup.DeviceUris()` for the
whole run, released in the `finally` so a claim survives `Finalise` (parking + warming is exactly when a
stray disconnect hurts most). **Polar alignment claims the mount and its capture devices, and a
planetary capture its camera** (P0c item 2): the host takes the claim with `DeviceLeaseSet.TryAcquire`
(a refused start is an answer, not an exception) and the run OWNS it from there, the polar session
releasing it only after restoring the mount. Every new kind of run owes the same.

- **A run's drivers ARE the hub's, so a node holds ONE driver per device.** A session connects each
  device through the hub (`ControllableDeviceBase.ConnectAsync(hub)`, which calls `IDeviceHub.AdoptAsync`),
  and **reads `Driver` only after that connect**, which can switch it to the hub's instance. Every run
  leaves the mount and guider as entries whose driver is down, which is why `ConnectedDevices` lists only
  drivers that are up. `docs/architecture/hosting-api.md`, invariant 6.
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
- **Stopping the rig is ONE sequence, `RigShutdown`** (`TianWen.UI.Abstractions`), for a quit and for a
  display that died: the runs first, each through its own ending (a session's and a flat run's `Finalise`,
  polar's mount restore), and the cameras warmed and disconnected only once every run has ENDED
  (`LiveSessionState.SessionEnded` / `FlatRunEnded` / `PolarRunEnded`, which each starter completes on
  EVERY path its run can end by). A quit aborts the runs; `DisplayLost` (P0a, #743) lets a session and a
  flat run finish on their own and answers their prompts unattended. **Never queue a camera warm-up
  beside a run's cancel**: that is how `Finalise` and the quit once ramped one camera at the same time.
  **Quitting is ONE rule too, `AppQuit`, for the GUI and the TUI**: ask first while this computer's
  session runs, cancel the host's OWN background work (planner, limit watcher, planetary; a separate
  token from the loop's), then `RigShutdown`, with the loop kept going to show it and a second quit
  refused. The TUI had none and hung on Q, draining a tracker whose limit watcher nothing cancelled (P0c).
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

`IPlateSolverFactory` selects in priority order: `CatalogPlateSolver` (built-in, ~6 matched stars),
`AstapPlateSolver` (wraps `astap_cli`, ~44 stars), `AstrometryNetPlateSolver` (wraps `solve-field`,
slower fallback). Every measurement, the dataset rationale and the quad-seed/parity-cache design:
`docs/plans/plate-solver-performance.md`. Rules that bite:

- **A `WCS` is in DETECTED-CENTROID coordinates, 0-based, everywhere in memory -- never subtract 1 from
  `SkyToPixel`, and never write CRPIX to a header by hand.** The header is the ONE place the two frames
  differ (`WriteToHeader` adds one and stamps `PIXORIG = 1`; `FromHeader`/`FromAstapIniFile` subtract
  one); getting this wrong put every TianWen-solved file one pixel off in astropy/PixInsight/Siril/ASTAP
  for months. On SCREEN a frame coordinate lands at `origin + (x + 0.5) * zoom` through
  `WcsAnnotationLayer.ImageToScreen`/`ScreenToImage` and NOTHING ELSE -- never re-derive the origin from
  the pan (`ViewportLayout.ImageOrigin`, built once). Pinned by `WcsPixelOriginTests` and
  `ViewerObjectSelectionTests`.
- **A header hint comes from `OBJCTRA`/`OBJCTDEC` first, and `RA`/`DEC` is NOT the frame centre**
  (`RA`/`DEC` is what the mount reported). `WCS.FromHeader` and `Image.Fits.ParseTargetCoords` must
  read CRVAL -> OBJCTRA/OBJCTDEC -> RA/DEC in that order and must not diverge.
- **A remembered parity (`SolveHintCache`) is a hypothesis BUDGET, never a skip**, keyed on
  `(Telescope, Instrument, RowOrder, Bin)` -- the LIGHT PATH, since an OAG guide camera is the opposite
  parity to the main camera on the same rig.
- **The quad seed (`TrySeedByQuadMatch`) runs BEFORE the parity race and answers where the frame IS,
  never a solution** -- a wrong quad seed costs a pass, never a wrong WCS.
- **`MinSampledFwhmPx` (2.0) only ever un-does a bin, never imposes one.**
- **Frozen real-field regression: `vela-mosaic-starlists.json.gz`** (24 real Vela pointings, 96 frames,
  78k catalog stars) -- three of four bugs it found would have passed a synthetic suite, which is built
  from a transform the test already knows.
- **A ctor with a non-generic `ILogger` parameter silently gets `null` from DI.** Take `ILogger<TSelf>`
  or register with a factory lambda: `docs/architecture/dependency-injection.md`.

### Comet & Small-Body Ephemeris (`TianWen.Lib.Astrometry.Comets`)

JPL comets are a **dynamic, ephemeris-computed catalog**: a comet's RA/Dec AND brightness are
functions of time, computed locally from cached orbital elements, alongside the VSOP87 planets.
`Catalog.Comet` + `ObjectType.Comet`, covered by `CatalogIndex.IsSolarSystemObject` so the sky-map
live-position path applies for free. One keyless bulk SBDB fetch (~4000 comets) IS the database,
cached to `AppData/SmallBodies/comets.json`; position and magnitude are then pure local computation,
offline. Consumed by the sky map, the planner and `tianwen-mcp catalog.lookup`.

**Design, math, the bake, and the shipped operational invariants (with the measurements behind them):
`docs/plans/comet-ephemeris.md`.** Read it before touching this area.
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
- **A request TIMEOUT is not a cancellation, and only the TOKEN tells them apart.**
  `HttpClient.Timeout` throws `TaskCanceledException`, which *derives from*
  `OperationCanceledException`, so both Horizons call sites' `when (ex is not
  OperationCanceledException)` filter excluded the one failure each was written for: the bake died
  with exit 134 and skipped a pages deploy, and the repository logged every failure EXCEPT a slow
  one. Filter on `ct.IsCancellationRequested`, or on nothing where the token is `None`.
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

### A Wikipedia link is the article the bake VERIFIED, never one built from a designation

`ICelestialObjectDB.TryGetArticle` reads `object_articles.gs.gz` (`tools/bake-object-imagery`): an
article is in it only because its Wikidata position agrees with ours, and it carries the lead image's
file name and credit, never pixels. **An object the bake did not verify gets NO link**, which is the
point: the designation guess it replaced 404'd or linked a same-named page silently. The bake keys main
catalogue entries and the lookup follows cross-indices (`M 92` reaches `NGC 6341`'s article), so a test
of that path must use an index that is not itself a key (M 42 is one). Re-bake by hand and commit; the
table is binary, so the bake's stdout report is the review. Measurements and the image rules (no SVG, no
charts, no unlicensed file): `docs/plans/object-imagery.md`.

**What leaves the app is a LINK, and a link only works where the host's router has `OpenUrl` wired.** The
object panel's Wikipedia and Sky atlas are underlined accent-coloured `LinkHit`s on their own row
(`ObjectInfoPanel.BuildLinkRow`), never buttons; the web host draws each as a real anchor. On the desktop
the router consumes a press on ANY region and opens a `LinkHit` only through `InputRouter.OpenUrl`:
`StandaloneViewerHost` had no subscriber, so every link in `tianwen-fits` took the hand pointer and
opened nothing while the same panel worked in the GUI. A new host owes that one line
(`APressOnThePanelsAtlasLinkOpensThePageOnce`). **A layout button lights through `.BgHover(GuiTheme.Hover(fill))`**,
one rule, only when a press would act, and **a host routes the press, the move AND the release through its
router**: a slider's press arms its drag there and only the router feeds it the moves, so a host routing
presses alone left every popover dial click-only (`APopoverDialFollowsADragAcrossItsTrack`). The router
also decides when a hover needs a frame; `StandaloneViewerHost` makes those frames full-surface, since the
viewer narrows a move's damage to the pixel readout.

### The atlas's hover highlight, and what a click would take

**A hover highlight and the click MUST come from one resolver, or they will disagree.**
`SkyMapSearchActions.TryResolveHit` is the search; `SelectObjectByClick` turns it into an info panel
and `ResolveHoverAtScreenPoint` into a `SkyMapHoverTarget` (`SkyMapTab.Hover.cs`). Two hit tests
written the same way is how a wash over one object and a panel about another happens, which is worse
than no wash. **Hover does not honour Ctrl** -- the modifier is read at the press, and a hover carries
none, so guessing is wrong exactly where it matters (picking a star out of a nebula). This is a
HIGHLIGHT, not hover selection: the click still selects (`docs/plans/in-app-sky-atlas.md`, 2026-09-10).

- **The wash is one `Renderer.FillEllipse`**, which all three renderers implement natively, so it needs
  no instance stream, no cache key and no shader. **It is the object's OWN ellipse** (since 2026-09-22,
  the same solver as the selection ring; a disc only for a shapeless object or a star), and **the FITS
  viewer has the same wash** through its click resolver (`ViewerState.HoverObject`, budgeted by pointer
  travel, never through a declared region). **Drawn FIRST of the annotation layers**, which is
  what makes a pointer resting over the search modal or the layer palette harmless with none of them
  claiming the pointer: the sky behind resolves, the wash paints under the panel covering it.
- **Only a CHANGED answer asks for a frame** (another object, or onto or off one), and resolves are
  bounded by the pending frame or, with none pending, by `HoverResolveMinInterval` (8 ms) on the app
  clock. Every resolve used to ask for one, so every pointer move repainted the whole atlas at
  display rate and a hover that changed nothing held the Adreno at up to 70 percent (2026-09-23).
  Assert with `HoverFrameRequests`, never `NeedsRedraw`, which a render sets for reasons of its own.
  **Both interactive hosts settle a switch** (`HoverSettle = SkyMapTab.InteractiveHoverSettle`, 120 ms):
  a new answer shows only once it has held, and one that returns first cancels it, which is what stops
  the wash flickering across a crowded field; the host supplies `RequestFrameAfter` (desktop: the
  per-iteration `TakeDueFrameRequest`; browser: a delayed coalesced repaint) and the frame commits it.
  **An object that fills the view is hovered but not washed.**
  The resolve cost itself, measured by
  `SkyMapHoverResolveBenchmarks` (Release, win-arm64): over an OBJECT ~9 us at any zoom, over bare
  STAR FIELD 157 us at 1 degree and 134 at 10, falling to ~1.7 us by 60, and **0 B on every row**
  (389 us / 225 KB before the fixes below). **The 16x is entirely WHETHER THE STAR PASS RUNS, not a
  per-star cost that varies with zoom** -- the nine index cells come from the unprojected pointer
  and are identical at every zoom, holding 1094 candidates whatever the FOV. **The DSO pass
  short-circuits it and floors its hit test at a FIXED 20 SCREEN PX**, whose sky footprint runs
  0.020 deg at 1 degree FOV to 4.200 at 170, so it reaches something catalogued only when zoomed
  out. `EffectiveMagnitudeLimit` is the minor term and runs the OTHER way (1 degree is dearer than
  10 over the same cells). **The star pass reads a Tycho-2 candidate through `TryGetTycho2Star` and
  looks up only the WINNER in full**: a `CelestialObject` names its constellation by precessing the
  star to B1875, which a hit test never reads, and that plus `ToCatalogAndValue`'s base91 decode
  was 320 of the 389 us and 197 of the 225 KB. Both are allocation-free now (`PrecessRadians`, the
  span `Base91.DecodeBytes`), which every catalogue lookup in the program inherits;
  `Tycho2LiteLookupParityTests` walks the whole catalogue to pin that the two lookups read the same
  star. **A cell is walked through `IRaDecIndex.EnumerateCell`, never the indexer, on a per-frame
  path**: the indexer built a `List` of the cell's Tycho-2 stars, a wrapper and an iterator per cell
  (the last 27.6 KB of a resolve), the struct (`RaDecCell`) scans the same regions as the caller
  advances and allocates nothing, and `RaDecCellEnumerationTests` holds the two equal for every
  cell of the sky against the catalogue bucketed independently, which is how the blob's 254
  duplicate identifiers and its one unaddressable star were found (`docs/known-limitations.md`).
  **Three corrections worth keeping: a Debug timing was
  ~5x pessimistic and blended the two paths; the cliff was attributed to the magnitude limit by
  reading the code; and a test-host Stopwatch ranked terms by JIT order. Attribute with
  `SkyMapHoverResolveCostProbe` (`TIANWEN_HOVER_PROBE=1 DOTNET_TieredCompilation=0`).**
- **The target is dropped when the view moved**, compared at DRAW time against the view it was
  resolved for, never cleared at each of the five call sites that move the view.
- **`ShowOnlyObjectsWithPicture` ([O]'s `I` sub-setting) goes through `OverlayEngine.PassesLayerFilter`,
  and so do the [O]/[D] gates.** Three callers: the desktop's background gather, the browser / offline
  primitive path, and the CLICK resolver -- the last must ask it or a filtered-out object stays
  selectable through apparently-empty sky. It does NOT reach the dark-nebula layer, which [O] does not
  govern (the predicate's [D] branch says why; four comments said the opposite until 2026-09-20), a
  pinned landmark survives it, and **it is in BOTH gather cache keys** because it strips the CACHED
  list.

### Smart Framing (planner co-framing groups)

Pinning M8 with a wide-field profile auto-groups M20 into the same pointing: the planner derives the
sensor FOV from the profile and collapses co-framable targets into one scheduled observation
("M8 + M20") at the combined-footprint centroid. Pure core in `TianWen.Lib/Sequencing/`
(`FramingGrouper`/`FramingPlanner`), sensor specs auto-captured on first camera connect into the
profile JSON (never the camera URI, which re-discovery replaces). Plan, phasing and every invariant
(grid-local neighbour discovery, index-based identity, RA-seam wrap): `docs/plans/smart-framing.md`.

**Catalog identity root-fix shipped alongside this (SIMBAD merge v4).** Messier numbers exist only as
cross-index aliases of NGC entries, so a bare index filter dropped SIMBAD records whose only
main-catalog identifier is an M-number; fixed via `ResolveToDirectIndex`. **Any change to the merge
logic requires bumping `SimbadMergeSnapshot.AlgorithmVersion` + re-running
`tools/precompute-simbad-merge.ps1`** (the embedded snapshot's hash guard covers inputs + version, not
code). Story and numbers: `docs/plans/smart-framing.md`. All lzip I/O goes through the managed
`tools/lzip-util.ps1`; there is **no external `lzip` binary anywhere**.

**OpenNGC data-quality audit: `docs/plans/smart-framing.md`** ("OpenNGC data-quality audit" section).
`OpenNgcCorrections` is the ONE place a wrong row is fixed, never the CSV; `tools/openngc-audit`
verifies every name/identifier against SIMBAD by position.

### Night Calendar (one forecast, one verdict)

The GUI status-bar date opens a month of NIGHTS (`NightCalendarPopover`, `NightCalendarActions`; pure
`NightSummary` + `NightVerdict` in `TianWen.Lib.Sequencing`). Design, thresholds and what is still open:
`docs/plans/night-calendar.md`. The rules:

- **One multi-day forecast serves the planner's weather band AND the calendar** (`NightCalendarActions.RefreshAsync`).
  The band is a SLICE of it, reused in memory for the drivers' one-hour cache life. Never add a per-night
  request back:
  - only a night BEFORE `ExtendedForecast.RangeFor` fetches on its own;
  - a night past the horizon fetches nothing, since Open-Meteo answers today (UTC) + 16 with a 400.
- **The status bar and the calendar cell read the same `NightSummary` through `NightVerdict.For`**; a verdict
  derived anywhere else is a second answer that will drift.
- **Every Open-Meteo hourly array is nullable per element.** The far end of a 16-day range is null, and one null
  in a `List<double>` fails the whole response.
- **A night is its EVENING date** (`NightCalendarActions.PlanningEveningDate`: the pinned `PlanningDate.Date`,
  else `AstronomicalEveningDate`).
- **The Moon mark is DRAWN from the palette, never an emoji glyph**, which Night mode cannot tint.
- **One widget on every host, never a copy**: the GUI paints `NightCalendarPopover` over its chrome, the TUI over its
  Sixel chart, the web over its WebGL canvas. Its keys come through `PopoverState.ContentKeys`, since a terminal has
  no hover; the web, having no profile, calls the `Transform` + weather-URI overloads with keyless Open-Meteo.
- **The pinned targets are a SECOND half (`NightPins`), never part of the verdict.** They are computed only while
  the calendar is open, keyed on the pin set (`NightPinsKey`), and unioned across pointings (one mount), never
  summed. A planet or comet is placed per night by its catalogue index, never from the pin's stored RA/Dec.

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
`docs/plans/meridian-flip-verification.md`

**Mount safety limits are NOT the meridian flip.** `MountLimits.Evaluate` (`Sequencing/MountLimits.cs`,
pure, beside `MeridianFlipDecision`) is the mechanical bound -- where the TUBE meets the pier or the
ground -- while a flip is a *scheduling* choice about a target still imaged from the other side. A rig
can have one, both or neither. Ported from GSServer's `CheckAxisLimits`; the derivations, phasing, live
verification and the wider GSServer sweep are in
`docs/plans/mount-safety-limits.md` and
`docs/plans/gss-parity-audit.md`. The rules that bite:

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
- **A run's site is `Session.Site`, settled at initialisation, never the configured site alone** (#798):
  the request's, else the mount's reconciled with the profile's (`MountSiteExtensions`, one rule for
  every host). The poll read the configured site, NaN for a request naming none, and a NaN altitude
  switches the HORIZON test off: every such server run went without one. `SessionTestHelper`'s default
  configuration names no site, so no session test had evaluated it.
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
Rationale in `docs/plans/gss-parity-audit.md`.

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
baseline participate -- same exposure, gain AND filter position (`FrameMetrics.IsComparableTo`, so a
filter ladder never compares one filter's chromatic focus shift against another's baseline), enough
stars -- and below `FocusDriftMinSamples` of them
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

All driver calls reachable from the session hot path go through `Session.ResilientInvokeAsync(...)`
(preset table, the escalation state machine, and `CatchAsync` vs `ResilientInvokeAsync` vs
`PollDriverReadAsync`): `docs/architecture/driver-resilience.md`.

- **Never introduce a raw `await driver.X(...)` on the session hot path.** Grep PRs for regressions.
- **Pick the preset:** `IdempotentRead` (3 attempts), `NonIdempotentAction` (1 attempt, would
  double-issue), `AbsoluteMove` (2 attempts, safe to re-issue).
- **A frame the camera never delivers throws nothing, so none of the above sees it.** The imaging
  loop only starts an exposure on an `Idle` camera, and a lost frame used to leave a DAL camera
  `Exposing` for ever: no frames, no error. `DALCameraDriver` gives a lost exposure up at
  `duration + 15 s + 10%` (and on an SDK `Failed`), and resets a `CanResetDevice` body before the
  next exposure after two in a row, restoring its settings. Measured cause and the hardware probe:
  the lost-exposure section of `docs/architecture/driver-resilience.md`.

**A command that fails BACKWARD (returns hardware to a state the driver believes it already reached,
e.g. Skywatcher's `:I1` sidereal-rate restore and `:K1`/`:K2` pulse stop) needs verified retry, not
best-effort.** `SendCommandVerifiedAsync` classifies the ack (accepted / refused / **null = timeout, a
different fact from a refusal**) and retries three times before throwing `SkywatcherDriverException`,
because before this a timeout surfaced nowhere and an unrestored `:I1` tracked RA at up to 2x sidereal
for the rest of the night. **Do not widen this to every command** -- only ask which of a new serial
driver's commands fail backward. Rationale + every finding: `docs/plans/gss-parity-audit.md` Finding 3.

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

`Session.TakeFlatsAsync` (`Session.Flats.cs`) is the automated end-of-session flat block: runs after
`ObservationLoopAsync` on normal completion only, before `Finalise` warms the cameras, gated on
`SessionConfiguration.TakeFlatsOnSessionEnd`. Same routines reachable on-demand via
`ISession.RunFlatsOnlyAsync` -> CLI `tianwen flats` / `POST /api/v1/session/flats`. Capture flows, the
exposure solvers, the cover-capability model, the GUI mode, config knobs and every test:
`docs/plans/flat-frame-automation.md`. What bites before you open it:

- **`SessionConfiguration.FlatSource` has exactly two values**, `Calibrator` (default) and
  `TwilightSky` -- a manual hand-switched panel is NOT a third one, it is a `ManualCoverDevice`
  captured through the same `Calibrator` path.
- **Output contract is by FITS headers, never the path.** `IMAGETYP/FRAMETYP=Flat`;
  `MasterFrameBuilder` matches by `MasterGroupKey`. **Never make flat-master matching depend on the
  path.**
- **`RunFlatsOnlyAsync` connects a subset** (never the guider); `FinaliseFlatsAsync` is its focused
  `Finalise` counterpart.
- **The GUI surface is a MODE on the Live Session tab, not a tab** (`LiveSessionMode.Flats`).
  `FlatsBootstrapper` sets `ActiveSession` **without** `IsRunning`, which is exactly why hardware
  guards must ask `DeviceOwnershipGate` and never a UI flag (see Device Ownership).
- **With no `PromptRequested` subscriber the session answers `UnattendedPromptResponse`, which
  defaults to `Decline`** -- proceeding would assert a physical act nobody performed.
- **Native Gemini FlatPanel Lite driver** (`AddGemini()`): an ASCOM-free serial `ICoverDriver`. Wire
  spec + its two silent traps: `docs/architecture/gemini-flatpanel-lite-protocol.md`.

### Deep-Sky Stacking + Enhance Pipeline (`TianWen.Lib.Imaging.Stacking`)

`StackingPipeline.RunAsync` (CLI `tianwen stack`) runs these steps in order:
1. scan DataRoot;
2. build the bias, dark and flat masters;
3. register each light group (star-quad match);
4. integrate. The strategy is auto-picked: Bayer drizzle on RGGB with at least `DrizzleOptions.MinFrameCount`
   frames, else AHD plus sigma-clip rejection;
5. `MasterPostProcessor.WriteMasterAsync`: plate-solve, SPCC white balance, FITS, autocrop, optional enhance,
   previews.

It is completely separate from the Planetary stacker below.

**Flowcharts, the render model, and every rule below with the measurements behind it:
`docs/architecture/stacking-render-pipeline.md`** (see "The rules in full", moved there from this file on 2026-09-25).
Comet integration: read `docs/plans/comet-integration.md` first, since it lists thirteen silent traps.
The rules that bite:

- **Output contract by data type:**
  - Linear (canonical) is FITS: the full-frame `master_<slug>.fits` AND the cropped `_autocrop.fits`
    (`--output-format exr` mirrors both).
  - The display outputs (PNG, `--split-plates` TIFFs) are ALWAYS the autocrop, rendered by
    `MasterPostProcessor`, never the CLI.
- **A written master is [0, 1] with true labels.**
  - Every strategy goes through `IntegratedMaster.Labelled`: observed peak as `MaxValue`, no
    `SensorFullScaleAdu`, and `MinValue` as the BLACK POINT.
  - `MasterPostProcessor` scales with `ScaleFloatValuesToUnitCeiling` into NEW planes. Not
    `ScaleFloatValuesToUnit`, which leaves any peak up to 2.0 alone.
- **Never take a whole-frame statistic over a CFA mosaic.** Split by photosite before any median, MAD, sigma
  or gain, and keep subsample strides ODD. A threshold flagging more than
  `BadPixelDetection.DefaultMaxMaskedFraction` is degenerate, so it masks nothing.
- **A NaN in a rejection sample column disables rejection in every rejector.** Mark absence with
  `PixelRejection.MarkAbsent`.
- **Per-pixel sidecars (coverage, rejection, bad pixels) are QUANTISED, then gzipped:** one rule,
  `IntegrationFitsWriter.MapStorage`.
  - Address a sidecar by its logical path, resolve it with `ExistingSidecarPath`, and ask `IsMapSidecarPath`
    before treating a `.fits` as a master.
  - The bad pixel map is APP's format, on the SENSOR geometry.
- **Drizzle rejects per deposited sample, at the stack's thresholds** (#93).
  - It judges each sample leave-one-out, and allows the cell's local slope (`DrizzleClip.SlopeScale`).
  - A rejecting drizzle streams `RawBayerFrames` twice, so a producer must be re-enumerable.
  - Its parallel strips (`DrizzleKernel.ForEachStrip`) and `RunSubsetsAsync` are BIT-IDENTICAL to the serial
    code, as `DrizzleParallelBitIdentityTests` pins.
- **Never re-ingest our own outputs.** The scan drops any TianWen-produced FITS (`STACK_N > 0` or our
  `SWCREATE`), unless `--include-integrations` is given.
- **Calibration is grouped by temperature RUN, and a session is matched on its lights' MEDIAN temperature**
  (`CalibrationEpochs.SetGroupKey`, `SplitSets`, `CalibrationResolver.SessionKey`). Flats rank by filter,
  then proof tier, then days; temperature only breaks ties.
- **The archive scan is ONE function, `SessionDiscovery.ScanAsync`, and it never excludes before reading.**
  - An unchanged header replays from `FitsHeaderIndex`.
  - A resume fingerprints the calibration a session CHOSE, never the whole library.
  - `--rebuild-session <wildcard>` rebuilds named sessions.
- **A master's width is its subs', plus the warp kernel's, plus any misregistration.**
  - `Lanczos3Clamped` is the default since 7.1. The clamp, at a measured 0.7 threshold, is mandatory on OSC.
  - The unmoved rule (`UnmovedTolerancePx`) and `Image.SinglePhotositeFractionMax` keep warm pixels out of
    registration and star lists.
- **A captured non-light says so in `IMAGETYP`.** AutoFocus rungs are `FrameType.Focus`, and scouts are
  `FrameType.Scout`. Never widen a consumer's filter to admit them.
- **`--enhance`** runs `SharpenPipeline` ONCE, into `_sharpened.fits`; `--split-plates` exports the same pass.
- **Render model:**
  - ONE SPCC white balance, then each plate self-stretches.
  - SPCC's clip test reads the OBSERVED peak.
  - The normaliser anchors on `Image.Pedestal`.
- **SPCC is broadband-only as a MODEL, not a gate.** Narrowband SPCC is blocked on Gaia data, and naive HOO
  is rank-deficient (`docs/plans/narrowband-colour.md`). The stretch's `ResolveAuto` is what refuses to
  assert the fit as colour.
- **The filter-curve matcher never answers with a brand, nor a more specific product.** Re-run
  `ReportKnownLightPollutionFilters` after adding a curve.
- **A QE curve keys on the DIE (`_cameraToSensorAliases`); a crop's geometry keys on the CAMERA.** A wrong
  alias is worse than a missing one.
- **Enhanced masters render with `MasterPreviewRenderer.WithZeroPedestal`.**
- **The CLI renders nothing.** The display-only stages (`--saturation`/`--contrast-boost`, `uhdr`) never
  touch a linear master.
- **Stellar-sharpen is opt-in, and skipped while a deblurrer is live.**
- **Enhance options parse once, in `EnhanceOptions.TryParse`**, for the CLI and the server endpoint alike.

### Planetary Lucky-Imaging Stack (`TianWen.Lib.Imaging.Planetary`)

A CPU-first planetary stacker, **completely separate** from the deep-sky `Imaging.Stacking` pipeline
(star-quad align + sigma-clip rejection don't apply to a featureless disk). Batch pipeline, live
streaming stacker, benchmarks: `docs/plans/planetary-stacking.md`. Live-capture drivers, controls, the
fake's noise model, the recenter loop: `docs/plans/live-planetary-capture.md`. Rules that bite:

- **`PlanetaryMaster` is the single shared "accumulators -> master" finalize**, so the batch and live
  masters can never drift.
- **Live capture: camera ADU normalises to [0,1] at the stream boundary** (`LiveCameraFrameStream.DeepCopy`);
  a colour sensor's video frame is a 1-channel Bayer mosaic, and the stream layout derives from the
  ACTUAL frame, **NOT** the camera's `SensorType`; **no driver call crosses onto the render thread**
  (it stages, the capture loop drains + applies). Preview defaults to **linear** (`StretchMode.None`).
- **Read the plan doc before touching the Canon path** -- it is a list of five things that fail
  SILENTLY. Auto-recenter defaults ON (ROI-only, zero mount disturbance); mount jog is opt-in OFF and
  its **sign is uncalibrated**.

### AI Image Enhancement: SETI Astro (ONNX) + RC-Astro (CLI)

`SharpenPipeline` (`TianWen.Lib/Imaging/Enhancement/`) orchestrates role-typed enhancers
(`IStarRemover` / `IStellarSharpener` / `INonStellarDeconvolver` / `IDenoiseEnhancer` /
`IGradientCorrector`) over an immutable `SharpenStep[]` program. Selection is **RC-preferred,
deferred, and license-gated**: `AddRcAstroAi()` wraps `AddTianWenAi()` and `Replace`s the three
RC-servable roles with `DeferredEnhancer` proxies that make the RC-vs-SAS choice AND the blocking
license probe on the FIRST `EnhanceAsync`, never at DI registration -- composing a service collection
spawns no `rc-astro` process. Design and every measurement: `docs/plans/ai-enhancement.md`,
`docs/plans/rc-astro-enhancers.md`, `docs/plans/osc-narrowband-denoiser.md` § 1o and
`docs/plans/denoiser-training.md`.

- **SETI Astro (SAS Pro AI4)** -- plain ONNX in-proc (`AddTianWenAi()`); models under
  `%LOCALAPPDATA%\TianWen\models`.
- **In-house N2N denoiser** (`N2nDenoiser`, OSC-only, throws on mono) -- opt-in (`--ai-backend n2n` /
  `AddTianWenN2nDenoiser`); Auto rescues with it only when SAS AI4 weights are absent. Weights ship
  in-repo (`src/TianWen.AI.Imaging/models/`) as an LFS object; **any new LFS file type the apps ship
  must be added to `APP_LFS_INCLUDE` in `dotnet.yml`**, or the publish matrix ships a pointer stub as
  the model (`publish-apps`'s "Verify LFS objects materialised" step is the backstop). Full history,
  including the two-week bug where the runner fed the graph pixels ~100x below its training band and
  cut every star's peak 30 percent with no metric catching it: `docs/plans/denoiser-training.md`.
- **RC-Astro (BlurX / NoiseX / StarXTerminator)** -- `AddRcAstroAi()`. `.onnx` files are encrypted at
  rest (license forbids extracting weights), so driven through the `rc-astro` CLI's `--json` NDJSON
  protocol, never loaded into ORT.

**A vendor's weights are read WHERE THE VENDOR PUT THEM, never only where a dev script copied them.**
`ModelResolver` also auto-detects GraXpert's own cache for `graxpert_bge.onnx` (no override -- the
version subdir isn't knowable ahead of time). **Never make a shipped capability depend on a script
only a checkout can run**: the Store build once shipped an Enhance failure against 207 MB of GraXpert
weights already on disk because the only bridge was a repo-relative dev script.

### Classical Background Extraction (`TianWen.Lib.Imaging.BackgroundExtraction`)

`ClassicalBackgroundExtractor` is the AI-free gradient corrector: a robust iterative degree-2 polynomial
on a block-mean working grid, with an optional inpainted low-pass surface on its residual
(`SurfaceRefinement`), applied in LINEAR with the model's median added back per plane. Both
`IBackgroundExtractor` (headless) and `IGradientCorrector`; `AddTianWenAi()` prefers GraXpert when its
weights resolve, falling back to this so a machine without GraXpert still flattens. Design, the
reference review, and every measurement: `docs/plans/background-extraction.md`. Rules that bite:

- **Every threshold is in noise units of the WORKING grid** (a block mean of `Downsample^2` pixels),
  so "2 sigma" is two sigma of a noise four times smaller than the frame's.
- **The polynomial stage iterates to convergence; the surface stage runs ONCE.** Closing that loop
  carves the peak out of an ordinary dome (7.1e-4 RMS model error vs 1.1e-4 single-pass) because the
  surface's high-pass residual leaks a smooth feature's amplitude back in.
- **Stars are not structure, and neither is a star's blur shadow.** Structure seeds exclude COMPACT
  pixels only (the positive high-pass core plus its eight neighbours); flagging the negative side too
  cost a third of the grid.
- **A one-channel `SensorType.RGGB` input is fitted per photosite colour**, never as one plane (which
  removes only the average gradient and leaves each colour's own behind).
- **The level is per plane; the pedestal field is untouched** -- one scalar level is background
  neutralisation, a separate step. Conflating them is what forced `WithZeroPedestal` on
  GraXpert-flattened masters.
- **Neither structure threshold is a tuning knob, measured over 118 real masters.** `SurfaceRefinement`
  is the switch that matters: turning it ON moves a real model 0.40 sigma RMS at p50 and drops kept
  fraction 0.795 to 0.581 against a 2.32-sigma median gradient, so it stays off by default (a flexible
  surface hollows a frame-filling nebula). Pinned by `ClassicalBackgroundExtractorTests`.
- **`tianwen dataset gradient-report` is the measurement tool.** A TianWen master's canvas ring is
  EXACT ZERO where no frame covered it and must be masked to NaN before fitting, or the fit chases the
  edge. `docs/plans/gradient-remover-training.md` H1.

### Hosting API (`TianWen.Hosting` + `TianWen.Server`)

Headless REST + WebSocket API plus an ASCOM Alpaca device plane on one ASP.NET Core host: **native v1**
(`/api/v1/`, multi-OTA, camelCase, POST mutations) is the session plane; the **ninaAPI v2 shim**
(`/v2/api/`, OTA[0], PascalCase, GET) is compatibility. Run `dotnet run --project TianWen.Server` or
`tianwen-server [--port 1888]`. **Endpoint inventory, the Alpaca plane, the enhance endpoint and the
full native-AOT rules, and the reasoning behind each rule below:
`docs/architecture/hosting-api.md`.** What bites:

1. **A pushed schedule beats the target queue.** `POST /session/schedule` preserves per-filter plans,
   the planner's `Start` and `AcrossMeridian`; `PendingTarget` carries none and `/session/start` stamps
   `Start = now`. Never route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** `EventBroadcaster`
   restores the guarantee (no NATIVE WebSocket client -> `SessionPromptEventArgs.DefaultIfUnanswerable` at
   once, since a ninaAPI socket cannot answer; one attached -> hold with no timer, liveness is the only
   bound), and it attaches as the node starts a run, never from its poll. **Liveness is the client's
   presence BEAT, never its socket** (a frozen window keeps its socket): a client beats from the loop that
   DRAWS it (`TianWenEventStream.Beat()`; the GUI from `SdlEventLoop.OnLoopIteration`, never a timer), and
   one whose beat is older than `NodeWire.PresenceLapse` is nobody to wait for. **Any new subscriber on a
   headless path owes the same**, and **whoever holds a prompt drops it on `Settled`**: the session
   withdraws one it stops waiting on. **A broadcast only queues** (`EventHub`, a bounded queue and one
   sender per client); a client that falls behind is dropped to resync by polling.
3. **Numeric enums on the wire** (no `JsonStringEnumConverter` on `HostingJsonContext`): a `required`
   enum on a request DTO is hostile to hand-written callers, default it.
4. **Previews go through the shared stretch, never a private one.** `PreviewEncoder` runs
   `StretchSolver` + `Image.RenderStretchedRgba`, the same pipeline as the GPU viewer and the TUI, and
   resolves `Auto` as the live pane does. The shim once divided by `Image.MaxValue` and called it an
   auto-stretch, which renders a linear sub near-black. **A preview LEASES the frame and answers
   `If-None-Match` with a 304 before touching it**, and its token is the slot's own
   `LastCapturedImageNumber`, never the camera's `FrameNumber`, which names the exposure in progress (a
   sub behind). A session's preview slot holds a lease of its own until the next frame replaces it
   (`Session.PublishCapturedImage`), which costs one camera array per OTA.
5. **The Alpaca plane is a DEVICE plane and cannot become the session plane.** Ownership there is the
   hub lease, not an Alpaca policy: actuation and `Connected=false` answer `0x40B`, reads and
   `Connected=true` always pass; never make the plane read-only during a session. Device numbers come
   from the **ACTIVE PROFILE, in profile order**, never from discovery. **The native and ninaAPI
   actuation routes ask the same lease (`ActuationGate`, 409 naming the run) before touching a driver**,
   and a new actuation route owes the same.
6. **AOT is verified by `dotnet publish -r <rid>`, not `dotnet build`.** RDG stays enabled in the
   **`TianWen.Hosting` library**, both JSON contexts stay registered via `ConfigureHttpJsonOptions`,
   and **never reintroduce a `ResponseEnvelope<object>` or an anonymous-type payload**.
7. **A run is the NODE's, never a request's.** Every start goes through `IHostedSession.TryStartAsync`
   (the node's token, a compare-and-swapped run record) and only `TryAbort` cancels it: Kestrel reuses a
   connection's cancellation source, so a request token let a later abandoned request on the same
   connection cancel the night. An abort ends the run through its `Finalise`, never disposing it
   underneath; a finished run never blocks the next; the host stopping aborts, awaits `Finalise`, then
   warms the hub's cameras inside `HostedSession.ShutdownBudget` (and systemd's `TimeoutStopSec` must allow
   as long). Pinned by `NodeRunLifecycleTests`.
8. **`new SessionConfiguration()` is the DECLARED defaults; `default(SessionConfiguration)` is all zeros.**
   A record struct whose primary constructor has required parameters zero-fills on `new()` unless it
   declares a parameterless constructor, and before one was added every API session synced the mount's
   site to 0, 0. `SessionConfigApiDto` carries every field: **a field added to the configuration is added
   there and to `SessionConfigApiDtoTests`'s round trip**, or it silently cannot cross the wire.
9. **A slow operation is a JOB, never an inline request.** Its endpoint starts it through
   `NodeJobs.StartOrJoin` and answers 202 with a `JobDto`; the node runs it on its own token (invariant 7's
   rule, for the same reason), `GET /jobs/{id}` is authoritative, `DELETE /jobs/{id}` cancels, and
   `JOB-PROGRESS` is the hint. `/devices/discover` ran inline on the request's token, so a client's 10 s
   budget cut a serial sweep off mid-probe (P0b item 17). One of a kind at a time; a second start joins it.
10. **The machine's node is found on its SOCKET, and one lock admits it** (`NodeSocket`, `NodeLock`): every node
   takes `node.lock` however it was started, only the lock's holder clears a stale socket (never probe-then-delete),
   and the lock file is never deleted. A client reaches a node through `NodeTransport` (`OverSocket` / `OverTcp`)
   and asks `GET /api/v1/node` first; compatibility is `NodeWire.Version`, never the build (P1, #917). A client
   finds or starts the machine's node through `LocalNodeLauncher`, which starts the KEEPER (`--keeper`) from the
   client's own directory, never the node and never a spawn of its own. **A node that dies leaves a crash journal**
   (`node.journal`, `NodeJournalService`) of its devices, its run and each camera's cooler INTENT, which the hub keeps
   (`IDeviceHub.SetCoolerIntent`): a ramp records its TARGET, never the step it reached, and **a new place that
   commands a cooler owes the same**. A journal is believed only from the node its keeper saw crash
   (`--after-crash <pid>`) or when younger than the machine's boot (`MachineBoot`, never the tick count). A believed
   journal is ACTED on (the devices reconnected, the mount first, each named in the journal BEFORE its connect; each
   camera re-cooled from its intent), never a run resumed, and two crashes within `NodeKeeper.CrashLoopWindow` reconnect
   nothing. **There is ONE cooling ramp, `CameraCoolingRamp`**, which the session delegates to and the hub drives
   without one (`CoolToSetpointAsync`): a second copy is a second answer to how fast a sensor may be cooled.

### Remote Rigs (mirror another node's session "as if local")

`docs/plans/remote-profile.md` (complete P1-P5) holds the design, the Home-tab decisions, the sidebar
tab-registration mechanism, and every measurement; the pieces are `TianWen.Hosting.Contracts` (wire
DTOs + `HostingJsonContext`) and `TianWen.RemoteClient` (`TianWenNodeClient`, `TianWenEventStream`,
`RemoteSessionMirror`). Rules:

- **The overlay model is the whole design: selecting a rig changes what you look at, never what this
  node owns.** A remote connect is a read-only HTTP mirror (no lease, no hardware); the single-session
  invariant is per NODE; `RemoteRigBinding` persists on a stable `NodeId`, never an address.
- **One `LiveSessionState` per view context**: Active (renders), Local (this node's own hardware --
  every quit/park/disconnect path belongs here), All (poll + redraw). Reaching for Active where Local
  is meant parks the local mount from a remote view. The reverse bites too: a button drawn over a
  remote rig's panel posts the same signal as the local one, so **every handler that drives this
  computer's rig asks `EnsureLocalContext` first** (runs, and since P0b item 9 the device actions:
  planetary Start, nudges, Goto, Solve and Sync, the focuser), and a new one owes the same.
- **`ISession`/`ISessionTelemetry` split**: telemetry is the wire-crossable read surface, `Setup` stays
  local, so a remote rig renders with no tab knowing it is remote.
- **Two wire traps:** never `required` on a nullable wire property (`WhenWritingNull` omits it); route
  a non-finite double through `ForWire`, or it is a bodiless 500 for the WHOLE endpoint.
- **Polling is authoritative; the WebSocket is a latency hint** -- `NodeResult<T>` carries a status
  code because 404 is not unreachable.
- **Every request has a time budget** (state 5 s, preview 30 s, control 10 s; 60 s `HttpClient`
  backstop). Budget expiry and caller cancellation both surface as `OperationCanceledException` meaning
  opposite things: keep `when (...)` filters on the ORIGINAL token, never the linked one.
- **Profile switching is gated** (`ProfileSwitchGate`) while connected/running or where drivers would
  strand in the hub.
- **The Home tab** (`Ctrl+H`) is a read-only PROJECTION: `HomeBoard.BuildCards` draws only from the
  `ImmutableArray<RigCard>` snapshot, never a live state.

### Colour Theme (`GuiTheme`, four states incl. Night)

`GuiTheme` (`TianWen.UI.Abstractions/GuiTheme.cs`) owns the one palette; `UiThemeState` is **System /
Light / Dark / Night**, `GuiTheme.Apply(state, desktopIsDark)` swaps it in as one reference write and
`Palette` is one reference read. The source XML comments carry the rationale (scotopic numbers
included); design + phasing: `docs/plans/colour-theme.md`.

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

`tianwen-fits` ships to the Microsoft Store as **Astro Photo Viewer**, which is what makes file
associations worth having and what makes every double-click a fresh AOT process unless the file is
handed to the window already open. Layering, the two CI lanes, the activation bug that shipped and
both MSIX traps: `docs/architecture/desktop-shell.md`; packaging: `packaging/windows/msix/`;
thumbnails: `docs/plans/explorer-thumbnails.md`. Rules that bite:

- **The gate is folder-scoped and the pipe IS the lock** (`InstanceGate`, SharpAstro.AppShell).
  `--new-window` / `TIANWEN_FITS_SINGLE_INSTANCE=0` opt out; failure is never fatal (opens in this
  process instead).
- **Activation is `sdlWindow.Activate()`** (AppShell's `IActivatableWindow`), never a toolkit-local
  copy -- two copies of one rule is what caused the shipped activation bug (raise alone leaves a
  minimised window off-screen; restore-first un-maximises, so it restores ONLY if minimised).
- **Explorer thumbnails run in the shell's surrogate and get a STREAM.** `Initialize` reads NOTHING
  and `WTSCF_FAST` answers `WTS_E_FASTEXTRACTIONNOTSUPPORTED`, because a read HYDRATES a cloud
  placeholder -- buffering there would download a whole OneDrive folder just to answer "do thumbnails
  exist". Inside a OneDrive sync root the handler is never even asked (one whole-root
  `ThumbnailProvider` under `SyncRootManager` answers for every extension): `docs/known-limitations.md`.
- **macOS ships a `.dmg`, not the App Store** (the sandbox would take away `ScanFolder`'s sibling-file
  read). **`Contents/MacOS` is `nested=true` in codesign's default resource rules, so EVERY file
  there must be signed**, not just Mach-O binaries -- an unsigned data file fails the signing of the
  executable and names whichever file the walk reached first, reading as one bad file when it is a
  whole class (cost two release runs). Debug artefacts are stripped before signing.

### Image Pipeline & Buffer Lifecycle

Camera → `ChannelBuffer` → `Image` → consumer → `image.Release()` → camera recycles. Ownership
vocabulary (own/borrow/consume), the four conventions and the DEBUG leak leg:
`docs/plans/frame-lifecycle.md`. Driver coverage matrix, full-scale numbers and the header parse:
`docs/architecture/image-pipeline.md`. Rules that bite:

- **Who owns a frame is stated ONCE, in the `<remarks>` on `Image`.** Never derive "may I release
  this?" from a `ReferenceEquals` -- the answer is always in hand one branch earlier, else make the
  producer CONSUME its input.
- Never hold an `Image` from `GetImageAsync` longer than needed; it pins the camera buffer.
- **A preview of a frame someone else owns LEASES it for the copy**: `AstroImageDocument.FromLiveFrameAsync`
  (the TUI; lease, copy, adopt the copy) or `LiveFramePreviewSource.AcceptFrame` (the GUI's live and guider
  panes; lease, normalise into its own planes). **Never `AdoptImageAsync`**, which consumes its input: the
  TUI once rescaled the session's own sub to [0, 1] in place while it waited for its FITS write. **And never
  a bare read, on the render thread either**: the owner releases from ITS thread, and the GUI's unleased
  read threw mid-autofocus in the P0a live check. A frame released before the lease is skipped, not shown.
- **A demosaic the viewer OFFERS must have its own branch in `image.frag`**, or a Save's CPU debayer
  silently writes a different picture from the one on screen. `DebayerAlgorithm.Auto` resolves via
  `ResolveAuto` before `GpuDebayerMode`, which THROWS on an unresolved Auto rather than falling
  through to MHC; VNG's thresholds are ABSOLUTE, so both GPU and CPU paths must be fed a `[0, 1]`
  mosaic. Pinned by `GpuVngDebayerParityTests`.
- **Every gradient in a demosaic must compare two samples of the SAME colour, or a flat field gets a
  colour-dependent bias invisible to a self- or GPU-parity test** (VNG's mixed colour differences put
  a 6.4-level, two-pixel-alternating stripe over every flat background -- thirty times what MHC/AHD
  show). Pinned by `VngFlatFieldBiasTests`, the one test that asserts on a FLAT field.
- `Array2DPool` is scratch only; camera buffers use `ChannelBuffer`. A buffer nobody released is
  findable in DEBUG (`ChannelBufferLeakTracker`); the recycle loop is complete for DAL/Fake/Alpaca/
  ASCOM/Canon, a streaming driver's multi-plane frames going through `PlaneRecycler`; FC.SDK.Raw's own
  decode buffers are that library's to recycle. **A full pool budget makes room by evicting the oldest
  arrays of other shapes, and its policy is tested on an `Array2DPoolCore` of its own, never through the
  shared pool**, whose Gen2 trim empties it above 90 % memory load and so makes any fill-the-budget
  test measure the machine (it emptied a 256 MiB fill halfway on a loaded box, and never ran on CI).
- **`Image.MaxValue` is the peak pixel OBSERVED, not saturation** (`ImageMeta.SensorFullScaleAdu` is
  the fixed value). Two "full scale" numbers must not be conflated: the BITPIX container width vs the
  native ADC resolution -- never route a native ADC depth through `BitDepthEx.FromValue` (falls back
  to the container width). `Image.UnitScaleDivisor` is the single source of truth for [0,1]
  normalisation; a private `1/MaxValue` diverges the moment `SensorFullScaleAdu` is present.

### The image is not necessarily in HDU 0

`Fits.ReadFirstImageHdu()` / `ReadFirstImageHduHeaderOnly()` (`FitsHduExtensions`) walk to the
first HDU that carries an image, and **every reader of an image file uses them** --
`Image.TryReadFitsFile`, `Image.TryReadFitsHeader`, `MasterCache.ReadFingerprint`,
`IntegrationFitsWriter.IsTianWenMaster`. A bare `ReadHDU()` on the read path is a regression.

- **A plain file's pixels come through FITS.Lib's `FitsReader`, and the first image is the first
  one holding a SAMPLE.** `TryReadFitsFile(path)` reads a plain FITS file through
  `TryReadThroughFitsReader` (2 MB positional reads straight into the float planes: a pooled 26 MP
  read went from 39.5 ms and 54 MB to 10.5 ms and 56 KB) and falls back to the HDU reader for `.gz`,
  `.fz` and anything the reader declines. **Not a memory mapping**: measured, a mapping read the same
  sub cold SLOWER than the old reader (105.6 against 90.4 ms). The two paths are pinned bit for bit by
  `FitsReadPathParityTests`, whose reader side must be `TryReadThroughFitsReader` itself: through the
  public entry, a file the reader quietly declined compares the HDU reader with itself. An image HDU
  with an axis of length zero holds no sample (FITS.Lib's placeholder primary before a table,
  `BasicHDU.DummyHDU`: NAXIS = 1, NAXIS1 = 0), so the walk, the header-only walk and the reader all
  pass over it. `docs/plans/frame-path-allocations.md` P5.

- **A file is OPENED through `Image.OpenFits`, never by constructing a `BufferedFile` beside a
  `.gz` test.** Handed FITS.Lib's own `BufferedFile`, a gzipped file reads back as an EMPTY HDU list
  rather than throwing, so every reader answered "unreadable" for one, silently, until the first
  compressed sidecar was written (2026-09-21). The opener uses a plain `FileStream` for a compressed
  file. **And a gzip stream cannot seek**, so `ReadFirstImageHduHeaderOnly` throws over one: skipping
  a data block is a seek. Read the whole HDU there, or arrange not to need the peek.

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
`docs/architecture/image-pipeline.md`.

- **A declared pixel scale beats `FOCALLEN`, which is only a hint** (`Image.GetImageDim`:
  `ImageMeta.DeclaredPixelScale` from `PIXSCALE`/`SCALE`, else pixel size x binning x focal length,
  else `null`, never a guess).
- **`DeclaredPixelScale` and `DerivedPixelScale` are in different conventions.** The declared one
  already includes binning; the derived one is per unbinned photosite: collapsing them double-counts
  `BinX`. `DerivedImageScale` (pixel size x `BinX`) is the one comparable with the declared scale, and
  it is what `PIXSCALE` is written from.
- **`ImageMeta.PixelSizeX` is the unbinned PHOTOSITE, the `XPIXSZ` card INCLUDES binning** (MaxIm DL,
  N.I.N.A., SharpCap: "microns, includes binning if any"). The one parse divides by `XBINNING`, the one
  writer multiplies back, nothing else converts. Both directions were wrong until 2026-09-23 and a
  bin-1 fixture sees none of it; test on a bin-2 frame, with a hand-set foreign `XPIXSZ`.
- **A light carries the guiding quality of ITS OWN exposure** (`ImageMeta.Guiding`;
  `GUIDERMS`/`GUIRMSRA`/`GUIRMSDE`/`GUIDEPK`/`GUIDEN`, arcsec, ours alone): `GuideStatistics.OverExposure`
  reduces `Session.GuideSamples` over the exposure window, **never a rolling session average**; null is
  not zero (an unguided rig writes NO cards); `GUIDEPK` catches the single gust RMS hides. Stamped via
  `ICameraDriver.GuideStats` just before `GetImageAsync`. Pinned by `GuideStatisticsTests` +
  `SessionImagingTests`.

### Image Mutability: Almost-Immutable with In-Place Escape Hatches

`Image` is logically immutable (no public setter, `GetChannelSpan -> ReadOnlySpan<float>`). Full
design, ownership vocabulary and ALL measurements below: `docs/plans/frame-lifecycle.md`,
`docs/plans/viewer-memory-footprint.md`, `docs/architecture/image-pipeline.md` § How a plane is READ.
Five things deliberately mutate `data[c]` or its planes in place, and any new mutating public API
follows the same `Adopt*` naming, never a neutral `CreateFrom*`: `ScaleFloatValuesToUnitInPlace`,
`Normalizer.ApplyCfaInPlace`, `Calibrator.Apply` (the one deliberate exception to "ownership transfer
is visible in the name", pinned by `CalibratorOwnershipTests`), `AstroImageDocument.AdoptImageAsync`,
and plane RESIDENCY (`TryEvictFloatPlanes`/`Image.ResidentPlanes()`) -- the one that is NOT opt-in,
costs +8.7% to +20.3% on bilinear resample loops if resolved per-sample instead of once per operation,
and is pinned by `ImagePlaneResidencyConcurrencyTests`.

**Eviction is NOT release** -- `Release()` spends ownership, `TryEvictFloatPlanes` is reversible and
the image stays usable, and the two words being one apart is the likely way to write an inverted
guard. **Every read must go through the `Planes` accessor**: `GetChannelArray`, the subpixel sampler
and `ScaleFloatValuesToUnitInPlace` all once read the evicted 0x0 stub directly, so a FITS write of an
evicted image emitted nothing and the in-place rescale threw on `plane[0, 0]`.

**A plane is `float[,]` and stays one; what changes is how a LOOP reads it** -- span-per-row beats
both a naive `[y,x]` index and a flat `float[]`, worth 2-3x on hot loops under AOT, but the multiplier
is per-MACHINE (arm64 and x64 disagree by up to 12x on which optimisation dominates): never quote one
without naming the box it came off. Full tables, the `Lanczos3Weights` angle-addition rewrite (1.78 to
1.80x, computed in double, judged against a double reference) and the double-vs-float tap-offset trap:
`docs/architecture/image-pipeline.md`.

**Every histogram goes through ONE vectorised kernel, `Image.Traverse`, bit for bit, and its running
sum is ORDERED.**
- Lanes are added in walk order.
- A change to it is checked against the scalar walk by `HistogramKernelParityTests`. That test compares
  the DOUBLE sum, because the float `Mean` hides almost any order change: a lane swap passed every check
  on the mean.
- A document open takes its stretch statistics and display histograms in one walk per channel
  (`Image.GetStats` / `StretchSolver.CollectStats`), with the channels in parallel.

**Parallel row bands reorder that sum, so they go through `Image.TraverseInBands` and nowhere else** (#490).
- It takes the sum from the bands only when it PROVES no order could change it: every addend is a
  multiple of the smallest ulp g among them, and a total magnitude below 2^53 g makes every partial sum
  exact. Otherwise it takes the sum again in walk order.
- The document open uses it (`GetStats`, the luminance statistic). `Statistics` / `Histogram` keep the
  single walk, because star detection runs them inside stacking that is already parallel.
- Real frames pass the bound 99.8 percent of the time; only drizzle weight sidecars failed it.
  `ExactSumBoundProbe` re-measures that.
- A parallel site rethrows a body's own exception (`ParallelFor.Run`), because the viewer shows it as the
  reason a file did not open. Measurements: `docs/plans/viewer-memory-footprint.md` (#631, #490).

**Test fixtures must not share `Image` instances across tests.** `SharedTestData` caches the extracted
temp file path, not an `Image` -- two parallel collections sharing one cached `Image` through
`AdoptImageAsync` produced a "1 ms / 0 stars" `FindStarsAsync` flake.

### A Canon raw is cropped to its active area on import

**The decoded raster is not the photograph.** Every Canon body records shielded photosites down the
left edge and across the top (the camera's own black reference), a narrow partly-shielded transition
after them, and a few spare columns and rows at the far edges. `Image.TryReadCanonRaw` crops to
`CanonRawFile.ActiveArea` (FC.SDK.Raw 3.1+), so `Image` is the picture: 6720x4480 from a 5D Mark IV's
6888x4546, 5088x3392 from an R5's 5248x3510. Uncropped it reached a stretched display as a flat black
L and put ~3% of the frame, pinned at the black level, into every statistic taken over it.

- **FC.SDK.Raw does not crop `BayerMosaic` and must not start**: its CR3 decoder is byte-exact
  against LibRaw's uncropped `unprocessed_raw`, the only reason to trust it. The crop is metadata;
  applying it is ours. The overscan therefore stays reachable through `CanonRaw.Open` for anything
  that wants a per-frame bias or read-noise reference -- it is simply not carried on `Image`.
- **Ask `ActiveArea.CfaPattern`, never `CanonRawFile.CfaPattern`, after cropping.** An odd offset
  re-phases the CFA, and the uncropped answer would hand the Bayer pipeline a frame with red and blue
  exchanged: a plausible picture in the wrong colours, not an error. Every body measured offsets
  evenly, so today they agree.
- **Measure MaxValue over the pixels you keep.** It used to scan the whole mosaic, so a margin pixel
  could set the peak the stretch pipeline divides by.
- **A dimension assertion is not enough in a test.** A crop from the wrong corner is still a
  photograph; `Cr3ImportTests.Cr3_CropsFromTheDeclaredOrigin` pins the offset, and has to SEARCH for a
  pixel where cropped and uncropped reads differ because the R5 fixture is almost all zero after black
  subtraction and every fixed block matched on both sides.

**Canon is the only sensor that is CROPPED.** A DAL camera (QHY first) now reads its effective and
overscan areas and RECORDS them, as `ImageMeta.DataSection` / `BiasSection` written to the IRAF
`DATASEC` / `BIASSEC` cards (and read back, `TRIMSEC` as `DATASEC`'s synonym only when it is absent),
but keeps the whole raster. Those sections are 1-based and inclusive, the same trap as CRPIX, so
`FitsSection` is the ONE place the convention is converted. Design, phasing and the measurements:
`docs/plans/sensor-active-area.md`.

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
`docs/plans/image-codecs-facade.md`.

### FITS Viewer Widget (`ImageRendererBase<TSurface>`)

Partial-class structure, one layout root, the shared slider, the live-preview host, the `?` menu and
toolbar label-width rule: `docs/architecture/widgets-and-controls.md` § The FITS viewer widget.

- **Two live sources, not interchangeable.** `LiveFramePreviewSource` is per-EXPOSURE (Live Session,
  guider, polar-align), holds no document; `LiveStackPreviewSource` is the video-rate one (planetary)
  and wraps an `AstroImageDocument`. A cost argument about "the live path" has to name which -- free at
  one frame per 120 s is not free at 60 fps. `StretchSolver.CollectPerChannelStats` (pedestal-removed,
  what the curve solves from) and `CollectChannelHistograms` (the frame's own levels, what the panel and
  overlay draw) are two collectors on purpose, for the same reason. Detail moved to
  `docs/architecture/widgets-and-controls.md`.
- **GPU resource lifetime**: `docs/architecture/viewer-gpu-lifetime.md`. Never call
  `UploadDocumentTextures` outside `PrepareFrame`; never destroy a bound Vulkan object or write a shared
  descriptor set from an upload path (`VulkanContext.DeferDestroy`); a resize is its own GPU-lifetime
  path; the cached image layer samples in TEXTURE space (divide UVs by CAPACITY). Run under
  `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` and read `validation_report` whenever this area is touched.
- **Auto-crop, two tiers**: `Image.LargestCoveredRectangle()` prefers a master's own coverage plane
  (`MAPKIND=COVERAGE` sidecar) and falls back to `CoverageEdgeWalk`; a master with no coverage plane can
  defeat the walk (V1045 Ori). A crop is a CLIP every draw path owes (destination AND source UVs),
  reaching the enhance INPUT too (`WCS.CroppedTo`, `AstroImageDocument.SourceCrop`, remembered across
  the toggle). Full rules and measurements: `docs/plans/viewer-prerelease-fixes.md` P25.
- **Absence is BORDER-REACHABLE for NaN as much as zero, and a weight deficit in the MIDDLE of a frame
  is not an edge** -- only reachability from the border counts, or a saturated core (the Great Orion
  Trapezium) reads as an uncovered rim. `Image.FillInteriorHolesInPlace` then fills every interior hole
  (never the ring) so nothing downstream sees a NaN; three callers, no fourth implementation. The
  `BitMatrix` flood, its word-level perf and the full incident: `docs/plans/viewer-prerelease-fixes.md`,
  issue #250.
- **The sky behind the frame is its own toolbar button and key (`Y`, `ToolbarAction.SkyBackdrop`), not
  the annotation ladder's next rung** -- a rung ANNOTATES the photograph from its own solution; the
  backdrop puts a second view BEHIND it and needs a WCS, which a rung has no precondition slot for.
  `ViewerState.ShowSkyBackdrop` is intent, `SkyBackdropActive` is capability (map + clock + catalog + a
  CD matrix); keep them apart so an unsolved frame remembers the request. Every other rule (grid
  ownership, pan clamp, site/instant provenance, object selection, the info panel, and why a click
  resolves against what is DRAWN rather than the catalogue) is shipped and pinned exactly as designed:
  `docs/plans/in-app-sky-atlas.md` § What shipped in the viewer, and P28 / P34 in
  `docs/plans/viewer-prerelease-fixes.md`.
- **Read the object catalogue through `ImageRendererBase.LoadedCatalog`, never
  `CelestialObjectDB.Value.Value`.** `AsyncLazy<T>.Value` is a non-blocking PEEK (`Result<T>?`); `Value`
  RETHROWS a failed load on every frame, so `LoadedCatalog` asks with `TryGet` instead.
- **The docked info strip REPORTS; it holds no controls.** Statistics collapse to a heading and open as
  a measured-column TABLE (`InfoPanelData.GetStatisticsTable`); controls live in toolbar popovers
  instead (white balance, tone).
- **A popover is a `Layout.Builder.Popover` node plus a `PopoverState` -- nothing else to declare.**
  DIR.Lib 10.0 retired the five-obligations-per-panel `IKeyboardClaimant` pattern for a topmost-first
  stack of painted popovers (`WindowUiSettings.PaintedPopovers`); a toolbar button still owes its own
  `onPress` (a handler-less region is silently dead under the router). Design + the incident:
  `docs/plans/dir-lib-10.md`.
- The **tone popover** (`ToolbarAction.Tone`) covers curves boost/mode and the highlight soft clip; the
  math (`HdrAmount`/`HdrKnee`/`Image.ApplyHdr`) is untouched, only the panel changed. Full design:
  `docs/plans/hdr-display.md`.

### The star field is culled on TWO axes, and both are load-bearing

`StarMagnitudeIndex` + `StarChunkIndex` (`TianWen.UI.Abstractions`) are the one implementation for
`VkSkyMapPipeline` and `WebGlSkyMapPipeline`: sky-region chunks, brightest-first within each, a 0.5-mag
prefix table; draw only the regions the view cone reaches and only their prefix. **Neither axis covers
the other** (magnitude bounds a wide field, ~3% of Tycho-2 at 60 degrees but 81% at V<=12; the cone
bounds a deep zoom and nothing at full sky). **Submitting the whole ~2.5M-star buffer TDR'd an Adreno
X1-85** and dropped 944 of 1287 frames in the browser. **A limit past the last bin clamps to
"everything", never wraps to zero** (pinned by a `[Theory]`). **The TOTAL is capped too**
(`StarChunkIndex.BudgetedMagnitudeLimit`, `MaxStarInstancesPerView` = 300k, both pipelines): the two
culls bound a view on two axes, never their product, and a limit raised by hand to 12 over a wide field
reached ~0.8M, so the limit drops a bin at a time until the view fits. Measurements, the WebGL2 `firstInstance`
workaround and the two cull details that bite: `docs/plans/web-tycho2.md`.

### A quantized cache key must not derive its grid from a continuous input

Both overlay caches (`SkyMapTab.BuildOverlayKey`, `OverlayGatherKey`) once quantized the view centre
into `FOV/8` cells from the RAW FOV while bucketing the FOV separately, so a zoom re-gathered on every
event (69 gathers against 8 over one pinch; a pan cost 3, and that asymmetry is the tell). **Take the
step from the BUCKETED value.** **Assert the gather COUNT, never the output**
(`SkyMapTab.PrimOverlayGathers`, like `SkyMapState.PlanetCacheRebuilds`): a stale-keyed rebuild draws
the identical frame. **`gathers <= 12` passes on `gathers == 0`** (`ShowObjectOverlay` is off by
default): pair the bound with `gathers > 0` and see it FAIL with the fix removed. Why it read as jank
in the browser and a never-settling walk on the desktop:
`docs/plans/web-showcase.md`.

### The web host paints per event, so continuous gestures must coalesce onto rAF

The browser build has no render loop: every input handler repaints synchronously (71% of move-driven
repaints were superseded inside their own 16.67 ms). `RequestRenderCoalesced()` (via
`wwwroot/raf-pump.js`) is for `OnPointerMove` / `OnWheel` / `OnPinch` **only**. **Clear the dirty flag
BEFORE painting and on the schedule-failure path**, or the canvas freezes for good. **A pointer move
repaints only when the router or a tab handled it, a gesture owns it, or the atlas hover's
`HoverFrameRequests` moved** (#339), never on `NeedsRedraw`, which a render sets for reasons of its own
(it was true on 41 moves of 41, so a gate on it passed every move); and **the settle wake paints only when
`SkyMapTab.PendingHoverDueIn` is zero** (re-arms when early, returns when null), or a pointer crossing a
star field paints once per star it passes. Pinned by `CanvasRenderCostTests.AHoverThatChangesNothingDoesNotPaintPerMove`. **A trackpad pinch
is `ctrl`+`wheel`** (Blazor `@onwheel`), a different path from the touch bridge and the densest gesture
the app sees. Details: `docs/plans/web-host-carve-out.md`.

### Sky Map / FITS Viewer GLSL (pre-baked SPIR-V, no runtime shaderc)

TianWen.UI.Shared's shaders are GLSL 450 files under `src/TianWen.UI.Shared/Shaders/*.vert|*.frag`,
**pre-baked to SPIR-V** (`Shaders/spirv/*.spv`, committed + embedded, loaded via `LoadShaderModule`) by
`tools/BakeShaders`; there is **no runtime shaderc** (SdlVulkan.Renderer 6.23 dropped
`Vortice.ShaderCompiler`; shaderc ships no android RID). Two rules: **edit a shader → re-bake → commit
the `.spv`** (`dotnet run --project tools/BakeShaders -c Release -- src/TianWen.UI.Shared/Shaders`;
**warning TWSH0001** flags a source newer than its `.spv` by more than `StaleBakeToleranceSeconds` (60,
since a fresh checkout writes a source a few ms after its `.spv`) or a missing `.spv`, names the shader,
and never fails; nothing in CI checks the `.spv`, so it is their only guard; TWIC0001 is its twin for
both icon recipes); **ASCII only**, shaderc's
lexer rejects non-ASCII bytes even inside comments. The `stereoProject` GLSL is inlined into the three
`skymap_*.vert` files; restoring a single source is a deferred cleanup (#634).

`Image.StretchValue()` is the single source of truth for the scalar stretch math (normalize → subtract
pedestal → rescale → MTF). Don't reimplement it.

### Stretch Pipeline: CPU/GPU Mirror

Two implementations must produce visually equivalent output for the same `StretchUniforms`: **GPU**
`Shaders/image.frag` for the live viewer; **CPU** `Image.StretchChannelCpu` / `StretchLumaPixelCpu` /
`ApplyHdr` / `ApplyCurveLut` / `ApplyBoost` / `RenderStretchedRgba` for `ConsoleImageRenderer` (TUI
Sixel) and tests. Order in both: pedestal subtract -> bg neutralization -> WB -> shadow/rescale -> MTF
-> luma blend -> curves -> HDR knee -> normalize -> clamp. **The subject in full, with the
measurements: `docs/architecture/stretch-pipeline.md`** (and `stacking-render-pipeline.md` sections
5-6). Rules that bite:

- **Wire a new stage into BOTH the GLSL and the CPU helpers**; `StretchTests_NewPipeline` is the
  end-to-end guard.
- **`Linked`/`Unlinked` mean what they mean in PixInsight, and the difference lives ENTIRELY in the
  uniforms.** Never re-derive a per-channel curve in the Linked branch.
- **`StretchMode.Auto` is a UI intent, resolved before any `StretchUniforms` is built, never a shader
  mode**, and **every renderer must resolve through it, headless included** -- a literal
  `StretchMode.Linked` in `MasterPreviewRenderer` once bypassed the narrowband line-selective veto and
  clipped red to zero on 5 of 139 gallery cards. Resolver, the four inputs, and the measurements:
  `docs/architecture/stretch-pipeline.md`.
- **Background neutralisation is solved POST-WB, so anything caching its gains owes the WB in its
  cache key** (they print at F4).
- **The SPCC / Calibrate toggle gates the RENDER, not the measurement**; an AI enhance INHERITS the WB
  triple (`InheritColorCalibration`) rather than re-fitting.
- **The manual WB is a SEPARATE multiplier from the auto calibration** (`shaderWhiteBalance` = auto x
  manual; sliders travel `[0.25, 4]`, never `GrayWorldWhiteBalance`'s clamp). Applies in the
  `StretchMode.None` linear path too, mono excepted.
- **Luma weights live in `StretchUniforms.LumaWeights`** (Rec.709 default, `SensorMatched` from QE x
  CFA); never hardcode Rec.709.

### Layout DSL (`DIR.Lib.Layout`)

GUI/TUI panels are immutable `Layout.Node` trees: `Layout.Engine.Arrange` measures,
`PixelWidgetBase.PaintLayout` draws and binds clicks **from the same arranged rect** (draw == hit by
construction). Engine + DSL reference: DIR.Lib's README; the engine features TianWen leans on, the five
traps in full, the alias and conditional-background rules, the TUI row contract and the pointer-cursor
rule: `docs/architecture/widgets-and-controls.md`, read it before any layout work. The short form:

- **Build trees with `Layout.Builder`** (`VStack/HStack/Text/Box/Fill/Spacer/Grid/Overlay/Split/Dock`)
  and the fluent `Layout.Node` methods, never `new Layout.Node.X { }` or `cursor += h`.
- **Alias, don't import**: `global using Layout = DIR.Lib.Layout;` and the qualified `Layout.Node`;
  `using DIR.Lib.Layout;` drops the `Node`/`Content`/`Size<T>` barewords into scope.
- **Conditional background**: `.Bg(color)` always sets a value, so `if (cond) n = n.Bg(color);`, never
  `.Bg(default)`.
- **Interactive sub-widgets** emit `Layout.Builder.Fill(key: "...")` and draw via `drawFill`; **a text
  field is NOT one**, it is `Layout.Builder.TextInput(state, fontSize)` (see below).
- **Responsive sizing is `Sizing.Star(weight, min, max)` + `.CollapseBelow(u)` + `WrapH`/`WrapV`**;
  orientation is a plain C# branch (canonical: `PlannerTab.BuildFrameLayout`).
- **Five silent traps** (all found on the Home board), in full in the doc above: `.RowH(h)` eats a
  preceding `.WFixed(w)`; a `Stack` places children at the cross-axis START; a `Node`'s default
  `Width` is `Auto`; never pair `.CollapseBelow(u)` with a Star minimum; an icon inks the full square
  it DECLARES.
- **A mark is a `Layout.Content.Icon`, never a symbol character in a `Text` run** (a glyph draws
  .notdef where the face lacks it); every step/jog/pan mark resolves in ONE place,
  `FormRowLayout.StepMark`.
- **A choice, a checkbox and a double-click are DECLARATIONS** (DIR.Lib 11.1):
  `Layout.Builder.ButtonGroup` (never per-segment hand-picked fills), `Layout.Builder.Checkbox` (never
  `"[x] "` in a label) and `.DoubleClickable(...)` (never a host arm on `clicks >= 2`). What is still
  hand-built and why: `docs/architecture/widgets-and-controls.md` rule 1.
- **`.PadX(u)` / `.Pad(across, down)` for a FIXED-height bar**, or the icon becomes a stub while the
  text overflows and goes on looking correct.
- **`PushClip(x, y, w, h)` / `PopClip()` on the widget base**, never `Renderer.PushClip` with a
  hand-built `RectInt`.
- **TUI rows are trees too** (Console.Lib 4.10): `IRowLayout.BuildRow(in RowContext)`, inline buttons
  via `.Clickable(...)` resolved by `ScrollableList.DispatchRowHit`; a new capability is a **field on
  `RowContext`**, never an overload.
- **A box should be the engine's MEASUREMENT of its content, not a sum of the constants the body draws
  with.** State a control's widest state as `widthSample:` ON the node. What still does this by hand:
  `docs/plans/viewer-layout-engine.md` (HIGH PRIORITY).

### UI Primitives: the cursor, a text field, and who holds focus

Full reasoning: `docs/architecture/widgets-and-controls.md`
(cursor) and `docs/plans/automatic-text-input.md` (field, focus,
key routing). **Where this is GOING is `docs/plans/dir-lib-10.md` (HIGH PRIORITY, 2026-09-15)**: the engine
already ships the pointer rule (click places the caret, a second click selects the word, a drag extends;
DIR.Lib 9.1 `TextInputInteraction.HandlePointer`) and every tianwen host still hand-rolls `clicks >= 2 ->
SelectAll()` instead, a node cannot declare a shortcut, and a popover or a slider costs a dispatcher line
per host. Until that lands, a new field, popover or drag follows the rules below; do not add a fourth
key router. The rules that bite:

- **The pointer's appearance is a property of a REGION, never a host predicate**: declare it beside the
  click (`RegisterClickable(..., cursor:)` / `.Clickable(hit, onClick, cursor)` / `.WithCursor(kind)`);
  the host asks `guiRenderer.CursorAt(x, y) ?? CursorKind.Default`; a region stating nothing is
  transparent (`null`, not Default), so a row inherits its card's cursor. The `CursorKind` -> SDL
  mapping lives in SdlVulkan.Renderer.
- **HOVER needs a z-order answer, `ViewerState.OverlayOwnsPointer`**, because hover is decided at PAINT
  time; add an overlay to that ONE property, never a call site. **It is NOT
  `WindowUiSettings.PointerOwner`, which an open `Popover` sets as it paints**: that one is a RECORD and
  confines hover for whatever paints AFTER it, which is free for a `PaintLayout` tree; this one is a
  PREDICTION, for hand-painted chrome that resolves its hover BEFORE any overlay has drawn. The viewer's
  toolbar, histogram and file list are all of the second kind, so the flag stays until they are trees.
- **Every host routes through `DIR.Lib.InputRouter`, and the ORDER is the engine's**: an open popover,
  then any PAINTED node whose declared `Shortcut` matches, then the focused field, then the widget. The
  desktop (`GuiEventHandlerBase`) and the browser (`Planner.razor`) each keep only what is theirs -- the
  platform binding, the pointer position, the rail's hover repaint, the rAF coalescing -- and each calls
  `AfterPaint()` once the frame is drawn. **A key binding is a `.WithShortcut(key, mods)` on a node, not
  an arm in a switch**, and whether it beats a focused field is `KeyChord.BeatsFocusedField` (Ctrl, Alt
  or F1..F12 do; a bare letter does not), so there is nowhere left to write `if (key == F3) return
  false;`. Matching against the PAINTED tree is what makes a binding inside a closed panel inert, and
  what makes a chord for a locked tab inert, with no guard beside the key.
  - **Two bindings have no node to sit on and stay in the host**: Ctrl+Tab / Ctrl+Shift+Tab name the
    NEXT tab rather than a tab, so they are answered before the router in `GuiEventHandlerBase`.
  - **A press on a region is CONSUMED there**, so anything that used to run after a hit test runs
    before the router instead, off a non-dispatching `HitTest` (the planner's handoff-divider drag, the
    one such site left; a `Content.Slider` deletes it when that divider becomes one).
- **A text field is a declaration**, `Layout.Builder.TextInput(state, fontSize)` and nothing else
  (`TextInputRenderer`, `TextInputHit`, `CursorKind.Text`; `CellLayout` on a terminal). `fontSize` is in
  DESIGN units (the painter crosses `ctx.FontScale`); intrinsic width comes from the placeholder.
- **Focus is global but not settable, and there is ONE owner per window**: `DIR.Lib.TextInputFocus`
  owns the transition, the host binds `FocusChanged` ONCE (SDL `StartTextInput`/`StopTextInput`, web
  `CanvasTextOverlay`); `Focus` is idempotent and SELECTS its seed, so `Focus(input, value)` is the
  whole of "open an editor on this value" and a following `SelectAll` is a second mechanism (there are
  none left). The instance is the window's `WindowUiSettings.Focus`: `GuiAppState.AdoptWindowSettings`
  points the desktop app state at the chrome's, and `WebSkyMapTab.ShareWindowWith` gives the browser's
  two sibling canvas widgets one context. **Two owners is the bug class**, not a tidiness point.
- **`BlurIfUnpainted` is the router's `AfterPaint()`**, called once per frame after the paint, with
  everything painted; calling it before, or with one surface's fields when the frame draws several,
  does the opposite of what it is for.
- **`TextInputInteraction` reads `ctx.Focus.Current`**, takes `KeyContext.TabFields` as a callback, and
  **swallows every key while a field is focused** -- which is exactly why a binding that must survive a
  focused field is a `.Shortcut` and not a case in the host's key switch.

### Per-Window Widget State: `DpiScale` / `FontPath` / `EmojiFontPath` are properties, not parameters

A value constant for the whole window is a `virtual` property on `PixelWidgetBase<TSurface>` (DIR.Lib):
set once by the host, propagated by a composite widget overriding the setter, resolved by
`RenderLayout`/`ArrangeLayout`/`PaintLayout` as `?? DpiScale` / `?? FontPath`; `dpiScale: 1f` is the
device-px escape hatch and a `PixelMeasureContext` overload covers a per-axis scale or a cell-authored
tree (build it ONCE and pass the same instance to Arrange and Paint). **Do NOT reintroduce these as
`Render`/helper parameters**: per-window constant -> property, per-call derived -> parameter; `fontSize`
is NEVER a property (`AltitudeChartRenderer`, `SkyMapRenderer` keep theirs). Breakdown:
`docs/plans/dpi-scale.md`.

**Which FACE they get is one decision, `BundledFonts.Resolve()`**, returning `(Text, Emoji, Fallback)`
together for all three hosts; **a direct `FontResolver.` call in production code is a regression**
(tests exempt). Resolving a subset is the bug it prevents (the viewer had faces but no
`FontFallback`, could not ask `CanRender`, and every missing glyph was found by eye); bundled first
because only a bundled face has known COVERAGE. **Both viewer and GUI bundle both faces**, and
which one draws a given rune is Unicode's DEFAULT PRESENTATION, not coverage order (DIR.Lib 8.15's
`EmojiPresentation`): a pictograph comes from the emoji face even where DejaVu has an outline for
it, while text-default marks (checks, stars, arrows, the warning sign) stay on the text face. So a
NEW mark is picked by what the codepoint IS, and a colour glyph cannot be tinted or dimmed, which
is what a baked icon is for. The `Lazy<FontSet>` cache and what is outstanding:
`docs/plans/font-roles-and-icon-baking.md`.

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
  instance (faster, self-documenting, compiler-enforced). None are left (swept 2026-09-24, #353); the
  one clause still unmet, `FileLoggerProvider` being reachable from a render thread, is #541. For a most-recent-N window polled by readers (guide
  samples, frame metrics), prefer the lock-free `CircularBuffer<T>` (`TianWen.Lib/Sequencing`):
  ImmutableArray + CAS replace, torn-free `Snapshot` reads, O(capacity) appends -- right when producers
  are low-rate (per exposure) and pollers high-rate (per frame).

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, `Span<T>` / `ReadOnlySpan<T>`
- **Immutability with controlled mutability**: types immutable by default; private mutable state with read-only views
- **Correct abstraction levels**: pure math/data in `TianWen.Lib`, UI state in `TianWen.UI.Abstractions`,
  Vulkan-specific rendering in `TianWen.UI.Shared` / `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **No code duplication**: reuse single sources of truth (e.g., `Image.StretchValue()`)
- **No null-forgiving `!` in production code.** There are ZERO left across every shipping project
  (swept 2026-09-11); a new one is a regression, and the fix is almost always to STATE the invariant
  instead of asserting it: `[MemberNotNullWhen]` / `[NotNullWhen]` / `[MaybeNullWhen]` on the
  predicate or the `out` that decides it (`WCS.HasSip`, `PulseGuideRouter.UseCamera`,
  `DeviceBase.SameDevice`, `FrameCache.TryGet`), a pattern bind where a bool was carrying a value it
  could not (`is { } x`), or a `?? throw` naming what broke. Two compiler traps found doing it: a
  SECOND property read on a struct receiver DISCARDS the member-null state the first established (so
  `WCS.WriteToHeader` binds the SIP arrays instead), and `x!.M()` marks `x` non-null for the rest of
  the block, so removing one `!` can make a later line warn. Tests and benchmarks are deliberately
  exempt: `= null!` on a `[GlobalSetup]` field is the BenchmarkDotNet idiom, and `null!` passed to
  prove a guard throws is the point of that test.
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

`%LOCALAPPDATA%/TianWen/` (`TianWenDataRoot`), or the folder `TIANWEN_DATA_ROOT` names: a whole tree kept
apart from the user's, which a test's node runs on and a spawned node inherits. The whole set, because there
is **no** single choke point that creates these: `IExternal.CreateSubDirectoryInAppDataFolder(name)` covers
four of them, `Planner`/`Session` are built from `AppDataFolder` directly, `Profiles`/`Logs` off
`SharedStaticData.CommonDataRoot`, and `models` + `lan-node-id.txt` + the node's socket and lock are resolved by
their own owners. Add a directory here when you add one there.
```
TianWen/
├── Logs/<date>/        # <appName>_<timestamp>.log per process and day: GUI_*, Server_*, Keeper_*, ... (FileLoggerProvider; rolls at local midnight)
├── Profiles/           # Per-profile data (*.json + NeuralGuider/*.ngm + BacklashHistory/*.json)
├── Planner/            # Pinned targets: <profileId>/<date>.json, remote rigs under rigs/<bindingId>/
├── Session/            # Session-setup state, <profileId>.json (SessionPersistence)
├── Guider/             # Guider frames dumped for plate solving (guider_*.fits + .ini)
├── Weather/            # OpenMeteo / OpenWeatherMap forecast cache
├── ObjectImages/       # Wikimedia object pictures, one file per (image, standard width) (ObjectPictureStore)
├── SmallBodies/        # JPL SBDB comet cache: comets.json + apparitions.json
├── models/             # AI ONNX models (ModelResolver; also probes SASpro's own models dir)
├── Secrets/            # Non-Windows only: 0600 file per device secret (Windows uses Credential Manager)
├── node.sock           # The machine's node's socket (NodeSocket), owner-only on Unix
├── node.lock           # One node per socket: held for the node's life, never deleted (NodeLock)
├── node-settings.json  # The node's own state: "Share this rig on the LAN" and its active profile (NodeSettings)
├── node.journal        # The node's crash journal: what it holds, gone once it holds nothing (NodeJournal)
└── lan-node-id.txt     # tianwen-server's stable LAN NodeId, the key remote-rig bindings persist against
```

**Every file here has more than one PROCESS on it** (the GUI, the TUI, the CLI, the server, the viewer, MCP), so it
is written with `IExternal.AtomicWriteJsonAsync` and read with `TryReadJsonAsync`, or `SharedFile` (`TianWen.Lib/IO`)
underneath both, never a bare `FileStream`. A write stages under a name of its own and replaces the file with the
POSIX-semantics rename on Windows, because `File.Move` refuses to replace a file any reader holds open, delete sharing
or not; a read shares read, write and delete, without which even that rename is refused; and **a reader believes a
file is gone only after looking again** (`SharedFile.TryOpenReadAsync` / `ListAsync`), because an NTFS replace, either
rename, hides the name for a moment and a planner that read "no pins" would save that back. **Do not replace those
looks with a reader/writer lock**: measured, the real-time scanner then holds a replaced file in kernel mode and every
rename onto it is refused for minutes. **A file every host ADDS to** (the comet apparition cache) goes through
`UpdateJsonAsync`, which holds its directory's `.lock` across the read, the merge and the write; a whole-file write
there drops what another host added. P0c item 3 of `docs/plans/hardware-in-the-server.md`; pinned by
`SharedAppDataFileTests`.
