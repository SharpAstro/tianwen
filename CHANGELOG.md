# Changelog

Release notes for TianWen, newest first, one section per `MAJOR.MINOR`.

The version NUMBER is not here. It is one line in `src/Directory.Build.props`
(`VersionMajorMinor`), and CI's `version` job reads that property back rather than restating it, so a
build can never declare a version this file disagrees with. Bump it there and add the entry here, in
the same commit.

The patch segment is CI's `github.run_number` and is never written by hand, which is why the tags
read `v7.0.1532` while the sections below read `7.0`. Per the org convention in the SharpAstro
`.github` repo, `X.Y` to `X.(Y+1)` is additive and `X.*` to `(X+1).0` is breaking.

Each GitHub Release is cut with `generate_release_notes: true`, so it already lists the commits.
This file is the half a commit list cannot write: what a release was FOR, and what breaks.

Entries before 7.1 were reconstructed from the release commits and the tags rather than written at
the time, so they record what each bump was for. Where a bump commit said nothing beyond the number,
the entry says so instead of inventing a theme. The date on each is its release tag's; a new entry
carries none, because the tag will.

**The history was rewritten once, during 4.2** (the LFS migration of 2026-04-22, between CI runs 601
and 611), and **releases before 4.2 no longer exist on GitHub.** Until 2026-09-06 the seven release
tags from `v3.0.440` through `v4.0.564`, plus 71 orphan tags, still pointed into the pre-migration
history, which kept 594 MB of superseded catalog and fixture blobs (the Tycho-2 binary in four
encodings, the `.fits.gz` fixtures) in every fresh clone: a 632 MB pack for a 48 MB repository.
Those seven releases and their binaries were deleted (four had never been downloaded; the others had
12 to 28 lifetime downloads, all from their first weeks), every orphan tag was removed, and the seven
release tags were re-created on `main` at the commits with the same subjects, so the entries below
stay, their dates stay verifiable, and `git log v3.6.493..v4.0.564` answers the same 200 commits it
always did. Hashes quoted in the docs from before the migration were re-pointed the same way. Any
other commit hash from before 2026-04-22 no longer resolves anywhere.

## 7.1

Additive, so a minor: every public member that existed at 7.0 survives (the only public line the
diff removes is `HfdPsfEstimator`'s old one-parameter constructor, replaced by the wider one), and
the new constructor parameters are all defaulted, so `new HfdPsfEstimator()` still compiles.

**`RichardsonLucy` (`TianWen.Lib/Imaging/Deconvolution`)** is the known-kernel maximum-likelihood
iteration, shipped as library surface rather than as a training script, because the deconvolution
work needs an oracle that anything trained can be scored against. It was validated against answers
known in advance before it was used to judge anything: a ceiling measured with a broken instrument
is worse than no ceiling, since every arm would be scored against it and the error would never
surface. It recovers 100, 97 and 86 percent of a known blur on noise-free synthetic stars given the
exact kernel, conserves interior flux to 0.000 percent, is inert to 4e-4 under a near-delta kernel,
and reports rather than absorbs a negative input, since the update is multiplicative and a negative
sample flips the sign of its own correction.

**`PsfKernel.Mirrored()`** is the adjoint the correction pass convolves with. It equals the original
for every kernel this family can build, and is built unconditionally rather than short-circuited, so
that a future asymmetric kernel turns it into a real correction instead of silently leaving every
deconvolution running the wrong operator. A `[Theory]` pins that equality.

**The measured oracle ceiling**, which is the number any trained deconvolver may be held to, at 60
Richardson-Lucy iterations with the exact kernel over 180 rows of real masters: full recovery to 1.3x
blur (residual 0.00 px), the recovered star 1.01 to 1.02 times the truth through 1.6x and 1.09 to
1.14 at 1.6 to 2.0x, so "within 10 percent up to about 2x" holds; past 2x the star is about 1.6x the
truth, most of the blur still present. A first table at 30 iterations read 1.6x as the boundary and
was under-converged by roughly three in residual; a too-low ceiling flatters every arm scored against
it, so it was re-measured rather than annotated. Nothing recovers below the truth (rec/truth at or
above 0.99 in every band), which is the line a trained arm may not cross, and ringing arrives before
recovery degrades (15 percent of stars at 1.1 to 1.3x while the residual is still 0.00 px), so the
usable range is set by the ringing column, not the width.

**`HfdPsfEstimator` takes its radius range at construction**, and TianWen's own measured contract
lands beside it as `TianWenMinRadiusPx` / `TianWenMaxRadiusPx` ([0.5, 4.0] px). **The default is
deliberately NOT changed**: it stays SAS's [1, 8], because the range belongs to the MODEL, and the
shipped `OnnxNonStellarDeconvolver` runs SAS AI4. A graph trained on [1, 8] handed a number encoded
over [0.5, 4] does not fail. It runs, and is quietly told a PSF about twice the one it was given. A
test pins that the container still resolves the shipped range, which is exactly what the new
optional constructor parameters could otherwise break in silence.

The encoding arithmetic behind that choice is worth stating, because the intuitive move is
backwards. `psf01` is a ratio of logs, so widening the range divides every difference by its span:
lowering the floor makes the spread SMALLER. [0.5, 4] and [1, 8] are both 8:1 and resolve
identically; the whole measured gain is unclamping the six of 79 masters that were pinned to SAS's
floor. The span sets the resolution, the window's position decides what clamps, and buying real
resolution means narrowing the span at the cost of clamping one end.

**`DatasetDegradationExporter`** gains `MaxBlurRatio`, `PerChannelKernels`, and psf01 labelling on
every blur row: `Psf01Estimated`, read off the DEGRADED cell, beside the training-only
`Psf01FromKernel`. Two rules the exporter enforces. The label is measured on the LINEAR cell,
because `OnnxNonStellarDeconvolver` measures it there too, before the runner stretches, and a tone
curve moves a star's half-maximum crossing. And a psf01 is never written without stars behind it:
the estimator falls back to a constant default radius when it finds none, so the exporter writes
null instead and a consumer drops the row, rather than conditioning a model on a number nothing
measured.

**The ceiling with an ESTIMATED kernel** (E1b), measured with the same probe: where the kernel is read
off the blurred frame's own stars, the recovered width stays within 0.01 to 0.05 of the exact-kernel
result everywhere but the 1.3 to 1.6x band, where the difference width is over-read by a quarter and
the width-only estimate over-deconvolves into fabrication (15 percent more stars than the truth has).
The number that decides the next step is that `PsfProfileFit` declined 75 of 180 rows, half of the
noisy ones, most of them narrowband masters: at deployment depth the estimator answers about half the
time, so a kernel estimator becomes its own step before any unrolled Richardson-Lucy is built.

**`DatasetPsfNoiseReport.SessionPsf` gains a per-sub identity**: `SubFile`, `SubEpochUtc`,
`SubAirmass` and `SubHeaderAirmass`, aligned index for index with the existing `SubFwhm`, plus
`SubSelection` naming which subs the arrays describe. Records written before this read back with the
new columns null; `SubIdentity` carries them through a master re-measure, which cannot recover them
(a master does not say which frames made it). **`tianwen dataset build --remeasure-subs`**
(`DatasetBuildOptions.RemeasureSubs`) re-runs the measure stage alone over every recorded session
and rewrites the sub arrays with the identity, about two minutes a session against ten for the
re-registration `--force-psf` falls back to; the two are refused together and run as two passes,
subs first. The store's JSON context now allows NaN, which the per-sub air mass is for every light
whose header has no site.

**`SiteContext.Airmass` and `SiteContext.AirmassFromAltitude`** are the one air-mass computation;
the session's sky gauge and the gradient report forward to it. The per-sub form answers NaN for a
target below the horizon rather than a clamped value that would read as a deep observation.
**`ImageMeta.Airmass`** reads the capture software's `AIRMASS` card, recorded beside the computed
value as a cross-check and never substituted for it. The archive-wide measurement it was built for
(E2.9) then found that computed and card agree to a median 0.015 on 50 of the 52 sessions carrying
both, that every SharpCap capture writes no `SITELAT`/`SITELONG` at all (27 of 79 sessions have no
computed value), and that no session's FWHM spread is explained by air mass at these focal lengths;
`tools/psf-airmass-report.py --airmass either` uses the card where the computation has no site,
labelled as such, on the strength of that agreement.

**`MoffatComposition` (`TianWen.Lib/Imaging/Degradation`)** is the width of one Moffat convolved
with another and its inverse, the kernel that takes a clean profile to an observed one. It exists
because a quadrature of FWHMs is the Gaussian rule and a Moffat is not a Gaussian: read as an
estimator does, `sqrt(obs^2 - clean^2)` over-reads a difference kernel by 1.11 to 1.24 on the
archive's cores (measured numerically, then found as the 1.24 the estimated-kernel oracle showed at
1.3 to 1.6x blur), and the composed inverse lands within 0.02 of the truth at every band from 1.3x up.
It also composes a continuous core with a `PsfKernel` **as sampled**, and inverts that into the
kernel's `EffectiveKernelFwhm`, because `PsfKernel` samples its profile at pixel centres and a
kernel narrower than about 1.5 px does not blur by its label: a nominal 1 px beta-4 Moffat widens a
2.15 px core as a 0.73 px continuous one would, and a nominal 0.5 px kernel is a near-delta. Nothing
downstream had noticed, since the training label is measured on the degraded cell; the oracle probe
had, reading a correct estimate as a 0.65 under-read against a width the kernel never applied. The
exporter's training-only `Psf01FromKernel` now composes the clean width with the kernel applied
instead of a quadrature with the drawn width.

**`PsfProfileFit` says why it refuses, and can select its stack by an absolute floor.** A
`Diagnostics` overload returns which of the five checks declined (`Refusal`) with the counts at each
stage, after two years of a bare null; the first tally over 180 oracle rows put 28 of 30 whole-frame
refusals on ONE check (`PoorFit`) on RICH fields, because the default brightness band is a
percentile of the frame's own detections and on a field with 5,000 stars it stacks faint ones whose
wings meet the noise floor within a few pixels. `StarSelection.SignalFloor` (opt-in, the default
band is unchanged so the archive survey stays comparable) takes every star over fifty background
MADs, brightest first, and cut the refusals to 30 of 180 with the widths unchanged; the ones that
remain are sparse or heavily blurred frames with too few such stars, a different and honest refusal.

**`OnnxNonStellarDeconvolver(perChunkPsf:)`**, off by default, conditions each inference tile on
its own region's PSF instead of one frame-wide value: `ChunkedInference.Layout` is the tile grid
without the pixels, `ChunkedNafnetRunner` takes its extra inputs per chunk, `IPsfEstimator` gains an
`EstimateChunkAsync` overload carrying the whole-image value as the fallback, and `HfdPsfEstimator`
answers it under `MinChunkStars` (8) stars. Measured on the seven Rim masters, the per-tile radius
spans 45 to 61 percent from p10 to p90 on the 2025-26 sessions with the CENTRE the soft end, so one
value tells most tiles the wrong width; the switch stays off until the output comparison against the
shipped whole-image graph reads, because that graph was trained on whole-image labels, and a master
whose radii sit under the shipped range's 1 px floor clamps every tile to the same value and cannot
be told apart either way.

**`tianwen stack --group-temp-tolerance <C>`** (`StackingOptions.LightGroupTemperatureToleranceC`)
lets a night whose cooler drifted across a degree stack as ONE master. The light grouping reuses the
calibration key, whose temperature is rounded to the degree, so a real 13.7 to 12.1 C session came
out as three masters of 49, 18 and 4 frames with three reference frames, which nothing downstream
can recombine. With a tolerance, a target's frames are cut only where consecutive temperatures are
further apart than it (a drift is one cluster, a different night's 8 C another) and the group's dark
is matched at its median temperature. The default stays 0, grouping exactly as before, because the
same rounding is what pairs a group with a dark at its temperature. `LightGroupKey.Assign` is the
one implementation; five facts pin it. Found by E2.10, which needs one manifest for one night.

## 7.0

Two releases: `v7.0.1513` on 2026-09-04 and `v7.0.1532` on 2026-09-06, the second carrying 60 more
commits, 27 of them features, listed at the end of this entry.

**BREAKING: `TryLease(out Image?)` is now `TryLease(out ImageLease)`**, cut in one wave with no
compatibility overload. **Migration:** convert each call site to `using`. `default(ImageLease)` is
inert, its `Dispose` a no-op and its `Image` throwing to name the call-site bug. The own side is
unchanged: an owner still calls `Image.Release`, which stays a no-op for self-owned frames. The
token is the BORROW side only.

The reason for the cut is that a shared refcount can DETECT a double release but cannot prevent one
(the offending call is byte-identical to a legitimate last release), and a bare `Image` returned
from `TryLease` carried its release obligation only in a doc comment. `ImageLease` is a readonly
struct whose `Dispose` is the borrower's whole obligation, 1:1 with the claim and spent exactly once
however many struct copies exist. Note that CA2000 does NOT catch a forgotten one: it ignores
value-type disposables, measured directly (a forgotten `ImageLease` beside a forgotten `FileStream`,
only the stream fired), so a dropped lease is the DEBUG leak tracker's catch and not the compiler's.

The cut also closed a latent poison. The bufferless branch used to hand out `this`, so the
borrower's dispose marked the SOURCE released and every later lease of the same published frame
refused: a repeat-polling preview got one frame and then 404s until the frame happened to swap. A
lease is now always a distinct image sharing the planes.

Also in 7.0:

- **Mount safety limits**, the mechanical bound where the tube meets the pier or the ground, which
  is not the meridian flip and can exist with it, without it, or neither. Enforced with no session
  running too, via `MountLimitWatcher`, so `tianwen-server` and the GUI both hold the rig.
- **A meridian flip is verified from the IMAGE, not the mount's word**, wherever the pointing state
  is computed rather than measured, plus `Session.GetSideOfPierAsync` as the canonical pier side and
  a solved field reporting its position angle.
- **Explorer thumbnails** for `.fits` / `.fit` / `.fts` / `.fz` / `.ser`, a NativeAOT COM DLL living
  inside the viewer's own publish tree.
- **Comet integration finished**: the nucleus comes from the raw frames, each channel gets its own
  amplitude, the model reaches as far as each channel's coma, and one run emits the star layer with
  the body subtracted out of it.
- **`StretchMode.Auto`**, the new viewer default, picking Linked when calibrated and Unlinked
  otherwise.
- **Session measurement frames are kept** (`SaveIntermediates`) and every light is stamped with the
  guide RMS of its own exposure.
- Plate solving searches for the frame when the hint is not where it says it is.

The second 7.0 release, `v7.0.1532`, added:

- **Classical background extraction** (`ClassicalBackgroundExtractor`), the AI-free gradient
  corrector: a robust degree-2 polynomial on a block-mean working grid, standing in for GraXpert
  wherever its weights are absent, and exposed as `tianwen image flatten`. Both of its thresholds
  were measured over 118 real masters and turned out not to be knobs.
- **The N2N trainer ported into the repo**, with E0 reproducing the v19d checkpoint bit for bit, one
  degradation exporter serving three trainings, and the injected noise shape treated as a
  measurement rather than a choice.
- **The shipped denoiser checkpoint moves to the gate-selected WIDE seed**
  (`tianwen_denoise_osc_e2wide_s2.onnx`) and the weights go back into LFS. The file name carries the
  checkpoint identity by design: a retrain gets a new name, never a silent replacement.
- Cross-night N2N pairs, both nights resampled onto the midpoint grid, and a training metric that
  asks Gaia whether its "faint stars" are stars (on a nebula field, two thirds were not).
- Viewer P18 to P22: Open and Save as baked marks, save the image as displayed at its own size, one
  frame's stretch held across the folder, a blink through the file list, Shift as the other
  direction rather than a mode, a right-click link into the web Sky Atlas (which now reads a share
  link), and saving the annotated view without a second drawing path.

## 6.3

Released 2026-08-22 (`v6.3.1352`). Additive.

- **Damage-based repaint plus the cached image layer**: a mouse move no longer repaints the window,
  and a divider drag repaints the strip it swept rather than the whole pane. Rides DIR.Lib 8.8,
  Console.Lib 4.27 and SdlVulkan.Renderer 7.25.
- An 8-bit document drops the float planes it duplicates, and uploads its own bytes rather than the
  floats it had been widened into.
- The viewer info pane reports what the camera was SET to (gain, ISO, offset), each suppressed where
  the header said nothing rather than printed as a -1 sentinel. The same fix retired a literal
  "Frame: None".
- A viewer window with nothing open adopts a file from anywhere, instead of letting a second window
  open beside it. A window already showing the folder still wins, and a non-empty one still never
  adopts across folders.
- Calibration gained time-aware master matching (a 2021 dark never blends into a 2026 master), a
  bad-pixel map derived from the lights themselves, and rejection of a wrong-gain dark by default.

## 6.2

Released 2026-08-20 (`v6.2.1314`). Additive, and cut because over 160 features had already shipped
under 6.1: the previous binary release was 2026-06-24, and main had taken 648 commits since, 162 of
them `feat`, every one of which would otherwise have gone out under the same minor.

- **Remote rigs**: the Home board listing every rig you can look at, bindings that survive a DHCP
  lease change, backoff on a rig that is not answering, and a mirrored guider tab that draws the
  guide star rather than an empty panel.
- **The in-house N2N denoiser ships in the repo**, one flag away (`--ai-backend n2n`), with Auto
  rescuing with it when the SAS weights are absent, and CI running the parity test.
- **The image is not necessarily in HDU 0**, so every reader walks to the first HDU that carries
  one. This is what made tile-compressed `.fz` readable at all, since a binary table is only legal
  as an extension.
- **Four-state colour theme** (System / Light / Dark / Night) on one palette, with F12 for dark
  adaptation, and the colour literals becoming roles.
- **Text input as a declaration** on both surfaces, including non-Latin input.
- Viewer toolbar rework: baked icons needing no font, a before/after split, a wrapping second row,
  and a zoom control that says what the zoom is.
- The web build fetches only the sky the view is looking at, off a region-aligned Tycho-2 bake.

## 6.1

Released 2026-06-24 (`v6.1.921`). Additive.

Headlined by the **planetary lucky-imaging stack**, phases 1 through 9: frame quality grading, 2D
FFT and sub-pixel phase correlation, global alignment, alignment points with a displacement-mesh
warp, per-AP quality-weighted integration, Bayer drizzle, a-trous wavelet sharpening with bandpass
and combo presets, the `planetary-stack` CLI, and the live rolling-window stack in the viewer with
adjustable sharpen sliders.

Also: SER planetary video opened as a frame sequence with off-thread frame-paced playback, MHC
debayer mirrored on GPU and CPU, manual and gray-world white balance, Alpaca ImageBytes binary
transfer, and the `TIANWEN_NOW` startup clock anchor.

## 6.0

Released 2026-06-20 (`v6.0.885`). The bump rode the layout foundation milestone and was described as
additive at the time, despite the major number. The sibling pins moved to DIR.Lib 5.0,
SdlVulkan.Renderer 6.4 and Console.Lib 3.0 in the same release, DIR.Lib 5.0 being where the layout
engine this migration consumes had shipped.

- **Shared `GuiTheme` plus surface-agnostic layout engine adoption**, replacing roughly 35 duplicated
  colour constants and six copies of the base font size across seven tabs and the renderer.
  Byte-identical by construction: only value-matching constants were migrated.
- A data-driven equipment panel via a section driver, with the per-OTA panel tree built from the
  layout engine.
- **API keys move to the OS credential vault**, off the profile URI.
- Solar-system planets become first-class in sky-map click, search and the info panel;
  centre-anchored zoom and a limiting-magnitude readout.
- Planner weather work: per-hour humidity, a solar-midnight marker, precipitation probability.

## 5.0

Released 2026-06-14 (`v5.0.862`).

**BREAKING, and the reason for the major: the CLI output formats split by data type.** OpenEXR
becomes the unstretched linear HDR master out of the stacking pipeline (`stack --output-format exr`,
full float32 mono and RGB), and JXR is restricted to stretched or processed output. So
`--output-format jxr` is now rejected by `stack` (use `exr`), and `exr` is rejected by `image`.

Also: the GHS reference math ported and validated, `stack --enhance` integrating `SharpenPipeline`
into `MasterPostProcessor`, sRGB v4 embedded in display TIFF/PNG/JPEG output, 16-bit PNG with cICP,
a zenith-anchored first-scout obstruction oracle with a cloud gate, moon-avoidance penalties in
target scoring, and the DEBUG-only live UI inspector with its MCP sidecar.

## 4.2

Released 2026-05-04 (`v4.2.640`). 242 commits since 4.0, and the first release cut on the rewritten
history (see the preamble).

The **hosting API completed all four phases**, including the ninaAPI v2 compatibility shim for Touch
N Stars, and `tianwen-server` got its README section and download table. The solution migrated to
`.slnx`, with `open-vs.ps1` replacing an auto-generated local solution.

The sky map grew most: an object overlay with priority-based label placement, DSO ellipses instanced
into one draw call, the Milky Way background baked from real Tycho-2 plus Planck radiance and dust
extinction, a Goto button and Pin/Unpin on the info panel, and the Notifications tab. Also: Canon
WPD discovery with mirror lockup and ISO gain modes, and astro defaults applied on connect; the
central serial probe service, with SkyWatcher moved onto it; ASCOM COM throws contained so a dead
hub cannot fail-fast the process; driver-resilience polls routed through `PollDriverReadAsync`; the
polar-alignment refine loop as a third mode of the Live Session tab, with frozen-seed quad matching
replacing ROI-anchor tracking in the incremental solver; the catalog binary format rollout; and the
first plan-status summary, `PLAN-summary.md`.

## 4.1

Never released. Bumped and superseded by 4.2 the same day (2026-04-07), so no tag carries it.

## 4.0

Released 2026-04-07 (`v4.0.564`; binaries removed 2026-09-06, see the preamble).

The bump marks TianWen running **a complete imaging session end to end**: device management,
observation planning, session configuration, automated imaging with guiding, auto-focus, filter
sequencing and live monitoring, for single and dual-rig setups. Dual-rig was the original
motivation, and the README was rewritten at this point to describe an imaging suite rather than a
library.

Also: QHYCCD camera, filter wheel and QFOC focuser support; a Weather device type with an
Open-Meteo forecast overlay in the planner; the moon altitude curve and phase; and hosting API
phases 1 and 2.

## 3.6

Released 2026-03-26 (`v3.6.493`; binaries removed 2026-09-06, see the preamble).

Sixel image preview in the TUI live session tab, per-OTA status in that tab, shared setup logic
consolidated, an application icon (the Helix Nebula), iterative centering with auto-focus pipeline
overlap, and unified per-frame star detection shared by the viewer, the drift check and the exposure
log. Sibling packages moved back to nuget.org and the local nupkg directory was removed.

## 3.5

Released 2026-03-18 (`v3.5.455`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.4

Released 2026-03-17 (`v3.4.454`; binaries removed 2026-09-06, see the preamble).

`IMountDependentGuider`, so a built-in guider receives the same mount driver instance rather than a
second `NewInstance`, wired through `SessionFactory`. `Setup` exposed on `ISession` to remove a
downcast in tests.

## 3.3

Skipped. The version went 3.2 to 3.4 directly.

## 3.2

Released 2026-03-15 (`v3.2.449`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.1

Released 2026-03-11 (`v3.1.442`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.0

Released 2026-03-09 (`v3.0.440`; binaries removed 2026-09-06, see the preamble).

**AOT compatibility**: `IsAotCompatible` and `IsTrimmable` on `TianWen.Lib`, a six-platform AOT
publish matrix (Windows, Linux and macOS on x64 and arm64), and CI split into the build, test,
publish-cli, release and publish-nuget jobs it still uses. This is the release that started cutting
GitHub Releases with binaries attached, which is why the tag history begins here.

## Before 3.0

No tags, so nothing is reconstructed here. For the record, the version lineage runs back through
2.0, 1.8, 1.7, 1.6 and 1.5 in `dotnet.yml`, with the framework bumps at .NET 10 (2026-02), .NET 8
(2024-01) and .NET 7 (2023-10). The first CI workflow was added 2022-05.
