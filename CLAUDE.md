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

Canonical project state lives in repo-root markdown files — read these before starting non-trivial work:

| File | Purpose |
|------|---------|
| `PLAN-summary.md` | Current status of every `PLAN-*.md` (DONE / PARTIAL / NOT STARTED) cross-checked against the codebase |
| `PLAN-*.md` | Per-feature implementation plans with phasing tables |
| `ARCH-*.md` | Architecture deep-dives with mermaid diagrams (e.g. `ARCH-driver-resilience.md`, `ARCH-fov-obstruction.md`) |
| `TODO.md` | Working list of unchecked tasks grouped by area |

## Custom Skills

Available in `.claude/skills/<name>/SKILL.md` — auto-invocable when the request matches the skill's description, or explicitly via `/<name>`.

| Skill | Purpose |
|-------|---------|
| `release-lib` | Release a SharpAstro sibling library to NuGet with full dependency chain |
| `release-tianwen` | Cut a TianWen binary release (workflow_dispatch + GitHub Release with .tar.gz assets) |
| `sibling-status` | Git status + version across all SharpAstro repos |
| `check-ci` | GitHub Actions CI status across all repos |
| `bump-version` | Bump TianWen version in all 4 required locations |
| `run-gui` / `run-tui` | Build and launch the GUI / CLI TUI with stderr redirect |
| `test-filter` | Run tests matching a name pattern |
| `tick-todo` | Mark a TODO item done and update CLAUDE.md, PLAN files, and memory |

## Project Overview

TianWen is a .NET 10 library for astronomical device management, image processing, and astrometry.
Supports cameras, mounts, focusers, filter wheels, and guiders via ASCOM, Alpaca (HTTP), ZWO, QHYCCD,
Meade LX200, Skywatcher, OnStep (serial + WiFi/mDNS), iOptron SkyGuider Pro, PHD2, and a built-in
guider. Published as `TianWen.Lib` on NuGet, plus four AOT-published binaries (`tianwen` CLI,
`tianwen-server` headless, `tianwen-gui`, `tianwen-fits`).

Repository: https://github.com/SharpAstro/tianwen

## Solution Structure

```
src/
├── TianWen.slnx                   # Solution file (XML format)
├── Directory.Build.props          # Auto-detect sibling repos (ProjectReference vs PackageReference)
├── Directory.Packages.props       # Centralized package version management
├── TianWen.Lib/                   # Core library (net10.0)
├── TianWen.Lib.Tests/             # Unit tests (xUnit v3)
├── TianWen.Lib.Tests.Functional/  # Functional/integration tests (Session loops with FakeTimeProvider)
├── TianWen.Cli/                   # CLI (AOT-published → `tianwen`)
├── TianWen.Hosting/               # ASP.NET Core Minimal API (REST + WebSocket)
├── TianWen.Server/                # Headless server (AOT-published → `tianwen-server`)
├── TianWen.UI.Abstractions/       # Widget system, layout, state, shared types
├── TianWen.UI.Shared/             # SDL→InputKey mapping, Vulkan FITS pipeline, VkSkyMapPipeline
├── TianWen.UI.Gui/                # N.I.N.A.-style integrated GUI (AOT-published → `tianwen-gui`)
├── TianWen.UI.FitsViewer/         # Standalone FITS viewer (AOT-published → `tianwen-fits`)
├── TianWen.UI.Benchmarks/         # BenchmarkDotNet performance tests
├── TianWen.AI/                    # ORT facade (EP resolver + session-options helpers)
├── TianWen.AI.Imaging/            # Image ↔ tensor bridge + concrete enhancer wrappers
└── TianWen.AI.MCP/                # MCP (Model Context Protocol) stdio server (AOT-published → `tianwen-mcp`)
```

CLI, Server, FitsViewer, Gui, MCP set `<AssemblyName>` to a short lower-case name so the published
binaries are `tianwen`, `tianwen-server`, `tianwen-fits`, `tianwen-gui`, `tianwen-mcp`.

## Build & Test Commands

```bash
# All commands run from src/
dotnet build
dotnet test
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
```

## SharpAstro Sibling Libraries

TianWen depends on in-house libraries published to nuget.org under the **SharpAstro** org. Their
source repos live as siblings under the same parent directory (`../`). csproj layout varies — not
every sibling uses `src/<Lib>/<Lib>.csproj`.

| Package | Source Repo | csproj path | Auto-detect |
|---------|-------------|-------------|:-----------:|
| `DIR.Lib` | `../DIR.Lib` | `src/DIR.Lib/DIR.Lib.csproj` | ✅ |
| `SdlVulkan.Renderer` | `../SdlVulkan.Renderer` | `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` | ✅ |
| `Console.Lib` | `../Console.Lib` | `src/Console.Lib/Console.Lib.csproj` | ✅ |
| `FITS.Lib` | `../FITS.Lib` | `CSharpFITS/CSharpFITS.csproj` (package name is `FITS.Lib`) | ✅ (separate `UseLocalFitsLib` switch) |
| `FC.SDK` | `../FC.SDK` | `src/FC.SDK/FC.SDK.csproj` | ❌ |
| `ZWOptical.SDK` | `../zwo-sdk-nuget` | `ZWOptical.SDK.csproj` (repo root) | ❌ |
| `QHYCCD.SDK` | `../QHYCCD.SDK` | `QHYCCD.SDK.csproj` (repo root) | ❌ |
| `SharpAstro.Fonts` | `../Fonts.Lib` | `src/SharpAstro.Fonts/SharpAstro.Fonts.csproj` | transitive |
| `TianWen.DAL` | `../TianWen.DAL` | — | ❌ |

**Auto-detection** (`Directory.Build.props`): for `DIR.Lib`, `Console.Lib`, `SdlVulkan.Renderer`,
`StbImageSharp`, `SharpAstro.Tiff`, the build switches to ProjectReference when all five sibling
working copies exist, otherwise PackageReference. Single property `UseLocalSiblings`. Override:
`dotnet build -p:UseLocalSiblings=false`. CI always uses PackageReference. `Fonts.Lib` is transitive
via DIR.Lib's own `UseLocalFontsLib` switch. **`FITS.Lib` has its own independent switch**
(`UseLocalFitsLib`): it auto-detects when `../FITS.Lib/CSharpFITS/CSharpFITS.csproj` exists, decoupled
from `UseLocalSiblings` because FITS.Lib has no transitive coupling to the DIR.Lib/Console.Lib chain.
Override: `dotnet build -p:UseLocalFitsLib=false`.

For libraries without auto-detection (`FC.SDK`, `ZWOptical.SDK`, `QHYCCD.SDK`, `TianWen.DAL`),
prefer to extend the `UseLocalSiblings` (or add an analogous `UseLocalXxx`) switch in
`Directory.Build.props` + add a conditional `ProjectReference` in the consuming `.csproj`
rather than reaching for local nupkg feeds. When that's not viable (e.g. cross-team release
cadence forces a version bump), commit + push + wait for NuGet publish — **do not** create
local nupkg feeds or run `dotnet pack` to short-circuit the release dance, since CI builds
will still pull from nuget.org and a local-only nupkg will mask version-skew bugs.

## Key Technologies

| Area | Technology |
|------|-----------|
| DI / Logging | Microsoft.Extensions.* |
| CLI | System.CommandLine v2 + Pastel |
| Testing | xUnit v3 + Shouldly + NSubstitute |
| Imaging | Magick.NET, FITS.Lib |
| UI / GPU | SDL3 + Vulkan (SdlVulkan.Renderer) |
| Hosting | ASP.NET Core Minimal API, StbImageWriteSharp |
| Astronomy | ASCOM, ZWOptical.SDK, QHYCCD.SDK, IAU SOFA (C# port) |

## Testing Conventions

- **xUnit v3** with `[Fact]` / `[Theory]` + `[InlineData]`; **Shouldly** for assertions; **NSubstitute** for mocks
- Test data: embedded resources in `Data/` subdirectories
- **Never use reflection in tests** — add an `internal` property/method instead (test project has `InternalsVisibleTo`)
- **Avoid duplication** — extract shared setup to helpers (e.g., `SessionTestHelper`)

### Test Collections & Parallelism

Tests grouped into `[Collection("X")]` by functional area. All `Session*Tests` share `[Collection("Session")]`
and run sequentially to avoid thread pool starvation from concurrent `Task.Run` + `FakeTimeProvider` timer
callbacks. `maxParallelThreads: 4` in `xunit.runner.json`. **No wall-clock `CancellationTokenSource` timeouts**
in session tests — use `[Fact(Timeout = ...)]`; inner timeouts cause flakes.

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
**Never** use `SleepAsync(subExposure)` in a pump loop — it advances fake time even when the `Task.Run`
hasn't been scheduled yet, causing targets to "set" before imaging starts. `Advance` fires timers
synchronously; `Task.Delay(1)` yields to the thread pool.

## Coding Style

Enforced via `.editorconfig`:
- 4 spaces, CRLF line endings, block-scoped namespaces (`namespace Foo { }`, not file-scoped)
- Primary constructors preferred for DI
- No implicit `new(...)` — always `new SomeType()`
- Expression-bodied: properties yes, methods/constructors no
- Interfaces prefixed with `I`; PascalCase types/properties/methods; `_camelCase` private fields

## Architecture

### Logger, TimeProvider, and SleepAsync

- `ILogger` and `ITimeProvider` resolved from `IServiceProvider` (not from `IExternal`)
- **`ITimeProvider.SleepAsync`** must be used instead of `Task.Delay(duration, timeProvider, ct)`.
  `FakeTimeProvider`'s `SleepAsync` auto-advances fake time; `Task.Delay` with `FakeTimeProvider`
  hangs waiting for external advancement. All code should be testable.
- `LoggerCatchExtensions` provides `ILogger.Catch/CatchAsync` for best-effort fallbacks

### Device Management

URI-addressed: `DeviceBase` (URI identity), `IDeviceSource<T>` (driver backends),
`ICombinedDeviceManager` (coordinates sources), `IDeviceUriRegistry` (URI → instance map).
Each subclass reads query keys (`?key=value`) defined in `DeviceQueryKey`. See class XML doc comments
for supported keys.

### Plate Solving

`IPlateSolverFactory` selects in priority order:
- `CatalogPlateSolver` — built-in, ~6 matched stars, no external dep, used by polar alignment refine loop
- `AstapPlateSolver` — wraps `astap_cli`; needs ~44 stars
- `AstrometryNetPlateSolver` — wraps `solve-field`; slower fallback

**`CatalogPlateSolver` requires Tycho-2 to be loaded.** The solver self-inits the
`ICelestialObjectDB` at the top of `SolveImageAsync` via the idempotent `InitDBAsync`
fast path (`_isInitialized`), so any caller (CLI, hosted API, tests) works without
remembering to init upstream. First call pays the Tycho-2 bulk-decode cost (~500 ms
typical); subsequent calls are free.

**DI registration uses a factory lambda** (`AstrometryServiceCollectionExtensions.cs`):

```csharp
.AddSingleton<IPlateSolver>(sp => new CatalogPlateSolver(
    sp.GetRequiredService<ICelestialObjectDB>(),
    sp.GetRequiredService<ILogger<CatalogPlateSolver>>()))
```

The short form `AddSingleton<IPlateSolver, CatalogPlateSolver>()` does NOT work for any
ctor with a non-generic `ILogger` parameter — `Microsoft.Extensions.Logging` only
registers `ILogger<T>` (open generic) and `ILoggerFactory`, never `ILogger` directly.
A ctor `(Foo, ILogger? logger = null)` therefore silently gets `logger = null` from DI,
and `_logger?.LogDebug(...)` lines never fire — which is exactly how the
"`CatalogPlateSolver` fails on drizzle outputs from `tianwen solve`" bug hid for weeks.
**Rule:** ctor params should be `ILogger<TSelf> logger` for direct DI resolution, or
use a factory lambda when a non-generic `ILogger` ctor parameter must be preserved
(e.g. so the same class can be manually constructed by another component that already
holds an `ILogger`).

### Session

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator. **Single-mount /
multi-OTA invariant**: `Setup.Telescopes` is plural for dual-/triple-saddle rigs, but there is exactly
one `Setup.Mount`. All OTAs share pointing and the current target. Multi-OTA buys parallel capture
(per-OTA camera/filter wheel/focuser) and per-OTA focus/filter/baseline state. Any future "branch"
or "re-order" logic must operate on the OTA set as a single unit.

`RunAsync` workflow: `InitialisationAsync` → wait for twilight → `CoolCamerasToSetpointAsync` →
`InitialRoughFocusAsync` → `AutoFocusAllTelescopesAsync` → `CalibrateGuiderAsync` → `ObservationLoopAsync`.
See class XML doc + `PLAN-*.md` for details on each phase.

`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (default 3 min,
covers slew + center + guider settle) before slewing to each target, via `WaitForScheduledStartAsync`
(`Session.Timing.cs`), so the scheduler's altitude-optimised slot times are honored. Same-Start / past-Start
schedules (hosted API stamping `Start = now`, legacy callers, existing tests) short-circuit the wait and
advance linearly, so that path is unchanged. Late starts proceed without clamping (the full `Duration` still
runs); a lead-adjusted start beyond session end skips the observation cleanly. The wait uses the same mount
clock (`GetMountUtcNowAsync`) as the loop condition.

### Driver Resilience on the Hot Path

All driver calls reachable from the session hot path go through `Session.ResilientInvokeAsync(...)`,
a thin wrapper over `ResilientCall.InvokeAsync` with `OnDriverReconnect` as the fault callback. See
[`ARCH-driver-resilience.md`](ARCH-driver-resilience.md).

- **Never introduce a raw `await driver.X(...)` on the session hot path.** Grep PRs for regressions.
- **Pick the preset:** `IdempotentRead` (status/position polls — 3 attempts, exponential backoff +
  inter-retry reconnect), `NonIdempotentAction` (slew/exposure/dither — 1 attempt, pre-reconnect only),
  `AbsoluteMove` (focuser/filter-wheel — 2 attempts, target is absolute so re-issue is safe).
- **Telemetry polls go through `PollDriverReadAsync` / `PollDriverReadAsyncIf`** (capability-gated).
  These count consecutive per-driver failures and fire a one-shot proactive reconnect at threshold.
- **Escalation:** every reconnect bumps `_driverFaultCounts[driver]`; successful frames decay it.
  When any driver crosses `SessionConfiguration.DeviceFaultEscalationThreshold` (default 5),
  `ImagingLoopAsync` returns `ImageLoopNextAction.DeviceUnrecoverable`.
- **`CatchAsync` is still correct** for best-effort predicate decisions (`IsSlewingAsync`,
  `IsTrackingAsync`), FITS header reads, and finaliser steps.

### Backlash Auto-Tuning

Every successful AutoFocus opportunistically infers per-direction backlash from the verification
exposure (no separate measurement routine — based on the cloudynights "no need to measure backlash,
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
See `PLAN-polar-alignment.md` for the math/algorithm.

### Hosting API (`TianWen.Hosting` + `TianWen.Server`)

Headless REST + WebSocket API. Two API layers on the same ASP.NET Core host:
- **Native v1** (`/api/v1/`): multi-OTA, camelCase JSON, POST for mutations
- **ninaAPI v2 shim** (`/v2/api/`): single-OTA (maps to OTA[0]), PascalCase JSON, GET for everything

`IHostedSession` holds `ISession?`, `ActiveProfileId`, `PendingTargets` (pre-session queue, drained
into `ScheduledObservation[]` at `/session/start`). `EventBroadcaster` (`BackgroundService`) subscribes
to `PhaseChanged` / `FrameWritten` / `PlateSolveCompleted` and pushes through `EventHub`'s dual pool.

Run: `dotnet run --project TianWen.Server` or `tianwen-server [--port 1888]`.

### Image Pipeline & Buffer Lifecycle

Camera → `ChannelBuffer` → `Image` → consumer → `image.Release()` → camera recycles. See
`ChannelBuffer` XML doc for ownership semantics.
- Never hold an `Image` from `GetImageAsync` longer than needed — it pins the camera buffer
- `DebayerIntoAsync` for viewer output, `DebayerAsync` only for FITS viewer (file-based)
- `Array2DPool` is for scratch only — camera buffers use `ChannelBuffer`/`_freeBuffers`

### Image Mutability — Almost-Immutable with In-Place Escape Hatches

`Image` is logically immutable: there is no public setter, the `data` arrays live as a
primary-ctor parameter, and the channel accessor is `GetChannelSpan → ReadOnlySpan<float>`.
**Two named exceptions deliberately mutate `data[c]` in place** and any new caller of these
must treat the source `Image` as consumed:

- **`Image.ScaleFloatValuesToUnitInPlace()`** — `internal` rescaler to `[0, 1]`. Returns a
  new `Image` view but reuses the underlying arrays. Original instance's `MaxValue` field
  becomes inconsistent with its samples after the call.
- **`Image.DebayerAsync(..., normalizeToUnit: true)`** — passthrough for non-Bayer images
  calls `ScaleFloatValuesToUnitInPlace` and returns the result; mutates the input.
- **`AstroImageDocument.AdoptImageAsync(Image, ...)`** — public ownership-transfer factory
  (was `CreateFromImageAsync` until the rename). Internally normalises the input via
  `ScaleFloatValuesToUnitInPlace`. **Caller must not retain or use `image` after this call.**
  Use the file-loading overload (`AstroImageDocument.OpenAsync(filePath, ...)`) for any case
  where the source `Image` is shared.

The rename to `AdoptImageAsync` is the canonical signal: any other public API that mutates
its `Image` input should follow the same naming convention (`Adopt*` / verb-form ownership
transfer), not the neutral `CreateFrom*` factory pattern.

**Test fixtures must not share `Image` instances across tests.** `SharedTestData` caches the
extracted *temp file path* (cheap to re-parse) but constructs a fresh `Image` per call; do
not reintroduce an `Image`-keyed cache. Two parallel collections passing the same cached
`Image` through `AdoptImageAsync` is enough to produce a "1 ms / 0 stars" `FindStarsAsync`
flake — the `Background()` histogram peak drifts off scale once the data has been rescaled
to `[0, 1]` while `MaxValue` still reads the original.

### Float TIFF Convention (Magick.NET ↔ DIR.Lib swap)

Float32 TIFF I/O has two competing readers in the wild and the swap from Magick.NET to
`DIR.Lib.Tiff.TiffWriter` (planned) must respect both:

- **Magick.NET / libtiff-HDRI**: stores file values normalised to `[0, 1]` and writes
  `SMinSampleValue=0` / `SMaxSampleValue=Quantum.Max` (tags 340/341) as the dynamic-range
  declaration. On read, libtiff multiplies file values by `SMaxSampleValue` to restore the
  in-memory `[0, Quantum.Max]` range. This is non-standard per the TIFF 6.0 spec, which
  treats SMin/SMax as informational only.
- **Scientific tools** (`tifffile`, PixInsight, ImageJ, FITS-aware viewers): treat float
  TIFFs as the literal data. SMin/SMax tags are read into metadata but **never used** to
  rescale pixels. Verified empirically with `tifffile` against the same files Magick.NET
  produces — it returns `[0, 1]` floats verbatim.

**The `[0, 1]` file convention works for both** — Magick.NET multiplies by `Quantum.Max`
(its convention), scientific readers get linear scene-light values. Writing
`[0, Quantum.Max]` literally would round-trip via Magick.NET but Magick.NET would re-scale
on read to `[0, Quantum.Max²]` ≈ 4.3 × 10⁹ — broken.

**Migration recipe** for swapping `Image.Export.cs` from Magick.NET to DIR.Lib:

1. Drop the `× Quantum.Max` hop in `DoToMagickImage` — write the source `[0, 1]` floats
   directly into the TIFF byte buffer.
2. Emit a `TiffPageOptions` with `SampleFormat = TiffSampleFormat.IeeeFloat`,
   `SMinSampleValue = 0f`, `SMaxSampleValue = Quantum.Max`. (Tag 339 is mandatory — without
   it readers misinterpret the float bits as uint. Tags 340/341 are required to keep
   Magick.NET reading back at `[0, Quantum.Max]`.)
3. Result is byte-equivalent to today's Magick.NET output → `TiffRoundTripTests` keeps
   passing, and PixInsight/ImageJ/`tifffile` see literal `[0, 1]` linear values.

The Q16-HDRI `× Quantum.Max` scaling sprinkled through `Image.Export.cs` / `Image.Import.cs`
is purely an in-memory hop to satisfy Magick.NET's `GetArea` / `SetPixels` range — it does
**not** affect on-disk bytes. When porting reads off Magick.NET, do the same: read file
floats as-is and trust `[0, 1]` as the canonical internal range.

See `DIR.Lib.Tests/TiffWriterRoundTripTests.cs` for the byte-level reader probe and
`TianWen.Lib.Tests/TiffWriterMagickDiffTests.cs` for the cross-library diff suite.

**DIR.Lib Phase-1.5 additions** (available as of DIR.Lib 2.14.x):

- `DIR.Lib.Color.IccProfiles.SRgbV4` — bundled 588-byte sRGB v4 profile bytes
  (LittleCMS-generated). Pass to `TiffPageOptions.IccProfile` or
  `PngWriter.Encode(..., iccProfile)` to embed a colour-management tag without
  having to source profile bytes yourself.
- `PngWriter.EncodeGray8` / `EncodeGray16` / `EncodeRgba16` — bit-depth and
  grayscale variants on top of the original RGBA8 entry point. 16-bit overloads
  accept `ReadOnlySpan<ushort>` in system-endian and byte-swap to PNG's required
  BE order internally.
- `PngWriter.Encode(rgba, w, h, iccProfile)` — emits an `iCCP` chunk between
  IHDR and IDAT, keyword "ICC profile", zlib-deflated. Empty span = no chunk.
- `DIR.Lib.Tiff.TiffReader.Read(stream | span)` — pure-managed decoder; v1
  scope is strip layout + Uncompressed/Deflate/ZlibPkzip + 8/16/32-bit uint
  or IeeeFloat + contig planar config. Both "II" (LE) and "MM" (BE) byte
  orders are accepted; the reader detects file-order from the header and
  byte-swaps pixels to *host* order on mismatch. Multi-page chains decoded
  fully. SampleFormat/SMin/SMax/IccProfile round-trip. Returns
  `TiffDocument(Pages)`; per-page `Pixels` is always in host byte order,
  zero-copy castable to `ushort[]`/`float[]` via `MemoryMarshal.Cast`.
- `TiffWriter` now declares the file's byte order to match the host (II on
  LE / MM on BE) so multi-byte tag values and pixel samples can be written
  verbatim from native memory with no swap step. The reader honours either
  order, so round-trip is correct regardless of host architecture.

### Sky Map / FITS Viewer GLSL

The Vortice.ShaderCompiler GLSL-to-SPIR-V compiler does **not** handle non-ASCII characters, even
in comments. Never use Unicode (em dashes, arrows, math symbols) inside GLSL raw string literals —
ASCII only.

`Image.StretchValue()` is the single source of truth for the scalar stretch math (normalize → subtract
pedestal → rescale → MTF). Don't reimplement it.

### Stretch Pipeline: CPU/GPU Mirror

The stretch pipeline runs in two parallel implementations that must produce visually equivalent
output for the same `StretchUniforms`:
- **GPU**: `VkFitsImagePipeline.glsl` `stretchChannel` (per-channel) + `StretchLumaPixelCpu` analogue
  in shader (Luma) — used by the live FITS viewer.
- **CPU**: `Image.StretchChannelCpu`, `Image.StretchLumaPixelCpu`, `Image.ApplyHdr`,
  `Image.ApplyCurveLut`, `Image.ApplyBoost`, `Image.RenderStretchedRgba` — used by
  `ConsoleImageRenderer` (TUI Sixel) and tests (`StretchTests_NewPipeline`). Never use the GPU.

Pipeline order in both: pedestal subtract → bg neutralization → WB → shadow/rescale → MTF →
luma blend → curves (LUT or boost) → HDR knee → normalize → clamp. Per-channel for
Linked/Unlinked, luma-Y'/Y for Luma. In Luma mode the producer always populates BOTH
`StretchUniforms.LumaStretch` (scalar Luma MTF params) AND per-channel `Shadows/Midtones/Rescale`
(linked branch params) so the shader can blend between them via `LumaBlend`.

`AstroImageDocument.ComputeStretchUniforms` is the single producer of `StretchUniforms` — it scales
per-channel stats by WB before deriving shadows/midtones/rescale so the post-WB norm and shadow
are in the same coordinate space. `ConvergeStretchFactor` takes a `whiteBalance` scalar and
operates entirely in post-WB space (median, mad, binNorm all multiplied) so the converged
stretchFactor matches the per-channel rendering.

Luma weights live in `StretchUniforms.LumaWeights` (Rec.709 / Rec.601 / Rec.2020 / SensorMatched
via the `LumaWeighting` enum, default Rec.709). The CPU `StretchLumaPixelCpu`, GLSL Luma branch,
and `StretchUniforms.ComputePostStretchBackground` all read from the uniform — never hardcode
Rec.709 constants. `LumaWeighting.SensorMatched` resolves via
`AstroImageDocument.ResolveLumaWeights` -> `FilterCurveDatabase.TryComputeSensorLumaWeights`
(integrates sensor QE × Sony CFA R/G/B over the visible, normalises to sum 1); silently falls
back to Rec.709 when the sensor model isn't recognised.

Post-stretch normalize: when caller passes `normalize: true` to `ComputeStretchUniforms`, the
producer calls `Image.PredictPostStretchMaxScale` (walks each channel histogram's top non-zero
bin through the full chain) and sets `StretchUniforms.NormalizeScale = 1/max`. CPU and GPU
multiply by this scale after curves+HDR but before the final clamp — single-pass, no GPU
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

### Signal Handler Pattern — Route, Don't Implement

The lightweight `SignalBus` is our alternative to MediatR/MVVM. `AppSignalHandler.cs` subscribe
lambdas must **route only**: take signal payload, call one or two helpers, reflect results back into
UI state. No loops over domain state, no direct persistence, no URI manipulation, no multi-step
business logic.

Where business logic goes:
- **Pure profile/equipment transformations** → `EquipmentActions` in `TianWen.UI.Abstractions`
- **Device-model operations** (URI reconciliation, discovery) → extension methods in `TianWen.Lib/Devices/*Extensions.cs`
- **Persistence** → dedicated helpers (`PlannerPersistence`, `SessionPersistence`, `Profile.SaveAsync`)

**Red flag**: a `foreach` or multi-step `if`/`await`/`save` chain inside a subscribe lambda — extract it.

### Shared UI State: `ImmutableArray<T>`, not `List<T>`

Any collection on shared UI state (`PlannerState`, `LiveSessionState`, `EquipmentTabState`,
`GuiAppState`) that can be touched by **both** the render thread and a background task must be
`ImmutableArray<T>` with atomic replacement. Writers build the new array (or use `array.Add(x)`,
`.RemoveAt(i)`, `.SetItem(i, x)`, `.Sort(cmp)` — all return new instances) and assign in one
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
| Ring buffer / accumulator | unguarded `_ring`/`_count`/`_head` | `lock (_gate)` on mutate + read; readers return snapshot array, not lazy `IEnumerable` |
| Large `record struct` cross-thread | unguarded auto-property `set` | private field + `lock` (struct writes > pointer-size aren't atomic) |

**Telemetry-poll-only state** can stay non-concurrent if it is genuinely only written from the
per-frame poll method. Mark it clearly so a future edit doesn't move the write into a continuation.
Canonical example: `AppSignalHandler.PollCameraTelemetry` and `EquipmentTabState.PendingTransitions`.

### Concurrency

- `SemaphoreSlim` / `DotNext.Threading` for resource locking
- `CancellationToken` propagated throughout
- `ValueTask` for allocation-free async paths
- **Never use `.GetAwaiter().GetResult()`** — make the method `async` and `await`

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, `Span<T>` / `ReadOnlySpan<T>`
- **Immutability with controlled mutability**: types immutable by default; private mutable state with read-only views
- **Correct abstraction levels**: pure math/data in `TianWen.Lib`, UI state in `TianWen.UI.Abstractions`,
  Vulkan-specific rendering in `TianWen.UI.Shared` / `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **No code duplication**: reuse single sources of truth (e.g., `Image.StretchValue()`)

## Package Management

Centralized in `Directory.Packages.props` — version numbers go there, not in individual `.csproj` files.

## Runtime Data (AppData)

`%LOCALAPPDATA%/TianWen/`:
```
TianWen/
├── Logs/        # Per-day log files: GUI_*.log, FitsViewer_*.log
├── Profiles/   # Per-profile data (*.json + NeuralGuider/*.ngm + BacklashHistory/*.json)
└── Planner/    # Persisted planner state (pinned targets)
```
