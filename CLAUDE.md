# CLAUDE.md - TianWen Project Guide

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powerhsell
- Use CRLF line endings for `.cs` and `.csproj` files

## Project Overview

TianWen is a .NET library for astronomical device management, image processing, and astrometry. It supports cameras, mounts, focusers, filter wheels, and guiders via ASCOM, INDI, ZWO, Meade, and Skywatcher protocols. Published as a NuGet package (`TianWen.Lib`).

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
├── TianWen.UI.Abstractions/       # Widget system, layout, state, shared types
├── TianWen.UI.Shared/             # SDL→InputKey mapping, Vulkan FITS pipeline, VkImageRenderer
├── TianWen.UI.Gui/                # N.I.N.A.-style integrated GUI (SDL3 + Vulkan)
├── TianWen.UI.FitsViewer/         # Standalone FITS viewer application
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

# Run a specific test collection by class name pattern:
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
dotnet test TianWen.Lib.Tests --filter "Skywatcher"
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Guider|FullyQualifiedName~NeuralGuide"
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
| UI / GPU | SDL3 + Vulkan (SdlVulkan.Renderer) |
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

### Test Collections & Parallelism

Tests are grouped into `[Collection("X")]` by functional area. Tests within the same
collection run sequentially; different collections run in parallel.

**Unit test collections** (`TianWen.Lib.Tests`):

| Collection | Classes | What to re-run when... |
|---|---|---|
| `Catalog` | CatalogIndex, Catalog, CatalogUtil, CelestialObjectDB, Constellation | catalog data or cross-references change |
| `Imaging` | FindStars*, SyntheticStarDetection, Stretch*, ContrastBoost, BitMatrix, FITS/TIFF roundtrip, Statistics | star detection, stretch pipeline, image I/O change |
| `Astrometry` | CoordinateUtils, Transform, PlateSolver*, VSOP87a, AstronomicalEveningDate, TimeUtil, TimeZoneLookup, OverlayEngine | coordinate math, plate solving, WCS overlay change |
| `Guider` | ProportionalGuideController, GuiderCentroidTracker, GuideErrorTracker, GuiderDevice, NeuralGuide* | guide algorithm, neural model, calibration change |
| `Scheduling` | ObservationScheduler*, FilterPlanBuilder, Filter, OpticalDesign, Mosaic*, TonightsBest, WaitForDark, FindBestFocus | scheduling, filter plans, mosaic, focus algorithm change |
| `Device` | Device, SensorType, ObjectType, SerialConnection, ManualFilterWheel | device URI parsing, serial protocol, sensor types change |
| `Skywatcher` | SkywatcherProtocol | Skywatcher motor controller protocol change |
| `Encoding` | Base91, SessionPersistence, StepExposure, TryParseExposureInput | data encoding, persistence formats change |
| `UI` | TextInputState, SessionTab | widget state, session config UI change |

**Functional test collections** (`TianWen.Lib.Tests.Functional`):
- All `Session*Tests` share `[Collection("Session")]` — runs sequentially to avoid
  thread pool starvation from concurrent `Task.Run` + `FakeTimeProvider` timer callbacks
- `maxParallelThreads: 2` in `xunit.runner.json` limits overall parallelism
- **No wall-clock `CancellationTokenSource` timeouts** in session tests — rely on
  `[Fact(Timeout = ...)]` instead; inner timeouts cause flakes under thread pool pressure
- `SessionTestHelper` defaults to `FakeMountDriver` (no `mountPort`); pass
  `mountPort: "LX200"` or `"SkyWatcher"` only for protocol-specific tests

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
| `FakeDevice` | `port`, `latitude`, `longitude` | `port` selects mount protocol: `LX200`, `SGP`, `SkyWatcher`, or default |
| `MeadeDevice` | `port`, `baud` | Inherited from `DeviceBase` |
| `SkywatcherDevice` | `port`, `baud` | `baud` = 9600 (legacy) or 115200 (USB); WiFi if `port` is an IP address |
| `IOptronDevice` | `port`, `latitude`, `longitude` | `ConnectSerialDevice` enforces 28800 baud; lat/lon optional seed |
| `BuiltInGuiderDevice` | `pulseGuideSource`, `reverseDecAfterFlip`, `reuseCalibration`, `useNeuralGuider`, `neuralBlendFactor` | Pulse source: `Auto`/`Camera`/`Mount`; neural guider (26 inputs) with encoder-phase PEC, blend ramps in over ~2 PE cycles |
| `OpenPHD2GuiderDevice` | *(none)* | Host/instance/profile encoded in URI path segments |
| `Profile` | `data` | Base64url-encoded `ProfileData` JSON blob |
| `NoneDevice` | *(none)* | Sentinel, fixed URI |
| Any camera device | `gain`, `offset` | Cross-cutting; resolved by `ObservationScheduler` at session creation |

### Key Abstractions

- `IExternal` — file I/O, serial ports, time management, logging
- `ISessionFactory` — creates observation sessions with bound devices
- `IPlateSolverFactory` — plate solving (ASTAP, astrometry.net)

### Skywatcher Motor Controller Driver

Skywatcher mounts (EQ6, HEQ5, AzEQ6, EQ6-R, AzGTi, StarAdventurer, etc.) use a distinct
motor controller protocol over serial and WiFi (UDP port 11880).

**Wire format**: ASCII, `:<CMD><AXIS>[DATA]\r` → `=<DATA>\r` (ok) or `!<ERR>\r` (error).
Data is hex ASCII, little-endian byte order. 24-bit positions offset by 0x800000.

**Key types** (in `TianWen.Lib/Devices/Skywatcher/`):
- `SkywatcherProtocol` — pure static helpers: LE hex encode/decode, T1 speed formula,
  firmware parsing, capability flags, mount model detection, gear ratio workarounds
- `SkywatcherDevice` — URI-addressed device record; `baud` query param (9600 legacy / 115200 USB);
  WiFi transport when `port` is an IP address
- `SkywatcherMountDriverBase<T>` — core driver implementing `IMountDriver`: two-axis GEM
  with semaphore-locked async serial I/O, tracking/slew/sync/pulse guide/park/camera snap
- `SkywatcherUdpConnection` — `ISerialConnection` wrapping `UdpClient` for WiFi (port 11880)
- `SkywatcherDeviceSource` — serial discovery (115200 then 9600 probe) + WiFi UDP broadcast

**Baud rates**: legacy mounts use external serial adapters at **9600 baud**, newer mounts
with integrated USB (e.g. EQ6-R) use **115200 baud**. Discovery tries 115200 first.

**Firmware & capabilities**: `SkywatcherFirmwareInfo` (model + version from `:e`),
`SkywatcherCapabilities` (flag nibbles from `:q`). Advanced 32-bit commands gated by
`_supportsAdvancedCommands` (MVP uses 24-bit legacy only).

**Fake driver** (`FakeSkywatcherSerialDevice`): full motor simulator responding to all
protocol commands with timer-based tracking/slew simulation. Activated via `port=SkyWatcher`
on `FakeDevice`.

**Test coverage** (78 tests):
- `SkywatcherProtocolTests` (61): hex LE roundtrips, position encoding, speed formula,
  firmware parsing, mount models, capability flags, advanced command gating, goto adjustment,
  gear ratio override, guide speed, command building, response parsing
- `FakeSkywatcherMountDriverTests` (17): connect/disconnect, tracking on/off, slew+abort,
  sync, pulse guide (all 4 directions), camera snap, axis position, park/unpark, capabilities,
  site coordinates

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

### Mosaic Support

For extended objects that don't fit in a single FOV, `MosaicGenerator` computes a grid
of panel centers with configurable overlap and margin. Panels are ordered by RA ascending
(column-first sweep) so that eastern panels — which cross the meridian first — are imaged
first, resulting in at most one GEM flip per mosaic cycle.

**Key types**:
- `MosaicPanel(Target, Row, Column, TransitTimeHours)` — single panel in the grid
- `MosaicGenerator` — static class, no DI:
  - `GeneratePanels(centerRA, centerDec, majorAxisArcmin, ...)` — grid from coordinates
  - `GeneratePanels(objectDb, catalogIndex, fovW, fovH, ...)` — grid from catalog lookup
  - `ComputeFieldOfView(focalLengthMm, pixelSizeUm, ...)` — FOV from OTA params
  - `ComputeRotatedEllipseBBox(majorDeg, minorDeg, pa)` — axis-aligned bbox of rotated ellipse
- `ProposedObservation.MosaicGroupId` — optional `Guid?`, panels sharing the same ID
  are scheduled as a contiguous block
- `SessionConfiguration.MosaicOverlap` / `MosaicMargin` — configurable (defaults 0.2 / 0.1)

**Panel generation math**: rotated ellipse bounding box → margin → step sizes with
cos(Dec) RA correction → grid dimensions → column-first panel centers. Single-panel
shortcut when the bbox fits within one FOV.

**Scheduling**: `ObservationScheduler.Schedule` groups proposals by `MosaicGroupId`,
allocates a contiguous time block, orders panels by RA ascending within the group,
and computes `AcrossMeridian` per panel using individual transit times.

**Output**: each panel is a separate `Target` named `{ObjectName}_R{row}C{col}` with
the parent `CatalogIndex` propagated, producing per-panel subdirectories for FITS output.

**Test coverage** (32 Session tests across 5 classes + 29 filter/optics tests + 29 mosaic tests, as of March 2026):
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
- `MosaicGeneratorTests` (22): panel generation, FOV computation, rotated ellipse bbox,
  single-panel shortcut, RA ordering, overlap/margin, near-pole handling, RA wrapping
- `MosaicSchedulingTests` (7): contiguous allocation, RA-ascending ordering,
  mixed mosaic+individual scheduling, per-panel AcrossMeridian, end-to-end generate+schedule
- `SkywatcherProtocolTests` (61): hex LE roundtrips, position encoding, speed formula,
  firmware parsing, mount models, capability flags, advanced command gating, goto adjustment,
  gear ratio override, guide speed, command building, response parsing
- `FakeSkywatcherMountDriverTests` (17): connect/disconnect, tracking, slew+abort, sync,
  pulse guide, camera snap, axis position, park/unpark, capabilities, site coordinates
- **Untested branch paths**: spare target fallback, guider failure/restart during imaging,
  filter switching during imaging loop (needs `FakeFilterWheelDriver`-equipped session test).

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

### GUI Widget System

The GUI uses a unified widget system built on `PixelWidgetBase<TSurface>` in
`TianWen.UI.Abstractions`. This is renderer-agnostic (works with VkRenderer and
RgbaImageRenderer).

**Core types** (all in `TianWen.UI.Abstractions`):
- `PixelRect` — float layout rectangle with `Contains`, `Inset`
- `PixelLayout` + `PixelDockStyle` — dock-based layout engine (Top/Bottom/Left/Right/Fill)
- `PixelWidgetBase<TSurface>` — base class with `RegisterClickable`, `HitTest`,
  `HitTestAndDispatch`, `HandleKeyDown`, `HandleMouseWheel`, `RenderButton`,
  `RenderTextInput`, `FillRect`, `DrawText`
- `ClickableRegion(X, Y, W, H, HitResult, Action? OnClick)` — registered during render
- `HitResult` — discriminated union: `TextInputHit`, `ButtonHit`, `ListItemHit`,
  `SlotHit`, `SliderHit`
- `IPixelWidget` — interface for hit testing, keyboard, mouse wheel, text input discovery
- `InputKey` + `InputModifier` — renderer-agnostic key codes and modifier flags
- `BackgroundTaskTracker` — tracks async operations, checks completions per frame, logs errors
- `TextInputState` — single-line text input with `OnCommit` (async), `OnCancel`,
  `OnTextChanged`, `OnKeyOverride` callbacks
- `SignalBus` — thread-safe typed deferred signal bus; widgets `PostSignal<T>()` during
  event handling, hosts `Subscribe<T>()` at startup, `ProcessPending()` delivers once per frame
- `DropdownMenuState` — generic popup dropdown menu with keyboard navigation, custom entry
  support, and full-screen backdrop dismiss

**Signal bus pattern** (replaces callback properties):
1. Widget calls `PostSignal(new SomeSignal(...))` during click/key handling — just enqueues
2. `OnPostFrame` calls `bus.ProcessPending(tracker)` — delivers all pending signals
3. Sync handlers run inline; async handlers submitted to `BackgroundTaskTracker`
4. Signals are `readonly record struct` value types defined in `GuiSignals.cs`
5. Built-in signals in DIR.Lib: `ActivateTextInputSignal`, `DeactivateTextInputSignal`,
   `RequestExitSignal`, `RequestRedrawSignal`
6. App signals in Abstractions: `DiscoverDevicesSignal`, `AddOtaSignal`, `EditSiteSignal`,
   `CreateProfileSignal`, `AssignDeviceSignal`, `UpdateProfileSignal`, `BuildScheduleSignal`,
   `ToggleFullscreenSignal`, `PlateSolveSignal`

**Click handling pattern**:
1. Widgets call `RegisterClickable(x, y, w, h, hitResult, onClick)` during render
2. Self-contained actions (offset stepping, list selection) pass an `OnClick` delegate
3. `HitTestAndDispatch(px, py)` invokes `OnClick` and returns the `HitResult`
4. Cross-component actions use `PostSignal` (text input focus, profile save, discovery)

**Keyboard handling pattern**:
1. `Program.cs` maps SDL scancodes to `InputKey` via `SdlInputMapping` (in `TianWen.UI.Shared`)
2. `GuiEventHandlers.HandleKeyDown` routes to active text input first
3. If not consumed, `Program.cs` routes to `ActiveTab.HandleKeyDown(inputKey, modifiers)`
4. Each tab overrides `HandleKeyDown` for tab-specific shortcuts (pure state mutations)
5. Cross-component actions use `PostSignal` (delivered in `OnPostFrame`)

**Mouse wheel pattern**:
- `GuiEventHandlers.HandleMouseWheel` delegates to `ActiveTab.HandleMouseWheel(scrollY, x, y)`
- Each tab overrides to handle its own scroll zones (target list, config panel, etc.)

**Async operations**:
- All background work goes through `BackgroundTaskTracker.Run(Func<Task>, description)`
- `Program.cs` calls `bus.ProcessPending(tracker)` then `tracker.ProcessCompletions(logger)`
  each frame — async signal handlers are submitted to tracker automatically
- `TextInputState.OnCommit` is `Func<string, Task>?` — submitted to tracker on Enter
- Shutdown: `tracker.DrainAsync()` awaits all pending tasks
- Zero fire-and-forget `_ = Task.Run(...)` in the codebase

**Inheritance hierarchy**:
```
PixelWidgetBase<TSurface>   (DIR.Lib — renderer-agnostic, has Bus + PostSignal)
  └─ VkTabBase              (Gui — pins TSurface = VulkanContext)
       ├─ VkGuiRenderer     (sidebar, status bar, content dispatch)
       ├─ VkPlannerTab      (planner with autocomplete, scheduling viz)
       ├─ VkEquipmentTab    (profile/device management, filter editing)
       └─ VkSessionTab      (session config + observation list)
  └─ VkImageRenderer        (Shared — FITS viewer toolbar, file list, histogram)
```

`VkGuiRenderer` extends `VkTabBase` so the sidebar tabs and status bar participate
in the same `RegisterClickable` / `HitTestAndDispatch` system as tab content.
`HandleMouseDown` in `GuiEventHandlers` first hit-tests the chrome (sidebar/status bar),
then falls through to the active tab.

**Event handling** (`GuiEventHandlers`):
- Generic event routing only — zero tab-specific logic in the routing methods
- Constructor subscribes DI-dependent signal handlers on the `SignalBus`
- Per-field `TextInputState` callbacks wired for planner search, site editing, profile creation
- `Program.cs` is a thin event loop + composition root
- Text input focus managed via `ActivateTextInputSignal` / `DeactivateTextInputSignal`

**SDL input mapping** (`TianWen.UI.Shared/SdlInputMapping.cs`):
- C# 14 extension blocks on `Scancode` and `Keymod`
- `scancode.ToInputKey` and `keymod.ToInputModifier` — used by both GUI and FitsViewer

### Session Configuration Tab

The session tab (🎯) sits between Planner and Viewer in the sidebar. It configures
the session before launching.

**Layout**: left panel = scrollable `SessionConfiguration` form, right panel = per-OTA
camera settings + observation list with frame estimates.

**Key types** (in `TianWen.UI.Abstractions`):
- `SessionTabState` — mutable `SessionConfiguration`, per-OTA `PerOtaCameraSettings`,
  scroll offset. Detects profile changes via `NeedsReinitialization`/`InitializeFromProfile`.
- `SessionConfigGroups` — renderer-agnostic field groupings (Cooling Ramps, Guiding,
  Horizon, Focusing, Imaging, Mosaic, Conditions) with per-field label, kind, format,
  increment/decrement lambdas
- `ConfigFieldKind` — `IntStepper`, `FloatStepper`, `TimeSpanStepper`,
  `NullableTimeSpanStepper`, `BoolToggle`, `EnumCycle`
- `PerOtaCameraSettings` — per-OTA setpoint temp, gain (numeric or mode), offset.
  Profile provides defaults, session tab allows per-session override.

**Features**:
- Smart exposure stepping: 10s below 1min, 30s for 1-2min, 60s above 2min
- Default exposure from f-ratio: `5 × f²` seconds, clamped [10, 600]
- Estimated frame count per target: `windowSeconds / (subExposure + 10s overhead)`

### Filter Wheel Configuration

**Design principle**: Seed on discovery, TianWen owns the data, sync back on connect.
Filter names and focus offsets are stored as URI query params (`filter1`, `offset1`, ...)
on the filter wheel device URI in the profile. All drivers read from URI params first,
with driver-specific fallbacks per slot.

| Driver | Slot count source | Name/offset fallback |
|--------|------------------|---------------------|
| ASCOM | `Names.Length` (COM) | COM `Names[i]` / `FocusOffsets[i]` |
| ZWO | `NumberOfSlots` (hardware) | `"Filter {N}"` / `0` (seeded at discovery) |
| Alpaca | REST API `names` array | API values |
| Fake | preset count (LRGB/Narrowband/Simple by device ID, max 8) | preset values |
| Manual | always 1 | `InstalledFilter.Name` |

**`Filter` type** (`TianWen.Lib/Imaging/Filter.cs`):
- `readonly record struct Filter(string Name, string ShortName, string DisplayName, Bandpass)`
- `Name`: code name (`HydrogenAlpha`), `ShortName`: abbreviation (`Hα`), `DisplayName`: user-friendly (`H-Alpha`)
- `FromName()` uses `[GeneratedRegex]` patterns, supports unicode α/β, handles dual-band filters
- `InstalledFilter.CustomName` preserves unknown filter names (e.g. "Optolong L-Ultimate")

**Equipment tab filter editing** (`VkEquipmentTab`):
- Filter table below filter wheel slot (expand/collapse via `[+]`/`[-]` header)
- Filter name: click opens `DropdownMenuState` with `CommonFilterNames` + "Custom..." entry
- Custom entry shows inline `TextInputState` for arbitrary names
- Focus offset: `[-]` / `[+]` stepper buttons
- All edits are in-memory (`EquipmentTabState.EditingFilters`); Save/Cancel buttons appear when dirty
- Save commits via `UpdateProfileSignal` through the signal bus

**`EquipmentActions`** (`TianWen.UI.Abstractions/EquipmentActions.cs`):
- `GetFilterConfig(ProfileData, otaIndex)` — reads filters from URI params
- `SetFilterConfig(ProfileData, otaIndex, filters)` — writes filters to URI params
- `UpdateOTA(ProfileData, otaIndex, ...)` — updates OTA name/focal length/aperture/optical design
- `FilterDisplayName(InstalledFilter)` — returns `DisplayName` (custom name for unknowns)
- `CommonFilterNames` — shared list used by dropdown and CLI

### Fake Camera Sensor Presets

`FakeCameraDriver` selects sensor specs by device ID (1-based, mod 9), alternating
color/mono:

| ID | Sensor  | Resolution    | Pixel  | Type |
|----|---------|---------------|--------|------|
| 1  | IMX294C | 4144×2822     | 4.63µm | RGGB |
| 2  | IMX533M | 3008×3008     | 3.76µm | Mono |
| 3  | IMX571C | 6248×4176     | 3.76µm | RGGB |
| 4  | IMX455M | 9576×6388     | 3.76µm | Mono |
| 5  | IMX585C | 3856×2180     | 2.9µm  | RGGB |
| 6  | IMX411M | 14208×10656   | 3.76µm | Mono |
| 7  | IMX410C | 6072×4042     | 3.76µm | RGGB |
| 8  | IMX464M | 2712×1538     | 2.9µm  | Mono |
| 9  | IMX678C | 3856×2180     | 2.0µm  | RGGB |

Guide camera (`FakeGuideCam`): IMX178M (3096×2080, 2.4µm, mono).
IDs 10+ wrap (mod 9), enabling dual-rig testing with identical sensors.

### Planner Features

- **Autocomplete search**: fuzzy matching on `CreateAutoCompleteList()`, dropdown with
  arrow key navigation, `CommitSuggestion` resolves exact entry via `TryResolveCommonName`
- **Planet support**: VSOP87 ephemeris for solar system object positions and altitude
  curves (`ComputeFineAltitudeProfile` uses `VSOP87a.Reduce` per time step)
- **Pin/unpin targets**: `[+]`/`[-]` buttons, sorted by peak altitude time, separator
  between pinned and unpinned sections
- **Scheduling visualization**: colored viable window fills between altitude curve and
  min-altitude line, bounded by draggable handoff sliders at curve intersections
- **Handoff sliders**: draggable vertical lines between adjacent pinned targets,
  monotonically increasing with 15min minimum gaps, HH:mm labels
- **Conflict detection**: yellow warning when peak times < 1h apart or allocated
  window < 1.5h
- **Cross-index aliases**: details panel shows all common names + catalog designations
- `CelestialObject.DisplayName` picks longest common name with alphabetical tiebreak

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

### Image Pipeline & Buffer Lifecycle

Camera → Channel → ChannelBuffer → Image → Consumer → Release → Camera recycles.

**Types**:
- `float[,]` — raw pixel data (H×W). The actual memory.
- `Channel` (`readonly record struct`) — typed view: `(float[,] Data, Filter, MinValue, MaxValue, Index)`. Returned by `ICameraDriver.ImageData`. Zero overhead.
- `ChannelBuffer` (`internal sealed class`) — ref-counted owner. Born refcount=1. `GetImageAsync` transfers to Image (single owner). `image.Release()` → refcount=0 → `onRelease` → camera's `_freeBuffers.Add()`.
- `Image` — wraps `float[][,]` + metadata. Holds optional `ChannelBuffer` refs. Call `Release()` after FITS write/processing.
- `Array2DPool<T>` — static pool for AHD debayer scratch (6 arrays). `RentScoped()` returns `Lease` (IDisposable). Disabled in tests (`Enabled=false`).

**Camera double-buffer**: `FakeCameraDriver` uses `ConcurrentBag<float[,]> _freeBuffers`. `Render(dest:)` reuses a recycled buffer. Old frame stays intact until all consumers `Release()`.

**Viewer zero-copy**: Session owns persistent `Channel[] _viewerChannels` per telescope. `DebayerIntoAsync` writes directly into them. `MiniViewer` uploads channel spans to GPU — no `AstroImageDocument` for live preview.

**Key rules**:
- Camera creates `ChannelBuffer`, `GetImageAsync` transfers ownership, consumer calls `image.Release()` when done
- Never hold an `Image` from `GetImageAsync` longer than needed — it pins the camera buffer
- `DebayerIntoAsync` for viewer output, `DebayerAsync` only for FITS viewer (file-based)
- `Array2DPool` is for scratch only — camera buffers use `ChannelBuffer`/`_freeBuffers`

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, and `Span<T>` over allocating new arrays. Use `ReadOnlySpan<T>` for read-only views.
- **Immutability with controlled mutability**: make types immutable by default. When mutation is needed (e.g., `HistogramDisplay.Recompute()`), keep mutable state private and expose only read-only views.
- **Correct abstraction levels**: pure math and data processing in `TianWen.Lib`, UI state and document model in `TianWen.UI.Abstractions`, Vulkan-specific rendering in `TianWen.UI.Shared` and `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **Avoid code duplication**: reuse existing methods (e.g., `Image.StretchValue()` as single source of truth for stretch) rather than reimplementing logic in multiple places.

## Package Management

Centralized in `Directory.Packages.props` — version numbers are defined there, not in individual `.csproj` files. When adding or updating packages, edit `Directory.Packages.props`.

## Runtime Data (AppData)

Application data is stored in `%LOCALAPPDATA%/TianWen/` (`C:\Users\<user>\AppData\Local\TianWen\`):

```
TianWen/
├── Logs/                    # Per-day log files: GUI_*.log, FitsViewer_*.log
│   └── 20260401/
├── Profiles/                # Per-profile data
│   ├── <guid>.json          # Profile configuration
│   └── NeuralGuider/        # Persisted neural guide model weights (.ngm files)
└── Planner/                 # Persisted planner state (pinned targets)
```

- **Logs**: file-based logging via `Microsoft.Extensions.Logging`, one file per app launch
- **Neural guide models**: `.ngm` binary files keyed by calibration hash, auto-loaded on guide start

## Namespace Structure

```
TianWen.Lib
├── Astrometry/          # Plate solving, catalogs, focus algorithms, SOFA, VSOP87
├── Connections/          # JSON-RPC, TCP, serial protocols
├── Devices/             # ASCOM, INDI, ZWO, Meade, Skywatcher, PHD2, Fake, DAL
├── Extensions/          # DI service registration extension methods
├── Imaging/             # Image processing, star detection, HFD/FWHM
├── Sequencing/          # Observation automation
└── Stat/                # Statistical utilities
```
