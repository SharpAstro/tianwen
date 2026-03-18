# CLAUDE.md - TianWen Project Guide

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powerhsell
- Use CRLF line endings for `.cs` and `.csproj` files

## Project Overview

TianWen is a .NET library for astronomical device management, image processing, and astrometry. It supports cameras, mounts, focusers, filter wheels, and guiders via ASCOM, INDI, ZWO, and Meade protocols. Published as a NuGet package (`TianWen.Lib`).

Repository: https://github.com/SharpAstro/tianwen

## Solution Structure

```
src/
├── TianWen.sln                    # Solution file
├── Directory.Packages.props       # Centralized package version management
├── .editorconfig                  # Code style rules
├── NuGet.config                   # Package sources
├── TianWen.Lib/                   # Core library (net10.0)
├── TianWen.Lib.Tests/             # Unit tests (xUnit v3)
├── TianWen.Lib.CLI/               # CLI application (AOT-published)
├── TianWen.Lib.Hosting/           # IHostedService extensions
├── TianWen.UI.Abstractions/       # Viewer state, document model, shared types
├── TianWen.UI.OpenGL/             # OpenGL renderer (Silk.NET)
├── TianWen.UI.FitsViewer/         # FITS viewer application
└── TianWen.UI.Benchmarks/         # BenchmarkDotNet performance tests
```

## Build & Test Commands

```bash
# All commands run from src/
dotnet restore
dotnet build
dotnet build -c Release
dotnet test
dotnet test -c Release
```

## Target Framework

- **.NET 10.0** (`net10.0`) across all projects
- Nullable reference types enabled globally
- CLI project has `PublishAot` enabled

## Key Technologies

| Area | Technology |
|------|-----------|
| DI | Microsoft.Extensions.DependencyInjection |
| Logging | Microsoft.Extensions.Logging |
| CLI | System.CommandLine v2 + Pastel |
| Testing | xUnit v3 + Shouldly + NSubstitute |
| Imaging | Magick.NET, FITS.Lib |
| UI / OpenGL | Silk.NET (GLFW) |
| Astronomy | ASCOM, WWA.Core, ZWOptical.SDK |
| Compression | SharpCompress |

## Testing Conventions

- Framework: **xUnit v3** with `[Fact]` and `[Theory]`/`[InlineData]`
- Assertions: **Shouldly** (`value.ShouldBe(expected)`, `Should.Throw<T>(...)`)
- Mocking: **NSubstitute** (with analyzer for correctness)
- Logging: `Meziantou.Extensions.Logging.Xunit.v3` for test output
- Test data: embedded resources in `Data/` subdirectories
- **Avoid code duplication in tests**: extract shared setup into helper classes (e.g.,
  `SessionTestHelper` for Session test context creation). Minor duplication in simple tests
  is acceptable, but complex setup should be shared across test classes.
- **Never use reflection in tests**: do not access private fields/methods via `System.Reflection`.
  If test code needs access to internal state, add an `internal` property or method to the
  production code — the test project already has `InternalsVisibleTo` access.

## Coding Style

Enforced via `.editorconfig`:

- **4 spaces** indentation, **CRLF** line endings
- **Block-scoped namespaces** (`namespace Foo { }`, not file-scoped)
- **Primary constructors** preferred for DI
- **No implicit `new(...)`** — always use explicit type: `new SomeType()`
- Expression-bodied: properties and accessors yes, methods and constructors no
- Interfaces prefixed with `I` (e.g., `IExternal`, `ICombinedDeviceManager`)
- PascalCase for types, properties, methods; `_camelCase` for private fields

## Architecture Patterns

### Dependency Injection

Services are registered via extension methods in `TianWen.Lib.Extensions`:

```csharp
builder.Services
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddAscom()
    .AddDevices();
```

### Device Management

Devices are URI-addressed and managed through:
- `DeviceBase` — abstract base with URI identity
- `IDeviceSource<T>` — plugin interface for driver backends
- `ICombinedDeviceManager` — coordinates multiple device sources
- `IDeviceUriRegistry` — maps URIs to device instances

#### Device URI Query Parameters

Each `DeviceBase` subclass reads specific query keys from its URI (`?key=value`).
Keys are defined in `DeviceQueryKey` enum (wire strings in parentheses).

| DeviceBase subclass | Query keys | Notes |
|---|---|---|
| `DeviceBase` (base) | `port`, `baud` | Used in `ConnectSerialDevice()` |
| `AscomDevice` | *(none)* | Identity in URI path only |
| `AlpacaDevice` | `host`, `port`, `deviceNumber` | HTTP endpoint + device index |
| `ZWODevice` | `filter{n}`, `offset{n}` | Dynamic keys (not enum), on filter wheel URIs |
| `FakeDevice` | `port`, `latitude`, `longitude` | `port` selects mount protocol: `LX200`, `SGP`, or default |
| `MeadeDevice` | `port`, `baud` | Inherited from `DeviceBase` |
| `IOptronDevice` | `port`, `latitude`, `longitude` | `ConnectSerialDevice` enforces 28800 baud; lat/lon optional seed |
| `BuiltInGuiderDevice` | `pulseGuideSource` | `Auto` (default), `Camera`, or `Mount` |
| `OpenPHD2GuiderDevice` | *(none)* | Host/instance/profile encoded in URI path segments |
| `Profile` | `data` | Base64url-encoded `ProfileData` JSON blob |
| `NoneDevice` | *(none)* | Sentinel, fixed URI |
| Any camera device | `gain`, `offset` | Cross-cutting; resolved by `ObservationScheduler` at session creation |

### Key Abstractions

- `IExternal` — file I/O, serial ports, time management, logging
- `ISessionFactory` — creates observation sessions with bound devices
- `IPlateSolverFactory` — plate solving (ASTAP, astrometry.net)

### Session (Most Critical Class)

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator for semi-automated
image capturing. It drives the entire observation workflow and is the most vital piece of the library.

**RunAsync workflow** (in order):
1. `InitialisationAsync` — connect devices, validate setup
2. `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` — timing
3. `CoolCamerasToSetpointAsync` — ramp CCD temperature down
4. `InitialRoughFocusAsync` — slew near zenith, take short exposures, verify ≥15 stars detected
5. `AutoFocusAllTelescopesAsync` → `AutoFocusAsync` — V-curve scan, hyperbola fit, store baseline HFD
6. `CalibrateGuiderAsync` — calibrate guiding
7. `ObservationLoopAsync` → `ImagingLoopAsync` — main imaging loop with guiding, dithering,
   focus drift detection, meridian flip handling

**ObservationLoopAsync** — per-target sequencing:
```
while (ActiveObservation && time < sessionEnd)
│
├─ EnsureTracking
├─ StopGuiding
├─ BeginSlewToTargetAsync (with rising-target wait and spare-target fallback)
│   ├─ Target below horizon? → wait or skip to spare/next
│   └─ Slew succeeded? → WaitForSlewComplete, recompute hourAngleAtSlewTime
├─ StartGuidingLoop
├─ (Optional) Refocus on new target (AlwaysRefocusOnNewTarget)
├─ ImagingLoopAsync ──► returns AdvanceToNextObservation / RepeatCurrentObservation
└─ AdvanceObservation
```

**ImagingLoopAsync** — per-tick frame capture with altitude-ladder filter sequencing:
```
PeriodicTimer(tickDuration) loop:
│
├─ Check guiding (restart if lost)
├─ For each idle camera:
│   ├─ Filter batch check (frameCounter >= Count? → advance cursor)
│   ├─ SwitchFilterIfNeededAsync (move wheel + apply focus offset delta)
│   ├─ Set FITS metadata (FocusPosition, Filter)
│   └─ StartExposureAsync(currentEntry.SubExposure)
├─ Write queued FITS files
├─ WaitForNextTick
├─ Fetch completed images → QueuedImageWrite(ActualSubExposure)
├─ Duration check: tickCount >= maxTicks? → advance
├─ Altitude check: target below min? → advance
├─ Pier side check: IsOnSamePierSideAsync
│   ├─ AcrossMeridian=true → PerformMeridianFlipAsync
│   │   ├─ Smart exposure handling (wait <30s / abort >30s)
│   │   ├─ Re-slew with RA offset (retry loop, verify HA flipped)
│   │   ├─ Restart guiding (guider auto-reverses DEC)
│   │   ├─ Reverse filter ladder direction (filterAscending = false)
│   │   └─ continue imaging with new hourAngleAtSlewTime
│   └─ AcrossMeridian=false → break (finished target)
├─ Focus drift check: HFD ratio > threshold? → auto-refocus
└─ Dither check: at filter batch boundaries
```

**Key methods** (25 total):
- `RunAsync` — top-level entry point
- `InitialisationAsync` — device setup
- `InitialRoughFocusAsync` — rough focus via plate solve readiness; moves to focus filter first
- `AutoFocusAllTelescopesAsync` / `AutoFocusAsync` — V-curve auto-focus; respects `FocusFilterStrategy`
- `CalibrateGuiderAsync` — guider calibration
- `ObservationLoopAsync` — observation sequencing (slew, guide, image)
- `ImagingLoopAsync` — per-observation imaging with filter ladder, drift detection, dithering
- `SwitchFilterIfNeededAsync` — move filter wheel + apply focuser offset delta
- `AdvanceFilterCursor` — traverse filter ladder forward (ascending) or backward (descending), clamped
- `PerformMeridianFlipAsync` — meridian flip: re-slew, verify HA, restart guiding, reverse filter ladder
- `CoolCamerasToSetpointAsync` / `CoolCamerasToSensorTempAsync` / `CoolCamerasToAmbientAsync`
- `MoveTelescopeCoversToStateAsync` — cover management
- `WriteImageToFitsFileAsync` — FITS output
- `GuiderFocusLoopAsync` — guider plate solve loop
- `Finalise` — warmup cameras, park mount, disconnect
- `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` / `SessionEndTimeAsync` — timing
- `CatchAsync` — error handling wrappers

### Filter Wheel & Altitude-Ladder Scheduling

The imaging loop sequences filters using an **altitude ladder** — an ordered plan from
narrowband (low-altitude tolerant) to luminance (needs best seeing at peak altitude):

```
FilterPlan: [Ha, SII, OIII, R, G, B, L]
             ↑ narrowband          ↑ RGB    ↑ Luminance (top)
             low-alt tolerant      mid-alt   peak seeing
```

**Traversal**: forward (index 0→N) while ascending (HA < 0, east of meridian),
reversed (N→0) after meridian flip. Clamped at ends — no wrapping. This naturally
puts narrowband at the edges of the night and luminance/RGB around transit.

**Key types**:
- `FilterExposure(FilterPosition, SubExposure, Count)` — one filter slot in the plan
- `FilterPlanBuilder` — builds the altitude ladder; `BuildAutoFilterPlan` orders
  narrowband → RGB → Luminance; `GetReferenceFilter` picks the focus filter
- `FocusFilterStrategy` (`Auto`, `UseLuminance`, `UseScheduledFilter`) — controls
  which filter is used during auto-focus, configured via `SessionConfiguration`
- `OpticalDesign` enum + `NeedsFocusAdjustmentPerFilter` extension — pure mirror
  designs (Newtonian, Cassegrain, RASA, Astrograph) are CA-free; refractive designs
  (Refractor, SCT, NewtonianCassegrain) need per-filter focus adjustment

**Focus strategy** (`FocusFilterStrategy.Auto`):
- Mirror/astrograph: focus on luminance, no offset needed on filter change
- Refractive + non-zero offsets: focus on luminance, apply `InstalledFilter.Position`
  delta via `BacklashCompensation` on each filter switch
- Refractive + no offsets: focus directly on the scheduled filter (longer exposure
  or higher gain for narrowband star detection)

**OTA configuration**: `OTA` and `OTAData` carry `Aperture` (mm) and `OpticalDesign`
for f/ratio computation and focus strategy decisions.

**Test coverage** (32 Session tests across 5 classes + 29 filter/optics tests, as of March 2026):
- `SessionAutoFocusTests` (6): `AutoFocusAsync`, `AutoFocusAllTelescopesAsync`
- `SessionCoolingTests` (6): `CoolCamerasToSetpointAsync`, `CoolCamerasToSensorTempAsync`
- `SessionImagingTests` (3): `ImagingLoopAsync` (utilization, altitude exit, single-target loop)
- `SessionObservationLoopTests` (5): `ObservationLoopAsync` (below-horizon skip, multi-target,
  altitude drop, refocus-on-switch, meridian flip)
- `SessionLifecycleTests` (12): `RunAsync`, `InitialisationAsync`, `CalibrateGuiderAsync`,
  `Finalise`, `CoolCamerasToAmbientAsync`, `SessionEndTimeAsync`, twilight wait,
  `GuiderFocusLoopAsync`, `InitialRoughFocusAsync`, `MoveTelescopeCoversToStateAsync` (3 tests)
- `FilterPlanBuilderTests` (21): altitude ladder ordering, narrowband/broadband classification,
  reference filter selection per optical design, single-plan fallback, frames-per-filter
- `OpticalDesignTests` (8): `NeedsFocusAdjustmentPerFilter` truth table for all designs
- **Untested branch paths**: dithering logic, focus drift mid-session refocus trigger,
  spare target fallback, guider failure/restart during imaging, filter switching during
  imaging loop (needs `FakeFilterWheelDriver`-equipped session test).

### FITS Viewer / GPU Stretch

- Stretch (MTF) is computed entirely in the GLSL fragment shader — no CPU reprocessing on parameter changes
- `FitsDocument` debayers once at load time; the debayered image is the permanent base
- Per-channel and luminance stretch stats are cached at load time
- `ComputeStretchUniforms()` produces shader uniforms from cached stats
- Three stretch modes: per-channel (linked/unlinked), luma (preserves chrominance ratios)
- HDR compression via Hermite soft-knee, also in the shader
- Background estimation via `Image.ScanBackgroundRegion()`: finds the darkest patch
  (skipping 5% border to avoid stacking artifacts), uses median (not mean) to reject hot pixels,
  parallelized with `Parallel.For`. Result is pedestal-subtracted and fed through `Image.StretchValue`
  to compute the post-stretch background level for the boost curve's symmetry point.
  After star detection, re-scanned with 48×48 squares and star mask for cleaner boost.
- `Image.StretchValue()` is the single source of truth for the stretch pipeline
  (normalize → subtract pedestal → rescale → MTF), used by CPU stretch, background computation, and tests
- WCS coordinate grid overlay rendered in the fragment shader with per-pixel TAN deprojection
- Grid labels placed at viewport edges where RA/Dec lines cross, with corner exclusion zones

### Star Detection

- `Image.FindStarsAsync()` detects stars using histogram-based background estimation,
  iterative detection level lowering, and per-star HFD/FWHM/SNR analysis
- Parallelized via interleaved chunk processing: even chunks first, then odd, to avoid
  locking the `BitMatrix` star mask used for deduplication
- `ChunkSize` is decoupled from `MaxScaledRadius` — chunk size stays fixed at
  `2 * (HfdFactor * BoxRadius + 1)` for stable parallelization, while `StarMasks`
  covers the full HFD range up to `HfdFactor * BoxRadius * 2`
- SNR calculation is scale-invariant: uses `aduScale = MaxValue > 1 ? 1 : ushort.MaxValue`
  so detection works on both raw ADU and [0,1]-normalized images
- The `BitMatrix` star mask built during detection is stored on `StarList.StarMask`
  and reused by `ScanBackgroundRegion` to exclude star pixels from background estimation
- `Image.BuildStarMask(stars)` can reconstruct a mask from a `StarList` standalone
- `FitsDocument.DetectStarsAsync()` runs as a background task after loading, cancellable
  on image switch via `CancellationTokenSource.CreateLinkedTokenSource`
- Star overlay rendered as green HFD-sized circles; boost and star overlay gated on
  `Stars is { Count: > 0 }`

### WCS (World Coordinate System)

- `WCS.FromHeader()` reads WCS from FITS headers with three-tier fallback:
  1. CD matrix (CD1_1/CD1_2/CD2_1/CD2_2) — full plate solution
  2. CDELT + CROTA2 — older convention
  3. PIXSCALE/SCALE + ANGLE/POSANGLE — approximate from mount/camera metadata
- Center coordinates fallback: CRVAL1/2 → RA/DEC → OBJCTRA/OBJCTDEC (HMS/DMS strings)
- `WCS.FromAstapIniFile()` reads companion ASTAP `.ini` plate solution files as fallback
- `IsApproximate` flag distinguishes tier-3 (approximate) from real plate solutions
- ANGLE→CROTA2 conversion accounts for ROWORDER and FLIPPED headers
- `header.GetDoubleValue()` must use explicit `double.NaN` default (returns 0.0 for missing keys otherwise)

### Concurrency

- `SemaphoreSlim` / `DotNext.Threading` for resource locking
- `ConcurrentDictionary` for thread-safe caches
- `CancellationToken` propagated throughout
- `ValueTask` for allocation-free async paths
- **Never use `.GetAwaiter().GetResult()`** — always make the method `async` and `await` instead

### Histogram Overlay

- `HistogramDisplay` (in `TianWen.Lib.Imaging`) handles CPU-side histogram computation: downsamples 65K raw bins to 512 display bins, with stretch-aware remapping via `Image.StretchValue()`
- GPU renders the histogram as a single quad with a GLSL shader sampling 1D R32F textures
- Log scale default: ON for unstretched, OFF for stretched; toggled via Shift+V
- `MemoryMarshal.CreateReadOnlySpan` provides zero-copy row access on the internal `float[,]` arrays

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, and `Span<T>` over allocating new arrays. Use `ReadOnlySpan<T>` for read-only views.
- **Immutability with controlled mutability**: make types immutable by default. When mutation is needed (e.g., `HistogramDisplay.Recompute()`), keep mutable state private and expose only read-only views.
- **Correct abstraction levels**: pure math and data processing in `TianWen.Lib`, UI state and document model in `TianWen.UI.Abstractions`, GL-specific rendering in `TianWen.UI.OpenGL`. Never put OpenGL calls in Lib or Abstractions.
- **Avoid code duplication**: reuse existing methods (e.g., `Image.StretchValue()` as single source of truth for stretch) rather than reimplementing logic in multiple places.

## Package Management

Centralized in `Directory.Packages.props` — version numbers are defined there, not in individual `.csproj` files. When adding or updating packages, edit `Directory.Packages.props`.

## Namespace Structure

```
TianWen.Lib
├── Astrometry/          # Plate solving, catalogs, focus algorithms, SOFA, VSOP87
├── Connections/          # JSON-RPC, TCP, serial protocols
├── Devices/             # ASCOM, INDI, ZWO, Meade, PHD2, Fake, DAL
├── Extensions/          # DI service registration extension methods
├── Imaging/             # Image processing, star detection, HFD/FWHM
├── Sequencing/          # Observation automation
└── Stat/                # Statistical utilities
```
