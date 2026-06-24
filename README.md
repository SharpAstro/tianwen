TiānWén (天文)
=============

TianWen is a free, open-source astronomical imaging suite for .NET. It manages cameras, mounts, focusers, filter wheels, and guiders via ASCOM, Alpaca, ZWO, QHYCCD, Meade, Skywatcher, OnStep, and iOptron protocols — with first-class support for multi-OTA (dual rig) setups that are difficult or expensive to achieve with existing software.

It ships as a NuGet library (`TianWen.Lib`), a cross-platform CLI with interactive TUI (`TianWen.Cli`), a headless REST API server (`TianWen.Server`) for remote operation via [Touch N Stars](https://github.com/Touch-N-Stars/Touch-N-Stars), a standalone FITS viewer (`TianWen.UI.FitsViewer`), and an integrated N.I.N.A.-style GUI (`TianWen.UI.Gui`).

## Features

- **Device Management**: 
  - Supports various device types such as Camera, Mount, Focuser, FilterWheel, Switch, and more.
  - Provides interfaces for device drivers and serial connections.
  - Includes a profile virtual device for managing device descriptors.

- **Profile Management**:
  - Create and manage profiles.
  - Serialize and deserialize profiles using JSON.
  - List existing profiles from a directory.

- **Image Processing**:
  - Read and write FITS files.
  - Analyze images to find stars and calculate metrics like HFD, FWHM, SNR, and flux.
  - Generate image histograms and background levels.
  - Debayer OSC images (AHD, bilinear) to color or synthetic luminance.
  - Scale-invariant star detection works on both raw ADU and normalized [0,1] images.

- **FITS Viewer** (`TianWen.UI.FitsViewer`):
  - GPU-accelerated stretch (MTF) with per-channel, linked, and luma modes.
  - GPU bilinear Bayer demosaic — raw mosaic uploaded as single texture, debayered per-pixel in the fragment shader. No CPU debayer needed.
  - HDR compression via Hermite soft-knee in the fragment shader.
  - Automatic star detection with HFD-sized overlay circles and status bar metrics.
  - Contrast boost with star-masked background estimation for clean nebula enhancement.
  - WCS coordinate grid overlay with RA/Dec labels.
  - Celestial object annotation overlay (NGC, IC, Messier, etc.) when plate-solved.
  - Per-channel histogram overlay (R/G/B colored) with log/linear scale toggle and stretch-aware bin remapping.
  - Plate solving via ASTAP or astrometry.net.
  - Multi-source: opens FITS, TIFF, and **SER** planetary video — a SER auto-switches to frame playback (off-thread decode-ahead, transport scrub/play/pause, timestamp readout).
  - **Live planetary lucky-imaging stack**: a RAW/STACK toggle runs a rolling-window stack of a SER that follows the playhead (sharpness-graded, globally aligned), with Registax-style 6-layer wavelet-sharpen sliders. All off the render thread, so slider adjustments stay instant regardless of stack time.
  - Manual per-channel white-balance sliders + gray-world Auto, shared across FITS / TIFF / SER.

- **External Integration**:
  - Interfaces for external operations such as logging, `TimeProvider` based time management, and file management.
  - Connect to external guider software using JSON-RPC over TCP.

## Installation

### Library

You can install the TianWen library via NuGet:

```bash
dotnet add package TianWen.Lib
```

### Server (Headless / Remote)

Pre-built native AOT binaries of `tianwen-server` are available from [GitHub Releases](https://github.com/SharpAstro/tianwen/releases):

| Platform | Architecture | Artifact |
|----------|-------------|----------|
| Windows  | x64         | `tianwen-server-win-x64.tar.gz` |
| Windows  | ARM64       | `tianwen-server-win-arm64.tar.gz` |
| Linux    | x64         | `tianwen-server-linux-x64.tar.gz` |
| Linux    | ARM64       | `tianwen-server-linux-arm64.tar.gz` |
| macOS    | x64         | `tianwen-server-osx-x64.tar.gz` |
| macOS    | ARM64       | `tianwen-server-osx-arm64.tar.gz` |

```bash
tianwen-server                    # Listens on http://0.0.0.0:1888
tianwen-server --port 8080        # Custom port
```

The server exposes both a native multi-OTA REST API (`/api/v1/`) and a ninaAPI v2
compatibility shim (`/v2/api/`) that works with [Touch N Stars](https://github.com/Touch-N-Stars/Touch-N-Stars)
for mobile control. WebSocket push events are available at `/api/v1/events` (camelCase)
and `/v2/socket` (PascalCase).

### CLI

Pre-built native AOT binaries of `TianWen.Cli` are available from [GitHub Releases](https://github.com/SharpAstro/tianwen/releases):

| Platform | Architecture | Artifact |
|----------|-------------|----------|
| Windows  | x64         | `tianwen-cli-win-x64.tar.gz` |
| Windows  | ARM64       | `tianwen-cli-win-arm64.tar.gz` |
| Linux    | x64         | `tianwen-cli-linux-x64.tar.gz` |
| Linux    | ARM64       | `tianwen-cli-linux-arm64.tar.gz` |
| macOS    | x64         | `tianwen-cli-osx-x64.tar.gz` |
| macOS    | ARM64       | `tianwen-cli-osx-arm64.tar.gz` |

### CLI Reference

The `tianwen` CLI (`TianWen.Cli`) provides non-interactive commands and a full-screen tabbed TUI (`tianwen tui`).

#### Global Options

| Option | Description |
|--------|-------------|
| `-a`, `--active <name>` | Select active profile by name or ID |
| `<path>` | FITS file or directory to view (shorthand for `view <path>`) |

#### Profile Management

```
tianwen profile list                           # List all profiles
tianwen profile create <name>                  # Create empty profile
tianwen profile delete <nameOrId>              # Delete a profile
```

#### Profile — Mount & Site

```
tianwen profile set-mount <deviceId>           # Set the mount device
tianwen profile set-site --lat 48.2 --lon 16.3 [--elevation 200]
                                               # Set observing site location
tianwen profile set-mount-port --port COM3 [--baud 9600]
                                               # Set serial port/baud on mount
```

#### Profile — Guider

```
tianwen profile set-guider <deviceId>          # Set the guider (PHD2 or built-in)
tianwen profile set-guider-camera <deviceId>   # Set dedicated guider camera
tianwen profile set-guider-focuser <deviceId>  # Set guider focuser
tianwen profile set-oag-ota <index>            # Set which OTA hosts the OAG
tianwen profile set-guider-options [--pulse-guide-source Auto|Camera|Mount]
                                  [--reverse-dec-after-flip true|false]
```

#### Profile — OTA (Optical Tube Assembly)

```
tianwen profile add-ota <name> --focal-length <mm> --camera <deviceId>
    [--focuser <id>] [--filter-wheel <id>] [--cover <id>]
    [--aperture <mm>] [--optical-design Refractor|Newtonian|SCT|...]
tianwen profile remove-ota <index>
tianwen profile update-ota <index> [--name <name>] [--focal-length <mm>]
    [--aperture <mm>] [--optical-design <design>]
    [--prefer-outward true|false] [--outward-is-positive true|false]
```

#### Profile — Camera & Filters

```
tianwen profile set-camera-defaults --ota <N> [--gain <N>] [--offset <N>]
tianwen profile set-filters --ota <N> --filters Luminance:0 Ha:+21 OIII:-3 SII:+25
```

Filter specs are `Name:FocusOffset` pairs. Offset is in focuser steps relative to the
reference filter (typically Luminance=0).

#### Profile — Quick Device Add

```
tianwen profile add <deviceId> [--ota <N>]     # Add device by type auto-detection
```

#### Device Discovery

```
tianwen device list                             # List cached devices
tianwen device discover                         # Force rediscovery
```

#### FITS Viewer (Terminal)

```
tianwen view <path>                             # Render to terminal (Sixel or ASCII)
tianwen <path>                                  # Shorthand for view <path>
```

#### Planetary Stacking

Stack a planetary SER video into a sharpened lucky-imaging master (linear + sharpened FITS + a high-key PNG):

```
tianwen planetary-stack <ser-file> [-o <dir>]
    --keep <0..1>                # fraction of sharpest frames to keep (default 0.25)
    --quality <Laplacian|Gradient>
    --drizzle <scale>            # Bayer drizzle, e.g. 1.5 (sub-Bayer resolution); --drizzle-global for whole-disk
    --sharpen-preset <default|bandpass|combo>   # or --sharpen-gains "g1,g2,..."; --no-sharpen to skip
    --global                     # whole-disk align only (skip alignment-point mesh)
    --png-gamma <g>              # high-key PNG midtones lift (default 0.75); --no-png to skip
    # advanced: --ap-spacing / --max-ap / --ap-patch / --mesh-spacing / --align-tile
```

For interactive planetary work (live rolling-window stack + wavelet sliders), open the SER in the FITS viewer (`tianwen-fits <file.ser>`) and press `K`.

#### Observation Planner

```
tianwen plan                                    # Tonight's best targets (requires profile)
```

#### Interactive TUI

```
tianwen tui                                     # Full-screen tabbed TUI (alternate screen)
```

The TUI provides an Equipment tab, Planner with altitude charts, Session configuration,
Live Session monitor with Sixel preview, and Guider tab with guide error sparklines
(RA/Dec), RMS stats, and settle progress.

#### Example: Building a Dual-Scope Rig

```bash
tianwen profile create "Dual Scope"
tianwen -a "Dual Scope" profile set-mount FakeMount1
tianwen -a "Dual Scope" profile set-site --lat 48.2 --lon 16.3 --elevation 200
tianwen -a "Dual Scope" profile set-guider FakeGuider1
tianwen -a "Dual Scope" profile set-guider-camera FakeCamera1

# OTA 0: widefield
tianwen -a "Dual Scope" profile add-ota "Samyang 135" \
    --focal-length 135 --camera FakeCamera1 --focuser FakeFocuser1
tianwen -a "Dual Scope" profile set-camera-defaults --ota 0 --gain 100

# OTA 1: narrowband with filter wheel
tianwen -a "Dual Scope" profile add-ota "RC8" \
    --focal-length 1625 --camera FakeCamera2 --focuser FakeFocuser2 \
    --filter-wheel FakeFilterWheel1 --aperture 203 --optical-design Astrograph
tianwen -a "Dual Scope" profile set-filters --ota 1 \
    --filters Luminance:0 Ha:+21 OIII:-3 SII:+25 R:+20 G:0 B:-15
tianwen -a "Dual Scope" profile set-camera-defaults --ota 1 --gain 120 --offset 10

# Plan tonight's observations
tianwen -a "Dual Scope" plan
```

### FITS Viewer

Pre-built native AOT binaries of `TianWen.UI.FitsViewer` are available from [GitHub Releases](https://github.com/SharpAstro/tianwen/releases):

| Platform | Architecture | Artifact |
|----------|-------------|----------|
| Windows  | x64         | `tianwen-fits-viewer-win-x64.tar.gz` |
| Windows  | ARM64       | `tianwen-fits-viewer-win-arm64.tar.gz` |
| Linux    | x64         | `tianwen-fits-viewer-linux-x64.tar.gz` |
| Linux    | ARM64       | `tianwen-fits-viewer-linux-arm64.tar.gz` |
| macOS    | x64         | `tianwen-fits-viewer-osx-x64.tar.gz` |
| macOS    | ARM64       | `tianwen-fits-viewer-osx-arm64.tar.gz` |

#### Keyboard Shortcuts

| Key | Action |
|-----|--------|
| T | Cycle stretch mode (none / per-channel / luma) |
| S | Toggle star overlay |
| C | Cycle channel display |
| D | Cycle debayer algorithm |
| V | Toggle histogram overlay |
| Shift+V | Toggle histogram log scale |
| F / Ctrl+0 | Zoom to fit |
| R / Ctrl+1 | Zoom 1:1 |
| Ctrl+2..9 | Zoom 1:N |
| Mouse wheel | Zoom in viewport |

### Interactive TUI (`tianwen tui`)

The interactive TUI provides a tabbed interface for the full imaging workflow:

| Tab | Key | Description |
|-----|-----|-------------|
| Equipment | 1 / F1 | Profile management, device discovery, OTA/filter configuration |
| Planner | 2 / F2 | Tonight's best targets with altitude chart, handoff sliders, scheduling |
| Session | 3 / F3 | Session configuration (cooling, guiding, horizon, focus), per-OTA camera settings |
| Live | 4 / F4 | Live session monitor: exposure progress, cooler sparklines, mount status, Sixel image preview |
| Guider | 5 / F5 | Guide error sparklines (RA/Dec), RMS stats, settle progress |

The live session tab includes a real-time Sixel image preview with viewer controls:

| Key | Action |
|-----|--------|
| T | Cycle stretch mode (None / Unlinked / Linked / Luma) |
| B | Cycle curves boost |
| +/- | Cycle stretch parameter presets |
| F | Zoom to fit |
| R | Zoom 1:1 |
| Escape | Abort session (with confirmation) |

### GUI (`TianWen.UI.Gui`)

The integrated GUI provides a N.I.N.A.-style interface with GPU-accelerated Vulkan rendering,
including all the same tabs as the TUI plus a full FITS image viewer with real-time stretch,
star overlay, WCS grid, and histogram.

## Architecture

Design deep-dives live under [`docs/architecture/`](docs/architecture/):

- [Device architecture](docs/architecture/device-architecture.md) — URI-addressed devices, the driver-factory hierarchy, and the combined device manager.
- [Image pipeline & buffer lifecycle](docs/architecture/image-pipeline.md) — camera → `ChannelBuffer` → `Image` ownership, the live-session data flow, and the GPU debayer/stretch path.
- [Driver resilience on the hot path](docs/architecture/driver-resilience.md) — `ResilientCall`, fault counters, proactive reconnect.
- [FOV obstruction detection](docs/architecture/fov-obstruction.md) — scout frames, altitude-nudge disambiguation, trajectory-aware waits.
- [Fake disturbance model](docs/architecture/fake-disturbance-model.md) — the believed/true pointing split used by the fake drivers for unattended end-to-end testing.

Per-feature implementation plans + status are in [`docs/plans/`](docs/plans/) (e.g. the planetary lucky-imaging stack in [`planetary-stacking.md`](docs/plans/planetary-stacking.md)); `CLAUDE.md` is the contributor architecture guide.
