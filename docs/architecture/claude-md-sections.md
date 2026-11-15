# Where each former CLAUDE.md section is now

`CLAUDE.md` was cut to one line per rule on 2026-11-15 (#2149). Code comments, plans and skills written before that cite
its sections by their old names ("CLAUDE.md, Hosting API: the wire traps", "the Concurrency section"). This table says, for
each old heading, which section of today's `CLAUDE.md` keeps its rules and which document holds its text in full (under
"Moved from CLAUDE.md, 2026-11-15" where the document did not already hold it).

| Former section | Section of `CLAUDE.md` now | Text in full |
|---|---|---|
| Project Tracking Docs | Project Tracking | `docs/architecture/project-tracking.md` |
| Custom Skills | Project Tracking | `docs/architecture/project-tracking.md` |
| Project Overview | Project Overview | `docs/architecture/project-tracking.md` |
| Solution Structure | Project Overview | `docs/architecture/project-tracking.md` |
| Build & Test Commands | Build & Test | `docs/architecture/testing.md` |
| SharpAstro Sibling Libraries | Sibling Libraries and Releases | `docs/architecture/sibling-builds-and-releases.md` |
| Releasing a sibling, and TianWen's own version | Sibling Libraries and Releases | `docs/architecture/sibling-builds-and-releases.md` |
| Key Technologies | (not kept) | `docs/architecture/project-tracking.md` |
| Testing Conventions | Testing Conventions | `docs/architecture/testing.md` |
| Device-Simulator Integration Tests (on-demand) | Testing Conventions | `docs/plans/device-simulator-ci.md` |
| Test Collections & Parallelism | Testing Conventions | `docs/architecture/session-test-harness.md` |
| Driving the GUI and the TUI unattended | Driving the GUI and the TUI unattended | `docs/architecture/unattended-ui-driving.md` |
| Coding Style | Coding Style | `docs/architecture/coding-rules.md` |
| Logger, TimeProvider, and SleepAsync | Clock and logging | `docs/architecture/coding-rules.md` |
| Device Management | Devices | `docs/architecture/device-architecture.md` |
| Device Ownership (the hub lease) | Devices | `docs/architecture/device-ownership.md` |
| Alpaca Backend (ASCOM Remote / Alpaca HTTP) | Devices | `docs/architecture/device-architecture.md` |
| Device Secrets (Credential Store) | Devices | `docs/architecture/device-architecture.md` |
| Plate Solving | Plate Solving | `docs/plans/plate-solver-performance.md` |
| Comet & Small-Body Ephemeris (`TianWen.Lib.Astrometry.Comets`) | Sky objects, planner, atlas | `docs/plans/comet-ephemeris.md` |
| Planner Pin Identity (a pinned planet is not its position) | Sky objects, planner, atlas | `docs/architecture/planner-pin-identity.md` |
| A Wikipedia link is the article the bake VERIFIED, never one built from a designation | Sky objects, planner, atlas | `docs/plans/object-imagery.md` |
| The atlas's hover highlight, and what a click would take | Sky objects, planner, atlas | `docs/plans/in-app-sky-atlas.md` |
| Smart Framing (planner co-framing groups) | Sky objects, planner, atlas | `docs/plans/smart-framing.md` |
| Meteor showers (`TianWen.Lib.Astrometry.MeteorShowers`) | Sky objects, planner, atlas | `docs/plans/yearly-planner.md` |
| Night Calendar (one forecast, one verdict) | Sky objects, planner, atlas | `docs/plans/night-calendar.md` |
| Session | Session | `docs/architecture/session-invariants.md` |
| Driver Resilience on the Hot Path | Session | `docs/architecture/driver-resilience.md` |
| Backlash Auto-Tuning | Session | `docs/architecture/session-invariants.md` |
| Polar Alignment | Session | `docs/plans/polar-alignment.md` |
| Flat-Frame Acquisition (automation) | Session | `docs/plans/flat-frame-automation.md` |
| Deep-Sky Stacking + Enhance Pipeline (`TianWen.Lib.Imaging.Stacking`) | Deep-Sky Stacking | `docs/architecture/stacking-render-pipeline.md` |
| Planetary Lucky-Imaging Stack (`TianWen.Lib.Imaging.Planetary`) | Planetary | `docs/plans/planetary-restoration.md` |
| AI Image Enhancement: RC-Astro (CLI) + TianWen's own models (ONNX) | AI Enhancement, colour composition, background extraction | `docs/plans/ai-enhancement.md` |
| Mono Colour Composition: PixInsight's Steps, One Verb Each (`ColourComposition`) | AI Enhancement, colour composition, background extraction | `docs/plans/narrowband-colour.md` |
| Classical Background Extraction (`TianWen.Lib.Imaging.BackgroundExtraction`) | AI Enhancement, colour composition, background extraction | `docs/plans/background-extraction.md` |
| Hosting API (`TianWen.Hosting` + `TianWen.Server`) | Hosting API and remote rigs | `docs/architecture/hosting-api.md` |
| Remote Rigs (mirror another node's session "as if local") | Hosting API and remote rigs | `docs/plans/remote-profile.md` |
| Colour Theme (`GuiTheme`, four states incl. Night) | Viewer, rendering, stretch | `docs/plans/colour-theme.md` |
| Desktop Shell: File Types, the Single-Instance Hand-off, and the MSIX Store Lane | Viewer, rendering, stretch | `docs/architecture/desktop-shell.md` |
| Image Pipeline & Buffer Lifecycle | Images and files | `docs/architecture/image-pipeline.md` |
| The image is not necessarily in HDU 0 | Images and files | `docs/architecture/image-pipeline.md` |
| A FITS header becomes an `ImageMeta` in exactly ONE place | Images and files | `docs/architecture/image-pipeline.md` |
| Image Mutability: Almost-Immutable with In-Place Escape Hatches | Images and files | `docs/architecture/image-pipeline.md` |
| A Canon raw is cropped to its active area on import | Images and files | `docs/architecture/image-pipeline.md` |
| Float TIFF Convention (`SharpAstro.Tiff` I/O; Magick.NET fully removed) | Images and files | `docs/plans/image-codecs-facade.md` |
| FITS Viewer Widget (`ImageRendererBase<TSurface>`) | Viewer, rendering, stretch | `docs/architecture/widgets-and-controls.md` |
| The star field is culled on TWO axes, and both are load-bearing | Viewer, rendering, stretch | `docs/plans/web-tycho2.md` |
| A quantized cache key must not derive its grid from a continuous input | Viewer, rendering, stretch | `docs/plans/web-showcase.md` |
| The web host paints per event, so continuous gestures must coalesce onto rAF | Viewer, rendering, stretch | `docs/plans/web-host-carve-out.md` |
| Sky Map / FITS Viewer GLSL (pre-baked SPIR-V, no runtime shaderc) | Viewer, rendering, stretch | `docs/architecture/stretch-pipeline.md` |
| Stretch Pipeline: CPU/GPU Mirror | Viewer, rendering, stretch | `docs/architecture/stretch-pipeline.md` |
| Layout DSL (`DIR.Lib.Layout`) | Layout and input | `docs/architecture/widgets-and-controls.md` |
| UI Primitives: the cursor, a text field, and who holds focus | Layout and input | `docs/architecture/widgets-and-controls.md` |
| Per-Window Widget State: `DpiScale` / `FontPath` / `EmojiFontPath` are properties, not parameters | Layout and input | `docs/architecture/widgets-and-controls.md` |
| Signal Handler Pattern: Route, Don't Implement | State, threads, code quality | `docs/architecture/coding-rules.md` |
| Shared UI State: `ImmutableArray<T>`, not `List<T>` | State, threads, code quality | `docs/architecture/coding-rules.md` |
| Background-Task State in `AppSignalHandler` | State, threads, code quality | `docs/architecture/coding-rules.md` |
| Concurrency | State, threads, code quality | `docs/architecture/coding-rules.md` |
| Code Quality Guidelines | State, threads, code quality | `docs/architecture/coding-rules.md` |
| Package Management | Build & Test | `docs/architecture/coding-rules.md` |
| Runtime Data (AppData) | Images and files | `docs/architecture/runtime-data.md` |
