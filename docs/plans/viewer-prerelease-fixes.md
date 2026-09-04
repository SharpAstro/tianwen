# Viewer pre-release fixes

Defects found while packaging `tianwen-fits` as **Astro Photo Viewer** for the Microsoft Store
(see `packaging/windows/msix/README.md`). Packaging the viewer meant opening a folder of real
processed exports for the first time in a while, which is how a cluster of these surfaced at once:
a Store listing puts the viewer in front of people whose folders look like this one, so these are
release blockers rather than backlog.

Everything below was reproduced on
`C:\Users\<user>\OneDrive\Pictures\Astro\My` and `..\Tests` (15 TIFFs, 3 FITS).

**Eight of the original ten are fixed** (P1-P8). P9 and P10 remain UNCONFIRMED: each was seen once
in a screenshot, and neither is worth chasing without a reproduction. **P11-P14 were added on
2026-08-22 from the user's own notes** and are not packaging defects but the next release's list:
they are here rather than in the backlog because they are what a Store user meets first (no version
string, no way to know whether Enhance has a backend, no documentation, and a second window where
the open one was empty). P1, P2 and P5 are in
`Codecs` and reach CI through **SharpAstro codecs 3.10.721** (released 2026-08-20; `src/Directory.Packages.props`
floats `3.10.*`); the rest are in this repo.

**"NEXT RELEASE" below means the one after 7.0.1513**, which went to the Store on 2026-09-04 without
P11.2, P13, P18 or P19. It did carry P17 and P11.1, the two fixed items that 6.3.1352 had missed.

**A local build proves nothing about these three.** `UseLocalSiblings` self-enables when the `../Codecs`
clone is present, so an ordinary build compiles the sibling source and never touches the pin -- which is
exactly the state the fixes were developed in. Verified on the package path instead:
`dotnet build TianWen.slnx -c Release -p:UseLocalSiblings=false` restores 3.10.721 and builds clean, and
the 35 TIFF / import / codec tests pass against it.

## Status

| # | Item | State |
|---|------|-------|
| P1 | TIFF Predictor 2 never inverted -> images decode to their own derivative | **FIXED**, shipped in codecs 3.9.711 |
| P2 | TIFF reader allocated per strip (2k-5.5k strips per file) | **FIXED** with P1 |
| P3 | Plate solve of a non-FITS document fails, and aborts the solver chain | **FIXED** |
| P4 | CMYK TIFF renders as a negative | **FIXED** (was briefly backlogged) |
| P5 | LZW TIFF unsupported | **FIXED**, shipped in codecs 3.10.721 |
| P6 | Un-clicking Calibrate does not restore the previous / slider WB | **FIXED** |
| P7 | File list is imperative: no declared regions, no cursor, no tooltip | **FIXED** |
| P8 | A solver exception puts raw binary in the status bar | **FIXED** |
| P9 | Stray text fragment at the left edge (needs confirmation) | UNCONFIRMED |
| P10 | Hand-off may select the adjacent row (needs confirmation) | UNCONFIRMED |
| P11 | `--help` reports no version, and the AI enhancer status is invisible | **HALF FIXED**: version + status shipped, model download remains |
| P12 | Gain/ISO and offset are parsed but never shown in the info pane | **FIXED** |
| P13 | No in-depth user documentation | NEXT RELEASE |
| P14 | An EMPTY instance does not adopt a file, because the gate is folder-scoped | **FIXED** |
| P15 | A faint residue is left at the end of the cursor readout (damage-era) | OPEN |
| P16 | `Frame: None` printed the enum default as if it were a frame kind | **FIXED** |
| P17 | Right-click on the image copies nothing (RA/Dec, value, position) | **FIXED** |
| P18 | No Save at all; Open is a word where an icon would do | **FIXED** 2026-09-04 |
| P19 | Stepping between frames re-solves everything, so there is no blink | **FIXED** 2026-09-04 |
| P20 | A share link to the web viewer (needs `&t=`) | BACKLOG (web) |
| P21 | A mosaic's channel views show the mosaic, not the debayered planes | BACKLOG |
| P22 | Save the ANNOTATED view (grid, markers, labels), beside P18's clean raster | BACKLOG (split off P18 2026-09-04) |

---

## P1. TIFF Predictor 2 was never inverted  — FIXED

**Symptom.** A 16-bit Deflate TIFF from GraXpert opened as an embossed grey relief map of the real
nebula; other files from the same folder opened as pure colour noise. Statistics read
`med=0.0  mean=0.5  MAD=0.5` on all three channels.

**Root cause.** A *predictor* is a reversible transform applied before compression so the
compressor sees smaller, more repetitive numbers. `SharpAstro.Tiff.TiffReader` inflated Deflate
strips but never read tag 317, so it never inverted the transform, leaving the **horizontal
derivative** of the image in the buffer. `TiffTag` did not even define the tag.

This is the silent class of wrong: correct dimensions, correct channel count, a full-size buffer,
no exception. And Predictor 2 is not an exotic corner — it is what essentially every writer turns
on alongside ZIP compression (Photoshop, PixInsight, GraXpert, `libtiff -c zip`).

Why it looked like two different bugs: the derivative of a *smooth* image is small and structured,
so it reads as an emboss; the derivative of an already-denoised-and-stretched image is
uncorrelated, so it reads as noise.

**Fix** (`Codecs`, `src/SharpAstro.Tiff/`): `TiffTag.Predictor = 317`, a documented `TiffPredictor`
enum, `UndoHorizontalDifferencing` (a wrapping running sum per row per channel, run after the
endian swap so the arithmetic is plain host-order), and a `NotSupportedException` for Predictor 3
rather than decoding past it.

**Corpus sweep.** An independent Python re-implementation (`tools/`-style throwaway, not committed)
decoded all 15 TIFFs and scored "roughness" = mean |dx| along a row over the robust value range. A
real astronomical image is locally smooth, so this is small; a derivative is not.

| File | Layout | roughness as-stored | inverted |
|------|--------|--------------------:|---------:|
| `Great_Orion_Nebula-RGB-session_H-Alpha-reg_graxpert_stretched` | 16b Deflate pred2, 2864 strips | 0.3752 | 0.0067 |
| `Southern_Pinwheel_Galaxy-...-csc_graxpert_denoised_..._adobe_rgb` | 16b Deflate pred2, 2701 strips | 0.5413 | 0.0080 |
| `Southern_Pinwheel_Galaxy-...-csc_graxpert_denoised_..._printer` | 8b Deflate pred2 **CMYK**, 2701 | 0.5499 | 0.0209 |
| `Southern_Pinwheel_Galaxy-RGB-session_1-crop-lpc-cbg-csc_gr...` | 16b Deflate pred2, 2451 strips | 0.5513 | 0.0084 |
| `Orion-RGB-session_1-cbg-St-v3` | 16b Deflate pred2, 2009 strips | 0.3594 | 0.0202 |
| `Rim_Nebula-RGB-session_1-crop_graxpert_bge_graxpert_stretched` | 16b Deflate pred2, 5529 strips | 0.4697 | 0.0128 |
| `eta_Car_Duo-RGBHOO_graxpert_stretched_v2` | 16b Deflate pred2, 4045 strips | 0.4213 | 0.0205 |

**7 of 15 files were affected** — every one that used Deflate. All eight uncompressed files were
always fine, which is why this went unnoticed: the sample data that gets tested is uncompressed.

Three uncompressed files scored above an arbitrary 0.05 "suspect" cut (`Comet 2021 A1` 0.2052,
`SMC_RGB_Drizzle` 0.0532, `Tarantula_Nebula` 0.0517). **None of them uses a predictor**, so this
bug cannot apply; they are noise-dominated linear/drizzled frames and the threshold is the thing at
fault, not the files. Recorded only so the number is not mistaken for a finding later.

**Tests.** `tests/SharpAstro.Codecs.Tests/TiffPredictorTests.cs` — hand-built fixtures (the writer
emits no predictors, so a round-trip through it could not reach this code). Verified **red before
the fix and green after**, and the fixtures assert their own premise (strip 0 must inflate back to
the bytes meant to be stored) so that a builder bug cannot masquerade as a reader bug — which it
did twice while these were being written. Full suite: 1704 passed, 0 failed.

## P2. The reader allocated per strip  — FIXED

Found while fixing P1. `InflateInto` did `strip.ToArray()` plus `new MemoryStream` **per strip**,
under a comment claiming "in practice strips are large enough that this is irrelevant". The
opposite is true for exactly the files that reach this code: a writer emitting ZIP compression
emits `RowsPerStrip=1`, so the corpus above runs **2,009 to 5,529 strips per image**. That is
thousands of short-lived arrays and streams per decode, on a path whose entire output is one
contiguous buffer.

Now one pooled scratch buffer and one reused `MemoryStream` for the whole loop; an uncompressed
page allocates neither. Only the per-strip `ZLibStream` remains, which is inherent — each strip is
an independent zlib stream. `Read(Stream)` also pre-sizes its slurp, since `MemoryStream` grows by
doubling and a 100 MB TIFF otherwise reallocates a dozen times before a byte is decoded.

**One trap worth remembering: `MemoryStream.SetLength` zero-fills when it grows.** Setting the
length *after* copying a strip in therefore wipes the tail of any strip longer than its
predecessor. It is silent and data-dependent — strips of a smooth image all deflate to
near-identical sizes and never grow — so it passed the smooth fixture and failed the differenced
one. SetLength goes *before* the copy.

## P3. Plate solve of a non-FITS document

**Symptom.** Plate-solving an open `.tiff` reports `Plate solve error: Not FITS format at 0:II*`
(`II*` being the TIFF magic). The user's question — "why did the catalog solver fail?" — has two
separate answers.

**This is NOT the external-solver path.** `ExternalProcessPlateSolverBase.MaterialiseSolvableFileAsync`
already detects a non-native extension and converts it to a temp FITS, precisely so a `.fz` or
`.tif` does not reach ASTAP as an unreadable file. That mechanism is correct and untouched.

**Cause (a).** `CatalogPlateSolver.SolveFileAsync` reads its input with
`Image.TryReadFitsFile(...)` — FITS only — while its sibling solvers go through the
format-agnostic conversion above. On a TIFF, FITS.Lib throws `IOException` from
`Header.cs:1434`.

**Cause (b), the worse half.** `PlateSolverFactory.SolveFileAsync` catches only
`PlateSolverException`. An `IOException` from one solver therefore escapes the whole loop, so
ASTAP — which *would* have converted the file and tried — never gets a turn. One solver's
unexpected failure takes down the chain whose entire purpose is fallback.

**Cause (c), and the honest answer to the question.** Even with the read fixed, `CatalogPlateSolver`
requires a `searchOrigin` and returns no solution without one. A TIFF carries no WCS headers, so
there is nothing to seed it with. It is a hint-refining solver (~6 matched stars, used by the polar
alignment refine loop), not a blind solver; blind solving is ASTAP's job. So "catalog solver failed
on a TIFF" is partly correct behaviour that was reported as an error.

**Fix.**
1. `CatalogPlateSolver.SolveFileAsync` reads via the format-agnostic path, keeping
   `TryReadFitsFile` only to harvest a FITS file's own WCS as a search origin.
2. Widen the factory's catch to record any exception as that solver's failure and continue, so the
   chain degrades instead of aborting. The existing `attempts` list already exists to say what each
   solver reported.
3. Consider having `AstroImageDocument.PlateSolveAsync` call `SolveImageAsync` with the image it
   already holds rather than `SolveFileAsync(_filePath, ...)` — it has the decoded pixels *and*
   1038 detected stars in memory at that point, so re-reading the file from disk is both slower and
   the reason the format matters at all. Keep the file path only where a FITS header hint is wanted.

## P4. CMYK TIFF renders as a negative  — BACKLOGGED

**Symptom.** `..._stretched_printer.tiff` renders with a white sky and dark stars, cyan-tinted, and
reports `3ch`.

**Root cause.** It is `Photometric = 5` (Separated/CMYK) with `SamplesPerPixel = 4`. The first
three samples (C, M, Y) are being read as R, G, B and K is dropped. In CMYK a high value means
*more ink*, i.e. darker, so the polarity inverts.

`SharpAstro.Tiff.TiffImageDecoder` already declares this out of scope
(`if (page.Photometric is not (MinIsBlack or Rgb)) return false;`) — but TianWen's
`Image.Import.cs` calls `TiffReader.Read(bytes)` **directly**, bypassing that guard.

**Decision needed.** Either honour the existing guard so the file fails to open with a clear
message, or convert CMYK->RGB. Accurate conversion needs the embedded ICC profile (the file has
one); the naive `R = (1-C)(1-K)` form is what most viewers do and would at least fix the polarity.
Recommendation: honour the guard (consistent with a decision the codebase already made) and treat
conversion as a separate, optional feature — a printer proof is a print export, not a working
frame. Worth confirming, since today the file shows *something*.

## P5. LZW TIFF unsupported

`2019-04-28-0908_7-RGB_g6_ap24_Drizzle15 (1).tif` is 16-bit **LZW**, and the reader throws. LZW is
the other historical default TIFF compression, so this is a real gap rather than an exotic one —
1 of 15 files in this corpus. Currently documented scope, not a regression. Needs a decoder in
`SharpAstro.Tiff`.

## P6. Un-clicking Calibrate does not restore the previous WB

Toggling `Calibrate` off leaves the calibrated white balance in place instead of restoring the
slider-based / previous triple. The toggle is not symmetric: the calibrated values are written
somewhere the un-click does not undo. Needs the pre-calibration triple stashed on activation and
restored on deactivation, and it must interact correctly with the manual sliders (which are a
*separate* multiplier from the auto calibration — see the two WB facts in `CLAUDE.md`, where
`shaderWhiteBalance = auto x manual`).

## P7. Make the file list a normal declarative list

The file list is the last imperative surface in the viewer. Rows **are** clickable — a tap on
release goes through `_fileListScroll.TakeAtomTap()` in `ImageRendererBase.Input.cs` — but the
rows are hit-tested from geometry and register **no `ClickableRegion`**. Consequences, all of which
the user hit:

- the inspector cannot see or drive them (`describe_ui` lists only the resize handle), so
  `click_label` on a filename fails and any UI test must click raw pixels;
- they declare no `CursorKind`, so the pointer never changes over them;
- there is no hover tooltip, so a truncated name (`Great_Orion_Nebula-RGB-s..`) cannot be read at
  all — and every file in a real export folder has a long name.

**Fix.** Rebuild the pane with the layout DSL and declare the regions, per the rules in
`CLAUDE.md`: paint via `PaintLayout` so draw-rect == hit-rect by construction, `.Clickable(...)`
per row with a `CursorKind`, and a tooltip carrying the full filename. The tooltip machinery
already exists — `_hoveredTooltip` + `RenderToolbarTooltip` in `ImageRendererBase.Toolbar.cs`,
already gated on `ViewerState.OverlayOwnsPointer` — so this is adoption, not new mechanism. Keep
the existing scroll/drag behaviour and the resize handle.

## P8. A solver exception puts raw binary in the status bar

The status bar showed `Plate solve error: Not FITS format at 0:II*` followed by junk glyphs.
FITS.Lib's message interpolates the raw bytes it read (`cbuf`), so a TIFF header's binary lands in
a user-facing string. Independent of P3 — even once the solver stops failing, an exception message
must not carry unprintable bytes into the UI. Sanitise at the status-message boundary.

## P9. Stray text fragment at the left edge  — UNCONFIRMED

One frame showed `576  B=1.002` — the tail of the info panel's `Calibrated R=0.576 B=1.002` — drawn
at the far left edge, roughly mid-height, far outside the info panel's rect. Seen once, in a
screenshot taken while a file was loading, so it may be a transient mid-frame artifact rather than
a placement bug. Needs a reproduction before it is worth chasing.

## P10. Hand-off may select the adjacent row  — UNCONFIRMED

A folder-keyed hand-off of `Great_Orion_Nebula-RGB-session_H-Alpha-reg_graxpert_stretched.tiff`
(row 2) left the running window showing `Great_Orion_Nebula_RGBHOO-...` (row 3). The hand-off
itself worked — the second process exited 0 and one process remained — so this is about which row
`ViewerActions.ScanFolder(state, folder, fileName)` selects. It could equally have been a manual
click during the same interval, so it needs an isolated repro: hand off a distinctly-named file
with nothing else touching the window, and assert the selected index.

## P11. `--help` reports no version, and the AI enhancer status is invisible  — NEXT RELEASE

Raised by the user 2026-08-22. Two things a person needs before they can file a useful bug, and
neither is reachable today.

- **Version with help.** `--help` should print the build version. It derives from the single
  `VersionMajorMinor` in `src/Directory.Build.props` (see CLAUDE.md), so this is a read of
  `VersionPrefix`, not a new number to maintain. A Store user has no other way to say which build
  they are on.
- **AI discovery status + download options.** The Enhance button is presence-gated on
  `EnhanceAvailable`, so where no backend resolved it simply is not there, which is
  indistinguishable from "this build has no enhance feature". The viewer should be able to report
  which backend it would use (RC-Astro vs SAS vs none), which RC products are licensed, and which
  SAS model files are missing -- plus an affordance to fetch the missing ones, because
  `tools/tianwen-ai-models-fetch.ps1` is a repo script and a Store install has no access to it.

**The trap:** the RC-vs-SAS choice and its blocking license probe are *deliberately* deferred to the
first `EnhanceAsync`, so that composing the service collection spawns no `rc-astro` process. A
status readout must not undo that by probing at startup. Either populate it lazily (report "not
probed yet" until something asks) or make the probe an explicit user action in the status view.

## P12. Gain/ISO and offset are parsed but never shown in the info pane  — FIXED

Raised by the user 2026-08-22. `ImageMeta` already carried `Gain` (FITS `GAIN`) and `Offset` (FITS
`OFFSET`, `BLKLEVEL`), both `-1` when unknown, and TianWen writes both on every frame it captures --
so the gain and offset half was purely a rendering gap in `InfoPanelData.GetMetadataLines`, and is now
three rows beside `Exposure` (the same fact: what the camera was set to), each **suppressed at `-1`**
rather than printed, so a header that carried nothing produces no row.

**The ISO half needed one field, which is more than "rendering".** There was no ISO anywhere in
`ImageMeta`, and it must not be folded into `Gain`: that is a `short` sized for a sensor gain register
and ISO 51200 does not fit in one. So `ImageMeta` gained `int Iso = -1` (appended, so every positional
caller is unaffected) and the Canon raw importer now carries `raw.Exif?.Iso` through -- SharpAstro.Exif
already parsed it, `BuildCanonRawImageMeta` simply dropped it on the floor. A file carries gain or ISO,
never both, so the panel shows whichever it has.

Pinned by `InfoPanelMetadataTests` (4): presence, the order beside `Exposure`, ISO-instead-of-gain for a
raw, and the suppression case which additionally asserts **no row anywhere contains `-1`** -- because
the failure mode of a formatted-unconditionally row is `Gain: -1`, which reads like a real value out of
a real header. Live-verified: the ZWO ASI120MC plate-solve fixture renders `Gain: 48` with no `Offset`
row, that file having no OFFSET card.

## P13. No in-depth user documentation  — NEXT RELEASE

Raised by the user 2026-08-22. The viewer ships to the Microsoft Store as Astro Photo Viewer with no
user-facing documentation at all: the keyboard shortcuts, the stretch model (and what Linked /
Unlinked / Luma actually do), Calibrate versus the manual WB sliders, the wavelet layers, plate
solve, Enhance, the SER transport, and the file associations plus the single-instance behaviour are
all discoverable only by experiment or by reading `CLAUDE.md`. The Store listing needs somewhere to
point, and P11's version line needs a document to sit beside.

## P14. An EMPTY instance does not adopt a file  — FIXED

Raised by the user 2026-08-22: *"if instance is empty (no folder open), opening any file should
re-use that instance."*

Today the gate is **folder-scoped** by design -- one primary per normalised folder, and the pipe name
IS the identity, which is what avoids enumerating live instances (see
[../architecture/desktop-shell.md](../architecture/desktop-shell.md)). The consequence is the
reported behaviour: a window with *nothing* open holds a claim on no folder at all, so a file from
any folder misses it and spawns a second process, which is exactly the cost the hand-off exists to
avoid.

**The "no folder" identity needed no sentinel.** `InstanceGate.ChannelFor(scope, identity)` defaults
`identity` to the empty string and `NormalizePathIdentity` always returns an absolute path, so the
empty identity is both collision-free and already exactly what the API's default means. An instance
launched with no folder now claims it.

**The order is folder first, then empty.** A window already showing the file's folder is the more
specific answer and keeps winning, so the file lands in the list the user is already looking at; only
when nothing holds that folder does an empty window get offered it. The launching process releases the
folder claim it had just taken before exiting, so the adopter's re-bind can take it.

**Nothing new was needed for the re-bind, as predicted:** `PumpInstanceGate` already keys on
`state.CurrentFolder` changing, so adopting a file moves the claim off the empty identity and onto that
file's folder by itself. Verified in the log, in order: the first launch reports *"Handed ... to the
instance with nothing open"*, and a second launch of the same file then reports *"Handed ... to the
instance already showing ...\p14"* -- which is only possible if the claim moved.

**The two open questions, decided:** two empty instances *race, and that is fine* (the user's call) --
one holds the empty channel, the loser opens what it was given, which is the existing "failure is never
fatal" rule. And a NON-empty instance still never adopts across folders: that is the folder-scoping
decision, and adopting there would silently replace the folder someone is looking at.

Live-verified end to end: an empty viewer plus a second launch carrying a file leaves **one** process
(the original), the second exiting 0 with no window, and the survivor shows `Files (scratchpad/p14)`
with `plate.fits` selected and loaded.

P10 is in the same code path (`ViewerActions.ScanFolder(state, folder, fileName)` choosing the selected
row after a hand-off) and is still worth reproducing; the hand-offs above selected the right row every
time.

## P15. A faint residue at the end of the cursor readout  — OPEN

Reported by the user 2026-08-22: *"the pointer now sometimes leaves a very faint ) at the end of the
position when moving pointer left to the file list, that might be a residue from the clip rect or
something. not dramatic but noticable"*.

**"Now" is the important word.** The readout is `Pos: (x, y)` from `InfoPanelData.GetCursorLines`, and
the pointer leaving the image pane clears it. Before damage-based repaint every frame cleared the
whole surface, so anything painted outside a fill was wiped for free; now only the declared damage is
repainted, and whatever is not covered by an actual paint inside it survives. So this is a
damage-era regression, not a new drawing bug.

**What has already been ruled out:** the float-to-integer conversion. `ClampToSwapchain`
(SdlVulkan.Renderer) truncates the near edge and takes `MathF.Ceiling` on the far edge, so the
scissor already covers the whole of a rect that ends mid-pixel -- the obvious cause is not the cause.
`ApplyClip` likewise intersects a widget clip with the damage region, so a clipped draw cannot escape
it either.

**The leading hypothesis is that the erase is narrower than the text was.** The narrowing declares
`_layout.InfoPanel` (and `_layout.StatusBar`) as damaged, but the repaint fills the panel's own
background rect; if a glyph's antialiased fringe extended a fraction past that fill, the fringe now
has nothing to cover it. That matches the symptom exactly -- a *very faint* remnant of one glyph
rather than a whole one, and the last glyph of the longest line at that. Worth measuring before
fixing: capture the panel's arranged rect and the measured width of the `Pos:` line at the DPI in
use, and compare.

**Two candidate fixes, and the choice matters.** Inflating the damage rect by a pixel would hide it
cheaply and would also hide the next one like it. Finding why text extends past its own background is
the root fix, and it generalises: any panel that draws text to its edge has the same exposure.

The high-level counters in
[inspector-high-level-telemetry.md](inspector-high-level-telemetry.md) would have made this
measurable from inside the app (damage area versus painted area) instead of by eye.

## P16. `Frame: None` printed the enum default as a value  — FIXED

Noticed by the user 2026-08-22 while reading the P12 screenshot: the info panel said `Frame: None`.
`FrameType.None` is the enum's zero, i.e. the file carried no `IMAGETYP` / `FRAMETYP` card (or one
that did not map), so the row was naming the absence of a fact as though it were a frame kind -- the
same failure as `Gain: -1`, one row down, and found the same way. The row is now suppressed for
`None` as well as for the unremarkable `Light`. Pinned by two more cases in
`InfoPanelMetadataTests` (unstated produces no row; a real `Flat` is still named).

## P17. Right-click on the image copies nothing  — FIXED

From the user's notes 2026-08-27: *"support right click (alternatively alt-click) to copy colour or
RA/Dec coord ... right click menu would be cool too"*, and on priority: *"the colour copy is the more
niche op, the more prominent one is certainly the RA/Dec copy"*.

Shipped as a context menu rather than modifier-clicks: a chord has to be documented somewhere to be
discovered, and the only place it could be documented is the panel nobody opens until something is
already wrong. The menu also gives the share link (P20) a home. Items, in order: **RA / Dec**
(sexagesimal as the info panel prints it, with decimal degrees on a second line because that is what
most tools take as input), the per-channel **value** (unit and 16-bit forms), and the **position**.
Every label carries its own value, so the menu answers the question without anything being copied.

**It computes nothing**: every mouse move already resolves `ViewerState.CursorPixelInfo`, so this is a
formatter over existing state (`ImageContextMenu`, non-generic and testable without a GPU, the same
split `InfoPanelData` makes). The **displayed colour is deliberately absent** -- it is a different
number (post stretch / WB / curves / HDR), the GPU owns it, a swapchain readback is the one operation
that wedges the render loop, and recomputing it on the CPU through the stretch mirror is a feature of
its own. A first attempt added exactly that and was reverted before it shipped.

It reuses `ViewerState.ToolbarDropdown` (keyboard claim, hover, scrolling, dismissal and the
`OverlayOwnsPointer` z-order answer already live there) and is called from **both** press dispatchers.

**It also surfaced that no viewer menu had ever had a hover state.** `RenderDropdownMenu` resolves its
row highlight from `PixelWidgetBase.Pointer`, and nothing in the viewer ever set it, so Zoom, the `?`
panel and the new menu all showed only the keyboard's `HighlightIndex`. `Render` now sets it from the
position both hosts already track, and a pointer move repaints while a dropdown is open.

## P18. No Save at all, and Open is a word where an icon would do  — FIXED

**Shipped 2026-09-04.** `DisplayRasterExport` (TianWen.Lib) renders through the CPU mirror of the
shader and writes PNG-16 / JPEG / float TIFF; `IFileDialogHelper.SaveAsync` is the save dialog none
of the three platforms had. "As seen" was settled as the CLEAN raster at full IMAGE resolution --
the annotated variant is P22. Open and Save became hand-drawn marks after every candidate glyph
baked solid (measurements in that commit, reasoning in `DrawFolderMark`'s remarks), and the toolbar
no longer wraps to a second row, which was the stated point of iconising them. Left out
deliberately: 8-bit PNG is in the API with no UI, because it shares `.png` with the 16-bit variant
and choosing it needs a menu that does not exist.

The original note follows.

From the user's notes 2026-08-27: *"Save as seen on screen option. Iconize Open (and Save)"*, *"+
Shift or whatever Save-As (choose png, jpeg, and what else we have)"*.

There is no `Save` in `ToolbarAction` today. Three parts, and the first is the one with a decision in
it: **"as seen on screen"** means the display raster, i.e. the stretch, WB, curves, HDR and channel
view currently applied -- which is exactly what `Image.RenderStretchedRgba` produces on the CPU, so
the value is available without a framebuffer readback. Note the viewer would then have a second
consumer of that path, which is an argument for the single-pixel helper P17 rejected.

Format choice: the codecs facade already writes PNG (8 and 16 bit, cICP, iCCP), JPEG, TIFF (float32,
the `[0,1]` + SMin/SMax convention) and EXR, so Save-As is a picker over what
`SharpAstro.Codecs` supports rather than new encoders. 16-bit PNG and float TIFF are the interesting
ones: "as seen" in 16 bit is lossless against the display raster.

Iconizing Open (and Save) frees toolbar width, which the two-row wrap makes measurable rather than
cosmetic. Marks go through `DrawToolbarMark` as `Content.Icon`, never a symbol character in a text run.

## P19. Stepping between frames re-solves everything, so there is no blink  — **FIXED** 2026-09-04

From the user's notes 2026-08-27: *"in folder open mode, when moving between one raw frame of same
type (.fits, etc) we copy over the calibration/stretch params etc so that they load faster"*, and then
*"did my task list also contain the blink mode, where we scroll through the file list one by one (if
the frames have same dims etc)"* -- it did not, and the two are the same item: **the param carry-over
is what makes a blink possible.** Without it each frame solves its own auto-stretch, so a sequence
flickers in brightness rather than showing what moved.

Two halves:
- **Carry the display state across frames of the same shape.** Same dimensions, same channel count,
  same declared depth, and same filter where stated. `AstroImageDocument.InheritColorCalibration`
  already exists for the enhance case and is the precedent for the WB triple; the stretch uniforms and
  the background neutralisation are the rest. Background neutralisation is re-solved per document by
  design elsewhere, so blink needs an explicit "hold it" mode rather than the default.
- **A transport over the file list.** The SER path already has `Space` play/pause and `Left`/`Right`
  frame stepping, and `Up`/`Down` already step files; blink is that transport pointed at the file list
  with a fixed interval, gated on the frames being comparable.

**Fixed as planned, with one design decision the plan had left open: the anchor is a DOCUMENT, not a
snapshot of its numbers.** `DisplayCarry` (`TianWen.UI.Abstractions`) decides which frame's statistics a
document is shown with; `AstroImageDocument.DisplayAnchor` is the one-hop reference, and every display
read (`PerChannelStats`, `LumaStats`, `StarMaskedStats`, `ChannelStatistics`, `PerChannelBackground`,
`LumaBackground`, `MaxValue`, `ColorCalibration` + its summary) goes through a private `Basis` accessor
that resolves to the anchor or to `this`.

- **Why a document and not a snapshot.** The anchor's own numbers arrive over TIME -- the SPCC triple
  seconds after the load, the star-masked background later still. A snapshot taken at adoption would be
  stale in both, and worse, the ANCHOR would go on rendering from its live values: the frame the run is
  measured against would then look different from every frame following it, which is the flicker the
  carry exists to remove. Reading through the anchor means there is one set of numbers by construction.
  The cost is one retained document per browsing run, released when the folder changes.
- **Comparability is `FrameShape`**: width, height, plane count, `BitDepth`, `SensorType`, and the
  filter's `IdentityKey`. The filter test is deliberately not symmetric-transitive -- a frame naming no
  filter is comparable to one that does, because a folder where only some frames carry a FILTER card is
  the common case and refusing there would disable the feature on the archives it was asked for. Every
  comparison is against ONE anchor, so the missing transitivity never has to hold.
- **The carry is ON by default** (`ViewerState.CarryDisplayAcrossFrames`), which is what the user asked
  for ("we copy over the calibration/stretch params etc so that they load faster"). It is also the
  "load faster" half by itself: a follower reports the anchor's `ColorCalibration`, so the auto-retrigger
  in `RestoreDocumentCalibration` never fires and the SPCC fit runs once per run instead of once per file.
- **The readout stays honest.** `MeasuredPerChannelBackground` / `MeasuredLumaBackground` are the
  frame's OWN numbers and are what the info panel prints; the status bar declares "Held to <file>"
  whenever a frame is not being shown with its own stretch. The carry is invisible by design, so the
  only defensible way to ship it is to say so on screen.
- **Blink** is `ViewerController.TickBlink`, ticked from the host loop beside `TickPlayback` for the
  same reason (it is what paces the step without a busy-spin). A step is never queued behind a load, and
  the renderer STOPS the blink, naming the file, when a frame arrives that the anchor cannot describe: a
  blink through two different fields compares nothing.
- **The keys are a transport, so Shift is the other DIRECTION**, not a mode: `Space` runs the blink
  forward (the SER transport still claims Space while a sequence is loaded), `Shift+Space` runs it
  backward, and pressing a direction while already running that way pauses while pressing the other one
  reverses -- so turning a comparison around never takes two presses. `Ctrl+Space` returns to the frame
  the run is HELD to, which is the affordance stepping cannot give: once a blink has walked several
  files, going back to the reference otherwise means finding it in the list by name, and the list does
  not mark it. Hold/release moved to `Shift+H`. The first cut put it on `Shift+Space`, where it read as
  a direction to anyone who has used a transport.
- **One residual, deliberate.** `ComputeBackgroundNeutralization`'s per-method gain cache now keys on the
  background ARRAY as well as the method and WB -- it is replaced rather than mutated, by star detection
  and by an anchor being taken up or dropped, and without that the pre-mask gains were served for the
  life of the document. That was a pre-existing staleness the carry would have widened.

Pinned by `DisplayCarryTests` (14), whose discriminating pair is that two frames with genuinely
different statistics render the SAME uniforms with an anchor and DIFFERENT ones without -- confirmed by
breaking `Basis` and watching three of them go red.

## P20. A share link to the web viewer  — BACKLOG (needs the web side)

From the user's notes 2026-08-27: *"right click menu could also have an option to create a share link
that shares direct links to the Tianwen website viewer, we would need to add a `&t=<time of capture>`
support for the links as well"*. The menu from P17 is where it goes. Blocked on the web build
accepting the parameters, so it is tracked with the web items rather than here.

## P21. A mosaic's channel views show the mosaic, not the debayered planes  — BACKLOG

From the user's notes 2026-08-27: *"use the new AsChannel* to show debayered channels?"*, resolved in
conversation to `Channel.AsSpan()` (from the `ImmutableArray<Channel>` constructor work) -- there is
no `AsChannel*` API anywhere in tianwen, DIR.Lib or Codecs, and no commit mentions one.

The real gap: `ChannelView.DisplayedSourceChannel` clamps to the channels the IMAGE has, so on a
1-channel RGGB mosaic Red / Green / Blue all resolve to channel 0, which is the mosaic itself. The
viewer never CPU-debayers by design (the GPU shader does it), so the cheap shape is a shader-side
"isolate channel N of the debayered result" rather than materialising planes on the CPU -- the
fragment shader already computes the RGB triple in `debayerBilinear` / `debayerMhc`. The cursor
readout is the part that would still need CPU values, which is where a `Channel.AsSpan()` view earns
its place. Deferred as agreed: *"we can skip the extract synthetic channel from debayer for now if too
hard. backlog it if we can't deliver it now."*

---

## Phasing

| Phase | Items | Rationale |
|-------|-------|-----------|
| A | P1, P2 | Done. Blocked only on a `SharpAstro.Tiff` release; TianWen's codec pin is a wildcard within the minor, so CI picks it up without a pin edit. |
| B | P3, P8 | DONE. Same user-visible failure, and P8 is the reason P3 looked like a parse bug rather than a solver-chain bug. |
| C | P7 | DONE. Self-contained, and it unblocks UI testing of everything else in the viewer. |
| D | P4, P6 | DONE. Correctness of presentation. P4 was briefly backlogged, then done anyway once P1 made the file decode at all. |
| E | P5 | DONE. An LZW decoder; the only item whose absence was already documented scope. |
| F | P9, P10 | Reproduce first; do not fix from a single screenshot. |
| G | **P12 + P14 DONE 2026-08-22**; **P11's version + AI status DONE 2026-08-27**; P11's model download and P13 remain | The next release, in that order: P12 is a two-row rendering gap, P11 is a read of an existing property plus a lazily-populated status, P14 is a host-policy change with two cases to settle, and P13 is best written last so it documents what P11/P14 actually do. |
| H | **P17 DONE 2026-08-27**; **P18 + P19 DONE 2026-09-04**; P20, P21, P22 backlogged | The second wave of the user's notes. P17 first because it is a formatter over state that already existed, and it is what found the missing dropdown hover state. P18 and P19 both touch the display raster and the file list, so they share a sitting. P20 waits on the web build; P21 is a shader change whose cheap form is not obvious yet. |

## Verification

- **P1/P2**: `dotnet test tests/SharpAstro.Codecs.Tests --filter TiffPredictorTests` in `Codecs`,
  plus re-running the corpus sweep and confirming every predictor file lands under ~0.03 roughness.
  Already done; re-run after the release to confirm the packaged build picks it up.
- **P3**: plate-solve an open TIFF and a `.fz`, and confirm the log's `attempts` line names each
  solver and its reason rather than the run ending on an exception.
- **P7**: `describe_ui` must list one region per visible row with a label, and `click_label` on a
  filename must select it. That is the acceptance test the current pane fails.
- **P6**: visual, against the specific files named above.

## P22. Save the ANNOTATED view  — BACKLOG

Split off P18 on 2026-09-04, when the user chose the clean raster for "as seen on screen" and asked
for the annotated variant to be backlogged rather than dropped.

P18 saves what `Image.RenderStretchedRgba` produces: the stretch, WB, curves, HDR and channel view,
at **full image resolution**, because that path needs no framebuffer readback and so is not bounded
by the window. Everything drawn OVER the image -- the WCS grid, star markers, object labels -- is
absent by construction.

The machinery for the annotated version exists: `PlateSolveAnnotator` already draws overlays onto a
CPU raster through the same `RenderStretchedRgba` + `StretchSolver` path. What makes it a backlog
item rather than a flag on P18 is that it is a **second drawing path beside the GPU one**, and the
two will drift: every overlay added to the shader would then owe a CPU twin, with nothing failing
when it is forgotten. If it is picked up, the thing to settle first is whether the CPU annotator
becomes the single source of truth for overlay GEOMETRY (the shader consuming the same computed
placements) rather than a parallel implementation of it.

