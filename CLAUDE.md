# CLAUDE.md - TianWen Project Guide

- Always use extended thinking when analyzing bugs or designing architecture or when refactoring.
- When running python temp scripts, always use python not python3
- Always use pwsh not powerhsell

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

### Key Abstractions

- `IExternal` — file I/O, serial ports, time management, logging
- `ISessionFactory` — creates observation sessions with bound devices
- `IPlateSolverFactory` — plate solving (ASTAP, astrometry.net)

### FITS Viewer / GPU Stretch

- Stretch (MTF) is computed entirely in the GLSL fragment shader — no CPU reprocessing on parameter changes
- `FitsDocument` debayers once at load time; the debayered image is the permanent base
- Per-channel and luminance stretch stats are cached at load time
- `ComputeStretchUniforms()` produces shader uniforms from cached stats
- Three stretch modes: per-channel (linked/unlinked), luma (preserves chrominance ratios)
- HDR compression via Hermite soft-knee, also in the shader
- Background estimation via `Image.ScanBackgroundRegion()`: finds the darkest 32×32 patch
  (skipping 5% border to avoid stacking artifacts), uses median (not mean) to reject hot pixels,
  parallelized with `Parallel.For`. Result is pedestal-subtracted and fed through `Image.StretchValue`
  to compute the post-stretch background level for the boost curve's symmetry point.
- `Image.StretchValue()` is the single source of truth for the stretch pipeline
  (normalize → subtract pedestal → rescale → MTF), used by CPU stretch, background computation, and tests
- WCS coordinate grid overlay rendered in the fragment shader with per-pixel TAN deprojection
- Grid labels placed at viewport edges where RA/Dec lines cross, with corner exclusion zones

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
