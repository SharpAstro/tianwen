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
| `docs/architecture/*.md` | Architecture deep-dives: the subject in full, where a section below keeps only the rules (e.g. `image-pipeline.md`, `stacking-render-pipeline.md`, `widgets-and-controls.md`, `hosting-api.md`, `unattended-ui-driving.md`, `desktop-shell.md`, `driver-resilience.md`) |
| `TODO.md` | Active / high-priority task list (repo root) |
| `docs/todo/*.md` | Full backlog + done-archive + unsorted inbox, split by area |
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
PackageReference. `Fonts.Lib` is transitive via DIR.Lib's own `UseLocalFontsLib` switch. `QHYCCD.SDK`
(`../QHYCCD.SDK/QHYCCD.SDK.csproj`) and `FITS.Lib` (`../FITS.Lib/CSharpFITS/CSharpFITS.csproj`) used to
be outliers (the latter via a separate `UseLocalFitsLib` switch) but were folded into the one switch;
there is **no** per-library switch anymore. Trade-off: a missing checkout of *any* listed sibling flips
the whole set back to packages (all-or-nothing), which is fine on a dev box that has them all.

**There are no CPM opt-outs left in `src/`, and a new one needs a real technical justification, not
"this project is not in the solution".** `TianWen.UI.Web` + `.E2E` were the two opt-outs and each drifted
exactly as you would expect: a sibling-family bump sweeping `Directory.Packages.props` cannot see an
inline pin, so WebGl.Renderer sat two minors behind and became the last consumer on DIR.Lib 7.0 after the
rest moved to 7.4 (the graph then unified DIR.Lib by highest-version rather than by intent), and
`Microsoft.NET.Test.Sdk` sat at 18.6.0 inline against 18.3.0 centrally. **Being outside a solution never
had any bearing on CPM**, which resolves by walking directories, so the opt-out bought nothing.

**A sibling in the `UseLocalSiblings` gate must also be in that property's own `Exists(...)` list.**
WebGl.Renderer was gated on the switch but absent from the list, so a box with every *other* sibling
cloned resolved it to `true` and aimed a `ProjectReference` at a path that was not there.

**Both web projects stay out of `TianWen.slnx`, which is a separate and legitimate decision.**
`TianWen.UI.Web` is a Blazor WASM app whose *deploy* CI is `pages.yml` (a mono AOT publish, far too heavy
for the per-push `dotnet.yml` loop), and `TianWen.UI.Web.E2E` needs a browser plus a running dev server, so
a solution-wide `dotnet test` must not sweep it up. Run them explicitly:
`dotnet build TianWen.UI.Web`, `dotnet test TianWen.UI.Web.E2E`.

**Being outside the solution is not a reason to be outside CI, and for a while it was treated as one.**
`dotnet.yml`'s `build` job now compiles both projects explicitly, after its artifact uploads, the QUICK
way: interpreted, no AOT, no relink (so no `wasm-tools` workload), reusing the libraries the job just
built, and passing the same `-p:Version` so nothing rebuilds. That closes the hole where a change to
`TianWen.UI.Abstractions` -- or a sibling pin bump moving `WebGl.Renderer` -- broke the web host and no PR
check could say so; it surfaced only in `pages.yml`, after merge, as a broken deploy of main. It does NOT
cover the AOT leg, trimming, or anything at runtime, and it compiles `TianWen.UI.Web.E2E` without running
it. Keep the version properties in that step identical to the `Build` step above: a different `-p:Version`
regenerates AssemblyInfo and turns a ~1 min incremental compile into a full rebuild of the graph.

**`open-vs.ps1` generates `TianWen.local.slnx`** at the repo root (gitignored) by re-rooting
`src/TianWen.slnx` and appending a `/Siblings/` folder, so Go To Definition lands in sibling *source*.
Its project list **must** match the `UseLocalSiblings` `Exists(...)` conjunction and **nothing enforces
that**: it had drifted to `../StbImageSharp` for all seven codec projects (that repo is `../Codecs`
now) and was missing three others. A generated solution with unresolvable entries loads with them
silently unloaded rather than failing, so touch one file and re-read the other.

For libraries without auto-detection (`FC.SDK`, `ZWOptical.SDK`, `TianWen.DAL`),
prefer to extend the `UseLocalSiblings` switch in
`Directory.Build.props` + add a conditional `ProjectReference` in the consuming `.csproj`
rather than reaching for local nupkg feeds. When that's not viable (e.g. cross-team release
cadence forces a version bump), commit + push + wait for NuGet publish; **do not** create
local nupkg feeds or run `dotnet pack` to short-circuit the release dance, since CI builds
will still pull from nuget.org and a local-only nupkg will mask version-skew bugs.

### Releasing a sibling (three traps the org doc does not cover)

**The mechanism is org-wide and documented once, in the imported `../.github/CLAUDE.md` ("Versioning")
plus `../.github/docs/dotnet-ci-pattern.md` (the org root's `.github` clone, one level up from this
repo, NOT this repo's own `.github/`): a release in ANY SharpAstro repo is editing
`<VersionMajorMinor>` in that repo's `Directory.Build.props` and nothing else.** Do not restate it
here. What follows is only what that doc omits:

- **`DOTNET_NOLOGO: 1` must be in the workflow `env:`.** The version is captured from `dotnet msbuild
  -getProperty` stdout, so the SDK's first-run banner must not be able to land in it. Pair it with a
  shape check that *fails the run*, so a renamed or unresolvable property cannot quietly stamp every
  package as `.<run>`.
- **Release notes go in `CHANGELOG.md` at the repo root, never beside the number.** They used to live
  in the workflow's `env:` comment block, justified by the double hyphen several entries contain,
  which XML forbids inside a comment (see the NU1015 note in memory) -- but that only ever ruled out
  the *csproj*, and markdown has neither problem. Nothing read them there (no `PackageReleaseNotes`,
  no read-back), so they were 90% of a CI file: DIR.Lib's `dotnet.yml` was 612 comment lines of 674.
  Converted for `DIR.Lib`, `Console.Lib`, `SdlVulkan.Renderer`, `WebGl.Renderer` and `Fonts.Lib`;
  newest entry first, one `## Major.Minor` section each.
- **A test step that rebuilds a `GeneratePackageOnBuild` project without `-p:Version` publishes a
  second, stray package.** It packs again at the csproj default `X.Y.0` into the same `bin/Release`
  the publish job globs with `**/*.nupkg`; both get pushed and `--skip-duplicate` hides it by making
  the re-push a no-op rather than an error. This cost WebGl.Renderer a stray package on every run for
  fifteen releases. Pass `--no-build` to `dotnet test` (what most repos do) or
  `-p:GeneratePackageOnBuild=false`. To audit: list a package's versions and look for a bare `X.Y.0`
  beside the run-numbered ones.

`LALR.CC` is deliberately exempt from the shared shape (tag-driven, version guarded against the pushed
`vX.Y.Z`); leave it alone.

**TianWen now uses that shape too** (converted 2026-08-09; it was seven hand-edited places until
then). `<VersionMajorMinor>` lives in `src/Directory.Build.props` and everything derives: `VersionPrefix`
for every project (guarded on empty so CI's `-p:Version` wins), `AssemblyVersion` in
`TianWen.Lib.csproj` as `$(VersionMajorMinor).0.0`, and CI's `VERSION_PREFIX`. `/bump-version` edits
the one line. **A version literal in a csproj or the workflow is now a regression**; delete it and
let it derive.

Two things specific to this repo's conversion:

- **The read-back writes BOTH `$GITHUB_ENV` and a `build` job output**, because `$GITHUB_ENV` is
  **per-job** and five jobs here consume `VERSION_PREFIX` (`build`, `test-unit`, `test-functional`,
  `publish-apps`, `release`). The bare-step form that single-job repos use (Lzip.Lib, the reference
  implementation) would have set it for `build` only and left the rest empty, silently malforming
  `-p:Version=` and tagging a release `v.`. So `build`'s "Resolve version prefix" step exports
  `version-prefix`, and every consumer declares `needs: build` plus a job-level
  `env: VERSION_PREFIX: ${{ needs.build.outputs.version-prefix }}`, which leaves all the `run:` lines
  untouched. **A new job that builds or publishes needs both halves.** It lives in `build` rather than
  a dedicated `version` job on purpose: `build` already checks out and sets up the SDK, so a separate
  job would only serialise its own runner startup onto every push and PR.
- **It closed a latent bug.** `TianWen.Lib` sets `GeneratePackageOnBuild` but had no `VersionPrefix`
  of its own, so a local `dotnet build` packed it at the SDK default **1.0.0** while its
  `AssemblyVersion` read 6.1; there was a 43 MB `TianWen.Lib.1.0.0.nupkg` sitting in `bin/Release`
  to prove it. The shared `VersionPrefix` now covers every project.

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

Kept off the push/PR path (like `publish-apps`/`release` -- an OmniSim download / a full Platform
install is too heavy for every push). Two entry points on `.github/workflows/simulators.yml`:
`workflow_dispatch` (`gh workflow run simulators.yml [-f suite=alpaca|ascom|both]`) and a **weekly
`schedule`** that runs the **Alpaca leg only** as an unattended regression guard (the Windows ASCOM
leg stays dispatch-only). A shared `catalogs` job feeds the `*.gs.gz` artifact so the Windows leg
skips the catalog LFS pull + preprocess (the `.lz` decode is managed via `Lzip.Lib`, so no leg needs
an external `lzip` binary). The PR `dotnet.yml` loop only *compiles* the project; the live-sim run is the
dispatch/schedule. **This suite earned its keep on the first run** -- it caught two real Alpaca driver
bugs (mono camera couldn't connect; filter wheel never populated slots) plus a stub audit
(`Gains`/`Offsets`/`ReadoutMode`/`LastExposureDuration`). Real-time settle waits go through a real
`SystemTimeProvider` (never a fake clock -- its auto-advancing `SleepAsync` would busy-spin), so the
"no raw `Task.Delay`" rule holds even for genuine wall-clock waits.
See [docs/plans/device-simulator-ci.md](docs/plans/device-simulator-ci.md).

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
returned at once ("an in-process guider has nothing to wait for" -- cancelling is synchronous, the exit
is not). Every target start is "stop guiding, slew, start guiding", so the next loop began on the guide
camera while the previous one was still mid-frame: two consumers of one camera, one guide frame released
twice (`ChannelBuffer: more releases than refs`), the new loop's `GuideLoop` nulled by the old one's
finally, and the session never saw its first exposure complete. That is what
`DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was -- **a race, not starvation**: it failed 6 of 9
runs in isolation on a quiet 12-core box and 0 of 10 after the fix. It was called starvation for a day
because every measurement had been taken under load; instrumenting the fake clock (fake time traversed,
per thread) is what settled it, and the log then named the double release outright.

**No wall-clock `CancellationTokenSource` timeouts** in session tests; use `[Fact(Timeout = ...)]`; inner
timeouts cause flakes. **A test that drives a whole run needs that bound**: a wedged run hangs rather than
fails, and an unbounded hang is a five-minute `--blame-hang` timeout and a multi-GB dump instead of one
red test -- the 60 s bound above is what turned an unattributable CI hang into a named, reproducible
failure.

**Less parallelism is faster here, and the config only counts if it is copied to the output.** All three
test projects carry an `xunit.runner.json` (`maxParallelThreads: 4`; Simulators pins 1 +
`parallelizeTestCollections: false`) **and** a matching
`<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`. `TianWen.Lib.Tests`
had neither for a long time while this file claimed otherwise, so xUnit silently defaulted to the core
count (12) and thrashed the box; adding the file plus the `Content Include` cut the suite from 8m45-12m
to 7m46 **and made it green**: contention was dominating. Never diagnose a slow suite by re-running it
repeatedly: one run with a TRX logger, then rank durations.

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
   (`Mount://FakeDevice/FakeMount1?latitude=…&longitude=…&port=SkyWatcher`, and so on) and only
   surface from discovery when `IncludeFake:true` -- the GUI auto-includes them when the active profile
   already references any fake URI (`ProfileData.ReferencesAnyFakeDevice`), otherwise Shift+Discover.
   Two query keys select behaviour: **`port=SkyWatcher`** on the mount picks
   `FakeSkywatcherMountDriver` (believed/true pointing seam + polar misalignment + worm PE, the variant
   that exercises meridian-flip and Dec-sense paths; omit it for the lightweight `FakeMountDriver`),
   and **`hasCover=false`** on a cover/calibrator picks the flap-less driver panel (models the Gemini
   FlatPanel Lite; absent = flip-flat). `ProfileData.SiteLatitude/Longitude` **must** match the mount
   URI's `latitude/longitude` (a split site throws "Could not calculate timezone"). Canonical wiring:
   `SessionTestHelper.CreateSessionAsync(mountPort:"SkyWatcher", latitude, longitude)`.
2. **Anchor the clock** with `TIANWEN_NOW` (see the TimeProvider section) to a real night at that site,
   so the planner computes visible targets and the session leaves `WaitingForDark` at once instead of
   stalling in daylight.
3. **Drive + observe via the DEBUG inspector, not screenshots.** A DEBUG build attaches
   `DebugInspector` (GUI, `sdl-ui-inspector` sidecar) or `ConsoleDebugInspector` (TUI,
   `Console.Lib.Inspector`), both compiled out of Release. Poll the `AppState` snapshot for coarse
   state; post any `*Signal` **by name** (the `SignalFactories` map is source-generated over every
   `*Signal` type by `DIR.Lib.SourceGenerators.SignalDirectoryGenerator`, so `list_signals` returns all
   ~40 and posting `StartSession` runs the whole `RunAsync` with no clicking); `describe_ui` gives
   clickable regions and `describe_layout` the FULL arranged `DIR.Lib.Layout` tree (the structural
   counterpart, for debugging placement). `StartSession` needs >=1 pinned target
   (`PlannerState.Proposals.Length > 0`), and planner pins persist per-profile, so pin once and every
   later unattended run reuses them.

Four rules that decide whether what you read means anything:

- **Ground truth for fine telemetry is the Debug log, not the inspector snapshot.** `AppState` reads
  `LiveSessionState`, which can lag during the guide loop; per-frame guide stats (errDec/corrDec/RMS),
  HA and pier side come from `%LOCALAPPDATA%/TianWen/Logs/<date>/GUI_*.log`. The describe path is for
  orchestration and coarse state; the log is what the drivers actually did.
- **Use `render_liveness`, not a screenshot, to decide IF the render thread is stuck.** Every inspector
  command runs ON the render thread, so a `ping` that round-trips proves the loop is pumping and a
  connected-but-silent probe means it is blocked -- and screenshot/describe block exactly when it is.
  A dead device is now distinguishable from a wedge: `VK_ERROR_DEVICE_LOST` is terminal and logs event
  115 instead of entering swapchain recovery (which used to surface as a "recovery storm", event 110,
  reading like a workload problem), and event 501 names the selected GPU.
- **`validation_report` with zero messages is evidence only when `active` is true.** `active` is the
  DEBUG + `SDLVK_VALIDATION=1` gate AND `layerAvailable`; before SdlVulkan.Renderer 7.11 only the gate
  was reported, so a host with no Khronos layer installed answered `enabled: true` with zero messages,
  indistinguishable from a clean run -- which sent a device-loss investigation down the wrong path.
- **A terminal reads back as TEXT, which is the one thing a GPU surface cannot offer.** `screen` /
  `row` / `cell` report the **front** cell buffer -- what was actually emitted, not a parallel model
  that can drift -- and `cell` adds the resolved pen, which is how a colour bug is caught at all (a
  glyph drawn `#000000` on `#000000` is invisible on screen yet identical to a correct one in a text
  dump). One gotcha: the modifier parameter is **`mods`** (`"Ctrl"`, `"ctrl+shift"`), not a `ctrl`
  boolean, and the verb echoes what it resolved, because a dropped chord is otherwise invisible.
  Diagnose repaints from the once-a-second `TUI paint: N frames, M cells (K opaque)` log line, never
  from the screen; steady state is ~1 cell/tick.

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
for supported keys.

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
  `LiveSessionState.IsRunning` checks (three of them a silent `return` with no message), and every one
  was wrong the same way: `IsRunning` is **false during a flat run**, which is precisely why
  `HasActiveRun` exists, and none of them used it, while polar-align and planetary capture set neither.
  So mid-flat-run the focuser could be jogged, the mount pulsed and slewed, and a planetary capture
  started on the camera being metered. A UI flag also cannot work for the hosted API or the Alpaca plane,
  which never see one. Ask `DeviceOwnershipGate`; in the GUI that is `EnsureDeviceControllable(uri)`.
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
by raw pixels. `AlpacaImageBytes.DecodeChannel` is the pure decoder (`AlpacaImageBytes.cs`);
`AlpacaClient.GetImageArrayBytesAsync` negotiates it via `Accept: application/imagebytes,
application/json` and verifies the response `Content-Type`. **Wire-order gotcha:** ImageBytes is
laid out `[Dimension1 = Width(X), Dimension2 = Height(Y)]` row-major (last index fastest), i.e.
column-major in image terms, flat index of `(x, y)` is `y + x*Height`. `DecodeChannel` transposes
that into `Channel`'s `[y, x]` row-major layout (see `AlpacaImageBytesTests`). `AlpacaCameraDriver`
downloads + decodes **once** when the server first reports `imageready`, populating `ImageData` /
`ChannelBuffer` for the default `ICameraDriver.GetImageAsync`; `StartExposureAsync` clears them so the
next frame re-downloads. Before this, `ImageData => null` meant the camera connected but never
returned a frame, which is why `AddAlpaca()` was previously left unregistered. **The HTTP round-trip
is validated against a live OmniSim** by `AlpacaSimulatorTests.Camera_ExposesAndDownloadsViaImageBytes`
(see the simulator suite above); the decoder stays separately byte-pinned by `AlpacaImageBytesTests`.

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

**A header hint comes from `OBJCTRA`/`OBJCTDEC` first, and `RA`/`DEC` is NOT the frame centre.**
`RA`/`DEC` is the position the *mount reported*; `OBJCTRA`/`OBJCTDEC` is the target the framing put on
the sensor. They agree only on a synced mount, nothing in the header says whether it was, and only the
second one describes the frame. `WCS.FromHeader` and `Image.Fits.ParseTargetCoords` therefore read
CRVAL -> OBJCTRA/OBJCTDEC -> RA/DEC, in that order, and must not diverge. Why it is load-bearing: the
pair-lock anchor pool is the brightest catalog stars that *project inside the frame from the hint*, so
a hint off by most of a field fills the pool with stars the image does not contain and the seed never
reaches consensus. Measured on an SMC integration whose mount was unsynced by 2.4 deg -- `RA`/`DEC`
gave 11-13 hits of 160 against a threshold of 24 (chance 0.9) and fell through to ASTAP, and widening
the search radius to 8 deg did not help because coverage was never the problem; `OBJCTRA`/`OBJCTDEC`
locked at 104/160 and passed the acceptance gate 116/120. TianWen writes both keywords from the same
`ImageMeta.TargetRA/TargetDec`, so the order is invisible on our own files.

**A solver-built WCS answers in DETECTED-CENTROID coordinates -- never subtract 1 from
`SkyToPixel`.** `AttachCDMatrix` derives the CD matrix from the affine that maps projected
pixels onto detected centroids and re-derives CRVAL per iteration as the sky at the frame-centre
pixel in that same space, so the emitted WCS is self-consistent with the centroids and needs no
1-based-to-0-based conversion. Applying one (the plausible-looking `px.X - 1.0`) injects a
constant (+0.91, +0.89) px bias -- measured over 1,209 mutual matches on Vela panel 3 and 1,225
on panel 11, where a shift sweep put the mean residual at (-0.07, -0.10) px unshifted and growing
monotonically with any shift. It had cost the acceptance gate 1.27 px of its 3 px tolerance, and
`ReProjectionError` the sharpness of the parity comparison it exists to make.

**Frozen real-field regressions: `TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz`**
(2.1 MiB) holds STAR LISTS -- not FITS -- from 24 real Vela mosaic pointings / 96 frames /
78k catalog stars: per-frame detected centroids + the gate-verified WCS (incl. SIP) as an oracle,
plus one mosaic-wide catalog so a catalog index is the same physical star in every panel.
`VelaMosaicFieldTests` drives `CatalogPlateSolver.TrySeedPairLock` and `PairRansacLock` over them;
`VelaMosaicStarListExport` (env-gated, needs the user's archive) regenerates the file. Star lists
because the dense-field failure was purely geometric -- reproducing it needs the positions and the
DENSITY, not the pixels, and 96 frames of FITS is ~9 GB against 2 MiB of lists. **Three of the
four bugs this data set found would have passed a synthetic suite**, because a synthetic field is
built from a transform the test already knows: the gate's origin bias above, the SIP fit's
reference-pixel mismatch, and the seed's anchor pool being diluted by undetectable off-frame
stars. What it covers that synthetic fields cannot: ~4,000 catalog stars per 5-degree frame, a
bright end scrambled by saturation, mount hints wrong by up to 40 arcmin, a meridian flip
mid-mosaic, and 106 overlapping / 272 disjoint panel pairs (the disjoint ones are the
dense-unrelated-field negative case at real density, and none of them lock).

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
  from tonight's list alone, dropping entries for anything the scheduler does not sweep -- and **the
  scheduler never sweeps planets** (they only ever arrive via search / `CommitSuggestion` / a sky-map
  pin), so any full recompute orphaned a pinned planet. And **solar-system bodies are stored in the
  object DB with `double.NaN` coordinates** (`CelestialObjectDB`, the predefined-object loop), so
  `PlannerPersistence.MatchTarget`'s DB fallback rebuilt a restored pin at NaN/NaN; it now prefers the
  saved proposal's own RA/Dec whenever the catalog's is not a number, and a comet -- never in the DB by
  design -- restores from the proposal directly instead of being discarded.

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
dropped SIMBAD records whose only main-catalog identifier is an M-number -- e.g. Sh2-25 (= "M 8" in
SIMBAD, which models NGC 6523 as a *contained* child) landed as a standalone "Lagoon Nebula" duplicate.
`ResolveToDirectIndex` now follows the cross-index table (strictly widening the old acceptance) and the
`bestMatches` computation is deliberately LINQ-free (per-record hot path: reused lists + in-place sort,
no enum-CompareTo boxing). **Any change to the merge logic requires bumping
`SimbadMergeSnapshot.AlgorithmVersion` + re-running `tools/precompute-simbad-merge.ps1`** (and
`precompute-hd-hip-cross.ps1` when any `*.gs.gz` input changed) -- the embedded snapshot's hash guard
covers inputs + version, not code. Catalog refresh flow: `Get-SimbadCatalogs.ps1` + `Copy-OpenNGC.ps1`
(in `Astrometry/Catalogs/`) re-fetch sources; the build's preprocess target regenerates `*.gs.gz`. All
lzip I/O (fetch compress + preprocess decompress) goes through the managed `tools/lzip-util.ps1`
(SharpAstro.Lzip encoder/decoder) -- **no external `lzip` binary anywhere**.

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
session carries a plain-language, user-actionable reason (which device to check, what to do) -- surfaced
verbatim by the GUI notification feed ("Session failed: …"), the hosted `/state` endpoint
(`SessionStateDto.FailureReason`), and the CLI. Throw `SessionFailedException(userMessage, inner)` for
failures with a clear user explanation (the inner exception carries the technical cause to the log);
anything unhandled falls to the generic catch, which reports "Unexpected error: …". Init device connects
go through `ConnectOrFailAsync` (`Session.Lifecycle.cs`), which names the device + telescope and is
**deliberately fail-fast** -- a device that cannot connect at init makes the night pointless (a flip-flat
we cannot open leaves the OTA blind), so fail at init rather than discover it at dawn. The END-of-session
flat block is the opposite: best-effort (a flats failure after a successful night never flips the session
to Failed; see the try/catch around `TakeFlatsAsync` in `RunAsync`). Pinned by `SessionFailureReasonTests`.

**Guider calibration pier-side invariant:** `CalibrateGuiderAsync` (`Session.Lifecycle.cs`) slews to
HA **−0.5h** (30 min *east* of the meridian, target still approaching transit) before calibrating, NOT
west. `HA = LST − RA`, so HA < 0 = east = *before* crossing. East keeps the GEM on its pre-flip pier
side for the whole calibration, so the learned Dec guide sense matches the side rising targets are
imaged on. Calibrating west (HA > 0) is past the flip boundary on the opposite pier side → inverted Dec
sense + ambiguous flip-edge → Dec runaway. Hemisphere-independent (only apparent left/right mirrors in
the south); pinned by a both-hemisphere `[Theory]` in `SessionLifecycleTests`.

`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (default 3 min,
covers slew + center + guider settle) before slewing to each target, via `WaitForScheduledStartAsync`
(`Session.Timing.cs`), so the scheduler's altitude-optimised slot times are honored. Same-Start / past-Start
schedules (hosted API stamping `Start = now`, legacy callers, existing tests) short-circuit the wait and
advance linearly, so that path is unchanged. Late starts proceed without clamping (the full `Duration` still
runs); a lead-adjusted start beyond session end skips the observation cleanly. The wait uses the same mount
clock (`GetMountUtcNowAsync`) as the loop condition.

**Meridian-flip oscillation invariant:** `MeridianFlipDecision.DecideFlipAction` must be gated so the
imaging loop can never re-issue a flip it already performed. Two backstops, in order: `if (hasFlipped)
return Continue` (a per-observation flag set after a successful flip in `Session.Imaging.cs`), then
`if (pierSideChanged) return AlreadyFlipped`. The HA-zone switch only reaches `CommandFlip` when
`!alreadyOnCorrectSide`, where `alreadyOnCorrectSide` compares the current pier side against
`DestinationSideOfPierAsync(target)`. **Why this is load-bearing on SkyWatcher:** the SkyWatcher driver
derives pier side from the Dec encoder (`GetSideOfPierAsync` → Normal while `0 < pos < CPR/2`), so a GEM
tracking west still reports `Normal` and a naive "flip when HA > 0" check is trivially true forever →
mount stuck `Slewing`, zero exposures. Never re-introduce a flip-success check like `HA > 0`; gate on the
*destination* side + the `hasFlipped` memory. Pinned by `MeridianFlipDecisionTests` (joined-already-west
→ Continue, hasFlipped backstop, precedence) + a `mountPort:"SkyWatcher"` observation-loop test.

**No-astro-dark night-window fallback:** `SessionEndTimeAsync` (`Session.Timing.cs`) derives the dark
window via `ObservationScheduler.CalculateNightWindow`, which has a fallback chain (astronomical −18° →
amateur-astro −15° → nautical −12° → polar-night 24h). It must **never** demand `EventTimes(...).Count == 1`
for astronomical twilight: at high-summer mid-latitudes (e.g. 50.9°N at solstice the sun bottoms ~−15.7°)
the sun never reaches −18°, and the old strict read threw, killing the session at a site that simply has
no astro-dark. Pinned by a no-dark German-solstice test in `SessionLifecycleTests`.

**Focus-drift refocus trigger (trend, not single-frame):** the imaging loop's drift check compares
`FocusDriftDetector.EstimateTrendHfd` -- a least-squares fit of median HFD over the last
`SessionConfiguration.FocusDriftSampleSize` frames (default 30; only samples that are valid and
comparable to the baseline participate: same exposure + gain, enough stars; below
`FocusDriftMinSamples` comparable samples it falls back to the newest frame's raw HFD) -- against
the per-target baseline at `FocusDriftThreshold` (the NINA `AutofocusAfterHFRIncreaseTrigger`
analogue), so one bloated frame (wind gust, passing haze) cannot trigger a spurious refocus. Two
invariants: the LSQ divisor is the INCLUDED-sample count, not the window length (dividing by the
window length biases slope + intercept whenever a sample is skipped -- the bug in the original
inline implementation); and the history window is cleared on a drift-triggered refocus and on
target change, so the fit never sees frames from a different focus position (a stale high-HFD
window fitted against the fresh post-refocus baseline would re-trigger immediately -- refocus
oscillation). The window lives in `CircularBuffer<T>`, the lock-free most-recent-N ring
(ImmutableArray + CAS replace; `Snapshot` is a torn-free reference read -- the GUI render thread
polls `Session.GuideSamples` off the same type every frame). Pinned by `FocusDriftDetectorTests` +
`CircularBufferTests`.

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
  `TwilightSky`, and **a manual hand-switched panel is NOT a third one** -- it is a `ManualCoverDevice`
  (a device, like `ManualFilterWheelDevice`) assigned to the OTA's cover slot and captured through the
  **same** `Calibrator` path. No `ManualPanel` enum, no session branching: **one path for every
  `ICoverDriver`**, device kind invisible to `TakeFlatsAsync`. A motorised cover with no panel, or no
  flat device at all, is skipped with a warning.
- **Auto-exposure is a pure solver, and the two paths differ in how often it is asked.** The panel path
  converges once per filter (`FlatExposureSolver`, metering frames measured then **discarded**); the sky
  path re-meters **every** frame (`SkyFlatExposureSolver.Decide`, which adds twilight-direction
  awareness: `Capture` / `Adjust` / `Wait` / `Stop`) because the sky brightness ramps.
- **Sky flats point near the zenith tilted anti-solar and turn tracking OFF** so the field drifts and
  stars average out of the master (no dither slews); covers are **opened**, the opposite of the panel
  path. Two independently-gated hooks so both windows can run in one night: dawn at the end-of-session
  block, dusk at session start before the wait-for-dark (`TakeSkyFlatsAtDusk`). Dusk runs pre-AutoFocus
  -- a known focus-match tradeoff accepted for the cloud insurance.
- **Output contract, identical for all sources:** `IMAGETYP/FRAMETYP=Flat` plus the same denorm metadata
  as lights, under `Flats/<date>/<filter>/Flat/`. The path is **cosmetic** -- `MasterFrameBuilder` groups
  and matches by FITS headers (`MasterGroupKey`), not folder layout. **Never make flat-master matching
  depend on the path.**
- **`RunFlatsOnlyAsync` connects a subset**: each OTA's camera / focuser / filter wheel / cover, plus the
  mount only for sky-flats, **never the guider**; `FinaliseFlatsAsync` is a focused counterpart to
  `Finalise` (no guider/park steps a flat run never used, so no spurious "partial shutdown" log).
- **The GUI surface is a MODE on the Live Session tab, not a tab** (`LiveSessionMode.Flats`, joining
  Preview / PolarAlign / Planetary via the mode pill). `FlatsBootstrapper` sets
  `LiveSessionState.ActiveSession` **without** `IsRunning`, which is exactly why hardware guards must ask
  `DeviceOwnershipGate` and never a UI flag (see Device Ownership above).
- **Session->UI user-prompt channel** (`ISession.PromptRequested` + `SessionPromptEventArgs`; general,
  flats now and darks later). **With no subscriber the session answers
  `SessionConfiguration.UnattendedPromptResponse`, which defaults to `Decline`** -- it skips the gated
  step rather than proceeding, because proceeding would assert a *physical* act nobody performed, and
  blocking forever would leave the rig exposed at dawn. Operator-invoked flat runs opt into `Proceed`.
  The flat routine prompts **only** on a present-but-`!CanControlBrightness` calibrator.
- **Native Gemini FlatPanel Lite driver** (`TianWen.Lib/Devices/Gemini/`, `AddGemini()`): an ASCOM-free
  serial `ICoverDriver` for a driver-controlled panel with no flap. Wire spec AND its two silent traps
  (probe-time DTR, `SerialPort.IsOpen` not being a liveness signal):
  [`docs/architecture/gemini-flatpanel-lite-protocol.md`](docs/architecture/gemini-flatpanel-lite-protocol.md).

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
- **Linear (canonical)**: FITS, written full-frame `master_<slug>.fits` AND cropped
  `master_<slug>_autocrop.fits`. `--output-format exr` mirrors both as float-true HDR `.exr`
  (Affinity-readable). Full-frame linear pixels live here -- the only place an uncropped raster
  exists.
- **Display / stretched (ALWAYS autocropped)**: the PNG quick-look and the `--split-plates`
  TIFFs. A PNG is a display artifact, so the pipeline (`MasterPostProcessor`, NOT the CLI) renders
  ONLY the autocrop; the bare `master_<slug>.png` appears only when coverage is full and there is no
  `_autocrop.fits` (then the full frame IS the autocrop). There is no uncropped PNG. The rendered
  image is its own stats source, so WB / bg-neut can never be poisoned by the partial-coverage /
  NaN-ring edges. The autocrop rect is a geometric footprint-intersection AABB, decoupled from the
  NaN-fill guard inside `SharpenPipeline`.

**Comet / moving-target integration (`stack --comet [designation]`)** registers on the BODY, so the
comet integrates sharp and the stars trail. The rate is derived from the frames -- designation off
`OBJECT`, site off `SITELAT`/`SITELONG`/`SITEELEV`, window off the exposure epochs, then a topocentric
JPL Horizons track fitted through the reference frame's WCS. `--comet-rate dx,dy` is the offline
counterpart and the override. Design + measurements:
[docs/plans/comet-integration.md](docs/plans/comet-integration.md). Three rules:

- **This is the ONE place the pipeline plate-solves anything but the finished master.** Registration
  is frame-to-frame star-quad matching and never needed to know where the sky was, so nothing solved a
  light frame before; the master's solve is far too late for a rate consumed *while* integrating.
- **An unknown site is a refusal, never a geocentric fallback.** Horizons answers a geocentric query
  happily and the result is wrong by 3.4 degrees of heading -- and it fits a STRAIGHTER line than the
  correct track, because parallax is exactly what bends the correct one. So the straightness residual
  is logged and **nothing may gate on it**: it ranks the wrong answer first. `SITEELEV` is the opposite
  case and defaults to sea level, being worth under a thousandth of a pixel.
- **The compose is canvas-space, after the star solution.** Dither was 88.6 px against a 44.7 px comet
  track, so a frame-space shift is simply the wrong quantity; `Matrix3x2` is row-vector, so
  `starSolution * translation` is the correct order and reversing it silently gives the wrong basis.
- **The companion STAR layer SUBTRACTS the body; it does not exclude it and cannot reject it.** One
  `--comet` run also writes `master_<slug>_stars.fits` (`--no-star-layer` opts out) AND the finished
  `master_<slug>_composite.fits` (`--no-composite` opts out): the star layer with the same model added
  back once at the ephemeris position, through the same `WriteMasterAsync` so it plate-solves and gets
  its own SPCC. Placement is the reference-space point the subtraction used, carried by the star
  layer's canvas shift; the gain is the ratio of the two layers' sky medians (measured, not assumed:
  1.0 when both normalise, ~34 if one drizzled). No WCS, no centroid. Two layers are combinable only if
  they share a reference frame, canvas origin, debayer, rejector and frame set, which one run guarantees. Kappa-sigma cannot substitute: at a pixel the body crosses it is present in a THIRD of
  the frames, which inflates the very sigma meant to detect it (0.086 rejection along the track
  against 0.036 baseline, leaving a 1.76 sigma ridge). **`CometModel` is the method and `CometMask`
  is only the fallback for a host with no `IStarRemover`** -- masking works only where travel greatly
  exceeds the coma, and 10P moves **45 px in 3.5 h**, so every radius past 23 px masks 100% of the
  session. Stopping short is worse than not masking: the bar it leaves at the edge is as bright as
  the coma is at whatever radius you stopped.
- **The model MUST come from a comet layer stacked from per-frame star-removed plates**
  (`--remove-stars`, artifact 3), and this is the single decision that makes it work. On a
  comet-aligned plate every star IS a trail and a star remover takes trails as readily as the comet,
  so a model built by differencing the comet master against its own `sxt` output carries that flux
  and smears each survivor into a dark streak at 89 comet-relative positions (p99 +0.47 sigma at
  r=600-1300; streaks to -0.68 sigma). From starless plates: ridge 2.38 -> **0.30 sigma**, streaks
  p0.5 **-0.035**. Costs ~10 min of per-frame `sxt` ONCE: the plates are cached at
  `<out>/starless/<slug>/` (beside `masters/`, never under `_staging`, which is wiped per group) and
  reused when `SRCDGST` + `STARMODE` match, so a re-run into the same `-o` is 3 min. The amplitude is
  **fitted per frame**, never derived, which is what absorbs transparency, normalisation and units (it
  ran 87 on one path and 3500 on another); it is the MEDIAN of per-pixel ratios over an annulus
  (12 px to the 15%-of-peak radius) against a per-CFA MEDIAN sky, never least squares over the core:
  the nucleus the remover took out of the model and a bright neighbour's halo both inflate a
  least-squares amplitude, and on 10P that dug a -1.1 sigma bowl round the track. **It is fitted PER
  CHANNEL** (`FitScales`): the comet layer normalised each channel to its own sky, so the model's
  channels are in different units, and one pooled amplitude painted SWAN's track red -0.84 / blue
  +0.36 sigma while the luminance mean read flat. Measure colour work per channel.
- **The nucleus comes from the RAW frames (`CometRawCore` + `CometModel.SpliceCore`).** A star remover
  takes a comet's central condensation with the stars (10P's peaks at 8656 ADU, +5544 over sky, thirty
  times the coma's surface brightness at 12 px), so a model from starless plates cannot carry it and it
  stays in every frame of the star layer as a +7 sigma line along the track. A comet-aligned MEDIAN
  stack of the raw frames' 81 px window (a star trails through any cell for a few frames of many) is
  related to the model by a gain and offset fitted over 12-30 px and spliced in under 12 px; the core
  then takes its own per-frame amplitude, because it is as sharp as that frame's seeing.
- **The model's reach is decided PER CHANNEL, from where that channel's profile stops falling, never
  from a fraction of the peak on channel 0.** Channel 0 is red, and on a gas-rich comet red is the
  faintest channel by 3x: a 1%-of-red-peak floor cut SWAN's model at 100 px while green still held
  1.4 sigma of coma there and wings to 300 px, and everything outside the box stayed in all 89 frames
  as a band the radial profile had called clean (0.39 sigma ridge, re-emerging 0.19 at 95-125 px). A
  coma's wings fall as ~1/r, so at ANY fixed fraction there is coherent signal left. Following each
  channel's annular-median profile to its minimum (that minimum is the pedestal, its radius the reach:
  210/420/330 px R/G/B) leaves the track flat to 0.06 sigma across 0-450 px against a 2.36 sigma
  control, and what came out decays monotonically with no plateau, so it is coma and not a pedestal
  error. Two absolute-position rules ride with it: the body is evaluated at the reference's
  MID-exposure (`CometCompose.BodyOnGrid`; 1.0 px at 245 px/h and 30 s), and the model centre is
  sub-pixel (a whole-pixel crop centre is up to 0.5 px off, which subtracts a dipole). The compose
  arithmetic lives once, in `CometCompose`. Pinned by `CometModelTests` + `CometComposeTests`.
- **Five preconditions of the SURROUNDING system break the model silently, and all five are already
  documented elsewhere in this repo.** The anchor epoch is not the reference epoch (the body sits at
  `anchor + rate*(t_ref - t_anchor)` on the comet grid); a comet-aligned canvas carries NaN and
  RC-Astro answers an all-NaN plate for any NaN input; a star remover cares where its input sits in
  [0,1] and `BayerDrizzle` does not normalise (background 0.0145 against the 0.5 it was proven on);
  `--remove-stars` must NOT replace the frame list or the star layer loses its stars; and each layer
  needs its own calibrator, since `integrationCalibrator` is a no-op under `--remove-stars` while the
  star layer reads raw originals. Full write-up + the measurements:
  [docs/plans/comet-integration.md](docs/plans/comet-integration.md).
- **A NaN in a pixel's sample column switched rejection off entirely, in every rejector, and always
  had.** Comparisons against NaN are all false, so the median is nonsense, MAD is NaN, the
  `mad <= 0` guard does not fire, both bounds are NaN and nothing is ever rejected. Warped frames
  carry NaN borders, so **canvas edges have never had rejection**; `CometMask` only made it visible
  mid-frame (0.0000 against 0.026-0.034 outside) as surviving hot-pixel clumps. Fixed via
  `PixelRejection.MarkAbsent` in all five; an absent sample counts as NOT rejected in the tally, or
  the rejection map paints every edge as heavily rejected. Pinned by `RejectorAbsentSampleTests`.
- **Judge these layers at 1:1, not by a band median.** Three real defects (the mask's edge bars, a
  correlated-noise texture, the trail streaks) were found by eye after the radial profile called the
  frame clean: a median across a band averages over exactly the edges, thin streaks and texture
  changes that matter. Use p0.5/min for streaks, a fine 15 px profile for edges, and autocorrelation
  for texture (the "checkerboard" was NOT CFA -- sub-lattice spread 0.006 sigma on track vs 0.015
  off, no bump at lag 2 -- but 1.09x rms with ~2x the correlation at 6-8 px). And **never compare a
  treated layer against a differently-integrated one**: mask-vs-drizzle changed two variables and
  read as "barely worked".

**Provenance skip (never re-ingest our own outputs).** The scan drops any TianWen-produced FITS so a
processed image parked alongside the lights is never re-stacked as a fresh sub. Two markers, both
gated by `--include-integrations`: `STACK_N > 0` (a master) OR a TianWen `SWCREATE`
(`IntegrationFitsWriter.IsTianWenProduct` -- catches AI sharpen / enhance outputs, which inherit the
master's `SWCREATE` but carry NO `STACK_N` and an `IMAGETYP=Light` copied from the original subs, so
the STACK_N check alone misses them, and they silently re-stack into a ghost master). The scan reports
a `ScanSummary` on the progress channel -- silent re-ingestion was the footgun.

**`--enhance`** runs `SharpenPipeline` on the master ONCE and writes `_sharpened.fits`
(+ `_sharpened_autocrop.fits`); the linear masters are never overwritten. The step program is
deblurrer-aware (`SharpenPipeline.SupportsDeblur`): RC-Astro present -> BlurX-first (deblur whole
frame -> gradient -> remove stars -> denoise starless + SCNR stars -> recombine, matching the
PixInsight OSC flow, NO stellar-sharpen); no RC deblurrer -> SAS-shaped (remove stars -> sharpen
stars -> deconvolve + denoise starless -> recombine). **`--split-plates` is a SINGLE AI pass** on
that same `ProcessAsync` (`KeepIntermediates: StarsAndStarlessLineage`), exporting the kept
stars-only + denoised-starless plates as edit-ready stretched sRGB-ICC float TIFFs; NO second
enhance runs.

**Render model: WB once, per-plate self-stretch (the PixInsight OSC order).** ONE SPCC white balance
on the enhanced (gradient-corrected, with-stars) master; the plates then share **only that WB
triple** and each computes its OWN background-neutralisation + MTF from its own pixels. Sharing only
WB is load-bearing: grafting the master's bg-neut onto a plate whose background differs
double-corrects it into a colour cast (the original `--split-plates` regression).

**SPCC's clip test reads the frame's OBSERVED peak from the pixels, per channel, never `MaxValue`.**
`MasterPostProcessor` rewraps every master with `MaxValue = 1.0` so the histogram and stretch treat it
as unit-scaled, but a normalised master has its sky at 0.5 and its stars far above 1.0 (36 / 75 / 34 on
the SWAN star layer). Against the tag, "clipped at 98% of the peak" was true of every bright star: 10P
dropped 545 of 545, SPCC gave up, and the sky-background fallback was the only reason it looked right;
with the fix it converges at (0.969, 1.000, 1.209). Every normalised master had been in that state. A
rewrapped `MaxValue` is a display convention, not a saturation level.

**SPCC's matcher claims each catalogue star ONCE, brightest detection first, in the tolerance probe
and in both match passes.** A deep master detects far more stars than Tycho-2 holds (SWAN: 5088
against 340 in the footprint), and the catalogue stars ARE the brightest detections. Before this, the
probe read a nearest-neighbour residual off every detection, so its "median residual" was a random
distance (15" median, 10" MAD on a WCS fitted to 0.23 px), the tolerance ran to its 30" cap, and the
passes accepted 1288 "matches" from 340 stars, each faint neighbour wearing a B-V that was not its own.
Observed colour was flat across every B-V bin and the fit landed on (1.88, 1.00, 3.00), a blue frame,
which the clip-test fix above was wrongly credited with causing (it changed that triple by 3%). With
the claim rule SWAN probes to 5" and matches 597 one-to-one. Read `SpccFunnel.Detected` against the
catalogue count in the footprint before trusting any SPCC triple, and `Duplicate` for what the rule
refused. (And the Optolong L-Quad Enhance is BROADBAND: five windows, ~200 nm of the visible, notches
at NaI and Hg; do not file its colour problems under narrowband. A filter missing from the model
cancels to first order in SPCC anyway; the sidecar `.tianwen-meta.json` is how a nosepiece filter gets
declared.)

**The stacking normaliser anchors every frame on its PEDESTAL, never on a pixel statistic.**
`Normalizer` maps `out = (in - floor) * target / (median - floor)`, and the floor used to be the frame's
per-channel MINIMUM, so one pixel set the gain of a whole frame and channel: a hot pixel, a cosmic ray,
a demosaic overshoot beside a saturated star, or a flat that reaches zero in a corner (the calibrator
divides by `max(flat, epsilon)`). Measured on the SWAN session through the default AHD debayer, the red
gain wandered x3.7 from frame to frame, green x2.3, blue x3.0, each channel independently; through MHC
one spike put the min near -1e9 and a whole layer integrated to a constant 0.5. That is why star
colour in a master spanned ~3x less than in the raw frames and why SPCC could not find a white balance
that made stars neutral. `PerChannelFloor` is `Image.Pedestal`, `Normalizer.ComputeScale` is the one
source for every integrator, and `Apply_AnOutlierPixelDoesNotChangeTheFrameGain` pins it. Absolute
normalised levels quoted before 2026-08-27 are in the old units. **Still open, measured and not yet
acted on:** the normaliser fix alone left SWAN's master star colours at x1.58 in R/G against the raw
frame's x4.5, so the compressor is the STACK DEBAYER: AHD's phase-4 3x3 median on (R-G)/(B-G) flattens
a 2-3 px star's chroma to the sky's (the same 400 stars span x2.93 through MHC, x1.80 through VNG,
x1.65 through AHD), while MHC overshoots to -10k beside saturated stars, so it is not a drop-in
default. The candidates and the yardstick are in
[docs/plans/comet-integration.md](docs/plans/comet-integration.md), colour section, "Continue here".

**SPCC is BROADBAND-ONLY, and a narrowband master has no colour path at all.** The white balance
integrates a Pickles SED against QE x CFA over the whole visible band, which is correct for an OSC
broadband frame and meaningless for a 3 nm passband, so an Ha/OIII/SII stack renders with whatever the
channel assignment plus per-channel autostretch produce. Two traps follow. **Do not extend SPCC to
narrowband by swapping in a narrow passband over the existing SEDs**: a Pickles template is a spectral
*type average*, so over 3 nm it cannot know whether a star shows Ha in absorption or emission, and it
would return a confidently wrong calibration rather than none. **Narrowband SPCC itself is not
impossible, only BLOCKED, and on data rather than on maths** -- both PixInsight and Siril ship it, by
convolving the declared passband against **per-star Gaia DR3 `xp_sampled` spectra**, which we do not
have; so it is a Gaia project, not a colour project (ADR-3 in the plan below). Two things do NOT
unblock it, and both look as though they might: a measured filter curve (we have those, and they are
already a richer model than the centre+FWHM PI asks for), and a least-squares fit over *sensor*
response curves (that is OSC passband synthesis, a different fit over different data -- see the
`siril-spectral-extract` note in the plan). And **naive HOO is rank-deficient**: `R = Ha`,
`G = OIII`, `B = OIII` makes G and B the same array, so every OIII region is exactly cyan and no
stretch or WB can make blue; a uniformly teal HOO render is the palette, not a renderer bug. Planned
with the algorithms + thirteen ADRs in
[docs/plans/narrowband-colour.md](docs/plans/narrowband-colour.md); root cause also recorded in
[docs/known-limitations.md](docs/known-limitations.md).

**The filter-curve matcher must never answer with a brand, nor with a MORE SPECIFIC product.** `FilterCurveDatabase` carries 183 curves
and matches a written `FILTER` card by token overlap, and its coverage gate only ever asked about the
KEY -- so for a two-token key like `OPTOLONG_B` (BRAND + CHANNEL) the brand alone satisfied it, while
the needle's own unmatched tokens cost nothing. `Optolong L-eNhance` therefore resolved to a broadband
blue LRGB dichroic, a bare `IDAS` to `IDAS_NBZ`, and `CFA_R` to `BAADER_R` (that last one put a mono
dichroic into a modelled OSC throughput and skewed a real SPCC fit). **A key of two tokens or fewer
must be covered in full**, the bare-channel-letter path (`R`, `Ha`) exempt because one token is all it
ever had. A wrong curve is worse than none: it is used as if it described the glass in the light path,
where declining is visible.

The mirror case bites when you ADD a curve: a new name containing an existing product's name as a
token SUBSET answers for it. `OPTOLONG_L_QUAD_ENHANCE` captured L-eNhance, L-eXtreme and L-Ultimate,
because "optolong" plus the single letter "l" already clears the half-coverage gate on a four-token
key. **An unmatched key token that appears in no other filter name is what makes that curve specific,
so the match is refused** -- and the test is DOCUMENT FREQUENCY over the catalogue, never a
stop-list, because the distinction is not lexical: "idas" unmatched by `LPS-D3` must be allowed
(three curves, so a brand), "light"/"pollution" unmatched by `IDAS LPS P3` must be allowed (two each,
a series suffix), while "quad" names exactly one. **Always re-run
`ReportKnownLightPollutionFilters` after adding a curve** and read every line, including the ones you
did not touch.

**Frequency does not cover the general case, and a two-sided token difference does.** If the written
name carries a token the curve lacks AND the curve carries one the written name lacks, they are naming
DIFFERENT products -- neither is a more specific version of the other, they diverge -- so the match is
refused. That is what catches the collision frequency sleeps through: adding `OPTOLONG_L_ULTIMATE`
captures L-eNhance and L-eXtreme, because "ultimate" appears in SEVEN names (the six pre-convolved
combos plus the standalone) so it is not rare, while `optolong` plus the single letter `l` already
clears half-coverage on a three-token key. `{enhance}` against `{ultimate}` is two-sided, and refused.

It is much narrower than it sounds, because **a one-sided difference still resolves in both
directions**: a name that says LESS (`LPS-D3` leaves `{idas}` on the key side, `Askar D1` leaves
`{colourmagic}`) and a name that says MORE, which is what a real filter-wheel slot looks like
(`Baader R CCD 31mm` leaves `{ccd, 31, mm}` on the needle side). Single-character tokens deliberately
COUNT -- `Baader B` against `BAADER_R` is `{b}` versus `{r}`, exactly the divergence this must reject.
A tokenisation artifact cannot trigger it either: `Askar Colour Magic D1` normalises to the curve's
own name and returns on the exact path first.

Pinned by `ABrandTokenAloneIsNotAFilterMatch`, `AddingASpecificProductDoesNotCaptureItsSiblings`,
`AOneSidedTokenDifferenceStillResolves` and `ATwoSidedTokenDifferenceIsRefused`; measurements in
[docs/known-limitations.md](docs/known-limitations.md).

**Standalone light-pollution / duo-band coverage is small and four of them are ours.** `IDAS_LPS_D3`,
`IDAS_NBZ`, `ASKAR_COLOURMAGIC_D1` (OIII+Ha), `ASKAR_COLOURMAGIC_D2` (OIII+SII),
`OPTOLONG_L_QUAD_ENHANCE` (quad-band), `OPTOLONG_L_ULTIMATE` (dual 3 nm) and `OPTOLONG_L_ENHANCE`
(tri-line) were digitised
from vendor charts by `tools/digitize-filter-curve/` and live as chart-unit CSVs under
`tools/import-sasp-data/local-filters/` (nm + percent, so a row is checkable against the chart; the
importer converts to the database's Angstrom + fraction). **Deleting a CSV and re-merging RETRACTS
its curve**, via the checked-in `local-filters/.merged-names.txt`: the `.gs.gz` is the merge's own
input, so a merge could otherwise only ever add, and `ORIGIN` cannot identify a locally-injected
curve because the upstream SASP data was itself built from CSVs. Upstream adds only
`IDAS_LPS_P3_LIGHT_POLLUTION`, `OPTOLONG_L-PRO_LIGHT_POLLUTION` and `SVBONY_SV260`. **L-eXtreme still exists only PRE-CONVOLVED with a sensor**
(`SONY_CMOS_*-UVIRCUT` / `CANON_FULL_SPECTRUM_*` x L-eNhance / L-eXtreme / L-ULTIMATE), so that bare
product name correctly returns no match.

**L-eNhance is TRI-LINE, not a duo-band, and that is a correctness matter rather than a label.** Its
blue window is 23 nm wide (the vendor annotates it "FWHM OIII&Hb"), so the channel that looks like
OIII carries OIII **plus H-beta** summed together -- anything unmixing an OSC frame shot through it on
a strictly two-line Ha/OIII model is solving the wrong system (see
[docs/plans/narrowband-colour.md](docs/plans/narrowband-colour.md)). Nor is the band flat: H-beta 486.1
reads **96.4%** against OIII 500.7's **85.9%**, because the band centres near 490 and 500.7 sits on
its falling shoulder. **Hb 486.1 is also the identity check against L-Ultimate**, whose 3 nm blue band
reads 0.0% there -- Optolong have published charts under the L-Ultimate name that are actually
L-eNhance, and that one wavelength separates them. Pinned as a pair by
`TheEnhanceIsTriLineAndPassesHBeta` and `TheUltimateIsTwoNarrowBandsAndDoesNotReachHBeta`.

**Which is why the ZOOMED charts matter.** At the ~1 px/nm of a full-range chart you cannot tell
whether Hb falls inside the blue band; at 9 px/nm you can. A wide chart yields a curve that looks fine
and loses the one fact that distinguishes the filter. The cost: L-eNhance has no full-range chart, so
its out-of-band is ASSERTED (zeros at 350/460/525/630/680/800) rather than measured -- each band is
bracketed by measured zeros, but UV/IR leakage is invisible to it. SPCC declines on the
two ColourMagic curves, and **the curve is not what is missing** -- the SED library is (see the
narrowband note above). They are here for sensor-matched luma weights, for the narrowband colour work
where which line lands in which CFA channel is the whole question, and as the pre-convolved response a
duo-band OSC frame must be modelled through rather than the bare CFA.

**Two rules for the digitiser, both learned by getting them wrong.** A chart it cannot calibrate must
FAIL rather than emit a smoothly mis-scaled curve, and the CSV is written only **after** the checks
pass -- it used to be written before, so a chart that failed its own notch check still left a file that
looked exactly like a good one. And `--grid-mode excel` (spreadsheet charts: dense horizontal
gridlines, no vertical ones, so the wavelength axis is ASSUMED to span the plot box) **requires
`--expect-peaks`**, because a narrowband filter's passbands sit on known emission lines and that is
the only thing standing between an assumed axis and the database. Where a vendor publishes the same
curve at two scales, take amplitude from the zoomed chart and coverage from the wide one: at 1.46
px/nm a 7 nm passband is ten pixels of near-vertical ink and the column centroid averages its own peak
down by ten points.

**Zero-pedestal render (parity fix -- do not regress).** The stretch derives per-channel shadows from
the **pedestal-subtracted** median, which is a no-op on raw masters (`MinValue ~ 0`) and the *only*
reason the historical render path was neutral. An **enhanced** master is GraXpert-flattened to a
half-scale floor, where subtracting it leaves faint per-channel residues that either explode or go
negative (drizzle -> frame renders black). `MasterPreviewRenderer.WithZeroPedestal` rewraps the stats
image with `MinValue=0` so the auto-stretch's own shadow clipping sets the black point.

**Unified display render.** `MasterPreviewRenderer` (SPCC + sky-bg WB + MinPivot bg-neut + MTF +
16-bit sRGB PNG) and `StretchSolver` (the stretch-uniform math the GLSL + CPU paths agree on) both
live in **`TianWen.Lib`** (CPU-only), so `MasterPostProcessor` drives them in-pipeline. **The CLI
renders nothing**: it sets `StackingOptions.RenderPreviewPng`, writes EXR from the emitted FITS, and
prints the SPCC summary from `GroupResult.Spcc`. The viewer's
`AstroImageDocument.ComputeStretchUniforms` / `ComputeSkyBackgroundWB` forward to `StretchSolver`,
keeping it the single producer.

**Two opt-in DISPLAY stages, and the rule both obey:** `stack --saturation X --contrast-boost Y`
(`Image.MaskedBoost`, the Affinity masked contrast + saturation macro) and `--output-format uhdr` (an
Android Ultra HDR gain-map JPEG, whose value over the cICP-PQ PNG is per-pixel highlight recovery from
the PRE-MTF signal) touch **only the display raster** -- never the linear FITS / EXR masters or the
split-plate TIFFs, and identity options collapse to null so the untouched path is byte-identical.
**Never apply the mask primitives to a LINEAR master** (the luminance mask degenerates to ~0
everywhere, which is why this is a render stage and not a `SharpenStep`). Both stages' invariants and
tests are in the architecture doc above.

**Stellar-sharpen is opt-in** (`image sharpen --stellar-sharpen`, default OFF) and **hard-skipped when
a deblurrer is live**, because BlurX already tightened the stars and the SAS sharpener turns tight
cores into square white blocks. RC-vs-SAS roles + the skip:
[`docs/plans/rc-astro-enhancers.md`](docs/plans/rc-astro-enhancers.md).

**CLI flags + viewer Enhance action.** `image sharpen` and `stack --enhance` both take
`--ai-backend auto|rc|sas|n2n`, `--deblur-sharpen`, `--denoise-strength`, `--denoise-iterations`
(backend-neutral names; each backend maps them to its own dial), parsed by the shared
**`EnhanceOptions.TryParse`** -- the single source of truth for the backend + tuning mapping, also
used by the server endpoint; never re-inline the switch -- and threaded as an immutable
`EnhanceOptions` through `SharpenPipeline.ProcessAsync` to each enhancer, so there is **no mutable
settings singleton** and parallel enhances cannot tear. `tianwen-fits` has an interactive Enhance
action (`ToolbarAction.Enhance` + 'E') that runs off the render thread and adopts the result via the
`ViewerController._enhanceTask` hand-off (no spin-render, so it does not contend the GPU the AI work
uses); left-click runs, right-click cycles the backend. The button is presence-gated by
`EnhanceAvailable`, so `tianwen-fits` registers `AddRcAstroAi()`; the GUI has no document-viewer tab so
it carries no enhance UI yet.

**Server enhance endpoint.** `POST /api/v1/image/enhance` + `GET .../status`, single-flight, tied to
`ApplicationStopping` rather than the request. Shape, DTO registrations and the publish-to-verify rule:
[`docs/architecture/hosting-api.md`](docs/architecture/hosting-api.md).

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
  accumulate kernel is linear, so +w then -w cancels exactly; no per-frame contribution images stored).
  The hot path is **align-bound** (~85-89%), so `GlobalAligner` caches the reference tile's forward FFT
  once.
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
  a coarse mount nudge on an edge-blocked axis via the one actuator `MountActions.PulseGuideArcsecAsync`).
  `DALCameraDriver` (ZWO/QHY native raw video) is Phase D, not implemented. **Read the plan doc before
  touching the Canon path** -- it is a list of five things that fail SILENTLY, and paraphrasing it is how
  one of them comes back. Auto-recenter defaults ON (ROI-only, zero mount disturbance); mount jog is
  opt-in OFF and its **sign is uncalibrated**.
- **Benchmarks/profiling**: `TianWen.UI.Benchmarks` `PlanetaryStackBenchmarks` /
  `PlanetaryMasterBenchmarks`, and `dotnet run --project TianWen.UI.Benchmarks -- profile planetary
  [--frames N]` prints a per-stage breakdown and tight-loops for `dotnet-trace`.

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
  `--ai-backend n2n` selects it per enhance for the denoise role while every other role stays Auto;
  **Auto rescues with it** when the SAS AI4 weights are absent and the input is OSC at the default
  variant (it replaces a crash, never a measured backend's result -- with SAS weights present, Auto is
  byte-for-byte the old path); and `AddTianWenN2nDenoiser` makes it the `IDenoiseEnhancer`
  unconditionally. It is deliberately **not** Auto's preferred denoiser: it has never been compared
  against the AI4 model on the enhance pipeline's own job. The user-facing strength dial is a **blend**,
  and the graph's own `strength` input is pinned to 1.0 (the conditioning-plane dial was measured and
  rejected). Design + measurements: [`docs/plans/osc-narrowband-denoiser.md`](docs/plans/osc-narrowband-denoiser.md)
  section 1o.
- **RC-Astro (BlurX / NoiseX / StarXTerminator)** -- `AddRcAstroAi()`. Its `.onnx` files are
  **encrypted at rest** (only the official binary can decrypt them; the license forbids extracting the
  weights), so they are driven through the `rc-astro` CLI's `--json` NDJSON protocol, **never** loaded
  into ORT: `RcAstroEnhancerBase` writes the plate to a temp FITS (BITPIX=-32), runs the product, parses
  the event stream and reads the result back. RC normalises to [0,1] internally, so no rescaling. Role
  mapping: sxt -> `IStarRemover`, nxt -> `IDenoiseEnhancer` (noise-adaptive `--dn`), bxt ->
  `INonStellarDeconvolver` (on the starless plate, auto-PSF). GPU-accelerated under win-arm64 x64
  emulation (DirectML -> native Adreno). Details: [`docs/plans/rc-astro-enhancers.md`](docs/plans/rc-astro-enhancers.md).

**Selection is RC-preferred, deferred, and license-gated.** `AddRcAstroAi()` calls `AddTianWenAi()`
then `Replace`s the three RC-servable roles with **`DeferredEnhancer` proxies**: the RC-vs-SAS choice
AND its blocking license probe run on the FIRST `EnhanceAsync`, never at DI registration/resolution --
so composing a service collection (or resolving `SharpenPipeline`) spawns **no** `rc-astro` process. RC
wins only when the CLI is present (`RcAstroCli.LocateExecutable`: `RC_ASTRO_CLI` env -> documented
per-OS default install dir -> PATH; RC-Astro writes **no** registry footprint, so no
Uninstall/App-Paths probe) AND the product is licensed (cached); else the SAS ONNX enhancer is used.
`IStellarSharpener` / `IGradientCorrector` stay SAS (no CLI equivalent).

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
local one. Two projects carry it: **`TianWen.Hosting.Contracts`** (wire DTOs + the shared public
`HostingJsonContext`, referenced by host *and* client so the contract cannot drift) and
**`TianWen.RemoteClient`** (`TianWenNodeClient` REST, `TianWenEventStream` WebSocket,
`RemoteSessionMirror`).

**The overlay model is the whole design: selecting a rig changes what you *look at*, never what this
node owns.** A *remote* connect starts a read-only HTTP mirror -- no lease, no hardware touched; a
*local* connect opens drivers and powers a mount. The single-session invariant is per NODE, so
mirroring six rigs is not six sessions. `RemoteRigBinding` (persisted, keyed on a stable `NodeId`,
**never** an address) + `RemoteRigRegistry` + `RemoteRigConnection`; the address is resolved per
connect from the LAN peer table with the stored `LastAddress` as a hint, so a rig that changed DHCP
lease reconnects on its own.

**One `LiveSessionState` per view context** (`ViewContexts` / `ViewContext`). Pick deliberately:
**Active** (what renders), **Local** (this node's own hardware -- every quit / park / disconnect path
belongs here, and it is the only one that is capturable), **All** (poll + redraw). Reaching for
Active where Local is meant is how a remote view ends up parking the local mount.

**`ISession` / `ISessionTelemetry` split.** Telemetry is the wire-crossable *read* surface; `Setup`
stays local (it holds live driver instances). Display-only facts ride on `TelescopeDisplayInfo`.
`RemoteSessionMirror` implements the telemetry side -- which is why the Live Session and Guider tabs
render a remote rig with no knowledge that it is remote.

**Two wire traps, both load-bearing:**
- **Never put `required` on a nullable wire property.** `WhenWritingNull` omits it, and the payload
  becomes undeserializable by its own contract.
- **A non-finite double reaching the writer is a bodiless 500 for the WHOLE endpoint** -- one NaN
  altitude kills the entire `/state` response. Route through `JsonNumber.ForWire`, whose policy is
  *derived* from the context's `NumberHandling` so the two cannot drift.

**Polling is authoritative; the WebSocket is a latency hint, not truth.** A poll swaps the whole DTO
in one reference write, so no field-by-field tearing is possible. `NodeResult<T>` carries a status
code because **404 is not unreachable** -- it is the node answering "no session", which is exactly
why `LastContactUtc` stamps on the 404 branch too. The outstanding user prompt rides on
`/session/state` and not only the event stream, so a client attaching late can still unblock a rig
that is waiting on a human.

**Every request has a time budget** (`NodeTimeouts` -- state poll 5 s, preview 30 s, control 10 s)
behind a 60 s `HttpClient` backstop. The 100 s default is far too long because a rig that is switched
off *black-holes* packets rather than refusing the connection (which would fail instantly). Budget
expiry and caller cancellation both surface as `OperationCanceledException` and mean opposite things:
keep the `when (...)` filters on the **original** token, never the linked one, or every timeout
rethrows and the poll loop dies.

**Profile switching is gated** (`ProfileSwitchGate`): a single-profile context refuses to switch while
connected or running, or where drivers would strand in the hub.

**The Home tab** (`GuiTab.Home`, the house glyph, `Ctrl+H`, **first** in `TabOrder`): the multi-rig
dashboard and the app's landing screen. Every rig you can look at, local and remote, titled by the rig
with the profile it runs underneath, plus phase / progress / cooling / flip countdown / guide RMS / HFD /
last notification and an outstanding-prompt badge (the badge is most of the justification -- a prompt
blocks a rig *indefinitely* and was otherwise visible only on the rig you happen to have selected). The
**TUI renders the same tree** (`TuiHomeTab`, `CellMeasureContext.PixelAuthored`) -- the one tree shared
across surface kinds, so a change to the card lands on both surfaces or neither.

- **It is a read-only PROJECTION, structurally.** `HomeBoard.BuildCards` is the pure projection and
  `HomeTab<TSurface>` only draws it, from the `ImmutableArray<RigCard>` snapshot on
  `GuiAppState.HomeCards` -- it never touches `RemoteRigRegistry` or a `LiveSessionState`, which is what
  makes painting a card from a concurrently-mutated session impossible. A card click changes which rig
  you *look at*; nothing on it connects a driver, commands anything, or takes a lease, and previews stay
  **off** (N mirrors each pulling JPEGs is the failure mode `RemoteSessionMirror.Previews` is opt-in
  for). Zero device I/O: cards are built in the pre-gate part of `PollPreviewTelemetry`. Card-height,
  cooling, progress and flip-countdown rules, the three board shapes and why Auto **says why** it
  swapped, and the theme cycler: [`docs/plans/remote-profile.md`](docs/plans/remote-profile.md).
- **A prompt's age is the raising node's truth.** `SessionPromptEventArgs.RaisedUtc` /
  `PendingPromptDto.RaisedUtc` (nullable, and deliberately **not** `required`). Never substitute "when
  this client first saw it": that dates the prompt from when the observer attached, so a rig stuck since
  dusk reads as freshly waiting and resets on every restart. Unknown must render as unknown.
- **`GET /api/v1/session/profile`** is how a node reports which profile it runs; `ActiveProfileId` had no
  way out of the node and `/profiles` lists what exists without saying which is live. Cached per
  `RemoteRigConnection`. The LAN beacon is **not** a second home for this -- a rig reached through its
  stored address hint has no beacon, and would be the one card with no label.
- **A dark rig is polled less often** (doubling to a 30 s cap), and that is for pointless traffic only:
  each mirror owns its own `Task.Run(PollLoopAsync)`, so an offline node structurally cannot stall the
  others. A **404 counts as an answer** and resets the backoff -- an idle rig is a healthy rig.

**Sidebar icon convention.** Every tab glyph is a **bare codepoint with no variation selector** -- the
VS16 emoji render inconsistently through the bundled emoji font. Icons live in
`VkGuiRenderer.TabChrome` and are written in source as backslash-U escape sequences, not literal
glyphs, so editing them needs a tool that can match escapes (see the memory note on this). Current
set: 🏠 Home, 🔭 Equipment, 📅 Planner, 🌌 Sky Map, 🎬 Session Setup (both the night's config *and* the Start
button -- a cog implied only the former, a rocket only the latter), 🎯 Guider, 🔔 Notifications, and
Live Session which swaps per mode (📷 idle, 📸 running, 🧭 polar, 🪐 planetary, 💡 flats). Adding a tab
touches six places: the `GuiTab` enum, `GuiAppState.TabOrder`, `TabChrome`, the `GuiEventHandlerBase`
Ctrl+letter map, the two `VkGuiRenderer` switches, and
`GuiTabNavigationTests.TabOrder_IsTheSidebarLayoutOrder` (which pins the order and will go red by
design).

### Colour Theme (`GuiTheme`, four states incl. Night)

`GuiTheme` (`TianWen.UI.Abstractions/GuiTheme.cs`) owns the one palette every surface paints with;
`UiThemeState` is **System / Light / Dark / Night**, and `GuiTheme.Apply(state, desktopIsDark)`
resolves + swaps it in as a single reference write. `Palette` is one reference read, never torn. The
source XML comments carry the full rationale (including the scotopic-sensitivity numbers); read them
before changing a colour.

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
that replaced an accumulator), so ask THAT. Where a producer offers no such predicate, make it
**consume** its input instead -- `Calibrator.Apply` now takes ownership of the light and the caller
owns the result whatever the configuration, which is why `RawLightDecoder` has no guard at all.
Reference checks that survive are asking a DIFFERENT question (an enhancer declining a plate, a
display-identity "is this a new frame to upload?", the flat preview's slot swap) and must not be
mechanically converted.
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
  `StackingPipeline.RunAsync` warns at `[end]` when anything is outstanding. Compiled out of Release,
  which is what the main CI leg builds, so `dotnet.yml`'s `test-unit` runs a second **DEBUG leg**
  selecting `--filter "Category=DebugOnly"`. A DEBUG-gated suite joins it by carrying that trait --
  the leg names no class. **It also asserts it ran something**: a filter matching nothing exits 0, so
  the step reads the count back from the TRX and fails at zero, or a renamed trait is a green no-op.
- The recycle loop is complete for DAL (ZWO/QHY), Fake, Alpaca, and ASCOM (the latter two closed in
  the 2026-07-06 buffer audit: Alpaca decodes into a recycled buffer, ASCOM caches the COM
  `ImageArray` marshal once per exposure; cleared on `ReleaseImageData`/`StartExposureAsync`).
  Canon wraps its RAW decode output (no recycle, deliberate). Coverage matrix + the by-design
  consumer copies: [docs/architecture/image-pipeline.md](docs/architecture/image-pipeline.md).
- **`Channel.MaxValue`/`Image.MaxValue` is the peak pixel actually OBSERVED in that frame**, not the
  sensor's saturation level; it varies frame to frame with scene brightness, seeing and hot pixels.
  The fixed value travels separately as the optional `ImageMeta.SensorFullScaleAdu` (from
  `ICameraDriver.MaxADU` at the `GetImageAsync` choke point, or a FITS `SATURATE` card on read).
  **Two "full scale" numbers exist and must not be conflated:** the FITS/BITPIX *container* width
  (`BitDepthEx.UnsignedFullScale` = 65535 for Int16), which is the right divisor for
  **N.I.N.A.-recorded** files because N.I.N.A. multiplies on recording; and the *native ADC*
  resolution (`AdcResolution`, 16383 for the ASI533MC Pro), which is what the vendor SDK actually
  hands TianWen, because it does **not** left-shift on capture. Never infer the SDK's delivered scale
  from third-party capture files, and never route a native ADC depth through `BitDepthEx.FromValue`
  (it silently falls back to the container width -- the original bug).
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

### Image Mutability: Almost-Immutable with In-Place Escape Hatches

`Image` is logically immutable: there is no public setter, the `data` arrays live as a
primary-ctor parameter, and the channel accessor is `GetChannelSpan → ReadOnlySpan<float>`.
**Two named exceptions deliberately mutate `data[c]` in place** and any new caller of these
must treat the source `Image` as consumed:

- **`Image.ScaleFloatValuesToUnitInPlace()`**: `internal` rescaler to `[0, 1]`. Returns a
  new `Image` view but reuses the underlying arrays. Original instance's `MaxValue` field
  becomes inconsistent with its samples after the call.
- **`Calibrator.Apply(Image light)`**: CONSUMES the light and the caller owns the result, whatever
  the configuration (P1 of `docs/plans/frame-lifecycle.md`). Each step releases what it consumed --
  a no-op for an unbuffered intermediate, the real handback for a pooled or camera-owned input.
  The one deliberate exception to "ownership transfer is visible in the name": an established
  domain verb, so it is documented and pinned by `CalibratorOwnershipTests` instead of renamed.
- **`Image.DebayerAsync` no longer consumes anything** (P4). It used to, but only for a mono/colour
  sensor AND only with `normalizeToUnit` AND only when the samples were not already unit-scaled --
  convention 5 on the INPUT side. It scales into a fresh image now; nothing ever reached the
  consuming path. **Membership of convention 4 is a property of the METHOD, never of an argument.**
- **`AstroImageDocument.AdoptImageAsync(Image, ...)`**: public ownership-transfer factory
  (was `CreateFromImageAsync` until the rename). Internally normalises the input via
  `ScaleFloatValuesToUnitInPlace`. **Caller must not retain or use `image` after this call.**
  Use the file-loading overload (`AstroImageDocument.OpenAsync(filePath, ...)`) for any case
  where the source `Image` is shared.

The rename to `AdoptImageAsync` is the canonical signal: any other public API that mutates
its `Image` input should follow the same naming convention (`Adopt*` / verb-form ownership
transfer), not the neutral `CreateFrom*` factory pattern.

**A third mutation exists and is deliberately invisible: plane RESIDENCY** (`TryEvictFloatPlanes`,
D1 of [docs/plans/viewer-memory-footprint.md](docs/plans/viewer-memory-footprint.md)). It breaks the
pattern of the two above on purpose -- it is not announced, not opt-in, and the caller is *expected*
to keep using the image -- because an evicted 8-bit image rebuilds its planes from the retained
raster on the next read, so the mutation is unobservable **by value**. That only holds if it is also
unobservable **by timing**, which is why: residency is DERIVED from the one `_planes` array rather
than tracked in a flag beside it (a flag and the array it describes are the same fact twice, and a
reader can catch the pair mid-update), every transition builds the whole replacement locally and
publishes it with ONE interlocked write (so a reader sees the whole before or the whole after, never
a half-restored array with some channels real and some 0x0 stubs), and a restorer that loses the
publication race discards its work rather than interleaving. **`Image` is public surface in a
published package**: a consumer reading two channels from two threads is entitled to do so against a
type documented as immutable, and cannot be expected to know a read can rebuild them. `volatile` on a
residency flag would NOT have bought this -- the tear is in the array, not the flag. Pinned by
`ImagePlaneResidencyConcurrencyTests`, whose racing-eviction case fails against the per-channel
version.

**Deriving residency is the expensive half, and `Image.ResidentPlanes()` is what pays for it.**
`WarpBenchmarks` ablates four variants, and the split matters: **D1' itself cost nothing** (a
predicted-not-taken bool), while the thread-safe derivation costs **+8.7% to +20.3%** on the bilinear
resample loops -- a second 72-byte `Channel` copy plus a dependent `.Data` load, 12.6M times for a
2048-square colour pass. Neither fact argues for going back to a flag; both argue for resolving
residency ONCE per operation and handing the loop plain `float[,]`, which returns to parity under
AOT. Two rules follow: **anything per-sample gets hoisted to a scope rather than made cheaper**, and
**a before/after pair spanning two commits is a band, not an attribution** -- it took four columns to
land this cost on the change that caused it.

**Eviction is NOT release, and they no longer share the word** (P0 of
[docs/plans/frame-lifecycle.md](docs/plans/frame-lifecycle.md)). `Image.Release()` spends OWNERSHIP:
the frame goes back to the camera or the pool and must never be touched again. `TryEvictFloatPlanes`
drops the float planes to save memory and is reversible, so an evicted image stays perfectly usable.
The two facts have opposite implications for a caller and were one word apart, which is the most
likely way to write the inverted guard; residency now says evict / restore / resident throughout, and
"released" means ownership and nothing else.

**Every read must go through the `Planes` accessor.** Three did not (`GetChannelArray`, the subpixel
sampler, `ScaleFloatValuesToUnitInPlace`) and so read the evicted 0x0 stub: a FITS write of an
evicted image emitted nothing and the in-place rescale threw on `plane[0, 0]`. Residency is also why
`TryLease` seeds from the LIVE planes -- seeding from the constructor argument handed a borrower the
float planes the image had since dropped, resurrecting exactly what D1 evicted.

**Test fixtures must not share `Image` instances across tests.** `SharedTestData` caches the
extracted *temp file path* (cheap to re-parse) but constructs a fresh `Image` per call; do
not reintroduce an `Image`-keyed cache. Two parallel collections passing the same cached
`Image` through `AdoptImageAsync` is enough to produce a "1 ms / 0 stars" `FindStarsAsync`
flake; the `Background()` histogram peak drifts off scale once the data has been rescaled
to `[0, 1]` while `MaxValue` still reads the original.

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

The **on-disk convention** predates the swap and must not regress, because two reader families
disagree about float TIFFs:

- **libtiff-HDRI readers** (ImageMagick-based tools): expect file values normalised to `[0, 1]`
  with `SMinSampleValue=0` / `SMaxSampleValue=65535` (tags 340/341) as a dynamic-range
  declaration they multiply by on read. Non-standard per TIFF 6.0 (SMin/SMax are informational),
  but widespread.
- **Scientific tools** (`tifffile`, PixInsight, ImageJ, FITS-aware viewers): read float TIFFs
  verbatim; SMin/SMax never rescale pixels.

**The `[0, 1]` file convention satisfies both**: HDRI readers rescale to their quantum,
scientific readers get linear scene-light values. So `Image.Export.cs` writes `[0, 1]` floats
with `SampleFormat = IeeeFloat` (tag 339 mandatory, without it readers misinterpret the float
bits as uint) + `SMinSampleValue = 0` / `SMaxSampleValue = 65535` (the `Q16HdriQuantumMax`
const, kept so ImageMagick-based tools read back at their expected `[0, 65535]`). `[0, 1]` is
the canonical in-memory range on read as well.

See the `Codecs` repo's `tests/SharpAstro.Codecs.Tests/TiffWriterRoundTripTests.cs` for the
byte-level reader probe and `TianWen.Lib.Tests/TiffRoundTripTests.cs` for the round-trip +
SATURATE/unit-scale guard.

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

**One slider widget, and it lives in DIR.Lib now.** The WB sliders, the 6 wavelet-layer sliders, and the
SER transport scrub are the same horizontal press/drag/release track. The single source is
`PixelWidgetBase` (DIR.Lib), upstreamed out of TianWen by the controls plan, so there is no
`ImageRendererBase.TrackSlider.cs` here to look for:
`DrawTrackSlider(trackX, trackW, barCenterY, handleY, handleH, frac, fillColor, hitBand, hit, chrome)`
(render + register the drag hit-band) plus a `(trackX, trackW, handleY, handleH, frac, ...)` overload for
the common case where the bar runs through the middle of the handle, and
`static TrackFrac(RectF32, px)` (the cursor-X -> fraction drag math). A new track-style control calls
these; never re-triplicate the bar/fill/handle/clamp math.

**One viewer (no mini viewer).** There is no separate "mini viewer" -- the Live Session preview, polar-align,
and guide-cam all host this same full viewer configured chromeless (`ViewerState.HideChrome` drops the
toolbar/status rows). The feed is `LiveFramePreviewSource : IPreviewSource` (`TianWen.UI.Abstractions`): it
normalises each camera frame to `[0,1]` and keeps a subsampled median/MAD stretch-stats scan (NOT the heavy
`AstroImageDocument.AdoptImageAsync` per frame), with `AcceptFrame(image, freezeStats)` doing the freeze
(`ViewerState.FreezeStretchStats`, set from polar phase; one-shot recompute on the off->on edge). Its
`ComputeStretchUniforms` delegates to the shared static `AstroImageDocument.ComputeStretchUniforms` (one path).
A document-less live source has no `document.Wcs`, so `ImageRendererBase.OverrideWcs` supplies the WCS for the
GPU grid + `WcsAnnotation` overlay (a plate-solved preview frame). Embedded hosts call `SetSurfaceSize(w,h)`
(sets the GPU projection dims, NOT `Resize`/`OnResize`, since they share the host renderer's surface) each
frame and draw any reticle/rings on top after `Render` returns. **`LiveFramePreviewSource.PerChannelBackground`
must be non-empty + channel-sized** -- the renderer's `ComputePostStretchBackground` indexes `[0]`
unconditionally (an empty array crashed the GUI; pinned by `LiveFramePreviewSourceTests`).

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

**`Linked` and `Unlinked` mean what they mean in PixInsight, and the difference lives ENTIRELY in the
uniforms** -- neither the GLSL nor `StretchChannelCpu` branches on the mode, so `StretchSolver` is the
only place the distinction exists and the only place it can silently collapse. **Linked writes ONE
curve into all three slots**, derived from the mean of the per-channel WB-applied medians and MADs
(PI's and Siril's linked STF), so a white balance survives as colour. **Unlinked writes each channel's
own auto-normalised curve**, which absorbs the auto calibration and neutralises the background -- that
is what the mode is FOR, not a bug. `ViewerActions.DefaultStretchMode` (= `StretchLinkModes[0]` =
Linked) is the single source for every default; `MasterPreviewRenderer` and `PreviewEncoder` both
render Linked.

Linked used to replicate channel 0's *stats* and scale each copy by that channel's own multiplier,
giving three curves whose anchors tracked the multipliers and divided them back out: a WB had **no
effect** on a linked render, three very different SPCC triples rendered identically, and the default
mode was Unlinked so SPCC looked like a no-op everywhere. Pinned by `StretchLinkedWhiteBalanceTests`;
measurements in [docs/known-limitations.md](docs/known-limitations.md). **Never re-derive a per-channel
curve in the Linked branch.**

**Background neutralisation is solved for a neutral POST-WB background, so its gains depend on the
calibration.** The gains run before the WB multiply, so neutralising the pre-WB background and then
multiplying by a non-neutral triple just re-tints it -- which rendered a correctly-calibrated SMC
master visibly blue while `NeutBg` reported `1.00/1.00/1.00`. Every
`BackgroundNeutralizationMethod` honours the `whiteBalance` argument (it was MinPivot-only, and Mean
is the default); a neutral WB reduces to the old arithmetic bit-for-bit. **Anything caching these
gains owes the WB in its cache key** -- `AstroImageDocument` keys on `(method, WB)`. Gains print at
**F4**: they are affine about 1.0 against a ~0.002 background, so the triple that fixes a 2.66x cast
is `(0.9981, 1.0003, 1.0005)` and F2 shows three 1.00s.

`AstroImageDocument.ComputeStretchUniforms` is the single producer of `StretchUniforms`; it scales
per-channel stats by WB before deriving shadows/midtones/rescale so the post-WB norm and shadow
are in the same coordinate space. `ConvergeStretchFactor` takes a `whiteBalance` scalar and
operates entirely in post-WB space (median, mad, binNorm all multiplied) so the converged
stretchFactor matches the per-channel rendering.

**Two WB facts the viewer's manual WB sliders depend on (don't regress):** (1) The stat scaling only
makes sense for the AUTO calibration (`ColorCalibration`); its whole job is to keep the background
neutral. A MANUAL WB multiplier that ALSO scaled the stats would be cancelled by a per-channel
auto-normalised stretch (Unlinked / linear), so the producer takes a separate `shaderWhiteBalance`
(= auto × manual) that goes to `StretchUniforms.WhiteBalance` while only the auto WB scales the stats.
A neutral manual triple leaves `shaderWhiteBalance == whiteBalance`, so the auto-only path is
bit-identical. This split is also why the two halves must stay separate rather than being collapsed
into one number: the auto half changes what an Unlinked stretch does with the calibration. **The
sliders show the composed EFFECTIVE triple** (`auto x manual`, via `StretchSolver.ComposeWhiteBalance`
so the panel cannot drift from the render) and a drag solves back for the manual factor; they showed
the manual triple alone until then, so a calibrated image sat at 1.00/1.00/1.00 on the one control
whose job is to report the white balance. Their travel is its OWN constant (`[0.25, 4]`), never
`GrayWorldWhiteBalance`'s `[0.5, 2]` clamp -- that bounds what the *estimator* may return, and a real
photometric fit lands outside it (R = 0.463), which the shared constant silently rounded to 0.50.
(2) **WB is applied in the `StretchMode.None` (linear) path** in the GLSL `else`
branch + the CPU `RenderStretchedRgba`/`RenderStretchedRgba16` + `ConsoleImageRenderer` None branches.
This is load-bearing: a SER opens in linear mode (`ViewerController`), and the old None path was a
pure passthrough that ignored `WhiteBalance`, so WB (manual OR auto Calibrate/SPCC) did nothing
until a non-linear stretch was toggled on. The mono None path stays a straight passthrough (WB is
meaningless for one channel), mirroring the GLSL mono branch.

Luma weights live in `StretchUniforms.LumaWeights` (Rec.709 / Rec.601 / Rec.2020 / SensorMatched
via the `LumaWeighting` enum, default Rec.709). The CPU `StretchLumaPixelCpu`, GLSL Luma branch,
and `StretchUniforms.ComputePostStretchBackground` all read from the uniform, never hardcode
Rec.709 constants. `LumaWeighting.SensorMatched` resolves via
`AstroImageDocument.ResolveLumaWeights` -> `FilterCurveDatabase.TryComputeSensorLumaWeights`
(integrates sensor QE × Sony CFA R/G/B over the visible, normalises to sum 1); silently falls
back to Rec.709 when the sensor model isn't recognised.

Post-stretch normalize: when caller passes `normalize: true` to `ComputeStretchUniforms`, the
producer calls `Image.PredictPostStretchMaxScale` (walks each channel histogram's top non-zero
bin through the full chain) and sets `StretchUniforms.NormalizeScale = 1/max`. CPU and GPU
multiply by this scale after curves+HDR but before the final clamp; single-pass, no GPU
reduction needed. Default 1.0 = no-op.

When adding a new pipeline stage (e.g. saturation boost, denoise, etc.), wire it into BOTH the
GLSL shader AND the CPU helpers. A stage that only exists in GLSL is a regression for tests + TUI.

### Test Verification: Full Pipeline Inputs

`StretchTests_NewPipeline.cs` is the end-to-end test for the stretch+colour pipeline. It exercises
every input field the GPU shader cares about and writes TIFF + JPEG per case to the temp test
output dir for visual regression. The companion `StretchTestBase.cs` adds per-channel float-value
range + AutoLevel quantum-range assertions to all four legacy stretch test files.

Pattern when extending tests: assert per-channel byte/float means stay inside `(epsilon, max-epsilon)`
to catch the channel-collapse regressions we hit during the WB+shadow coordinate-space refactor.

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
- **Prefer a lock-free hand-off over `lock {}` blocks.** For producer/consumer hand-off (a
  background task feeding a render or poll loop), return the result *through* the `Task<T>` and let
  the consumer poll it: `if (_task is { IsCompleted: true } t) { _task = null; if (t.IsCompletedSuccessfully && t.Result is { } x) use(x); }`. The Task is the synchronisation primitive, so no shared
  mutable field crosses threads; in a synchronous loop where you can't `await`, that poll is the
  stand-in for `await _task`. For a single grab-and-clear reference, use `Interlocked.Exchange`.
  Rationale: a `lock (new object())` block serialises a hot path, hides the ownership model, and is
  almost always avoidable with a Task hand-off or an atomic swap. (Canonical example: `SkyMapTab`'s
  async Milky Way load uses `Task<DecodedMilkyWay?>` polled on the render thread, mirroring
  `TryApplyPendingStarBuild`.)
- **Never build the value for a `CompareExchange` inside the call.** An argument is evaluated before
  the call it is passed to, so `Interlocked.CompareExchange(ref _task, Task.Run(Work), null)` starts
  `Work` on **every** racing caller, not just the CAS winner. The losers return the winner's task and
  look correct while their own copy runs on. In `FilterCurveDatabase.LoadAsync` that appended a second
  copy of every curve (180 filters became 360). Publish a `TaskCompletionSource` placeholder first, do
  the work behind it, and raise any "ready" flag only once the data is there -- a flag set by the CAS
  winner *before* the work runs answers true over empty state.
- **Standing rule for `lock () {}`** (any lock, anywhere): (1) it needs a strong justification as a
  comment at the lock site -- why a Task hand-off / `Interlocked` / ImmutableArray-CAS swap doesn't
  fit; (2) the locked path should not be reachable from a rendering thread (a contended lock there
  is a frame stall -- hand the render thread an immutable snapshot instead); (3) if the lock stays,
  it must be `System.Threading.Lock` (C# 13) -- never `lock` on an `object`, a collection, or any
  other reachable instance. Rationale for `Lock`: faster (no monitor syncblock), self-documents
  intent, compiler-enforced correct usage. Remaining `object`-based sites are inventoried as a
  sweep item in [docs/todo/infra.md](docs/todo/infra.md). For a most-recent-N window polled by
  readers (guide samples, frame metrics), prefer the lock-free `CircularBuffer<T>`
  (`TianWen.Lib/Sequencing`): ImmutableArray + CAS replace, torn-free `Snapshot` reads, O(capacity)
  appends -- right when producers are low-rate (per exposure) and pollers are high-rate (per frame).

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
