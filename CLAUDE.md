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
- A plan with open work has a **milestone** of the same name, holding its issues. A new issue joins its plan's milestone, and **a PR carries the milestone of the plan it advances** (`gh pr create --milestone <plan>`). `tools/plan-issue-report.py` (the `plan-report` skill) checks all of it, and the `plan-report` workflow runs it `--strict` on every PR touching `docs/plans/` and weekly, so a renamed heading cannot leave an issue's link dead. What is happening is the repository's shared issue views and the Milestones page, not a generated report.

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

Available in `.claude/skills/<name>/SKILL.md` (each carries its own description, which the session lists;
auto-invocable when the request matches, or via `/<name>`): `release-lib`, `release-tianwen`, `sibling-status`,
`check-ci`, `bump-version`, `run-gui` / `run-tui` / `run-fits` (build and launch DETACHED via
`tools/start-app.ps1`: pid + redirected stdout/stderr paths; survives a reaped background shell), `test-run`
(TRX + no truncation; hunts flakes), `test-filter`, `test-image-diff`, `test-output-prune`, `stack`,
`digitize-filter`, `curate-session`, `dataset-gallery`, `plan-report` (`tools/plan-issue-report.py`, also the
`plan-report` workflow), `tick-todo` (close a backlog ISSUE, preferably through its PR, and update CLAUDE.md,
the plan files and memory), `chrome-review` (runs the read-only `.claude/agents/chrome-review.md` reviewer, on
Sonnet, over a branch's UI diff for layout arithmetic the engine should own). **It is asked for mechanically**: a
Stop hook (`.claude/settings.json`, `.claude/hooks/chrome_review_gate.py`) blocks the end of a turn ONCE when the
branch's `src/TianWen.UI.*` diff changed in the session and that diff was never reviewed, and the continuation
counts as the review. Answer it with the agent, or with one line saying why the change carries no layout.

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
├── TianWen.slnx, Directory.Build.props (sibling auto-detect); Directory.Packages.props is at the REPO ROOT
├── TianWen.Lib/ (core, net10.0)   TianWen.Devices.Native/ (ZWO + QHYCCD drivers, so Lib carries no vendor natives)
├── TianWen.Lib.SourceGenerators/ (Roslyn: DispatchInterfaceGenerator)
├── TianWen.Lib.Tests/ (unit, xUnit v3)   .Tests.Functional/ (Session loops with FakeTimeProvider)
├── TianWen.Lib.Tests.Simulators/ (on-demand tests vs LIVE Alpaca/ASCOM simulators; gated, skip by default)
├── TianWen.Cli/ (AOT → `tianwen`)   TianWen.Server/ (headless, AOT → `tianwen-server`)
├── TianWen.AscomHost/ (Windows-only out-of-proc host for in-proc COM ASCOM drivers)
├── TianWen.Hosting.Contracts/ (wire DTOs + the shared HostingJsonContext, host AND client reference it)
├── TianWen.Hosting/ (ASP.NET Core Minimal API: REST + WebSocket + Alpaca device plane)
├── TianWen.RemoteClient/ (TianWenNodeClient / EventStream / SessionMirror)
├── TianWen.UI.Abstractions/ (widget system, layout, state, shared types)
├── TianWen.UI.Shared/ (Vulkan FITS pipeline, VkSkyMap pipeline + tab; the SDL→InputKey map is SdlVulkan.Renderer's `SdlInputMapping`)
├── TianWen.UI.Gui/ (N.I.N.A.-style GUI, AOT → `tianwen-gui`)   TianWen.UI.FitsViewer/ (AOT → `tianwen-fits`)
├── TianWen.UI.Web/ (WebAssembly showcase, WebGl renderer)   .Web.E2E/ (Playwright)   TianWen.UI.Benchmarks/ (BenchmarkDotNet)
└── TianWen.AI/ (ORT facade)   TianWen.AI.Imaging/ (image ↔ tensor bridge + enhancer wrappers)   TianWen.AI.MCP/ (MCP stdio server, AOT → `tianwen-mcp`)
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

TianWen depends on in-house libraries published to nuget.org under the **SharpAstro** org, each a sibling clone
at `../<repo>` (csproj layout varies; the full table with paths and the auto-detect column:
`docs/architecture/sibling-builds-and-releases.md`): `DIR.Lib`, `SdlVulkan.Renderer`, `Console.Lib`, `FITS.Lib`
(csproj `CSharpFITS/CSharpFITS.csproj`), `FC.SDK` (+ `FC.SDK.Raw`), `ZWOptical.SDK`, `QHYCCD.SDK`,
`SharpAstro.Fonts` (`../Fonts.Lib`, transitive), `SER.Lib`, `Lzip.Lib`, `Serial.Lib`, `LAN.Lib`, `WebGl.Renderer`,
`SharpAstro.AppShell` (`../AppShell`), `TianWen.DAL` and the `Codecs`-repo codec family.

**Auto-detection** (`Directory.Build.props`): a **single** property `UseLocalSiblings` gates them all. The build
switches to ProjectReference when **every** sibling working copy exists, otherwise it falls through to
PackageReference (all-or-nothing: one missing checkout puts EVERY library back on the package; CI always uses
PackageReference). **A sibling's FOLDER must carry its repo's current name** (the check is a path: a clone still
under `zwo-sdk-nuget` for the renamed `ZWOptical.SDK` was a missing sibling, 2026-09-19).
`dotnet msbuild src/TianWen.Lib/TianWen.Lib.csproj -getProperty:UseLocalSiblings` answers `true` or nothing;
override with `dotnet build -p:UseLocalSiblings=false`. `Fonts.Lib` is transitive via DIR.Lib's own
`UseLocalFontsLib`. The history behind the rules (CPM drift, web projects in CI, the `open-vs.ps1` /
`Exists(...)` divergence, release traps) is in `docs/architecture/sibling-builds-and-releases.md`. The rules:

- **No CPM opt-outs left in `src/`**, and a new one needs a real technical justification (being outside a
  solution never had any bearing on CPM).
- **A sibling gated on `UseLocalSiblings` must also be in that property's own `Exists(...)` list**, and
  `open-vs.ps1`'s project list must match the same conjunction -- nothing enforces either, and a generated
  solution with unresolvable entries loads with them silently unloaded.
- **`TianWen.UI.Web` is IN `TianWen.slnx`; only `.E2E` stays out** (`IsTestProject` + Playwright, so a
  solution-wide `dotnet test` would need a browser) and `dotnet.yml` compiles it. The web host consumes
  `UI.Abstractions` from `.razor`, which no `--include=*.cs` grep sees: a rename passed both and broke CI. Run
  E2E explicitly: `dotnet test TianWen.UI.Web.E2E`.

A new sibling gets auto-detection the same way (an `Exists` entry + a conditional `ProjectReference`), never local nupkg feeds. When that's not
viable, commit + push + wait for NuGet publish; **do not** create local nupkg feeds or run `dotnet pack` to
short-circuit the release dance (CI pulls from nuget.org, and a local-only nupkg masks version-skew bugs).

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
- **A test's temporary folder comes from `TempFolders`**, never a bare `Directory.CreateTempSubdirectory`, which
  nothing removes (1,253 had piled up in `%TEMP%`, #1197). It is made under the tests' common root,
  `%TEMP%/TianWen.Lib.Tests/t`, deleted when disposed (a test class's field, disposed after each test), and anything
  a delete missed is swept after a day. A node harness owns its own (`NodeHarness.StartAsync(onItsSocket: true)`,
  `KeptNode`). A helper that names a folder after its test takes the test's `TempFolders` (`FitsFixture.CreateTempDir`,
  `TempStackingWorkspace`): a fixed parent of its own under `%TEMP%` is a folder nothing sweeps (#1323: about
  9,000 folders and 1.1 GB in a week). `TestTempFoldersTests` fails on a bare `CreateTempSubdirectory` in any test project.
- **A thread stress test runs off CI** (the owner, 2026-10-10; `StressTestGate.SkipOnCi`): one that hammers an object
  from several threads for wall-clock time measures the runner, and on a shared 4-core runner its loops held the thread
  pool and stalled the whole test process into the hang dump (#1385). Its loops take threads of their own
  (`TaskCreationOptions.LongRunning`), never pool workers. A deterministic test of a known interleaving runs on CI as any other.
- **A test that takes 30 s or more on a CI runner is `[Trait("Category", "Heavy")]`** (the owner, 2026-10-10): the PR legs
  filter it out (`dotnet.yml`) and `heavy-tests.yml` runs it nightly against main and on dispatch; a local `dotnet test`
  runs everything. Tag by MEASUREMENT on a green x64 leg (its log prints each test's time), never by guess: 28 such tests
  were 46 % of the suite's summed time and held the x64 leg at 21 to 28 minutes.
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

Every rule below in full, with the incident behind it: `docs/architecture/session-test-harness.md` (the guider
race, the time pump, and "Test collections and parallelism in full (moved from CLAUDE.md, 2026-10-09)").

- **Any test that drives a `Session` belongs in `[Collection("Session")]`: the rule is about what a test DOES,
  not what it is CALLED.** If it calls `SessionTestHelper.CreateSessionAsync`, it is a session test.
- **A fake-clock `SleepAsync` must throw on a cancelled token, and a guider's `StopCaptureAsync` must not return
  until its loop has exited** (`DeviceOwnershipTests.AFinishedRunGivesTheRigBack`, a race misdiagnosed as
  starvation for a day).
- **No wall-clock timeout inside a test**, a `CancellationTokenSource` or a `Stopwatch` budget alike: use
  `[Fact(Timeout = ...)]` and wait on `TestContext.Current.CancellationToken` (#940; `NodeWait` is the shape).
  **A test that drives a whole run needs that bound**, or a wedged run is a `--hangdump` and a multi-GB dump.
- **That bound is reliable under xunit 4.x's default `parallelAlgorithm`, Conservative**: never set
  `parallelAlgorithm: aggressive` without giving it up.
- **A `Timeout` on a SYNCHRONOUS test does nothing at all** (`xUnit1069`): the test must reference
  `TestContext.Current.CancellationToken`.
- **Less parallelism is faster here, and the config only counts if it is copied to the output**: every test
  project carries an `xunit.runner.json` (`maxParallelThreads: 4`; Simulators 1 + `parallelizeTestCollections:
  false`) **and** its `<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`. Never
  diagnose a slow suite by re-running it: one run with a TRX, then rank durations.
- `SessionTestHelper` defaults to `FakeMountDriver`; pass `mountPort: "LX200"` or `"SkyWatcher"` only for
  protocol-specific tests.
- **Use the cooperative time pump** (`FakeTimeProviderWrapper.PumpUntilCompletedAsync`) for a session loop run via
  `Task.Run`, **never choose a step** (#1122), and **always pass the progress probe**: the budget bounds a STALL,
  not the run. **Never** use `SleepAsync(subExposure)` in a pump loop.

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
- **Ground truth for fine telemetry is the NODE's Debug log, not the inspector snapshot.** The rig runs in
  `tianwen-server` (P6, #936), so per-frame guide stats, HA and pier side come from
  `%LOCALAPPDATA%/TianWen/Logs/<date>/Server_*.log`; the GUI's `LiveSessionState` is a mirror of it and can lag
  during the guide loop, and `GUI_*.log` records only what the window did.
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
- Target-typed `new(...)` where the type is apparent (a declared field, local, parameter, return or collection element type),
  `new SomeType()` where it is not (the owner relaxed "always `new SomeType()`" on 2026-10-09)
- Expression-bodied: properties yes; a method while it is small, one that fits on one line (`when_on_single_line` in
  `src/.editorconfig`) or a single switch expression, wrapped or not; a block body otherwise; constructors no
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

URI-addressed: `DeviceBase` (URI identity), `IDeviceSource<T>` (driver backends), `ICombinedDeviceManager`
(coordinates sources), `IDeviceUriRegistry` (URI → instance map). Each subclass reads query keys (`?key=value`)
defined in `DeviceQueryKey` (see the class XML docs). Full driver hierarchy and the rules below in full:
`docs/architecture/device-architecture.md` ("Device management rules (moved from CLAUDE.md, 2026-09-29)", "Serial
probing rules in full (moved from CLAUDE.md, 2026-10-09)").

- **Every native serial protocol has ONE architecture document** (indexed under "Native serial protocols" there),
  and a new native driver ships with its document in its first commit, mapping every driver member to the wire,
  including a connected device that does not answer: a transient THROW (#810).
- **One driver per device; a driver's connect is all or nothing** (#806).
- **A vendor's native binaries reach an app through the REFERENCE GRAPH, and nothing downstream can filter them
  out**, so the ZWO and QHYCCD drivers live in `TianWen.Devices.Native`, not `TianWen.Lib` (namespaces stayed
  `TianWen.Lib.Devices.*` on purpose; `internal` behind `InternalsVisibleTo`). Only `tianwen-server` references it
  (P6, #936): the GUI, the CLI and `tianwen-fits` must NOT, nor a new consumer.
- **A profile scan never probes a COM port, and a port that will not TAKE bytes is given up, not retried**
  (`DiscoverOnlyDeviceType(type)`; only an uncompleted WRITE raises `ISerialConnection.HasAbandonedIo`, a READ
  timeout never does). **A Bluetooth port whose far end can never be an instrument is not probed at all**
  (`SerialProbeExclusion`, `SerialPortInfo.Bluetooth`); a pinned port is verified whatever it is.
- **Serial I/O is the Serial.Lib sibling's, and closing a connection is asynchronous** (`SerialConnection` only
  adapts it; `ISerialConnection` closes through `TryCloseAsync`, a synchronous `Dispose` uses `CloseInBackground`
  and never waits). A new transport guarantee goes into Serial.Lib with a test there, never into the adapter:
  `docs/plans/serial-lib.md`.

### Device Ownership (the hub lease)

A run that is driving hardware **claims it from the hub**, and nothing else may disconnect or command a claimed
device: `IDeviceHub.TryAcquireLease` / `DeviceLeaseSet.Acquire` (all-or-nothing) / `DeviceOwnershipGate.Evaluate`
(the one verdict + `Describe()`). `Session.RunAsync` and `RunFlatsOnlyAsync` claim `Setup.DeviceUris()` for the
whole run (released in the `finally`); **polar alignment claims the mount and its capture devices, a planetary
capture its camera** (`DeviceLeaseSet.TryAcquire`), and **every new kind of run owes the same**. Rules in full:
`docs/architecture/device-ownership.md`. What bites:

- **A run's drivers ARE the hub's** (`ControllableDeviceBase.ConnectAsync(hub)`, `IDeviceHub.AdoptAsync`): **read
  `Driver` only after that connect**.
- **Reads are never leased**; a lease refuses only *taking the driver away* and *commanding it*.
- **Never guard hardware access on a UI flag** (`LiveSessionState.IsRunning` is false during a flat run; hence
  `HasActiveRun`): ask `DeviceOwnershipGate`, which the node does, and show its refusal.
- **Enforcement is asymmetric, deliberately**: `DisconnectAsync` throws `DeviceLeasedException` unless `force:
  true`, **which is for process shutdown only** (GUI "Force Off" means "skip the warm-up"); actuation call sites ask
  the gate, with no override by design.
- **Stopping the rig is ONE order, `RigShutdown`**: the run ends first, then the devices are warmed and disconnected
  as the node's jobs (`RigShutdownOrderTests`). **Never queue a camera warm-up beside a run's cancel.**
- **Quitting is ONE rule, `AppQuit`**, for the GUI and the TUI: only the LAST client attached asks
  (`ClientsAttached`), the question is `LiveSessionState.QuitDialog`, and every quit cancels the host's OWN
  background work first.
- `GetDisconnectSafetyAsync` is a **hardware**-safety check, not an ownership check: ask the gate first.

Pinned by `DeviceOwnershipTests`, including three that drive a real `Session`/flat run end to end.

### Alpaca Backend (ASCOM Remote / Alpaca HTTP)

`AddAlpaca()` is a **fully functional** device source (camera, telescope, focuser, filter wheel, switch,
cover-calibrator) over the ASCOM Alpaca REST API, wired into CLI / Server / GUI alongside `AddAscom()`: the
primary cross-platform path for a headless Linux / Raspberry Pi host, where the Windows-only COM bridge is
unavailable. Write-up: `docs/architecture/device-architecture.md`, "Alpaca camera image transfer".

**Camera image transfer goes through the binary `application/imagebytes` protocol, NOT the legacy JSON
`imagearray`** (an order of magnitude slower for full frames): `AlpacaImageBytes.DecodeChannel` is the pure
decoder, `AlpacaClient.GetImageArrayBytesAsync` negotiates it via `Accept: application/imagebytes,
application/json` and verifies the response `Content-Type`. **Wire-order gotcha:** ImageBytes is
`[Dimension1 = Width(X), Dimension2 = Height(Y)]` row-major, i.e. column-major in image terms, so the flat index
of `(x, y)` is `y + x*Height`; `DecodeChannel` transposes into `Channel`'s `[y, x]`. `AlpacaCameraDriver`
downloads + decodes **once** when the server first reports `imageready`; `StartExposureAsync` clears the
buffers so the next frame re-downloads. Validated against a live OmniSim by
`AlpacaSimulatorTests.Camera_ExposesAndDownloadsViaImageBytes`; the decoder is byte-pinned by
`AlpacaImageBytesTests`.

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
- OS-selected in `AddExternal`, **and the file store on any OS under `TIANWEN_DATA_ROOT`**
  (`TianWenDataRoot.IsOverridden`): a data tree kept apart from the user's (a test's node) must not write the user's
  vault. A node composed without `AddExternal` falls back to the file store under its own data folder. Tests exercise
  `FileCredentialStore` over a temp dir (the Windows vault is not unit-tested; it would write to the real per-user store).

A masked `DeviceSettingDescriptor` (`Mask: true`) routes its edit to the store, never the URI, by ONE rule for the GUI
and the node (`DeviceSettingHelper.Commit`; the GUI through `AppSignalHandler`'s `StringSettingInput.OnCommit`, which
re-fetches weather afterwards, the node through `PUT /api/v1/devices/setting`, socket only, which never answers the
value back). A leftover
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
- **An external tool never outlives the call that started it** (`BoundedProcess.WaitForExitOrKillAsync`, ONE rule
  for the plate solvers and the planetary corpus's 7-Zip: the probe and a path translation are bounded by `ProbeTimeout`,
  a solve by the caller's token, and either way the whole process tree is killed). `PlateSolverFactory` awaits EVERY solver's probe before solving, so an
  unbounded `wsl solve-field -h` against a wedged WSL blocked all plate solving, the built-in solver
  included, and every process that probed stranded its `wsl.exe` pair (2,444 of them on 2026-09-27; an
  orphan also holds its caller's output pipe). `tools/restart-wsl.ps1` clears a wedged service.

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

**A hover highlight and the click MUST come from one resolver, or they will disagree**:
`SkyMapSearchActions.TryResolveHit`, turned into an info panel by `SelectObjectByClick` and into a
`SkyMapHoverTarget` by `ResolveHoverAtScreenPoint` (`SkyMapTab.Hover.cs`). **Hover does not honour Ctrl**; it is a
HIGHLIGHT, not hover selection. Rules in full, with the measurements: `docs/plans/in-app-sky-atlas.md`, "Hover
highlight: rules in full (moved from CLAUDE.md)".

- **The wash is one `Renderer.FillEllipse`** of the object's OWN ellipse (`ViewerState.HoverObject` in the FITS
  viewer), **drawn FIRST of the annotation layers**.
- **Only a CHANGED answer asks for a frame** (`HoverResolveMinInterval`, 8 ms): assert with `HoverFrameRequests`,
  never `NeedsRedraw`.
- **A crowded field is settled by the RESOLVER, never by delaying the answer** (`MarkerHitRadiusPx`, 12 px; the
  smallest containing footprint wins, the 20 px near tolerance only when none contains it).
- **Resolve cost is WHETHER THE STAR PASS RUNS, not a per-star cost** (`SkyMapHoverResolveBenchmarks`): **a cell is
  walked through `IRaDecIndex.EnumerateCell`, never the indexer, on a per-frame path**; attribute with
  `SkyMapHoverResolveCostProbe`, never a Debug timing or a test-host Stopwatch.
- **The target is dropped when the view moved**, compared at DRAW time.
- **`ShowOnlyObjectsWithPicture` and the [O]/[D] gates go through `OverlayEngine.PassesLayerFilter`**, the CLICK
  resolver included, **and it is in BOTH gather cache keys**.

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

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator. `RunAsync`: `InitialisationAsync` →
wait for twilight → `CoolCamerasToSetpointAsync` → `InitialRoughFocusAsync` → `AutoFocusAllTelescopesAsync` →
`CalibrateGuiderAsync` → `ObservationLoopAsync`. The invariants below in full, with their reasons:
`docs/architecture/session-invariants.md` (both "Moved from CLAUDE.md" sections), and the docs each names.

- **Single-mount / multi-OTA**: `Setup.Telescopes` is plural, `Setup.Mount` is one; all OTAs share pointing and
  the target, so any "branch" or "re-order" logic must operate on the OTA set as a single unit.
- **A failed run carries a plain-language `ISession.FailureReason`** (`SessionStateDto.FailureReason`; throw
  `SessionFailedException(userMessage, inner)`); init connects (`ConnectOrFailAsync`) are **deliberately
  fail-fast**, the END-of-session flat block best-effort. Pinned by `SessionFailureReasonTests`.
- **Guider calibration slews to HA −0.5h, EAST of the meridian, NOT west** (`CalibrateGuiderAsync`,
  `Session.Lifecycle.cs`): west is past the flip on the opposite pier side, so an inverted Dec sense and a Dec
  runaway. Hemisphere-independent; pinned by a both-hemisphere `[Theory]` in `SessionLifecycleTests`.
- **`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (3 min) on the MOUNT
  clock** (`WaitForScheduledStartAsync`, `GetMountUtcNowAsync`); a same- or past-Start schedule advances linearly,
  a late start runs its full `Duration`, one past session end is skipped.
- **Meridian-flip oscillation: never re-introduce an HA-only flip check.** `MeridianFlipDecision.DecideFlipAction`
  returns `Continue` once `hasFlipped`, `AlreadyFlipped` once the pier side changed, and reaches `CommandFlip`
  only when not already on `DestinationSideOfPierAsync(target)`. **Load-bearing on SkyWatcher**, whose pier side
  is the Dec encoder (stuck `Slewing`, zero exposures). Pinned by `MeridianFlipDecisionTests` + a
  `mountPort:"SkyWatcher"` loop test.
- **Whether a flip HAPPENED is read off the IMAGE wherever the pointing state is `Computed`** (LX200 base, SGP):
  `WCS.RotationDeg` against the recentre's own solve, `MeridianFlipVerification.FromSolves`, `Inconclusive` falls
  back to the mount. `FakeMountDriver`'s tube state only MOTION changes (**a sync must not touch it**).
  `docs/plans/meridian-flip-verification.md`.
- **No astro-dark is a fallback, never a throw**: `SessionEndTimeAsync` takes
  `ObservationScheduler.CalculateNightWindow`'s chain (−18° → −15° → −12° → polar-night 24h) and must **never**
  demand `EventTimes(...).Count == 1` (at 50.9°N at solstice the sun bottoms at −15.7°).
- **Focus-drift refocus is a TREND** (`FocusDriftDetector.EstimateTrendHfd` over the last
  `SessionConfiguration.FocusDriftSampleSize` comparable frames, `FrameMetrics.IsComparableTo`, against
  `FocusDriftThreshold`): **the LSQ divisor is the INCLUDED-sample count**; **the window is cleared on a drift
  refocus and on target change**; **one baseline per ACQUISITION SETTING** (`AcquisitionSetting`, #820). Assert
  through `Session.DriftRefocusCount`. `docs/plans/focus-tolerance.md`.

**Mount safety limits are NOT the meridian flip.** `MountLimits.Evaluate` (`Sequencing/MountLimits.cs`, pure) is
the mechanical bound, where the TUBE meets the pier or the ground; a flip is a *scheduling* choice. Derivations,
the GSServer sweep, live verification: `docs/plans/mount-safety-limits.md`, `docs/plans/gss-parity-audit.md`.
What bites:

- **HORIZON keys on HOUR ANGLE, not pier side**; **MERIDIAN is an RA-AXIS test where the pointing state is
  load-bearing** (`Normal ? -HA : HA`).
- **`IMountDriver.GetAxisAngleAsync` (MECHANICAL, SkyWatcher only) WINS when present** (`MountLimitVerdict.Basis`).
  **Only a MEASURED pointing state, or one the SESSION verified, may drive it** (`MountLimits.TrustedPointingState`;
  `Session._verifiedPointingState`; `MountLimitWatcher` keeps the two-argument form).
- **Warn and act are a threshold plus a non-negative EXTRA**; **`alreadyActed` is a latch that downgrades to
  `Warn`, never clears**.
- **The meridian limit is in MINUTES and is the ULTIMATE CLAMP on the flip** (inside `MeridianFlipDecision`);
  never derive the limit from the flip.
- **Config lives on `ProfileData.MountLimits`**, never `SessionConfiguration`; **enforcement is in
  `PollDeviceStatesAsync`** and routes to `ImageLoopNextAction.LimitReached`, NOT `DeviceUnrecoverable`;
  **parking is opt-in for both limits**.
- **A mount that stops tracking unasked is a LIMIT EVENT, not a fault** (`Session.DetectDriverEnforcedStop`;
  `_raPulseOnStoppedAxis`).
- **Test traps:** `default(PointingState)` is `Normal`, SILENT for the meridian test, so place the mount by SYNC;
  **a run's site is `Session.Site`** (#798, `MountSiteExtensions`): a NaN altitude switches the HORIZON test off.
- **The verdict is telemetry** to the Home card's Flip column, on CLASS transitions only.
- **`MountLimitWatcher` (`Sequencing/`) is the enforcement half with no session running**: each 5 s it matches a
  connected mount against every discovered profile's `Mount` and skips one a session leases; a `BackgroundService`
  in `tianwen-server` only, since P6 (#936).

**A guide pulse is TWO methods, and picking the wrong one is silent.** `StartPulseGuideAsync` (`IMountDriver` /
`ICameraDriver` / `IPulseGuideTarget`) commands the hardware and RETURNS; `PulseGuideAsync`
(`PulseGuideTargetExtensions`) starts AND waits, which a caller almost always means. **Every driver honours the
primitive**: **the in-flight count rises BEFORE the first write and falls only when the hold ends** (GSS #109),
and **a failed restore parks in `_pendingPulseFault`**, re-thrown from the next `StartPulseGuideAsync` *and*
`IsPulseGuidingAsync`. **A test for the non-blocking starter needs `ExternalTimePump` and a `[Fact(Timeout=…)]`**.
`docs/plans/gss-parity-audit.md`.

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
- **A Canon body holds every object it announces until it is released, and a press until it is let go of**, and answers
  `DeviceBusy` to every write while it holds either, across reconnects. So every announced object goes through ONE queue that
  downloads the raw an exposure is owed and releases the rest (a RAW+JPEG body wedged after 8 frames), mirror lockup is taken
  per exposure (`TakePictureWithMirrorLockupAsync`), never left armed on the body (armed, a release only raises the mirror),
  and a shutter speed is one the body announces. All five causes, measured: `driver-resilience.md`, "What stopped the body".

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
See `docs/plans/polar-alignment.md` for the math/algorithm. **`PolarAlignmentRun` is the ONE routine from
start to restore**, which the GUI runs in its own process and the node for a client (`/api/v1/polar`): it
resolves the devices from the profile, claims them, runs Phase A and the refine loop, and restores the mount
however it ends; a host decides only where its state and frames go. **Cancellation is how it ends** (Done and
Cancel are one exit), never a failure.

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
- **The GUI surface is a MODE on the Live Session tab, not a tab** (`LiveSessionMode.Flats`). The flat run
  is the node's (P6), started with the session tab's configuration; the view knows it by its mirror's run
  (`LiveSessionState.IsFlatRunGoingOn`), never by `IsRunning`, which a flat run does not set. That is exactly
  why hardware guards ask `DeviceOwnershipGate` and never a UI flag (see Device Ownership).
- **With no `PromptRequested` subscriber the session answers `UnattendedPromptResponse`, which
  defaults to `Decline`** -- proceeding would assert a physical act nobody performed.
- **Native Gemini FlatPanel Lite driver** (`AddGemini()`): an ASCOM-free serial `ICoverDriver`. Wire
  spec + its two silent traps: `docs/architecture/gemini-flatpanel-lite-protocol.md`.

### Deep-Sky Stacking + Enhance Pipeline (`TianWen.Lib.Imaging.Stacking`)

`StackingPipeline.RunAsync` (CLI `tianwen stack`): scan DataRoot; build the bias, dark and flat masters;
register each light group (star-quad match); integrate (auto-picked: Bayer drizzle on RGGB with at least
`DrizzleOptions.MinFrameCount` frames, else AHD plus sigma-clip rejection); `MasterPostProcessor.WriteMasterAsync`
(plate-solve, SPCC white balance, FITS, autocrop, optional enhance, previews). Completely separate from the
Planetary stacker below.

**Flowcharts, the render model, and every rule below with its measurements:
`docs/architecture/stacking-render-pipeline.md`** ("The rules in full"). Comet integration: read
`docs/plans/comet-integration.md` first (thirteen silent traps). The rules that bite:

- **Output contract:** linear (canonical) is FITS, the full-frame `master_<slug>.fits` AND the cropped
  `_autocrop.fits`; the display outputs (PNG, `--split-plates` TIFFs) are ALWAYS the autocrop, rendered by
  `MasterPostProcessor`, never the CLI (which renders nothing).
- **A written master is [0, 1] with true labels** (`IntegratedMaster.Labelled`); scale with
  `ScaleFloatValuesToUnitCeiling` into NEW planes, not `ScaleFloatValuesToUnit`.
- **Never take a whole-frame statistic over a CFA mosaic** (split by photosite, keep strides ODD;
  `BadPixelDetection.DefaultMaxMaskedFraction`). **A NaN in a rejection sample column disables rejection in
  every rejector** (`PixelRejection.MarkAbsent`).
- **Per-pixel sidecars are QUANTISED, then gzipped** (`IntegrationFitsWriter.MapStorage`, `ExistingSidecarPath`,
  `IsMapSidecarPath`); the bad pixel map is APP's format, on SENSOR geometry.
- **Drizzle rejects per deposited sample** (#93, `DrizzleClip.SlopeScale`); a rejecting drizzle streams
  `RawBayerFrames` twice; its parallel strips are BIT-IDENTICAL to serial (`DrizzleParallelBitIdentityTests`).
- **Never re-ingest our own outputs** (`STACK_N > 0` or `SWCREATE`, unless `--include-integrations`).
- **Calibration is grouped by temperature RUN; a session is matched on its lights' MEDIAN temperature**
  (`CalibrationEpochs.SetGroupKey`, `SplitSets`, `CalibrationResolver.SessionKey`); **a flat run is its
  FOLDER whatever its exposures** (`JoinFlatRuns`, sky flats), never a flat key without exposure, which
  blends separate nights.
- **The archive scan is ONE function, `SessionDiscovery.ScanAsync`, and never excludes before reading**
  (`FitsHeaderIndex`; `--rebuild-session <wildcard>`).
- **A master's width is its subs', plus the warp kernel's, plus misregistration** (`Lanczos3Clamped` is the
  default, its clamp mandatory on OSC; `UnmovedTolerancePx`, `Image.SinglePhotositeFractionMax`).
- **A captured non-light says so in `IMAGETYP`** (`FrameType.Focus`, `FrameType.Scout`); never widen a
  consumer's filter to admit them.
- **`--enhance`** runs `SharpenPipeline` ONCE (`CanonicalProgram`, shaped by what SERVES); options parse once in
  `EnhanceOptions.TryParse`; `--ai-backend auto|rc|tianwen`. Stellar-sharpen is opt-in and served by nothing today.
- **Render model:** ONE SPCC white balance, then each plate self-stretches; SPCC's clip test reads the OBSERVED
  peak; the normaliser anchors on `Image.Pedestal`; enhanced masters use `MasterPreviewRenderer.WithZeroPedestal`.
  **An enhance solves it on the LINEAR master first** (`MasterPreviewRenderer.SolveWhiteBalanceAsync`), and a
  broadband SPCC fit goes INTO the pixels the enhance sees (`WhiteBalanceStep` at the program head, PixInsight's
  order); every enhanced file and document says so (`ColourCalibration.Applied`, FITS `WBAPPLD`) and is shown with no
  balance of its own, while the raw master keeps its pixels and the triple. Nothing solves SPCC on enhanced stars
  (5 to 26 % bluer on Centaurus A); the sky-background estimate and a line-selective fit stay display multipliers.
  **SPCC is broadband-only as a MODEL, not a gate** (`docs/plans/narrowband-colour.md`; `ResolveAuto` refuses to
  assert the fit as colour).
- **The filter-curve matcher never answers with a brand, nor a more specific product** (re-run
  `ReportKnownLightPollutionFilters`); **a QE curve keys on the DIE (`_cameraToSensorAliases`), a crop's
  geometry on the CAMERA.**

### Planetary Lucky-Imaging Stack (`TianWen.Lib.Imaging.Planetary`)

A CPU-first planetary stacker, **completely separate** from the deep-sky `Imaging.Stacking` pipeline
(star-quad align + sigma-clip rejection don't apply to a featureless disk). Batch pipeline, live
streaming stacker, benchmarks: `docs/plans/planetary-stacking.md`. Live-capture drivers, controls, the
fake's noise model, the recenter loop: `docs/plans/live-planetary-capture.md`. The restoration programme
(R0 to R9, Saturn S1 to S6, the enhanced pipeline): `docs/plans/planetary-restoration.md`; its literature:
`docs/architecture/planetary-literature.md`. **Every rule below in full, with the measurement behind it:
`planetary-restoration.md` and `live-planetary-capture.md`, "Rules in full (moved from CLAUDE.md,
2026-10-09)"**; read it before changing what a rule names. One line per rule here:

- **`PlanetaryMaster` is the single shared "accumulators -> master" finalize**, so the batch and live
  masters can never drift.
- **Live capture: camera ADU normalises to [0,1] at the stream boundary** (`LiveCameraFrameStream.DeepCopy`);
  the stream layout derives from the ACTUAL frame, **NOT** the camera's `SensorType`; **no driver call crosses
  onto the render thread** (it stages, the capture loop drains + applies). Preview defaults to **linear**
  (`StretchMode.None`).
- **The planetary corpus is read, never written** (R0, `planetary survey` / `convert` / `crop`): a crop keeps
  EVERY frame, the Bayer phase and the source's header and trailer BYTE FOR BYTE (`SerReader.CropTo`, never a
  `SerWriter` copy); every write goes through `ScratchSpace`, which keeps the drive's 109 GB reserve.
- **A planet's orientation comes from `PhysicalEphemeris`, never from an image**: the pole angle is referred to
  the pole of DATE, and Meeus's central meridians are corrected for phase.
- **Saturn's rings are drawn from the ephemeris, never fitted** (`SaturnRings`, #1231), and **the limb fit models
  them** (`LimbFitOptions.Rings`, `StartRinged`, #1232: a ring's covered and shaded shares NESTED, never
  multiplied); **B and A carry a slope** (`LimbFit.RingSlopes`, #1184). **A metric reads Saturn as globe and
  rings** (`MetricDisk.Rings`, `ClearRadiiAt`, `RingTouched`), **a limb profile compares the same PIXELS**
  (`PlanetaryMetrics.LimbReachPx`), and **an OPAL Saturn map is read through `PlanetMap.FilledZonally`**.
- **A disk's centre and scale come from `PlanetaryLimbFit`, never the centre of mass or an edge detector**;
  **always model the phase** (10 degrees ignored put a centre 2.7 px off); start from `PlanetaryLimbFit.Start`,
  never `PlanetaryDisk.BoundingBox`. Its evaluation runs on every core with one walk's bits (#1106; never a
  reduction across bands). **Each COLOUR is placed by its own limb fit** (`PlanetaryChannelAlignment`, #1202),
  never by correlation or any measure that weighs brightness; **a live view reads its colours ONCE, by its
  Derive** (`PlanetaryLiveLimb.Channels`, `TryAlign`).
- **Which telescope took a capture is read off its frames, one-sided** (`planetary aperture`): a measured cutoff
  is only a LOWER bound; spikes say Newtonian, their absence says nothing. A plain FFT screen is 28 % short of
  Kolmogorov without subharmonics; a small render grid prints a four-fold halo.
- **A synthetic capture is compared through `PlanetaryCaptureStatistics`, one routine for twin and real**
  (`planetary degrade`): the disk's motion is the limb fit's (`LimbSeeingRms`); an 8-bit sky is fitted through
  the rounding (`SkyByPixels`); **a statistic is read against its spread over seeds** (`--seed`: lag-1 wanders
  67 %, so a 10 % band tests nothing); a twin's level is the planet's own (`PlanetaryDegrade.ShownLevelGain`,
  #1233); a warped frame keeps the pupil's light; a Saturn twin's rings are structured (`SaturnRings.Structured`,
  `RingFootprint`).
- **The stack's defaults ARE the measured best, and the recipe before is one switch away**
  (`PlanetaryStackOptions.Legacy`, `--legacy`, #1159). **The rolling stack takes the gradient and plain
  correlation, never Lanczos-3** (`RollingWindowOptions`, #1272), **folds its best quarter and keeps its
  reference in place** (`KeepFraction` 0.25, `ReReferenceInPlace`, #1174), and **a pixel no folded frame reaches
  reads 0** (`RollingWindowStacker.UncoveredWeight`, #1319). **Judge a live stack by the frames it GRADES a
  second and its rebuilds by cause (`RebuildCauses`), never by how far its window's end moved.** **No step may
  grow faster than linearly in frames** (the user, 2026-10-02). **A test that depends on one of these choices
  pins it.** **Lanczos-3's clamp applies only where every tap is non-negative** (`Image.Lanczos3Finish`). A run
  is de-rotated unasked only past `PlanetaryDerotationOptions.MinimumTurnPx`, its north decided by the run's
  quarters, never the limb fit.
- **A recorded capture's best stack is `PlanetaryBestStack`, ONE routine for `planetary stack` and `tianwen-fits`'
  Best stack** (Shift+K, #1159; `DerotationFor`, `Sharpen`, `OutputPaths`, `PupilFor`,
  `PlanetaryTelescopePersistence`, #1179); the planet and filter are said before the run (`PlanetaryCaptureName`).
  **A master names its planet in `OBJECT` and opens in `StretchMode.Planetary`** (`StretchMode.ForFrame`, ONE rule
  for the viewer, the thumbnail and `tianwen view`). **The batch stack runs on every core with one walk's bits**
  (`ParallelFor.RunBands`, `PlanetaryFrameBatches`): **never split a fold across FRAMES**, whose sum is ordered.
- **A planetary master is sharpened by `PlanetarySharpening`, ONE routine for `planetary stack`, `planetary
  sharpen` and the live view's Derive** (`PlanetaryBestStack.DeriveGains`, `WaveletDerivation`,
  `PlanetaryLiveLimb`, `PlanetaryLimbWindow`, #1201: the limb belongs to the capture, so Reset keeps it):
  - **No sharpening is drawn outside the limb** (#1171, `PlanetaryLimbFix.ModelFeathered`) but for a moon
    (`PlanetaryMetrics.CompactSources`, #1181; `PlanetaryLimbWindow.PasteMoonsBeyond`, #1211). **A moon is a
    source above the PLANE its surroundings make, never only above its box's median** (#1301,
    `PlanetaryMetrics.PeakAbovePlane`, `PlanetaryDering.KeepMoon`); read every CHANNEL's moons.
  - **A diffraction PSF's grid spans twice the extent it is used over** (`PlanetaryRender.DiffractionGridFor`,
    #1213); a twin carries the pupil's far wing only with `--far-wing` (`DegradeOptions.FarWing`, #1222).
  - **The derived gains are the truth's; anything past it is the strength OPTION, never the default** (#1251,
    `PlanetarySharpenOptions.Strength`, `--strength`, `PlanetaryWaveletGains.Boost`); **never multiply derived
    gains**. **"The truth" is the planet through the pupil's own diffraction**: undoing the telescope too is
    an option (`PlanetarySharpenTarget.Aperture`, `--target aperture`, #1366; score it `--score-against
    aperture`). **Read a gain set by its FILTER** (`PlanetaryWaveletGains.Transfer`). The panel's
    `PlanetaryStrength` is never saved.
  - **One Derive fits every stop** (`PlanetarySharpening.StrengthStops`, `DerivedGains.GainsAt`,
    `ViewerState.ChooseStrength`, #1314); **every channel keeps its own derived gains**
    (`DerivedGains.ChannelGainsAt`, `WaveletSharpenOptions.ChannelGains`); a new stop goes into that list,
    never a second one.
  - **A master opened as a file gets the stacked view's own layer** (`LiveStackPreviewSource` on a
    `FixedMaster`), never a second sharpening path; **a Save writes what is on show** (`ShownDocument`); **a
    SER's view is one switch, Frames / Live / Best** (`ViewerState.ChoosePlanetaryView`,
    `PlanetaryBestStackResult.Layer`); **the derived dials never clamp at a master's peak** (`SliderOptions`,
    `Clamp = false`).
  - **Saturn is sharpened as Jupiter is** (#1184, `ClearRadiiAt` at most 1); **its globe turns under rings that
    stay** (#1234: a de-rotation never moves a `RingTouched` pixel nor reads a source under one).
  - **The window is the frame MIRRORED, never zero-padded**; on a crop with no sky past 2.5 radii the metrics'
    sky is the farthest tenth past 1.3 (`PlanetaryMetrics.SkyLevel`).
  - **A colour master's finest band is kept as stacked** (`ColourFinestBand`, #1187); read a colour lattice PER
    CHANNEL, since it cancels in the luminance.
  - **A colour master of Jupiter or Saturn is balanced to the planet's own colour AFTER the sharpening**
    (`PlanetaryColourBalance`, #1212, `PlanetaryLiveLimb.Balance`; Saturn's globe where its rings leave it clear,
    #1235), never a saturation about grey; **saturation 1 by default, 1.4 an option** (`EyeSaturation`,
    `--colour-saturation`). **A camera matrix from spectral curves is a least-squares FIT onto the CIE functions**
    (`CameraColorMatrix.ComputeCamXyz`, `CameraColorMatrixTests`), never their projections. **OPAL's I/F factors come
    from its readme** (`PlanetaryColour.ReadmeFilters`), never a constant. **A balanced master's stretch takes ONE
    black point** (`ImageMeta.IsColourBalanced`, CBALSAT, #1229). **Colour is compared with a post AS SHOWN**
    (`PlanetaryReferenceJudge.ReadShown`, #1273). **A colour look is a RENDERING, never a master**
    (`PlanetaryColourLook`, `planetary look`, `ColourLook.Boosted` / `Posted`, #1305; a looked COPY in the viewer,
    `Prepare` / `OnMaster`, #1277).
  - What was measured and NOT adopted, so do not re-run it as new: `--finish cutoff` / `wiener` / `adaptive`
    (#1279, #1281; **the derived gains are already a Wiener**), `--shrink` (#1313, read off `planetary stack
    --halves`), `--noise halves` (#1373, `PlanetarySharpenOptions.NoiseHalves`), `--colour-finest bounded` and
    `--colour-target cut` (#1376: the colour gap is the KERNEL; a true kernel closes 80 % of it, no reshaping
    half). **Every twin is sampled coarser
    than its optics resolve** (`PlanetarySharpenResult.Cutoffs`), so anything near the cutoff is unread there;
    **a twin standing in for a long capture is calibrated at that capture's length**.
  - **Check what a reference IS before reading it** (the 12-inch SCT Jupiter's PNG is its raw stack). The
    sharpening **needs the planet, the instant and the telescope**. **Judge a limb fix by `LimbProfileError` on
    the twins AND by eye on a real capture hard-stretched about its sky** (`LimbRebound` failed R3's rule; the
    dark trough at the limb is left, #1171), and judge the edge on a core-and-halo kernel, never a single Gaussian.
- **A stack is scored through `PlanetaryMetrics` and `planetary measure`; a truth-free metric judges a real
  capture only once it ranks candidates as its truth-based twin does** (Spearman at least 0.8: the limb's
  undershoot does, the halves do not). **A real capture is judged against its own `_stack`/`_post` through
  `planetary judge`** (`PlanetaryReferenceJudge`, #1250), never by a display picture's raw values.
- **A frame's quality is read in the MID bands, never the finest** (`planetary grade`): at 8 bits the Laplacian
  ranks frames at +0.19 against +0.87 to +0.99 for the gradient, `FftHighBandEstimator` and the reference gain;
  **never judge a quality statistic read by the Laplacian**. **Register onto a truth with
  `CorrelationRegistrar`**, never a phase correlation. **A keep is chosen after the sharpening** (#1083,
  `planetary keeps`: about half the frames, against a raw stack's 5 to 10 %).
- **A twin's blur varies over the disk only with a layer at an altitude** (#1071, `--high-r0`,
  `PlanetaryDegrade.Layered`); a real capture's coherent quality needs the still layer to renew in place
  (`--local-renew-ms`, about 200 ms).
- **On an 8-bit frame phase correlation places a patch 3 times worse than plain cross-correlation**
  (`AlignmentPointMatchingTests`; `PlanetaryStackOptions.WhitenedCorrelation` /
  `CaptureStatisticsOptions.WhitenedCorrelation`); **a warp statistic read whitened is the matching's noise**
  (`--plain-correlation`, `CaptureStatisticsOptions.DefaultAlignmentPatchSize`).
- **A dewarp is judged against the TRUE warp, at the mesh the stack applies, never at the points alone**
  (`<capture>.warp`, `planetary dewarp`; `WarpPoolFrames` and `MedianGeometry` change nothing).
- **A stack's resampling kernel is its own blur** (`PlanetaryStackOptions.Interpolation`,
  `PlanetaryResamplingTests`), **and a correlation's peak is CLIMBED** (`PhaseCorrelation.ClimbPeak`, the one
  climb; never a second). **The three-cornered hat (`planetary registration`) credits two estimators that share an
  error with too little, SILENTLY**: rank only in a triple the twin's recorded motion clears.
- **A frame the camera corrupted in readout scores zero everywhere a frame is graded**
  (`FrameGrader.IsCorruptReadout`, `FrameGrader.Grade`), **and so does a frame whose planet is cut or absent**
  (#1237, `FrameGrader.IsCutOrEmpty`, `DropsCutFrames`), **cut by a straight line INSIDE the frame** (#1291,
  `PlanetaryDisk.CutInside`), **smeared** (#1300, `FrameGrader.SmearRatio`, `PlanetaryDisk.Elongation`) or **DIM**
  (#1307, `FrameGrader.DimRatio`). **The planet is found at a level set from the sky and its own peak**
  (`PlanetaryDisk.SkyQuantile`, `SmearLevel`, `PlanetShare`, `FramesCutKept`), **never from the frame's mean or
  spread**, and a frame whose box finds no planet is graded over its planet's own blob; `SelectBest` keeps no zero
  while any frame scores above it.
- **A batch master is cropped to where 0.95 of the frames' FOOTPRINTS reached** (`CropToCoverage`, `--no-crop`,
  `PlanetaryCoverage`, `LargestCoveredRectangle(coverage)`), never the weight the stack folds, and never into the
  planet's `Footprint`; `PlanetaryComposition.Register` moves one night's mono stacks onto one grid.
- **A capture's span is its frames' earliest and latest TIMES, never its first and last frames** (#1292,
  `CaptureSpan`; a de-rotation whose north the run cannot tell is not done, `NorthUnread`). **A long run saved as
  many files is one SESSION** (`PlanetaryCorpus.Sessions`, `--session`, `--epoch`, #1308), **split by filter
  AFTER it is chained** (#1336), **each file taking the session's north** (#1347,
  `PlanetaryDerotationOptions.North`, `LuckyImagingStacker.ReadNorthAsync`, `--each-file`). **A keep sweep grades a
  file once** (`--grade-cache DIR`, `FrameGradeCache`, #1351), and **a new estimator owes a `CacheKey` naming every
  parameter that moves a score**.
- **A colour master is sharpened per channel, which UNMIXES colour** (#1295; `LuminanceOnly`,
  `--sharpen-luminance`, an option): read a colour change against a truth (`ColourAgainstTruth`, `--truth`),
  never by eye on one capture.
- **An alignment point's patch is cut at the EXACT global shift** (`PlanetaryTile.ExtractLumaAt`). **A point's
  plain correlation SHRINKS the shift it reads** (#1081; `PlanetaryStackOptions.MeshGain` stays one). **Dense
  points pay** (#1195), and **`MaxAlignmentPoints` (64) caps any grid silently**. **Judge a point estimator
  against its Cramer-Rao bound** (#1082, `AlignmentPointMatcher.Bound`; `PlanetaryPointEstimator.SquareDifference`
  goes with #1195 or not at all). **Each point keeping its own best frames is an option, never the default**
  (`PlanetaryStackOptions.PointKeep`, `--point-keep`, #1350: 73 % of a point's quality is its frame's), and **a
  point's choice blends between neighbours only** (`AlignmentPointSpacing`).
- **Bayer drizzle to the sensor grid beats the demosaic UNSHARPENED; nothing past the sensor grid pays.**
  **Sharpened, the demosaic stays the colour default until the owner says otherwise** (#1091, #1092;
  `PlanetaryDrizzleOptions.DefaultPixfrac`). **A drizzle luminance in the demosaic's colours is a verb, not the
  default** (`planetary compose luminance` then `lrgb`, #1330).
- **A planet is de-rotated through the spheroid, as ALBEDO, only from sources inside 0.9 radii**
  (`PlanetaryDerotation`, `PlanetaryProjection`, Minnaert's law). **North comes from agreement, never the limb fit
  alone** (`PlanetaryDerotation.AgreementBothWays`, `planetary derotate`). **A mono camera's stacks are composed by
  `PlanetaryComposition`** (`planetary compose`, #1278): each moved onto the reference's disk FIRST, then
  de-rotated; IR luminance (`--lrgb`) is an option. **Each frame is carried inside the stacker**
  (`PlanetaryStackOptions.Derotation`); **two stacks of one camera are moved with ONE north**; **a night's zonal
  drift is below what two stacks agree on** (`planetary drift`, `planetary belts`).
- **The blur and its inverse (R7, R8), when re-measured**: the spectral ratio (`planetary spectral-ratio`) cancels a
  static CONVOLUTION, never a static phase, and an 8-bit sky's noise is its pixels' own spread from frame to frame;
  **the limb fit's kernel is the TOTAL blur and not the stack's in the finest bands**, so never an inverse's kernel
  as fitted (`planetary blur`, `--map`, `planetary inverse`, #1120); **a per-band gain is fitted JOINTLY over the a
  trous bands** (`planetary ceilings`, `planetary gains`, `PlanetaryWaveletGains`, the DISK a term of its own); **an
  inverse is judged by what its knob does to the OTHER bands** (`planetary inverses`); **a ring below the sky is a
  composite kernel's negative lobe: hold the COMPOSITE non-negative** (`planetary ringing`); **sharpen the limb as
  its own channel** (`planetary dering`, `PlanetaryDering.LimbChannel`).
- **The finest band is read off the limb's EDGE, never the limb fit's kernel** (`planetary finest-band`,
  `PlanetaryFinestBand`; noise past about 0.3 cycles a pixel, #1140). **A moon read is the kernel's SHAPE and
  DIRECTIONS, never its level** (`--moons`, `PlanetaryMoonProbe`, `GalileanMoons`; `planetary elongated`,
  `planetary lucky-frames`, `planetary score`, #1155). **A ghost is taken out as what is NOT ROUND** (`planetary
  ghost`, `PlanetaryGhost.Shell`), judged through the SAME source.
- **Read the plan doc before touching the Canon path** (`docs/plans/planetary-stacking.md`, "Live-capture drivers
  and the recenter loop (shipped)": five things that fail SILENTLY). **Recentering is opt-in**, and so is the
  mount jog, whose **sign is uncalibrated**.
- **A stream's depth, high-speed readout and USB bandwidth are its own** (`VideoCaptureOptions`), never the
  camera's settings; **a frame declares the full scale of the depth it was READ OUT in**
  (`DALCameraDriver.MaxAduFor`).
- **The capture loop is `PlanetaryCapture` (Lib), ONE for the GUI, the node and the Preview's live view**
  (`LiveCaptureKind.LiveView`, `NodeLiveView`, #1111); the GUI's `PlanetaryCaptureController` sends controls only as
  they CHANGE (`NodePlanetaryCapture`) and shows the node's masters (`NodeMasters`). **A live view STREAMS and the
  client ASKS for each frame** (`FrameStreamWire`, drop-to-latest); **over this machine's socket every frame goes
  through shared memory** (P4b, #932; `FrameSlotDto`, `NodeFrameSlots`, `AddNodeSharedMemory`). **A recording
  (`SerRecording`) never slows the capture** and **says what it was taken of and through** (#1179,
  `PlanetaryCaptureName.RecordingFileName`). It claims only the camera, so **a recenter nudge asks
  `DeviceOwnershipGate` over the mount first**.

### AI Image Enhancement: RC-Astro (CLI) + TianWen's own models (ONNX)

`SharpenPipeline` (`TianWen.Lib/Imaging/Enhancement/`) orchestrates role-typed enhancers (`IStarRemover` /
`IStellarSharpener` / `INonStellarDeconvolver` / `IDenoiseEnhancer` / `IGradientCorrector` / `IImageDeblurrer`)
over an immutable `SharpenStep[]` program. Selection is **RC-preferred, deferred, and license-gated**:
`AddRcAstroAi()` wraps `AddTianWenAi()` and `Replace`s the RC-servable roles with `DeferredEnhancer` proxies that
choose AND probe the licence on the FIRST `EnhanceAsync`, never at DI registration. Design and every measurement:
`docs/plans/ai-enhancement.md` (this section in full: "Rules in full (moved from CLAUDE.md, 2026-10-09)"),
`docs/plans/rc-astro-enhancers.md`, `docs/plans/osc-narrowband-denoiser.md` § 1o, `docs/plans/denoiser-training.md`.

- **The program is shaped by what SERVES, never by what is registered** (`SharpenPipeline.CapabilitiesFor(input,
  options)`, `CanonicalProgram`): one program for the viewer, the CLI, `stack --enhance` and the endpoint. **A gate
  on a role asks `IEnhancerAvailability.Serves`, never `is null`**: a deferred RC role is registered everywhere.
- **The SETI Astro (SAS Pro AI4) tier was REMOVED on 2026-09-26** (its licence allows use only within SASpro):
  nothing loads its weights, `EnhanceBackend` value 2 stays unassigned, and a model file is never opened or derived
  from; its GPL-3.0 Python is ours to LEARN from (`docs/plans/model-training-roadmap.md` § 8).
- **TianWen's own deconvolver is the whole-frame DEBLUR, and it serves only when asked** (`OnnxTianWenDeconvolver`,
  E3.4d, #844): `--ai-backend tianwen` AND a stated kernel (`EnhanceTuning.Deconvolution`, `--deconv-kernel`), since
  nothing finds the blur in one frame or declines a frame with none (#741). The graph takes the KERNEL as an input;
  `OperatorDeconvolutionRunner` is `n2n_operator_master.py` step for step through `SplineZoom` (scipy's order-3
  zoom), held to `training/denoise/n2n_operator_runtime.py`. **DirectML runs it only because the exporter clears
  `allowzero` on every Reshape** (`clear_reshape_allowzero`; torch.export writes 1 and DirectML fails the session
  build on it), and a new graph exported through torch.export owes the same. `docs/plans/deconvolver-training.md` § 6.
- **DirectML takes the high-performance GPU by PREFERENCE, never adapter 0** (`ExecutionProviderResolver`): DXGI's
  adapter 0 is the Intel UHD 630 beside the GTX 1070 on this desktop, and every TianWen model ran there until
  2026-10-09 (a deconvolution tile 39 s against 9.9 s). Never pass a device index to DirectML.
- **The NAFNet pre-stretch measures COVERED pixels only** (`ChunkedNafnetRunner.ApplyInputStretch`,
  `Image.AbsentPixels`, `Image.MinAndShiftedMedian`; the ring put back exactly, `Image.CopyAbsent`); an interior zero
  still counts.
- **The canvas ring is the PIPELINE's, not a step's** (#1399). `SharpenPipeline.SanitiseForEnhance` makes a border
  ring the zero ring every step reads as absence (it used to get each channel's mean, which every step read as sky),
  and `ProcessAsync` puts it back after EVERY enhancer (`Image.WithRingFrom`: one walk of the ring when the step kept
  it, a copy only when it wrote over it, as an RC-Astro product does). **A gradient corrector holds the ring out of its
  fit**: the classical one fitted a zero ring as sky, an interior model error of 3.5e-3 against 2.4e-6 on a 0.01 sky.
- **In-house N2N denoiser** (`N2nDenoiser`, OSC-only): the default local `IDenoiseEnhancer` and the fallback behind
  NoiseXTerminator, weights an LFS object in `src/TianWen.AI.Imaging/models/`; **any new LFS file type the apps ship
  must be added to `APP_LFS_INCLUDE` in `dotnet.yml`**, or the publish ships a pointer stub as the model.
- **A TianWen model loads only against its contract** (#824, `ModelContract`): `<stem>.contract.json` beside the
  `.onnx` states the weights' SHA-256, the graph's inputs and output and the domain it is FED, checked at first use
  against the file, the graph and the runner's own `ModelFeed`; an absent sidecar, or a field the runner relies on
  left out, is a refusal. A retrain gets a new file name and contract (a plain tracked `Content` file, not LFS), and
  a new model ships its contract in its first commit.
- **RC-Astro (BlurX / NoiseX / StarXTerminator)** is driven through the `rc-astro` CLI's `--json` NDJSON protocol,
  never loaded into ORT (encrypted weights). **A plate outside `[0, 1]` is mapped in and back by
  `RcAstroEnhancerBase`**; **BlurX stops at 1**, so a deblur hands it the brightest star at a quarter of the ceiling
  (`NarrowbandCombination.DeblurAsync`; the enhance pipeline not yet, #1270).
- **A vendor's weights are read WHERE THE VENDOR PUT THEM** (`ModelResolver` finds GraXpert's own cache for
  `graxpert_bge.onnx`), and **never make a shipped capability depend on a script only a checkout can run**.

### Mono Colour Composition: PixInsight's Steps, One Verb Each (`ColourComposition`)

Mono masters made one colour image as a PixInsight mono workflow makes it (`image align`, `linear-fit`, `deblur`,
`remove-stars`, `continuum`, `add-line`, `add-stars`, `luminance`, `denoise`, `lrgb`, `combine`). **Every step is
one verb on one routine, and `image combine` is `ColourComposition.RunAsync`, which calls those same routines in
order and nothing between them** (`ColourCompositionTests`: to the bit). In full: `docs/plans/narrowband-colour.md`
("Mono colour composition rules in full").

- **A step writes on its input's scale**, and several masters go through one enhancer on ONE scale; **what a later
  step needs travels in the file** (`ImageMeta.FluxScale`, `FLUXSCAL`), the continuum scale alone by hand
  (`image continuum --dry-run`).
- **A stars image is read unmasked** (`image add-stars`). **Stars are never denoised**, and the luminance's noise
  weights are read over 4 px blocks (`PixelNoise.FromBlocks`).

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
`tianwen-server [--port 1888]`. **Endpoint inventory, the Alpaca plane, the enhance endpoint, the native-AOT
rules, and every rule below in full with its reason: `docs/architecture/hosting-api.md`** ("Six invariants on
the session plane", "Invariants 7-13 in full"); the node's lifetime and version skew:
`docs/plans/hardware-in-the-server.md`. What bites:

1. **A pushed schedule beats the target queue.** `POST /session/schedule` keeps per-filter plans, the planner's
   `Start` and `AcrossMeridian`; `PendingTarget` carries none and `/session/start` stamps `Start = now`. Never
   route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** `EventBroadcaster` answers
   `SessionPromptEventArgs.DefaultIfUnanswerable` at once with no NATIVE client that may command, else holds with
   no timer, attaching as the node starts a run, never from its poll. **Liveness is the client's presence BEAT,
   never its socket**: a client beats from the loop that DRAWS it (`TianWenEventStream.Beat()`, the GUI from
   `SdlEventLoop.OnLoopIteration`, never a timer), older than `NodeWire.PresenceLapse` is nobody. **Any new
   subscriber on a headless path owes the same**; **whoever holds a prompt drops it on `Settled`**; **a broadcast
   only queues** (`EventHub`), and a client that falls behind is dropped to resync by polling.
3. **Numeric enums on the wire** (no `JsonStringEnumConverter` on `HostingJsonContext`): default a `required`
   enum on a request DTO.
4. **Previews go through the shared stretch, never a private one** (`PreviewEncoder`: `StretchSolver` +
   `Image.RenderStretchedRgba`, `Auto` resolved as the live pane does). **A preview LEASES the frame and answers
   `If-None-Match` with a 304 before touching it.** **Every route that serves a frame reads `NodeFrames`**: ONE
   token per source, the node's, never a producer's own number nor the camera's `FrameNumber`; a session's
   preview slot holds a lease of its own (`Session.PublishCapturedImage`).
5. **The Alpaca plane is a DEVICE plane and cannot become the session plane.** Ownership there is the hub lease
   (actuation and `Connected=false` answer `0x40B`; never make the plane read-only during a session); device
   numbers come from the **ACTIVE PROFILE, in profile order**, never from discovery. **The native and ninaAPI
   actuation routes ask the same lease (`ActuationGate`, 409 naming the run)**, and a new one owes the same.
6. **AOT is verified by `dotnet publish -r <rid>`, not `dotnet build`.** RDG stays enabled in the
   **`TianWen.Hosting` library**, both JSON contexts stay registered via `ConfigureHttpJsonOptions`, and
   **never reintroduce a `ResponseEnvelope<object>` or an anonymous-type payload**.
7. **A run is the NODE's, never a request's.** Every start goes through `IHostedSession.TryStartAsync` and only
   `TryAbort` cancels it; an abort ends through `Finalise`, and the host stopping warms cameras inside
   `HostedSession.ShutdownBudget`; **a request that lasts ends at `ApplicationStopping`** (#985). **A run is of
   any kind** (`INodeRun`; `PlanetaryCapture.TryPrepare`, then `StartPrepared`): a refused start NAMES the run
   going on (`NodeRuns.AlreadyGoingOn`), a stop NAMES the run it means (`TryAbort(INodeRun)`), and an interactive
   run stops unwatched (`INodeRun.EndsUnwatched`, `NodeRunWatch`; `NodeRunsProcessTests`, `--detach-grace`).
8. **`new SessionConfiguration()` is the DECLARED defaults; `default(SessionConfiguration)` is all zeros.** **A
   field added to the configuration goes into `SessionConfigApiDto` and `SessionConfigApiDtoTests`'s round
   trip**, or it silently cannot cross the wire.
9. **A slow operation is a JOB** (`NodeJobs.StartOrJoin`, 202 + `JobDto`; `GET /jobs/{id}` is authoritative,
   `DELETE` cancels, `JOB-PROGRESS` is a hint). **A job on a device holds the DEVICE** (`NodeJobs.TryStartOrJoin`:
   the same kind joins, another is a 409 naming the holder), **and so does a run's start** (#981); a device-plane
   refusal is an answer before the job (`DeviceOperations`).
10. **The machine's node is found on its SOCKET, and one lock admits it** (`NodeSocket`, `NodeLock`: only the
   holder clears a stale socket, never probe-then-delete; `node.lock` is never deleted). A client uses
   `NodeTransport`, asks `GET /api/v1/node` first (`NodeWire.Version`) and starts the KEEPER (`--keeper`) through
   `LocalNodeLauncher`. **A node that dies leaves a crash journal** (`node.journal`, `NodeJournalService`), believed
   only after `--after-crash <pid>` or when younger than `MachineBoot`, ACTED on, never a run resumed
   (`NodeKeeper.CrashLoopWindow`); **a new place that commands a cooler owes `IDeviceHub.SetCoolerIntent`**, and
   there is **ONE cooling ramp, `CameraCoolingRamp`.** **A node a client started exits a minute after nothing uses
   it** (`NodeIdleExit`, `--idle-exit`; a test whose clients do not beat gives its node a long one, `KeptNode`), and
   **a client replaces an idle, unattended node of another BUILD, not only another wire** (`IsAnotherBuild`).
11. **A device is read through ONE set of readers, `DeviceHubReadingExtensions`** (GUI polls and
   `DeviceStatePoller`), and **the node never reads a device a run holds**. `DEVICE-STATE` is pushed on a change
   at the resolution a reader is shown.
12. **The node writes a profile through ONE writer, `NodeProfiles`, and never from a cached copy** (#930: a stale
   REVISION is a 412, every write pushes `PROFILE-CHANGED`); **a READ goes through it too**. Changing a profile is
   socket-only.
13. **Over TCP, seeing is free and a command needs control** (#1021), decided in ONE middleware, `NodeAccessGate`,
   by the surface a route's GROUP declares (`NodeProtocolMetadata`; a grant is `Authorization: Bearer` /
   `LanGrants`, asked through `LanInvites`). **A new route goes in its surface's group** (`.ReadsOnly()`,
   `.OpenToAsk()`); `NodeAccessTests` fails on a route outside the groups. A native refusal is a 401, never a
   socket-only route's 403. `docs/architecture/hosting-api.md`, "Who may command the node over TCP".

### Remote Rigs (mirror another node's session "as if local")

`docs/plans/remote-profile.md` (complete P1-P5) holds the design, the Home-tab decisions, the sidebar
tab-registration mechanism, every measurement and **every rule below in full** ("Rules in full (moved from
CLAUDE.md)"); the pieces are `TianWen.Hosting.Contracts` (wire DTOs + `HostingJsonContext`) and
`TianWen.RemoteClient` (`TianWenNodeClient`, `TianWenEventStream`, `RemoteSessionMirror`). What bites:

- **Selecting a rig changes what you look at, never what this node owns** (a read-only mirror, no lease);
  `RemoteRigBinding` persists on a stable `NodeId`, never an address.
- **A rig's frames are LINEAR and follow the SCREEN** (`GET /frames/{source}/latest`, never the preview JPEG;
  publish the successor, THEN release; `ViewContexts.PollAll`); a `LastFramePath` is a file only over the local
  socket (`SavedFramePathOnThisMachine`).
- **One `LiveSessionState` per view context**: Active renders, Local is this node's hardware (every
  quit/park/disconnect path), All polls. **Every handler that drives a rig resolves its node with
  `CommandTargetOrSay` at post time**, never `LocalNodeOrSay`; a control of the RUN on screen goes to its own node
  (`StopActiveRun`, `LiveSessionPrompts`).
- **`ISession`/`ISessionTelemetry` split**: telemetry is the wire-crossable read surface, `Setup` stays local.
- **Three wire traps:** never `required` on a nullable wire property or one whose SOURCE can be null; **an
  unknown crosses the native wire as null (`JsonNumber.OrNull`) and reads back NaN (`FromWire`), never 0** (a
  non-finite double is a bodiless 500 for the WHOLE endpoint; the shim, Alpaca and broadcasts keep `ForWire`'s
  0); **a serialised property with a declared default is `set`, never `init`** (`WireDefaultsTests`).
- **`MirrorParityTests` measures that a mirrored session renders as the same session** (`TabPictures`): a member
  added to `LiveSessionState` goes into its snapshot, and a listed divergence that has come to agree is deleted.
- **A run's notes are worded ONCE, `SessionNotes` (Lib)**, its END once it has ended (`RunEnded`).
- **Polling is authoritative; the WebSocket is a latency hint** (`NodeResult<T>`: 404 is not unreachable).
  **Every event the node broadcasts is one the mirror handles** (`RemoteSessionMirror.Dispatch`,
  `BroadcastEventSerializationTests.EveryEvent`). **Whether a rig answers is ONE rule,
  `ISessionTelemetry.Contact`, worded by `RemoteRigActions.DescribeContact`.** **A session's histories cross
  once** (`SessionStateCursor`, `HistoryFrom`, `RemoteSessionMirror.Histories`).
- **A rig's view is planned with the rig's own profile** (`ViewContext.RigProfile`,
  `AppSignalHandler.ProfileOnShow`), **dropping the other view's pins first**. **An idle rig's devices are its
  node's** (`GET /devices/state`, `RigDevices`); **a mount no one holds is `MountState.Unknown` (NaN), never
  `default`** (sexagesimal formatters throw on NaN); `HasActiveRun` asks the node (`ReportedRun.NoSession`).
- **Every request has a time budget** (state 5 s, preview 30 s, control 10 s; 60 s backstop): keep `when (...)`
  filters on the ORIGINAL token, never the linked one.
- **Profile switching is gated** (`ProfileSwitchGate`). **The Home tab** (`Ctrl+H`) is a read-only PROJECTION
  (`HomeBoard.BuildCards` over an `ImmutableArray<RigCard>`); it commands no hardware (`RigSharing`).
- **Control of a rig is its connection's** (P6b, #1021): the token lives in the credential store by node id
  (`NodeGrants`), never a binding file (`NodeConnection`, `ACCESS-CHANGED`, `AskForControlAsync`,
  `ControlRequestQuestion`).

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
`docs/plans/frame-lifecycle.md`. Driver coverage, full-scale numbers, the header parse, and **this section and
the four after it in full**: `docs/architecture/image-pipeline.md`, "Rules in full (moved from CLAUDE.md,
2026-10-09)". Rules that bite:

- **Who owns a frame is stated ONCE, in the `<remarks>` on `Image`.** Never derive "may I release this?" from a
  `ReferenceEquals`: the answer is in hand one branch earlier, else make the producer CONSUME its input.
- Never hold an `Image` from `GetImageAsync` longer than needed; it pins the camera buffer.
- **A preview of a frame someone else owns LEASES it for the copy** (`AstroImageDocument.FromLiveFrameAsync`,
  `LiveFramePreviewSource.AcceptFrame`). **Never `AdoptImageAsync`**, which consumes its input, **and never a bare
  read, on the render thread either**; a frame released before the lease is skipped.
- **A demosaic the viewer OFFERS must have its own branch in `image.frag`**; `DebayerAlgorithm.Auto` resolves via
  `ResolveAuto` before `GpuDebayerMode`, which THROWS on an unresolved Auto; feed VNG a `[0, 1]` mosaic
  (`GpuVngDebayerParityTests`).
- **Every gradient in a demosaic compares two samples of the SAME colour** (`VngFlatFieldBiasTests`, the one test
  on a FLAT field), **and a tap past the frame's edge mirrors about the edge sample, never repeats it** (#1258;
  `AtMirrored`, `SerImaging.At`, the shader's `rawAt`; `DebayerMhcTests`).
- `Array2DPool` is scratch only; camera buffers use `ChannelBuffer` (`ChannelBufferLeakTracker` in DEBUG,
  `PlaneRecycler`). **A full pool's eviction policy is tested on an `Array2DPoolCore` of its own, never through the
  shared pool**, whose Gen2 trim makes such a test measure the machine.
- **A driver's frame is in ADU counts that agree with its `BitDepth` and `MaxADU`** (#1101), the Canon's clipped at
  `CanonWhitePoint` **with the black level kept in**.
- **`Image.MaxValue` is the peak OBSERVED, not saturation** (`ImageMeta.SensorFullScaleAdu`); never route a native
  ADC depth through `BitDepthEx.FromValue`; `Image.UnitScaleDivisor` is the single source of truth for [0,1].

### The image is not necessarily in HDU 0

**Every reader of an image file walks to the first HDU holding an image** (`Fits.ReadFirstImageHdu()` /
`ReadFirstImageHduHeaderOnly()`, `FitsHduExtensions`; `Image.TryReadFitsFile`, `Image.TryReadFitsHeader`,
`MasterCache.ReadFingerprint`, `IntegrationFitsWriter.IsTianWenMaster`): a bare `ReadHDU()` on the read path is a
regression. An `.fz` image is never in HDU 0, and an axis of length zero holds no sample (`BasicHDU.DummyHDU`).

- **A plain file's pixels come through FITS.Lib's `FitsReader`** (`TryReadThroughFitsReader`, falling back to the
  HDU reader for `.gz`, `.fz` and anything it declines); **not a memory mapping** (measured slower). Pinned bit for
  bit by `FitsReadPathParityTests`, whose reader side must be `TryReadThroughFitsReader` itself.
- **A file is OPENED through `Image.OpenFits`, never a `BufferedFile` beside a `.gz` test** (handed one, a gzipped
  file reads back as an EMPTY HDU list, silently); **a gzip stream cannot seek**, so
  `ReadFirstImageHduHeaderOnly` throws over one.
- **`WCS.FromFits` deliberately keeps its single `ReadHDU`**: a solver's `.wcs` is a header with `NAXIS = 0`.
- **`.fz` is matched on `.fz` alone** (`Image.Import.cs`, `AstroImageDocument`, `FitsFolderFrameSource.FitsExtensions`,
  `FileAssociationRegistrar`); a `.fit.fz` entry would be dead code.

### A FITS header becomes an `ImageMeta` in exactly ONE place

`ParseImageMetaFromHeader`, called by BOTH `Image.TryReadFitsFile` and `Image.TryReadFitsHeader`: **a card added to
one read path is a bug in the other** (two copies once read an `EXPOSURE`-only frame as a zero-second exposure);
`FitsPixelScaleTests.TheTwoReadPathsAgreeOnEveryMetadataField` fails on the next divergence.

- **A declared pixel scale beats `FOCALLEN`, which is only a hint** (`Image.GetImageDim`,
  `ImageMeta.DeclaredPixelScale` from `PIXSCALE`/`SCALE`; else `null`, never a guess). **`DeclaredPixelScale` and
  `DerivedPixelScale` are in different conventions**: compare the declared one with `DerivedImageScale`.
- **`ImageMeta.PixelSizeX` is the unbinned PHOTOSITE, the `XPIXSZ` card INCLUDES binning**: the one parse divides by
  `XBINNING`, the one writer multiplies back, nothing else converts; test on a bin-2 frame.
- **A light carries the guiding quality of ITS OWN exposure** (`ImageMeta.Guiding`;
  `GUIDERMS`/`GUIRMSRA`/`GUIRMSDE`/`GUIDEPK`/`GUIDEN`): `GuideStatistics.OverExposure` over `Session.GuideSamples`,
  **never a rolling session average**; **a sample is one guide CORRECTION** (`IGuider.GuideCorrectionEvent`),
  **never a poll of `GetStatsAsync`** (#821); null is not zero. Stamped via `ICameraDriver.GuideStats`.

### Image Mutability: Almost-Immutable with In-Place Escape Hatches

`Image` is logically immutable (`GetChannelSpan -> ReadOnlySpan<float>`); design and measurements:
`docs/plans/frame-lifecycle.md`, `docs/plans/viewer-memory-footprint.md`. Five things mutate in place, and **any
new mutating public API is named `Adopt*`, never a neutral `CreateFrom*`**: `ScaleFloatValuesToUnitInPlace`,
`Normalizer.ApplyCfaInPlace`, `Calibrator.Apply` (the one exception, `CalibratorOwnershipTests`),
`AstroImageDocument.AdoptImageAsync`, and plane RESIDENCY (`TryEvictFloatPlanes`/`Image.ResidentPlanes()`, resolved
once per operation, never per sample; `ImagePlaneResidencyConcurrencyTests`).

- **Eviction is NOT release**, and **every read goes through the `Planes` accessor** (a read of the evicted 0x0
  stub wrote an empty FITS).
- **A plane is `float[,]` and stays one; what changes is how a LOOP reads it** (span-per-row), and a speed-up is
  per-MACHINE: never quote one without naming the box.
- **Every histogram goes through ONE vectorised kernel, `Image.Traverse`, and its running sum is ORDERED**
  (`HistogramKernelParityTests` compares the DOUBLE sum); **parallel row bands go through `Image.TraverseInBands`
  and nowhere else** (#490, `ExactSumBoundProbe`); a parallel site rethrows a body's own exception
  (`ParallelFor.Run`).
- **Test fixtures must not share `Image` instances across tests** (`SharedTestData` caches the path).

### A Canon raw is cropped to its active area on import

**The decoded raster is not the photograph**: `Image.TryReadCanonRaw` crops to `CanonRawFile.ActiveArea`
(FC.SDK.Raw 3.1+). **FC.SDK.Raw does not crop `BayerMosaic` and must not start** (its decoder is byte-exact against
LibRaw's uncropped raw; the overscan stays reachable through `CanonRaw.Open`). **Ask `ActiveArea.CfaPattern`, never
`CanonRawFile.CfaPattern`, after cropping** (an odd offset swaps red and blue). **Measure MaxValue over the pixels
you keep.** **A dimension assertion is not enough in a test** (`Cr3ImportTests.Cr3_CropsFromTheDeclaredOrigin`).
**Canon is the only sensor that is CROPPED**: a DAL camera RECORDS its areas (`ImageMeta.DataSection` /
`BiasSection`, `DATASEC` / `BIASSEC`, `TRIMSEC` only when `DATASEC` is absent), 1-based and inclusive, converted
ONLY in `FitsSection`. `docs/plans/sensor-active-area.md`.

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

Partial-class structure, one layout root, the shared slider, the live-preview host, the `?` menu, the toolbar
label-width rule, and **this section in full**: `docs/architecture/widgets-and-controls.md` (§ The FITS viewer
widget, and "Rules in full (moved from CLAUDE.md, 2026-10-09)").

- **Two live sources, not interchangeable**: `LiveFramePreviewSource` is per-EXPOSURE and holds no document,
  `LiveStackPreviewSource` is video-rate and wraps an `AstroImageDocument`; a cost argument about "the live path"
  names which. `StretchSolver.CollectPerChannelStats` and `CollectChannelHistograms` are two collectors on purpose.
- **GPU resource lifetime**: `docs/architecture/viewer-gpu-lifetime.md`. Never call `UploadDocumentTextures` outside
  `PrepareFrame`; never destroy a bound Vulkan object or write a shared descriptor set from an upload path
  (`VulkanContext.DeferDestroy`); the cached layer samples in TEXTURE space (divide UVs by CAPACITY). Run under
  `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` and read `validation_report` whenever this area is touched.
- **Auto-crop prefers a master's own coverage plane** (`Image.LargestCoveredRectangle()`, `MAPKIND=COVERAGE`, then
  `CoverageEdgeWalk`); **a crop is a CLIP every draw path owes, the enhance INPUT included** (`WCS.CroppedTo`,
  `AstroImageDocument.SourceCrop`). `docs/plans/viewer-prerelease-fixes.md` P25.
- **Absence is BORDER-REACHABLE, for NaN as much as zero**; a deficit in the MIDDLE is not an edge.
  `Image.FillInteriorHolesInPlace` fills every interior hole (never the ring): no fourth implementation (#250).
- **The sky behind the frame is its own toolbar button and key (`Y`, `ToolbarAction.SkyBackdrop`), not an
  annotation rung**; `ViewerState.ShowSkyBackdrop` is intent, `SkyBackdropActive` capability: keep them apart.
  `docs/plans/in-app-sky-atlas.md` § What shipped in the viewer.
- **Read the object catalogue through `ImageRendererBase.LoadedCatalog`, never `CelestialObjectDB.Value.Value`**,
  which RETHROWS a failed load on every frame.
- **The docked info strip REPORTS; it holds no controls** (`InfoPanelData.GetStatisticsTable`); controls live in
  toolbar popovers.
- **A popover is a `Layout.Builder.Popover` node plus a `PopoverState`, nothing else to declare**
  (`WindowUiSettings.PaintedPopovers`); a toolbar button still owes its own `onPress`, or it is silently dead under
  the router. `docs/plans/dir-lib-10.md`. The **tone popover** (`ToolbarAction.Tone`): `docs/plans/hdr-display.md`.

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
(it was true on 41 moves of 41, so a gate on it passed every move). Pinned by
`CanvasRenderCostTests.AHoverThatChangesNothingDoesNotPaintPerMove`. **A delayed step in this host waits at
least 1 ms and loops, never calls itself**: `Task.Delay` truncates to whole milliseconds and completes a zero
delay synchronously, so the atlas's former hover-settle wake, which re-armed itself with what was left, never
waited for a wake due in under a millisecond, and on the browser's coarsened clock it recursed until the
WebAssembly stack overflowed and the runtime exited (#953). **A trackpad pinch
is `ctrl`+`wheel`** (Blazor `@onwheel`), a different path from the touch bridge and the densest gesture
the app sees. Details: `docs/plans/web-host-carve-out.md`.

### Sky Map / FITS Viewer GLSL (pre-baked SPIR-V, no runtime shaderc)

TianWen.UI.Shared's shaders are GLSL 450 files under `src/TianWen.UI.Shared/Shaders/*.vert|*.frag`,
**pre-baked to SPIR-V** (`Shaders/spirv/*.spv`, committed + embedded, loaded via `LoadShaderModule`) by
`tools/BakeShaders`; there is **no runtime shaderc** (SdlVulkan.Renderer 6.23 dropped
`Vortice.ShaderCompiler`; shaderc ships no android RID). Two rules: **edit a shader → re-bake → commit
the `.spv`** (`dotnet run --project tools/BakeShaders -c Release -- src/TianWen.UI.Shared/Shaders`;
**warning TWSH0001** flags a source whose SHA-256 is not the one the bake recorded in
`Shaders/spirv/sources.sha256` (commit it with the `.spv`), a source missing from it, or a missing
`.spv`, names the shader, and never fails; nothing in CI checks the `.spv`, so it is their only guard;
TWIC0001 is its twin for both icon recipes, against the `Recipe SHA-256` header line
`tools/bake-icons.ps1` writes into each table). **Both compare CONTENT, never modification times**: a
time check fired for ever on a clone that pulled a source edit the bake left byte-identical (#792), so
never bring a timestamp or a tolerance back; **ASCII only**, shaderc's
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
- **`StretchMode.Auto` (and `.Planetary`, resolved by the document from the frame's percentiles) is a UI
  intent, resolved before any `StretchUniforms` is built, never a shader mode**, and **every renderer must resolve through it, headless included** -- a literal
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
construction). Engine + DSL reference: DIR.Lib's README; the engine features TianWen leans on, the five traps,
the TUI row contract, and **this section and the next two in full**: `docs/architecture/widgets-and-controls.md`
("Rules in full (moved from CLAUDE.md, 2026-10-09)"), read it before any layout work. The short form:

- **Build trees with `Layout.Builder`** and the fluent `Layout.Node` methods, never `new Layout.Node.X { }` or
  `cursor += h`. **Alias, don't import**: `global using Layout = DIR.Lib.Layout;`, never `using DIR.Lib.Layout;`.
- **Conditional background**: `if (cond) n = n.Bg(color);`, never `.Bg(default)`.
- **Interactive sub-widgets** emit `Layout.Builder.Fill(key: "...")` and draw via `drawFill`; **a text field is
  NOT one**, it is `Layout.Builder.TextInput(state, fontSize)`.
- **Responsive sizing is `Sizing.Star(weight, min, max)` + `.CollapseBelow(u)` + `WrapH`/`WrapV`**; orientation
  is a plain C# branch (`PlannerTab.BuildFrameLayout`).
- **Five silent traps**: `.RowH(h)` eats a preceding `.WFixed(w)`; a `Stack` places children at the cross-axis
  START; a `Node`'s default `Width` is `Auto`; never pair `.CollapseBelow(u)` with a Star minimum; an icon inks
  the full square it DECLARES.
- **A mark is a `Layout.Content.Icon`, never a symbol character in a `Text` run**; every step/jog/pan mark
  resolves in ONE place, `FormRowLayout.StepMark`.
- **A choice, a checkbox and a double-click are DECLARATIONS** (DIR.Lib 11.1): `Layout.Builder.ButtonGroup`,
  `Layout.Builder.Checkbox` (never `"[x] "` in a label), `.DoubleClickable(...)` (never a host arm on
  `clicks >= 2`).
- **`.PadX(u)` / `.Pad(across, down)` for a FIXED-height bar**; **`PushClip(x, y, w, h)` / `PopClip()` on the
  widget base**, never `Renderer.PushClip` with a hand-built `RectInt`.
- **TUI rows are trees too** (Console.Lib 4.10, `IRowLayout.BuildRow(in RowContext)`,
  `ScrollableList.DispatchRowHit`): a new capability is a **field on `RowContext`**, never an overload.
- **A box is the engine's MEASUREMENT of its content, not a sum of the constants the body draws with**
  (`widthSample:` ON the node; what still does it by hand: `docs/plans/viewer-layout-engine.md`, HIGH PRIORITY).
- **A declared node takes DESIGN units**: a `Base*` constant, never a `Foo => BaseFoo * DpiScale` property, unless
  the tree is arranged at `DesignScale.One` (`DeclaredLayoutTakesDesignUnitsTests`; `/chrome-review` for the rest).

### UI Primitives: the cursor, a text field, and who holds focus

Full reasoning: `docs/architecture/widgets-and-controls.md` and `docs/plans/automatic-text-input.md`; **where this
is GOING is `docs/plans/dir-lib-10.md` (HIGH PRIORITY)**. Do not add a fourth key router.

- **The pointer's appearance is a property of a REGION, never a host predicate** (`RegisterClickable(...,
  cursor:)` / `.Clickable(hit, onClick, cursor)` / `.WithCursor(kind)`; the host asks `guiRenderer.CursorAt(x, y)
  ?? CursorKind.Default`; a region stating nothing is transparent, `null`).
- **HOVER needs a z-order answer, `ViewerState.OverlayOwnsPointer`**: add an overlay to that ONE property, never a
  call site. **It is NOT `WindowUiSettings.PointerOwner`** (a RECORD a `Popover` sets as it paints; this is a
  PREDICTION for hand-painted chrome).
- **Every host routes through `DIR.Lib.InputRouter`, and the ORDER is the engine's**: an open popover, a PAINTED
  node's matching `Shortcut`, the focused field, the widget; hosts call `AfterPaint()` once the frame is drawn.
  **A key binding that belongs to a CONTROL is declared with it** (`.WithShortcut(key, mods)`, or the viewer's
  `ViewerShortcuts` table); whether it beats a focused field is `KeyChord.BeatsFocusedField`. Ctrl+Tab is answered
  before the router; **a press on a region is CONSUMED there**, so what ran after a hit test runs before the
  router, off a non-dispatching `HitTest`.
- **A text field is a declaration**, `Layout.Builder.TextInput(state, fontSize)` (`TextInputRenderer`,
  `TextInputHit`, `CursorKind.Text`, `CellLayout`); `fontSize` is in DESIGN units.
- **Focus is global but not settable, and there is ONE owner per window** (`DIR.Lib.TextInputFocus`, bound ONCE via
  `FocusChanged`; `Focus(input, value)` opens an editor; the instance is `WindowUiSettings.Focus`). **Two owners is
  the bug class.** **`BlurIfUnpainted` is the router's `AfterPaint()`.**
- **`TextInputInteraction` swallows every key while a field is focused**, which is why a binding that must survive
  one is a `.Shortcut`, not a case in the host's key switch.

### Per-Window Widget State: `DpiScale` / `FontPath` / `EmojiFontPath` are properties, not parameters

A value constant for the whole window is a `virtual` property on `PixelWidgetBase<TSurface>` (DIR.Lib), resolved
by `RenderLayout`/`ArrangeLayout`/`PaintLayout` as `?? DpiScale` / `?? FontPath` (`dpiScale: 1f` is the device-px
escape hatch; build a `PixelMeasureContext` ONCE for Arrange and Paint). **Do NOT reintroduce these as
`Render`/helper parameters**; `fontSize` is NEVER a property. `docs/plans/dpi-scale.md`.

- **Widgets in one window do NOT share a scale**: the GUI's chrome carries `GuiTheme.InterfaceScale` (1.15), the
  viewers it embeds do not. **Never read `Ui.DpiScale`** in a widget
  (`DeclaredLayoutTakesDesignUnitsTests.NoWidgetReadsTheWindowsDpiStraight`), and never carry one widget's scale or
  metrics into another's layout (`chrome-review` rule 7).
- **Which FACE they get is one decision, `BundledFonts.Resolve()`** (`(Text, Emoji, Fallback)` together); **a
  direct `FontResolver.` call in production code is a regression**. Which face draws a rune is Unicode's DEFAULT
  PRESENTATION (`EmojiPresentation`), so a NEW mark is picked by what the codepoint IS, and a colour glyph cannot
  be tinted: that is what a baked icon is for. `docs/plans/font-roles-and-icon-baking.md`.

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
- **Never copy an array through a cast `Clone()`** (`(float[])x.Clone()`, the owner detests it; `NoCastArrayCloneTests`
  fails on one in `src/` or `tools/`). First ask whether the copy is needed: a reader takes the array or a
  `ReadOnlySpan<T>`, a routine that reorders its input (a median, a selection) reads a scratch from `ArrayPoolHelper`, and
  a solver whose caller is done with the system runs in place (`PlanetaryCeilings.SolveInPlace`). A copy that is needed is
  typed: `float[] copy = [.. source];`, or `Copy()` (`ArrayCopyExtensions`) for a 2-D array.
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
- **No JSON type holds a nullable struct (`T?`) whose last property can be null** (`JsonStreamReadTrapTests`, #1356; its
  list of exceptions is empty and meant to stay so). A stream read (`DeserializeAsync`, `ReadFromJsonAsync`) throws
  "could not be converted" when its buffer ends between that trailing `null` and the brace, while the same bytes parse
  whole: System.Text.Json's own bug, open as dotnet/runtime#110450. One offset in about 350, and only past the first
  16 KiB, since the reader fills its buffer before parsing. **Make the TYPE safe, never the read**: end the struct on a
  value that cannot be null, a number or an enum where the data allows (`ColourCalibration.Source`, which was a string of
  two known values), or a last property saying what the nullable ones mean (`LimbFit.Ringed`, read off `RingLevels`; never
  a `[JsonIgnore]`d one, which writes nothing), or write it through a context that omits nulls or defaults
  (`HostingJsonContext`), which writes no null at all and which the guard counts as safe by itself. Parsing from bytes
  (`PlanetaryCaptureStatistics.TryLoadAsync`) is a second protection, not a reason: nothing checks how a payload is read.
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

`%LOCALAPPDATA%/TianWen/` (`TianWenDataRoot`), or the folder `TIANWEN_DATA_ROOT` names: a whole tree kept apart
from the user's, which a test's node runs on and a spawned node inherits. The annotated tree, and this section in
full: `docs/architecture/runtime-data.md`. **There is NO single choke point that creates these directories**
(`IExternal.CreateSubDirectoryInAppDataFolder(name)` covers four; `AppDataFolder`,
`SharedStaticData.CommonDataRoot` and their own owners the rest), so **add a directory to that tree when you add
one in code**.

**Every file here has more than one PROCESS on it**, so it is written with `IExternal.AtomicWriteJsonAsync` and read
with `TryReadJsonAsync`, or `SharedFile` (`TianWen.Lib/IO`) underneath both, **never a bare `FileStream`**: a write
replaces the file with the POSIX-semantics rename, a read shares read, write and delete, and **a reader believes a
file is gone only after looking again** (`SharedFile.TryOpenReadAsync` / `ListAsync`). **Do not replace those looks
with a reader/writer lock** (the real-time scanner then refuses every rename for minutes). **A file every host ADDS
to** (the comet apparition cache) goes through `UpdateJsonAsync`, never a whole-file write. Pinned by
`SharedAppDataFileTests`; P0c item 3 of `docs/plans/hardware-in-the-server.md`.
