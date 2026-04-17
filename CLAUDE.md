# CLAUDE.md - TianWen Project Guide

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powerhsell
- Use CRLF line endings for `.cs` and `.csproj` files
- **Exit codes 127 and 13x from GUI / CLI / Server processes mean the .NET process crashed**, not
  "command not found" or "shell killed it". Always read the stderr log (e.g. `gui-stderr.log`) for
  the actual .NET exception + stack trace before drawing conclusions from the exit code.

## Custom Skills (Slash Commands)

Available in `.claude/commands/`:

| Command | Purpose |
|---------|---------|
| `/release-lib <name>` | Release a SharpAstro sibling library to NuGet with full dependency chain |
| `/sibling-status` | Git status + version across all SharpAstro repos |
| `/check-ci` | GitHub Actions CI status across all repos |
| `/bump-version <X.Y>` | Bump TianWen version in all 4 required locations |
| `/run-gui` | Build and launch the GUI with stderr redirect |
| `/test-filter <pattern>` | Run tests matching a name pattern |
| `/tick-todo <text>` | Mark a TODO item done and update CLAUDE.md, PLAN files, and memory |

## Project Overview

TianWen is a .NET library for astronomical device management, image processing, and astrometry. It supports cameras, mounts, focusers, filter wheels, and guiders via ASCOM, INDI, ZWO, QHYCCD, Meade, and Skywatcher protocols. Published as a NuGet package (`TianWen.Lib`).

Repository: https://github.com/SharpAstro/tianwen

## Solution Structure

```
src/
├── TianWen.slnx                   # Solution file (XML format)
├── Directory.Build.props          # Auto-detect sibling repos (ProjectReference vs PackageReference)
├── Directory.Packages.props       # Centralized package version management
├── .editorconfig                  # Code style rules
├── NuGet.config                   # Package sources
├── TianWen.Lib/                   # Core library (net10.0)
├── TianWen.Lib.Tests/             # Unit tests (xUnit v3)
├── TianWen.Lib.CLI/               # CLI application (AOT-published)
├── TianWen.Lib.Hosting/           # ASP.NET Core Minimal API — REST + WebSocket endpoints
├── TianWen.Lib.Server/            # Headless server executable (tianwen-server, AOT-published)
├── TianWen.UI.Abstractions/       # Widget system, layout, state, shared types
├── TianWen.UI.Shared/             # SDL→InputKey mapping, Vulkan FITS pipeline, VkSkyMapPipeline, VkImageRenderer
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
- CLI, Server, and FitsViewer projects have `PublishAot` enabled

## SharpAstro Sibling Libraries

TianWen depends on several in-house libraries published to nuget.org under the **SharpAstro**
org. Their source repos live as siblings under the same parent directory (`../`). Note that
csproj layout varies — not every sibling uses the `src/<Lib>/<Lib>.csproj` convention:

| Package | Source Repo | csproj path | Auto-detect | Purpose |
|---------|-------------|-------------|:-----------:|---------|
| `DIR.Lib` | `../DIR.Lib` | `src/DIR.Lib/DIR.Lib.csproj` | ✅ | SignalBus, BackgroundTaskTracker, PixelWidgetBase, InputEvent |
| `SdlVulkan.Renderer` | `../SdlVulkan.Renderer` | `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` | ✅ | SDL3 + Vulkan rendering, font atlas, GLSL pipeline |
| `Console.Lib` | `../Console.Lib` | `src/Console.Lib/Console.Lib.csproj` | ✅ | RgbaImageRenderer, MarkdownWidget, Sixel, TUI widgets |
| `FITS.Lib` | `../FITS.Lib` | `CSharpFITS/CSharpFITS.csproj` (package name is `FITS.Lib`, not CSharpFITS) | ❌ | FITS file reading/writing |
| `FC.SDK` | `../FC.SDK` | — | ❌ | Canon DSLR PTP/USB/WiFi SDK |
| `ZWOptical.SDK` | `../zwo-sdk-nuget` | `ZWOptical.SDK.csproj` (at repo root, not under `src/`) | ❌ | ZWO ASI camera / EAF / EFW native SDK wrappers |
| `QHYCCD.SDK` | `../QHYCCD.SDK` | `QHYCCD.SDK.csproj` (at repo root, not under `src/`) | ❌ | QHY camera / CFW / QFOC native SDK wrappers |
| `SharpAstro.Fonts` | `../Fonts.Lib` | `src/SharpAstro.Fonts/SharpAstro.Fonts.csproj` | transitive | Consumed by DIR.Lib (managed font loader/rasterizer) |
| `TianWen.DAL` | `../TianWen.DAL` | — | ❌ | Data access layer types (e.g., `GuideDirection` enum) |

**Local sibling auto-detection** (`Directory.Build.props`): For `DIR.Lib`, `Console.Lib`, and
`SdlVulkan.Renderer`, the build automatically switches between ProjectReference (when all
three sibling working copies exist at `../../<repo>/src/<lib>/<lib>.csproj`) and PackageReference
(when any is missing). This lets developers iterate in-tree without publishing NuGet packages.

- A single property `UseLocalSiblings` is set to `true` when all three sibling `.csproj` files
  exist, empty otherwise. Each `.csproj` conditions its ItemGroups on this property.
- Override: `dotnet build -p:UseLocalSiblings=false`
- **CI** does not have sibling repos checked out, so it always falls through to PackageReference
  with versions from `Directory.Packages.props`.
- `FITS.Lib`, `FC.SDK`, `ZWOptical.SDK`, `QHYCCD.SDK`, and `TianWen.DAL` are not yet wired up —
  still consumed as PackageReference only. For cross-repo development on those, use local nupkg
  feeds with bumped versions (see `feedback_local_nuget_dev.md`).
- **`Fonts.Lib` (published as `SharpAstro.Fonts`) is a transitive dependency via DIR.Lib.**
  TianWen has no direct reference to it, but DIR.Lib uses the same local-sibling switch
  (`UseLocalFontsLib`) to pick between `../../Fonts.Lib/src/SharpAstro.Fonts/...` and the
  published package. When checking upstream publish status before pushing TianWen, include
  Fonts.Lib — an unpublished Fonts.Lib commit only matters if DIR.Lib also needs an
  unpublished commit that depends on it (i.e. multi-repo update in flight).

For libraries without sibling auto-detection, changes require publishing a new NuGet version first.
For local cross-repo development on those, use local nupkg feeds with bumped versions.

## Key Technologies

| Area | Technology |
|------|-----------|
| DI | Microsoft.Extensions.DependencyInjection |
| Logging | Microsoft.Extensions.Logging |
| CLI | System.CommandLine v2 + Pastel |
| Testing | xUnit v3 + Shouldly + NSubstitute |
| Imaging | Magick.NET, FITS.Lib |
| UI / GPU | SDL3 + Vulkan (SdlVulkan.Renderer) |
| Hosting | ASP.NET Core Minimal API, StbImageWriteSharp (JPEG) |
| Astronomy | ASCOM, ZWOptical.SDK, QHYCCD.SDK, IAU SOFA (C# port) |
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
collection run sequentially; different collections run in parallel. Grep `[Collection(`
in test files to see current groupings.

**Functional test collections** (`TianWen.Lib.Tests.Functional`):
- All `Session*Tests` share `[Collection("Session")]` — runs sequentially to avoid
  thread pool starvation from concurrent `Task.Run` + `FakeTimeProvider` timer callbacks
- `maxParallelThreads: 4` in `xunit.runner.json` limits overall parallelism
- **No wall-clock `CancellationTokenSource` timeouts** in session tests — rely on
  `[Fact(Timeout = ...)]` instead; inner timeouts cause flakes under thread pool pressure
- `SessionTestHelper` defaults to `FakeMountDriver` (no `mountPort`); pass
  `mountPort: "LX200"` or `"SkyWatcher"` only for protocol-specific tests
- **Cooperative time pump pattern** for tests that run session loops via `Task.Run`:
  ```csharp
  ctx.External.ExternalTimePump = true;
  var loopTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(...));

  var pumpIncrement = TimeSpan.FromSeconds(5);
  var maxFakeTime = TimeSpan.FromHours(4);
  var pumped = TimeSpan.Zero;
  while (pumped < maxFakeTime && !loopTask.IsCompleted && !ct.IsCancellationRequested)
  {
      ctx.External.Advance(pumpIncrement);
      pumped += pumpIncrement;
      await Task.Delay(1, ct);
  }
  ```
  **Never** use `SleepAsync(subExposure)` in a pump loop — it advances fake time even when
  the `Task.Run` hasn't been scheduled yet, causing targets to "set" before imaging starts.
  `Advance` fires timers synchronously; `Task.Delay(1)` yields to the thread pool.

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

### Logger, TimeProvider, and SleepAsync

- `ILogger` and `ITimeProvider` are resolved from `IServiceProvider` (not from `IExternal`).
- `DeviceDriverBase` resolves both in its constructor via `serviceProvider.GetRequiredService<>()`.
- Non-driver classes (e.g., `AppSignalHandler`) resolve from SP in their own constructors.
- **`ITimeProvider.SleepAsync`** must be used instead of `Task.Delay(duration, timeProvider, ct)`
  in any code. `FakeTimeProvider`'s `SleepAsync` auto-advances fake time; `Task.Delay` with
  `FakeTimeProvider` hangs waiting for external advancement. All code should be testable.
- Static helpers (`BacklashCompensation`, `GuiderCalibration`) accept `ITimeProvider` for `SleepAsync`.
- `LoggerCatchExtensions` (in `TianWen.Lib.Extensions`) provides `ILogger.Catch/CatchAsync`.

### Device Management

Devices are URI-addressed and managed through:
- `DeviceBase` — abstract base with URI identity
- `IDeviceSource<T>` — plugin interface for driver backends
- `ICombinedDeviceManager` — coordinates multiple device sources
- `IDeviceUriRegistry` — maps URIs to device instances

Each `DeviceBase` subclass reads specific query keys from its URI (`?key=value`).
Keys are defined in `DeviceQueryKey` enum. See each device class's XML doc comments
for supported query parameters and their semantics.

### Key Abstractions

- `IExternal` — file I/O, serial ports
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

**ObservationLoopAsync** sequences targets: EnsureTracking → StopGuiding → BeginSlewToTargetAsync
(with rising-target wait and spare-target fallback) → StartGuidingLoop → optional refocus →
ImagingLoopAsync → AdvanceObservation.

**ImagingLoopAsync** runs a PeriodicTimer loop per observation: check guiding, start exposures
with altitude-ladder filter sequencing, write FITS, check duration/altitude/pier-side,
perform meridian flip if needed (with smart ≤30s wait / >30s abort — see code comments),
detect focus drift via HFD regression, dither at filter batch boundaries.

### Neural Guider

`NeuralGuideModel` (`TianWen.Lib/Devices/Guider/NeuralGuideModel.cs`) is a tiny hand-rolled
MLP used for predictive guide corrections. Not a transformer, no attention — the temporal
"sequence" (t, t-1, t-2, t-3 errors plus short/medium/long rolling means and gear-phase
sin/cos) is flattened into the 26-element input vector via feature engineering.

- **Architecture**: `Input(26) → Dense(32, ReLU) → Dense(16, ReLU) → Dense(2, Tanh)`, 1,426 params
- **Inference**: `TensorPrimitives.Dot` for mat-vec multiply, zero-allocation hot path (<1µs target on Pi-class CPU)
- **Training** (`NeuralGuideTrainer`): supervised, P-controller as teacher, MSE loss, mini-batch SGD
  (lr=0.001, batch=32), manual backprop with cached pre/post-activations
- **Init**: Xavier normal via Box-Muller
- **Output**: RA/Dec pulse corrections normalized to [-1, 1], scaled to MaxPulse downstream
- **Persistence**: `.ngm` files in `%LOCALAPPDATA%/TianWen/Profiles/<profile>/NeuralGuider/`

Rationale for the small MLP: <1µs latency budget inside the guide loop + low-dimensional
input make a transformer overkill.

### Filter Wheel & Altitude-Ladder Scheduling

The imaging loop sequences filters using an **altitude ladder** — narrowband (low-altitude
tolerant) to luminance (needs best seeing at peak altitude). Forward traversal while ascending
(HA < 0), reversed after meridian flip. See `FilterPlanBuilder` and `Session.Imaging.cs` XML
doc comments for full details.

**Focus strategy** (`FocusFilterStrategy.Auto`):
- Mirror/astrograph: focus on luminance, no offset needed on filter change
- Refractive + non-zero offsets: focus on luminance, apply delta via `BacklashCompensation`
- Refractive + no offsets: focus directly on the scheduled filter

### Mosaic Support

`MosaicGenerator` computes panel grids with configurable overlap/margin. Panels ordered by
RA ascending (column-first) so eastern panels are imaged first — at most one GEM flip per
mosaic cycle. See class XML doc comments for panel generation math and scheduling details.

### FITS Viewer / GPU Stretch

- Stretch (MTF) is computed entirely in the GLSL fragment shader — no CPU reprocessing on parameter changes
- `FitsDocument` debayers once at load time; the debayered image is the permanent base
- Three stretch modes: per-channel (linked/unlinked), luma (preserves chrominance ratios)
- `Image.StretchValue()` is the single source of truth for the stretch pipeline
  (normalize → subtract pedestal → rescale → MTF), used by CPU stretch, background computation, and tests
- WCS coordinate grid overlay rendered in the fragment shader with per-pixel TAN deprojection

### Sky Map / GPU 3D Rendering

The sky map (`VkSkyMapTab`) renders stars, constellation lines, grid, and horizon using
GPU shaders via a side-car Vulkan pipeline (`VkSkyMapPipeline` in `TianWen.UI.Shared`).

**Architecture** (same approach as Stellarium, but projection in vertex shader):
```
Star catalog (RA/Dec) → J2000 unit vectors → persistent GPU vertex buffer (built once)
View matrix (CenterRA/Dec) + FOV → UBO uniform buffer (updated per frame)
Vertex shader: viewMatrix × unitVec → stereographic projection → screen NDC
```

**GLSL shader strings:** The Vortice.ShaderCompiler GLSL-to-SPIR-V compiler does **not**
handle non-ASCII characters, even in comments. Never use Unicode (em dashes, arrows, math
symbols) inside GLSL raw string literals — use ASCII only.

**RA direction convention:** The view matrix's right vector points toward decreasing RA
(leftward on a sky map viewed from inside the celestial sphere). The `stereoProject` GLSL
function therefore uses `+camPos.x` (not negated) — the view matrix already encodes the
inside-sphere RA direction. The CPU `SkyMapProjection.Project` negates X explicitly because
it works directly in RA/Dec without a view matrix.

**Key types:**
- `VkSkyMapPipeline` — owns pipeline layout, UBO, star pipeline (instanced), line pipeline
- `SkyMapState.ComputeViewMatrix()` → `Matrix4x4` (J2000 → camera rotation)
- `SkyMapState.RaDecToUnitVec()` — RA/Dec → unit vector on celestial sphere
- `SkyMapProjection` — kept for CPU-side inverse projection (drag-to-pan, label placement)
- `SkyMapRenderer` — CPU software renderer, kept as TUI fallback

**Sub-pipelines:**
- `StarPipeline` — instanced quads (6 verts × N instances), B-V color + soft radial falloff
  in fragment shader, additive blending
- `LinePipeline` — `VK_PRIMITIVE_TOPOLOGY_LINE_LIST`, push constant vec4 color, alpha blending

**Geometry buffers:**
- Stars: ~118k instances × 20 bytes (vec3 pos + float mag + float bv) ≈ 2.3 MB, persistent
- Constellation figures: ~500 line segments, persistent
- Boundaries: ~7800 tessellated arc segments, persistent
- Grid: 5 scale levels, each a persistent line-list buffer
- Horizon + meridian: dynamic, written to per-frame ring buffer

### GUI Widget System

The GUI uses a unified widget system built on `PixelWidgetBase<TSurface>` in
`TianWen.UI.Abstractions`. This is renderer-agnostic (works with VkRenderer and
RgbaImageRenderer).

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

**Key patterns** (see `AppSignalHandler` XML doc for SignalBus delivery contract):
- Widgets call `RegisterClickable` during render; `HitTestAndDispatch` dispatches clicks
- Self-contained actions use `OnClick` delegates; cross-component actions use `PostSignal`
- All background work goes through `BackgroundTaskTracker` — zero fire-and-forget
- SDL scancodes mapped to `InputKey` via `SdlInputMapping` (C# 14 extension blocks)
- `Program.cs` is a thin event loop + composition root

### Signal Handler Pattern — Route, Don't Implement

The lightweight `SignalBus` is our alternative to MediatR / MVVM frameworks — it deliberately
avoids that class of soul-sucking abstraction. But lightweight ≠ lawless. The
`AppSignalHandler.cs` subscribe lambdas must follow proper separation of concerns:

**A subscribe lambda's job is to route a signal.** It takes the signal payload, calls one
or two helpers, and reflects any results back into UI state. That's it. No loops over
domain state. No direct persistence. No URI manipulation. No multi-step business logic.

**Where business logic actually goes**:
- **Pure profile/equipment transformations** → `EquipmentActions` in `TianWen.UI.Abstractions`
  (takes immutable inputs, returns new state; may do I/O via passed-in `IExternal`)
- **Device-model operations** (URI reconciliation, discovery cache queries) →
  extension methods on the relevant interface in `TianWen.Lib/Devices/*Extensions.cs`
- **Persistence** → dedicated helpers like `PlannerPersistence`, `SessionPersistence`,
  or `Profile.SaveAsync` — never inlined into a subscribe lambda

**Canonical example** (`DiscoverDevicesSignal` handler):
```csharp
bus.Subscribe<DiscoverDevicesSignal>(async sig =>
{
    // ... routing only ...
    await dm.DiscoverAsync(cts.Token);
    eqState.DiscoveredDevices = [.. /* projection */];

    // Route the business operation to a helper:
    var changes = await EquipmentActions.ReconcileAllProfilesAsync(dm, external, cts.Token);

    // Reflect results back into UI state:
    foreach (var (original, updated) in changes)
    {
        if (appState.ActiveProfile?.ProfileId == original.ProfileId)
            appState.ActiveProfile = updated;
    }
});
```

**Why it matters**:
- Handler file stays a scannable routing table, not a feature file
- Business operations are directly testable without signal-bus + SP setup
- CLI / Server hosts can reuse the same helpers (they don't have the UI signal bus)
- Adding a new URI field to `ProfileData` touches one helper, not every call site

**Red flag**: if you're writing a `foreach` or a multi-step `if`/`await`/`save` chain
inside a subscribe lambda, extract it to `EquipmentActions` (or a new sibling helper class).

### Filter Wheel Configuration

**Design principle**: Seed on discovery, TianWen owns the data, sync back on connect.
Filter names and focus offsets stored as URI query params (`filter1`, `offset1`, ...) on the
filter wheel device URI in the profile. All drivers read from URI params first, with
driver-specific fallbacks per slot. See each filter wheel driver class for fallback details.

### QHY Device Discovery & QFOC Focuser

`QHYDeviceSource` discovers all QHY devices in three phases during `DiscoverAsync`:
1. **Enumerate cameras** via QHY SDK (lightweight open/close for identity)
2. **Probe serial ports** for standalone CFWs (QHYCFW3, VRS command) and QFOC focusers (JSON init)
3. **Check camera-cable CFWs** by re-opening each camera to query `IsCfwPlugged`

`RegisteredDevices` returns pre-discovered lists — no I/O on the sync path.

**QFOC Focuser** (`QHYFocuserDriver`): JSON-over-serial at 9600 baud. Supports both Standard
(board version 1.x) and High Precision (2.x) variants via the same protocol. Protocol types
and full command reference are in `QfocProtocol.cs` with AOT-safe `QfocJsonContext`. Key features:
absolute positioning (±1M steps), dual temperature sensors (external NTC + chip), TMC StallGuard,
12V supply detection.

### Hosting API (`TianWen.Lib.Hosting` + `TianWen.Lib.Server`)

Headless REST + WebSocket API for remote operation (Raspberry Pi, Touch N Stars mobile UI).

**Architecture:** Two API layers on the same ASP.NET Core host:
- **Native v1** (`/api/v1/`): Multi-OTA routes, camelCase JSON (`HostingJsonContext`), POST for mutations
- **ninaAPI v2 shim** (`/v2/api/`): Single-OTA (maps to OTA[0]), PascalCase JSON (`NinaApiJsonContext`), GET for everything (ninaAPI convention)

**Key types:**
- `IHostedSession` — singleton holding `ISession?`, `ActiveProfileId`, `PendingTargets` (pre-session target queue)
- `HostedSession` — implementation with `DrainTargets()` for atomic target consumption at session start
- `EventHub` — dual-pool WebSocket broadcaster (native camelCase + ninaAPI PascalCase)
- `EventBroadcaster` — `BackgroundService` subscribing to `PhaseChanged`, `FrameWritten`, `PlateSolveCompleted`

**Target lifecycle:** Targets are queued via `AddTarget()` before session start, drained into
`ScheduledObservation[]` when `/session/start` is called. Cannot be modified mid-session.

**Running:** `dotnet run --project TianWen.Lib.Server` or `tianwen-server [--port 1888]`

### Image Pipeline & Buffer Lifecycle

Camera → Channel → ChannelBuffer → Image → Consumer → Release → Camera recycles.
See `ChannelBuffer` XML doc for ownership semantics.

**Key rules**:
- Camera creates `ChannelBuffer`, `GetImageAsync` transfers ownership, consumer calls `image.Release()` when done
- Never hold an `Image` from `GetImageAsync` longer than needed — it pins the camera buffer
- `DebayerIntoAsync` for viewer output, `DebayerAsync` only for FITS viewer (file-based)
- `Array2DPool` is for scratch only — camera buffers use `ChannelBuffer`/`_freeBuffers`

### Concurrency

- `SemaphoreSlim` / `DotNext.Threading` for resource locking
- `CancellationToken` propagated throughout
- `ValueTask` for allocation-free async paths
- **Never use `.GetAwaiter().GetResult()`** — always make the method `async` and `await` instead

### Shared UI state collections: `ImmutableArray<T>`, not `List<T>`

Any collection that lives on shared UI state (`PlannerState`, `LiveSessionState`,
`EquipmentTabState`, `GuiAppState` etc.) and can be touched by **both** the render
thread and a background task (planner recompute, pin sync, preview telemetry,
session polling, ...) **must be typed as `ImmutableArray<T>` with an atomic
replacement pattern**, not `List<T>`.

- Writers build the new array locally (or use `array.Add(x)`, `.RemoveAt(i)`,
  `.SetItem(i, x)`, `.Sort(cmp)` — all return new instances) and assign the
  property in one atomic reference update.
- Readers snapshot the property into a local and iterate that snapshot; the
  property itself can still be swapped mid-read without corrupting their view.
- Pattern match on `.Length`, not `.Count` — `ImmutableArray<T>` exposes `Count`
  only via explicit `IReadOnlyCollection<T>` implementation, which doesn't bind
  in expressions or patterns.

See `PlannerState.Proposals` / `SearchResults` / `TonightsBest` as canonical
examples, and `LiveSessionState.PreviewOTATelemetry` for the "rebuild the whole
array" pattern with `.ToBuilder()` / `.ToImmutable()` when many writes are
batched together.

Using `List<T>` here **will** produce `InvalidOperationException: "Collection
was modified; enumeration operation may not execute."` under real load — even a
single `foreach` racing with one `Add()` on a different thread is enough.
`Dictionary<K, V>` has the same hazard; treat it the same way where shared.

### Code Quality Guidelines

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, and `Span<T>` over allocating new arrays. Use `ReadOnlySpan<T>` for read-only views.
- **Immutability with controlled mutability**: make types immutable by default. When mutation is needed, keep mutable state private and expose only read-only views.
- **Correct abstraction levels**: pure math and data processing in `TianWen.Lib`, UI state and document model in `TianWen.UI.Abstractions`, Vulkan-specific rendering in `TianWen.UI.Shared` and `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **Avoid code duplication**: reuse existing methods (e.g., `Image.StretchValue()` as single source of truth for stretch) rather than reimplementing logic in multiple places.

## Package Management

Centralized in `Directory.Packages.props` — version numbers are defined there, not in individual `.csproj` files. When adding or updating packages, edit `Directory.Packages.props`.

## Runtime Data (AppData)

Application data is stored in `%LOCALAPPDATA%/TianWen/` (`C:\Users\<user>\AppData\Local\TianWen\`):

```
TianWen/
├── Logs/                    # Per-day log files: GUI_*.log, FitsViewer_*.log
├── Profiles/                # Per-profile data (*.json + NeuralGuider/*.ngm)
└── Planner/                 # Persisted planner state (pinned targets)
```
