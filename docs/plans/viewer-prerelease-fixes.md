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
| P13 | No in-depth user documentation | **FIXED** 2026-09-06 (on the org site) |
| P14 | An EMPTY instance does not adopt a file, because the gate is folder-scoped | **FIXED** |
| P15 | A faint residue is left by a narrowed repaint (damage-era) | **FIXED** 2026-09-06 |
| P16 | `Frame: None` printed the enum default as if it were a frame kind | **FIXED** |
| P17 | Right-click on the image copies nothing (RA/Dec, value, position) | **FIXED** |
| P18 | No Save at all; Open is a word where an icon would do | **FIXED** 2026-09-04 |
| P19 | Stepping between frames re-solves everything, so there is no blink | **FIXED** 2026-09-04 |
| P20 | A share link to the web viewer (needs `&t=`) | **FIXED** 2026-09-04 |
| P21 | A mosaic's channel views show the mosaic, not the debayered planes | BACKLOG |
| P22 | Save the ANNOTATED view (grid, markers, labels), beside P18's clean raster | **FIXED** 2026-09-06 |
| P23 | Blink toggles on every auto-repeat of Space, and the blinked frame can be off-screen | **FIXED** 2026-09-08 (with DIR.Lib 8.14 + SdlVulkan.Renderer 7.33) |
| P24 | The A/B divider leaves its bar behind in the letterbox around the image | **FIXED** 2026-09-08 |
| P25 | No auto-crop: a master's stack artefacts and NaN margins have to be cropped elsewhere | **FIXED** 2026-09-08 |
| P26 | The `?` panel cannot report a bug: no issue, no logs, no version attached | **FIXED** 2026-09-08 |
| P27 | Escape and an open panel: NOT a defect, and now pinned. My first diagnosis was wrong | **VERIFIED** 2026-09-08 |
| P28 | The menu cannot name the object under the cursor, and the atlas link only copies | **FIXED** 2026-09-08 |
| P29 | A touchscreen pinch does not zoom: the events were raised and dropped | **FIXED** 2026-09-09 |

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

## P13. No in-depth user documentation  — FIXED

Raised by the user 2026-08-22. The viewer shipped to the Microsoft Store as Astro Photo Viewer with
no user-facing documentation at all: the keyboard shortcuts, the stretch model (and what Linked /
Unlinked / Luma actually do), Calibrate versus the manual WB sliders, the wavelet layers, plate
solve, Enhance, the SER transport, and the file associations plus the single-instance behaviour were
all discoverable only by experiment or by reading `CLAUDE.md`.

**Shipped 2026-09-06 as `guide/viewer.html` in the `sharpastro.github.io` repo**
(`https://sharpastro.github.io/guide/viewer.html`), linked from the Astro Photo Viewer section of
the landing page, which already carried the Store button and the screenshot. It lives on the org
site rather than in this repo because that is where a Store reader lands and where the product page
already is; the cost is a second repo, and this entry is the pointer.

**Written last, and it earned that.** P15, P18, P19 and P20 all landed after it was raised and all
changed what there was to describe -- Save did not exist, the blink and the display hold did not
exist, and the share link did not exist.

**Every claim is checked against the code rather than recalled**, which matters more here than in
any other entry: a guide is the one artefact that can be confidently wrong for a year. The shortcut
tables come from the app's own `?` panel list (`ShortcutLines`) and from `GetToolbarButtonTooltip`,
the two places the app already documents itself, so the page and the program cannot drift apart on
what a key does without one of those changing too.

**The current limitations are stated rather than omitted**: photometric calibration is
broadband-only, and a mosaic's single-channel views show the mosaic (P21). A guide that lists only
what works is the one a reader stops trusting at their first surprise, and both are things a user
meets by accident.

**As written it listed a third, and P22 retired it four hours later** -- "Save writes the clean
raster with no grid, markers or labels". The guide was updated with the feature (`6fc64c3`, "Saving
a view now includes the annotated one") and the limitation bullet was left behind, so for a day the
page documented the annotated save and listed its absence as a limitation. Corrected 2026-09-07
while assembling the 7.0.1568 submission. **The lesson is the ordering one this entry already
argues, pointed the other way**: writing the guide last is what makes it accurate, and the same
property makes it the first thing stale when the item written last ships anyway. A limitation
retired by a feature is two edits in the guide, not one.

Structured so the CLI, the server and the session runner can join as sibling pages under `/guide/`
without a rewrite. `assets/guide.css` is loaded only by these pages and is written entirely in
`site.css`'s own custom properties, so the landing page's stylesheet is untouched.

**Rendered and read before committing**, which caught three defects the markup was clean through:
consecutive paragraphs ran together (site.css gives its paragraphs their rhythm through the landing
page's own section selectors, so a bare `<p>` inherited none), every chord broke across the line end
at its `+` (adjacent inline-block `<kbd>`s give the browser a break opportunity, so it is one `<kbd>`
per chord now -- which is also how the app's panel writes them), and the Store button was
`btn-primary`, which that site reserves for the sky atlas.

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

## P15. A faint residue left by a narrowed repaint  — FIXED

Reported by the user 2026-08-22: *"the pointer now sometimes leaves a very faint ) at the end of the
position when moving pointer left to the file list, that might be a residue from the clip rect or
something. not dramatic but noticable"*. **"Now" was the important word** -- before damage-based
repaint every frame cleared the whole surface, so anything painted outside a fill was wiped for free.

**The hypothesis this entry carried until 2026-09-06 was wrong, and measuring it is what said so.**
It read: the erase is narrower than the text was, so an antialiased fringe of the `Pos:` line
survives past the info panel's own background fill. Two facts kill it. The panel fills its ENTIRE
rect before drawing anything into it, so nothing inside it can survive; and the host merges damage
into a single bounding BOX (one scissor per draw -- `SwapchainDamage`), so `StatusBar` ∪ `InfoPanel`
comes out as the full width from below the toolbar to the bottom of the window. The info panel is
never the region at risk. The region at risk is the toolbar strip, the only thing that box excludes.

**The measurement is a new suite, `ViewerRepaintResidueTests`, and it is the shape this needed all
along.** `ViewerFrameDamageTests` asserts what was DECLARED; this one asserts what the screen shows.
It models the host exactly, because the host is simple: paint both frames in full, composite the old
one with the new one inside the damage box, and compare against the new one. Every pixel that differs
is one the user can see. Two properties of the harness are load-bearing -- the surface is CLEARED
between frames (painting over the previous contents let an alpha-blended panel background converge
across repaints, and a no-input control frame then differed from its predecessor by 150,307 pixels,
swamping any real residue and reading exactly like one), and a no-input control test is what says the
number means anything at all.

**Two real residues, both in the class the report named, both now fixed:**

- **The before/after divider left its half labels behind** (the user's own repro, 2026-09-06:
  *"when I move the A|B slider to the right there's residue ... on the left side"*). The labels are
  aligned AGAINST the divider and travel with it, so the strip that changed is wider than the
  divider's path. `SweepBetween` widened it by a guessed `SweepLabelMargin = 220f`, which was wrong
  twice over: a DESIGN-unit constant subtracted from a SURFACE-pixel coordinate and never scaled, so
  a 150% display got two thirds of the slack it was owed; and the labels name WHAT DIFFERS between
  the halves, so their width is a property of the user's settings and no constant can bound it. Two
  differing controls already outgrow 220 units at 100%. Measured: **3,193 stale pixels at DPI 1 and
  7,257 at DPI 1.5** -- the DPI factor visible in the numbers. Fixed by measuring instead of guessing:
  the paint already measures both labels to decide whether each fits its own half, and now reports
  them through `SplitCompareController.SetLabelExtents`, before the fit checks so the frame where a
  label stops fitting still erases it.
- **A hover repaint was silently replaced by the readout's narrow region** (the user, same day:
  *"if I hover over a button like Auto, tooltip appears, moving over the actual canvas makes it
  flicker"*). `HandleViewerMouseMove` has three branches that ask for a frame because hover chrome
  changed -- an open menu, the toolbar button highlight and its tooltip, the file-list row -- and each
  sets `NeedsRedraw` and FALLS THROUGH to the narrowing at the end, which declares the readout's two
  rects and nothing else. The two collide exactly when the pointer crosses out of the image pane,
  which is when both change at once. Measured: **4,532 stale pixels** of tooltip and **7,854** of
  file-list row highlight. The tooltip is anchored at its button's bottom edge, inside the toolbar
  strip -- the one band the damage box excludes -- and because damage is tracked PER SWAPCHAIN IMAGE
  the leftovers survive in some images and not others, which is why it reads as a flicker rather than
  as a stuck tooltip. Fixed with a local flag those branches set and the narrowing checks.

**The guard in `ImageRendererBase.Damage.cs` could not have caught the second one**, and that is worth
knowing before trusting it again: it forces a full repaint when an event asks for a frame WITHOUT
narrowing, which covers a non-declaring event in a different dispatch. Here both changes arrive inside
one handler and the narrowing is genuine -- it is simply not the whole truth.

**What the cost is:** one full frame per pane crossing, and none at all while the pointer travels
across the image, which is the 8%-GPU case the mechanism was measured for.
`AMoveWithinTheImageStillRepaintsOnlyTheReadout` pins that, and it is not optional -- a full repaint
trivially satisfies every residue assertion here, so without it "narrow nothing, ever" would pass the
whole file. All six tests were confirmed to fail with each fix removed.

**The original `)` was never reproduced**, and the user reported on 2026-09-06 that they no longer see
it. It is the same class as the two above -- chrome painted outside the declared damage -- and the
likeliest candidate is the file-list header's full-path tooltip, which draws the untruncated folder
name and would end in `)` for any folder named that way. Left as measured rather than asserted.

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
the annotated variant is P22, shipped 2026-09-06. Open and Save became hand-drawn marks after every candidate glyph
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

## P20. A share link to the web viewer  — **FIXED** 2026-09-04

From the user's notes 2026-08-27: *"right click menu could also have an option to create a share link
that shares direct links to the Tianwen website viewer, we would need to add a `&t=<time of capture>`
support for the links as well"*. The menu from P17 is where it goes.

**It was never blocked on anyone else** -- the "web side" is `TianWen.UI.Web`, in this repo. And the
note anticipated one gap where there were two: the web build read exactly two query keys, `view` and
`object`, and `object=` takes a CATALOG TOKEN, so there was no way to point at a plain coordinate
either. A link from an arbitrary plate-solved frame had nothing to say.

**The vocabulary is `SkyAtlasLink` (`TianWen.UI.Abstractions`), defined once for both ends:**
`?view=sky&ra=<deg>&dec=<deg>&fov=<deg>&t=<ISO-8601 Z>`. The viewer's right-click menu writes one;
`Planner.razor` reads it.

- **RA travels in DEGREES while everything internal is in hours.** SIMBAD, Aladin and WWT all take
  degrees, so an hours value pasted anywhere else reads as a 15x-wrong position -- silently, because
  both readings of any number are a legal RA. That is why the conversion is in one place with a test
  rather than at each call site, and why the E2E's tolerance is a tenth of a degree.
- **No SITE in the link, deliberately.** The equatorial view at an instant is the same sky wherever it
  is opened; only the horizon and altitude overlays are site-dependent and those should be the
  recipient's own. Time is what pins the sky, and time is in the link.
- **`fov` is the frame's own** (`SkyAtlasLink.FieldOfViewDeg`: plate scale x the LONG axis), so the
  atlas opens showing roughly what the image covered. An APPROXIMATE WCS still answers -- a
  FOCALLEN-derived scale is a poor astrometric solution but a fine statement about geometry, which is
  all this number is. That is the opposite of the grid labels' rule.
- **A missing capture time drops `t=` entirely.** Both import paths use a SENTINEL rather than a null
  (no `DATE-OBS` parses to `DateTime.MinValue`, no EXIF capture time to the epoch), so an unguarded
  link would have drawn the sky of the year 1. One comparison against the epoch covers both.
- **The menu label does not carry the URL**, which is the one exception to P17's rule that every label
  ends in its own value: a hundred-character link would be the widest thing in the menu and unreadable
  at that size. `ImageContextMenuTests` exempts it BY NAME so any other item that stops carrying its
  value still fails.

**What only the browser E2E could catch, and did on its first run:** `SkyMapTab.Render` installs a
HOME position on its first pass -- the site's local sidereal time at the visible pole -- so the link's
pointing was applied and then overwritten one frame later. Both ends of the URL were individually
correct, so no unit suite on either side could see it; the atlas answered 8.073 h, which is just the
LST at the link's own `t=`. **Setting `Initialized` from outside does not fix it** (the first attempt,
and it failed identically): that same `if` also fires whenever the site differs from the one last
homed for, and the comparison starts against `double.NaN`, so it is unconditionally true the first
time whatever `Initialized` says. The fix is `SkyMapState.ExternalViewPending`, a ONE-SHOT the pass
consumes -- it still records the site, so a later profile switch re-homes as before.

Pinned by `SkyAtlasLinkTests` (20, including the whole URL exactly) plus `ImageContextMenuTests`, and
end-to-end by `TianWen.UI.Web.E2E`'s `ShareLinkTests` (3). The E2E carries the canonical URL as a
LITERAL because that project is deliberately reference-free; the unit test that pins the same string
against the writer is what keeps the two in step, and it names the file.

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
| G | **P12 + P14 DONE 2026-08-22**; **P11's version + AI status DONE 2026-08-27**; **P13 DONE 2026-09-06**; P11's model download remains | P13 was written last on purpose, and by the time it was written the things it had to describe were P15 and phase H's three rather than P11/P14 -- which is the argument for writing it last, not against it. P11.2 (fetching the missing vendor models) is deferred: nothing on the resolution path copies, so an installed SAS Pro or GraXpert is already read in place. |
| H | **P17 DONE 2026-08-27**; **P18 + P19 + P20 DONE 2026-09-04**; **P22 DONE 2026-09-06**; P21 backlogged | The second wave of the user's notes. P17 first because it is a formatter over state that already existed, and it is what found the missing dropdown hover state. P18 and P19 both touch the display raster and the file list, so they share a sitting. P20 waits on the web build; P21 is a shader change whose cheap form is not obvious yet. P22 came off the backlog once the second-drawing-path question had an answer that needed no second path. |

## Verification

- **P1/P2**: `dotnet test tests/SharpAstro.Codecs.Tests --filter TiffPredictorTests` in `Codecs`,
  plus re-running the corpus sweep and confirming every predictor file lands under ~0.03 roughness.
  Already done; re-run after the release to confirm the packaged build picks it up.
- **P3**: plate-solve an open TIFF and a `.fz`, and confirm the log's `attempts` line names each
  solver and its reason rather than the run ending on an exception.
- **P7**: `describe_ui` must list one region per visible row with a label, and `click_label` on a
  filename must select it. That is the acceptance test the current pane fails.
- **P6**: visual, against the specific files named above.

## P22. Save the ANNOTATED view  — FIXED

**Shipped 2026-09-06.** `AnnotatedRasterExport` (TianWen.UI.Abstractions) writes the display raster
with the WCS grid, star markers, object markers and labels, and any caller-supplied `WcsAnnotation`
drawn over it, at the image's own resolution. Reached from the Save toolbar button, which now opens
a two-row dropdown -- "Image as displayed..." and "Image with overlays..." -- while a right-click
still saves the clean raster in one click.

**The question the backlog entry said to settle first was settled by NOT writing a second drawing
path.** The export runs `ImageRendererBase<TSurface>` itself -- the same class the window is drawn
by -- over a CPU `RgbaImageRenderer` surface the size of the image. The layout pass, the placement,
`OverlayEngine.ComputeOverlays` and the label collision avoidance are therefore the SAME code, and
only three primitives (ellipse, cross, line) plus the image blit are backend-specific. An overlay
added to the viewer appears in the export for free, which is precisely what a parallel CPU annotator
in the shape of `PlateSolveAnnotator` could not have promised.

Three things fell out of that choice and are worth knowing:

- **The surface IS the image**, so screen coordinates and image coordinates coincide: the export
  state renders at zoom 1, pan 0, chrome off, and the image pane fills the whole surface. Nothing
  projects differently from the way the window projects it; the window is just a different size.
- **The annotation furniture is scaled, the pixels are not.** A 6-pixel marker on a 9576-pixel-wide
  master is a speck, so the export sets `DpiScale` from the image width against a 1600-pixel
  reference -- the same lever a HiDPI window pulls, so markers, labels and placement grow together.
- **It is 8-bit, and that is a property of the rasteriser** (`RgbaImageRenderer` composites into an
  8-bit surface), not a choice. The clean raster keeps its 16-bit PNG and float TIFF.

`ViewerState.ForAnnotatedExport()` is the display contract in one place: a copy rather than a
temporary mutation, because the export runs off the render thread and flipping zoom and chrome on
the live instance would race the frame in flight. **A new display setting belongs in that list** --
omitting one makes the saved file differ from the screen in exactly that respect, silently, which is
why `AnnotatedRasterExportTests` fails when `ShowStarOverlay` is dropped from it (verified by
sabotage, along with the ellipse primitive).

The strongest of those tests is the one that asserts the annotated save with nothing switched on is
the clean save **pixel for pixel**: the two files are of one picture, and the annotation is the only
thing allowed to differ.


## P23. Blink: Space repeats, and the blinked frame can be off-screen  (FIXED 2026-09-08)

From the user's notes 2026-09-07: *"holding down space when we are blinking should pause it, right now
it start/stops rapidly. blinking should also always put the current frame into view."* Two independent
defects in what P19 shipped, and neither one is in the blink.

**The repeat is a seam that drops a fact.** SDL delivers auto-repeat as a stream of key-down events,
and nothing on the viewer's path carries the repeat flag. `TianWen.UI.FitsViewer/Program.cs` wires
`loop.OnKeyDown = (inputKey, inputModifier) => imageRenderer.HandleInput(new InputEvent.KeyDown(...))`,
so `case InputKey.Space` in `ImageRendererBase.Input.cs`, which TOGGLES
(`state.IsBlinking = !(state.IsBlinking && state.BlinkStep == step)`), runs once per repeat at the OS
repeat rate. That is the stutter, and it is not specific to Space: every key on that switch repeats.
Space is where it SHOWS, because it is the only one whose action is a toggle rather than a step, and a
step is what auto-repeat exists for.

**The fix belongs at the seam, not in the case.** An `InputEvent.KeyDown` that says whether it is a
repeat (SDL's `key.repeat`, surfaced through SdlVulkan.Renderer's loop) lets a toggle ignore repeats
while `Up`/`Down`/`Left`/`Right` keep benefiting from them. A viewer-local "Space is already down"
latch would fix this key and leave the shape wrong for the next toggle anyone adds; there is one on
the same switch already (`Shift+H`, hold/release), which repeats today for the identical reason and
has gone unnoticed only because nobody holds it down.

**One ambiguity to settle before building it.** *"holding down space ... should pause it"* reads two
ways: either suppressing the repeat is the whole ask (one press pauses, holding does nothing more), or
hold is meant to be MOMENTARY, pausing while held and resuming on release. The second is a different
gesture and would take Space away from the toggle it currently is. Suppressing the repeat is the
reading the observed bug demands, and it is a subset of the other, so it is safe to do first either
way.

**The scroll is a missing side effect.** `ViewerActions.SelectFile` sets `SelectedFileIndex` and
`RequestedFilePath` and nothing else; the only writer of `ViewerState.PendingFileListScrollTop` is
`ScanFolder`, on open. So a blink walks the selection out of the visible window while the file list
goes on showing whatever it was showing. `Up`/`Down` stepping does the same, and so does P19's
`Ctrl+Space` snap back to the anchor, which is the worst of the three: it exists to return you to the
reference frame, and the list does not follow it there. Blink is where it got noticed, because the
selected row is the only on-screen statement of WHICH frame is being compared.

**Fixed as diagnosed, in three repos, and the seam is where most of it landed.** `InputEvent.KeyDown`
gained an init-only `Repeat` (DIR.Lib 8.14; init-only because consumers match this record as
`KeyDown(var key, var mods)` in dozens of places), `SdlWindowView.OnKeyDown` now takes that event rather
than `(key, modifiers)` and the loop fills `Repeat` in from SDL (SdlVulkan.Renderer 7.33, a breaking change
whose port deletes a line at both call sites, since each was already rebuilding the record by hand), and
the viewer states the rule ONCE at the top of `HandleViewerKey`: a repeat acts only for a key in
`RepeatsAsAStep` (arrows, page keys, zoom). Everything else on that switch either toggles or is a one-shot,
so the allow-list is the short half. `Shift+H` was carrying the same latent bug and is fixed by the same
line. Pinned by `ViewerBlinkTransportTests`, whose Space case asserts a real press either side of five
repeats, so the fix cannot degenerate into swallowing the key.

**The scroll is one call and a one-shot.** `ViewerActions.SelectFile` sets
`ViewerState.PendingFileListEnsureVisible`; the paint consumes it after `SetExtent` through
`ListScrollController.EnsureVisible`. Two tests bound it from both sides: a selection that walks off the
visible run scrolls, and a step inside the run does not scroll at all, which is what makes it a clamp
rather than a re-centring. Every assertion here was seen red with its fix removed (three of the six).

**The momentary hold was the reading meant, and it is now shipped too** (asked 2026-09-08, answered
"yes KeyUp it is"). Holding Space suspends a running blink and releasing resumes it, while a TAP still
stops it, so the toggle that existed is not taken away. The two are told apart by the platform's own
auto-repeat rather than by a duration: a tap produces no repeat, a held key produces a stream, so the
first repeat is what promotes the press from "stopped it" to "is holding it". No timer, no clock reading,
and nothing that has to be tuned.

This needed a new event. `InputEvent.KeyUp` (DIR.Lib 8.14) is the release half, dispatched by
`SdlEventLoop` through `SdlWindowView.OnKeyUp` (SdlVulkan.Renderer 7.33) and only to a host that binds it,
so a host wanting nothing pays nothing. It is a separate record rather than a flag on `KeyDown`, because
every existing consumer reads a `KeyDown` as "a press happened" and a release arriving through that type
would fire all of them a second time.

**It fails safe, which is the part worth keeping.** SDL sends no key-up when the window loses focus
mid-hold, so a lost release leaves the blink STOPPED: visible, and one press from running again, rather
than running with nothing able to stop it. Any fresh Space press clears the pending resume, so a stale
flag cannot outlive the press that set it. `ViewerBlinkTransportTests` pins all five paths, the lost
release included.

**`ScanFolder`'s rule is not the one wanted, and the right one already exists.** `Math.Max(0, index - 5)`
puts the selection near the TOP, which per blink tick would scroll the list continuously. The clamp is
`ListScrollController.EnsureVisible(atom, marginAtoms)` in DIR.Lib: a no-op while the atom is visible,
otherwise the least offset that brings it back, clamped to `MaxOffset`. The planner (GUI and TUI), the
equipment device list and the session config list all call it on a selection change, and the viewer's
file list holds the same controller (`_fileListScroll`). This is a call, not a mechanism, and nothing
about it belongs in DIR.Lib.

**Where the call goes matters twice.** It has to run after `SetExtent` in the paint, because
`VisibleAtoms` is derived from the viewport handed over there. And it has to fire on a selection CHANGE
rather than every frame: called unconditionally, the list would drag itself back to the loaded file and
the user could never scroll away from it. That is exactly what `PendingFileListScrollTop` already
models, a one-shot written by the action and consumed once at paint, so the shape to copy is beside it.
`ViewerActions.SelectFile` is the natural writer; it already early-returns on an unchanged index, which
is the change test.

## P24. The A/B divider leaves its bar behind outside the image  (FIXED 2026-09-08)

From the user's notes 2026-09-07: *"the A|B slider vertical bar can leave residue in the non-imaging
canvas area"*. The fourth report in P15's class and the second on this divider, but not the same rect. P15
fixed the divider's LABELS (measured extents in place of a guessed 220-unit margin) and the
hover-versus-readout narrowing collision. This is the BAR, in the letterbox: the part of the image pane
the picture does not fill.

**Nothing was painting the pane, and the harness could not have said so.** The sweep was never the
suspect and is not the fix: what was missing is that the image pane had no ground of its own. On a FULL
frame the letterbox is the render pass's clear colour, so it looks deliberate; on a partial frame
`VulkanContext.BeginFrameRenderPass` takes the `VkAttachmentLoadOp.Load` pass and scissors to the damage
box, so a pixel nobody draws keeps what it had. The divider bar had been drawn there the frame before.

**The harness said zero because it was painting over a cleared surface**, where an undrawn pixel is the
same black in the partial frame and in the reference. `ViewerRepaintResidueTests` now measures which
pixels a frame actually draws, by painting it over two different sentinel grounds and treating a pixel as
untouched only when it comes back as its own sentinel both times. Compositing the new frame onto the old
one instead would answer the same question and answer it wrongly: an alpha-blended panel over its own
previous pixels converges to a different colour than over a cleared surface, which is the 150,307-pixel
effect P15 already documents. All five older cases were moved onto the stricter model and still pass, so
the two superseded helpers are gone.

**Measured on the drag the report describes: 4,072 stale pixels in x[733..1095], y[40..875]**, a
full-height strip from the divider's old position to its new one, and zero with the fill in place. The
first number this produced was 556,776, which is the harness stubbing `RenderImageQuad`: with no picture
drawn at all, the whole pane is letterbox. That is why the count to quote is the one taken with the
sabotage applied to the FIX rather than the one taken before the model was corrected.

**`ImageRendererBase.CanvasBackground` is stated by the HOST**, because it has to equal the colour that
host clears its window to (`SdlWindowView.BackgroundColor`: `0x1a1a1a` in tianwen-fits, `0x121218` in the
GUI, each now a single literal used for both). A palette colour would have been the obvious choice and
the wrong one: the two would drift, and the letterbox would then change colour with the repaint PATH,
which is far worse than the residue. `VkGuiRenderer` fans the value out to the three viewers it hosts.

**The declared damage already covers it, which is what makes this different from P15.**
`ImageRendererBase` calls `Split.SetTrack(_layout.ImageArea)`, the PANE rather than the drawn picture,
and `SweepBetween` returns `new RectF32(x0, _track.Y, x1 - x0, _track.Height)`: the full height of that
pane, letterbox included. P15's defect was a sweep narrower than what had been painted. Here the sweep
is not the suspect, so the question is what PAINTS the letterbox on a narrowed frame. Nothing in
`TianWen.UI.Abstractions` fills the image area's background, so that band is covered by the host's
clear, and a damage-scissored clear is exactly the kind of thing that covers a region on some frames
and not on others.

The case is pinned by `DraggingTheSplitDividerLeavesNothingBehindWhereThePictureIsNot`, beside the label
case P15 fixed. On screen the two behaved differently in one way worth remembering: this one is
intermittent, because damage is tracked per swapchain image, which is also why P15's tooltip read as a
flicker rather than as a stuck tooltip.

## P25. No auto-crop for stack artefacts and NaN margins  (FIXED 2026-09-08)

From the user's notes 2026-09-07: *"we should have an auto-crop button that crops away stack artifacts,
NaN areas, etc"*.

**The rectangle is NOT reusable, which this entry assumed on the way in and the code says otherwise.**
`MasterPostProcessor` writes a `_autocrop.fits` sibling from a rect it is HANDED, and what computes it is
`CanvasGeometry.ComputeFootprintsAndStatsRect`: the intersection polygon of every frame's footprint on the
canvas, derived from the registration TRANSFORMS. That exists only while frames are being registered. A
document the viewer merely opened has none, ours or foreign, so there is nothing to reach for.

**So the viewer needs a pixel detector, and the marker is only a hint.** Mark a pixel absent where it is
NaN or exact zero in every channel, then take the largest axis-aligned rectangle containing no absent
pixel (largest-rectangle-in-histogram over the mask, one pass, O(W*H)). Our own ring is exact zero by
construction and about 0.3 percent of a frame at the median, which is the sanity check the result has to
land near on a TianWen master; a foreign master may carry NaN, zero, or partially covered edges with no
marker at all, and the detector is what covers all three.

Three things to decide when it is picked up:

- **Whether it crops the DOCUMENT or only the view. Taken: only the view.** It is reversible, costs no
  pixels, and composes with Save (P18 / P22) writing what is displayed. A real crop changes what a
  subsequent plate solve and every statistic run on, and it is one keystroke from being irreversible on a
  master someone spent a night collecting. If a destructive crop is wanted later it is a separate,
  explicit action, not this button's default.
- **What counts as absent.** NaN and exact zero in EVERY channel: a zero in one channel of three is a
  dead pixel or a genuinely black one, not a place no frame covered.
- **It has to be the largest RECTANGLE, not the bounding box of the good pixels**, and a real file says so
  rather than the argument saying so. On `Sag_Triplet_OIII-HOO_1.fits` (an APP composite, 3073 x 3085),
  **97,589 pixels (1.029%) are exact zero in all three channels and none are NaN**, and those absent
  pixels **touch every edge**: they span rows 0 to 3084 and columns 0 to 3072. A bounding box of the good
  pixels therefore trims essentially nothing while leaving the ragged border in. The largest absent-free
  rectangle is **2987 x 3061 at (25, 8), 96.45% of the frame**, trimming 25 left, 8 top, 61 right and 16
  bottom. That is the number the button has to produce.
- **What it does on a frame with nothing to crop**, which has to be visibly nothing rather than a
  one-pixel nibble.

**Shipped, and the detector is verified against an independent implementation.**
`Image.LargestCoveredRectangle()` (`Image.Coverage.cs`) marks a pixel absent when it is exactly zero in
EVERY channel or NaN in any, then runs one largest-rectangle-under-a-histogram pass with a monotonic
stack: O(width x height) time, O(width) space, channels read one at a time down the outer loop so every
read is sequential and nothing image-sized is allocated. On the reported composite it answers
**2987 x 3061 at (25, 8), 96.45% of the frame, trimming 25 / 8 / 61 / 16**, which is digit for digit what
a throwaway numpy implementation of the same problem produced, in **52.6 ms**. That timing is the reason
it sits behind a press rather than at load, and the reason the scan runs on the pool: it would be several
dropped frames on the render thread.

**The viewer half is geometry only.** `ViewerState.DisplayCrop` (image pixels) is what
`ComputeImagePlacement` fits, centres and clamps the pan against; the quad still covers the whole image
and the border is simply never rasterised, so the readout keeps reporting the frame's own coordinates and
a plate solve still runs on all of it. `ClipToShown` narrows the three draw clips **only while a crop is
in force**: without one it returns the rect untouched, because clipping to the quad instead would be
invisible on screen yet would change the declared clip, which `ViewerSplitTests` and the damage suites
pin as the PANE. Three of them said so by going red, which is how that came to be gated rather than
guessed.

**A crop that does not fit the loaded frame is ignored rather than obeyed or cleared.** That is what lets
it survive a step to the next file: a folder of masters off one rig shares its canvas ring, and a frame
of another size shows in full without anyone having to remember to clear anything.

**Reachable three ways, all one toggle**: the Crop button (group 4, beside Solve and Enhance), `Shift+C`
(C alone has cycled the channel view since before there was one), and `AutoCropSignal` for a host. The
status bar declares `Crop WxH` while one is in force, for the same reason it declares a held display: the
only other evidence is a border that is missing, which looks like the file. A frame with nothing to
discard says so ("Nothing to crop: the frame is covered edge to edge") instead of appearing to do nothing.

Pinned by `LargestCoveredRectangleTests` (7, including a ragged ring whose covered-pixel bounding box is
the whole frame) and `ViewerAutoCropTests` (8, three of which were seen red with the crop ignored).

**The save honours it, and the two save paths do it at opposite ends for stated reasons** (added
2026-09-08, after the crop shipped view-only and P18's "save as seen on screen" would otherwise have
contradicted it). `ViewerState.ResolveDisplayCrop` is now the ONE rule for whether a crop applies, asked
by the renderer's placement and by both exports.

- **The plain export crops the PIXELS, after the debayer.** `DisplayRasterExport.WriteAsync` takes a
  `Rectangle? crop` and applies it to `source`, which by that point is already colour. Never before the
  debayer: a crop origin with an odd coordinate re-phases a CFA mosaic, which would exchange red and
  blue in the file while the screen, which debayers the whole frame and only then clips, stayed correct.
  The auto-crop's own origin is (25, 8), so that is a live case and not a hypothetical.
- **The annotated export crops the finished RASTER.** Annotations are placed through the document's WCS
  in FULL-frame coordinates, so cropping its input would shift every marker by the crop origin while the
  sky it names stayed put: right size, wrong labels. Cropping the output is also exactly what the screen
  does, so the file and the window agree by construction.
- **`ForAnnotatedExport` deliberately does NOT carry `DisplayCrop`**, which is the one exception to that
  method's own "copy every display setting" rule, and its doc now says so: the exporter's
  `RenderImageQuad` blits 1:1 on the stated assumption that the surface IS the image, so an offset
  placement would move the annotations and leave the picture where it was, and the raster crop would then
  apply a second time.
- **`Image.Crop`** is the single crop, hoisted out of `MasterPostProcessor`, which now delegates to it.
  Statistics ride across unchanged on purpose: `MaxValue`, `MinValue` and the pedestal describe the
  EXPOSURE, and cropping a border away does not re-expose it.

Both are pinned by parity rather than by dimensions, which is the assertion that matters: the saved crop
must equal the same sub-rectangle of the uncropped save, byte for byte. A dimension check passes on a
file that re-derived its stretch from the cropped pixels and came out brighter than the window it was
cropped in. All three were seen red with the crop removed.

### It cropped the UNION, and the name said intersection (found and FIXED 2026-09-08)

The detector marks a pixel absent when it is exactly zero in every channel or NaN in any, which finds
where NO sub reached. It therefore answers "the largest rectangle inside the area at least one sub
covered", and the commit that shipped it claims "the area every sub covered". Those are different
regions and the difference is visible: reported as a top edge that still looks ragged with a crop
applied.

**Measured on the reported file** (OIII channel, across the crop's own columns, MAD against the interior
at rows 1400 to 1500):

| Rows inside the crop | Noise vs interior |
|---|---|
| 8 to 24 | 1.52x to 1.60x |
| 24 to 32 | 1.31x |
| 32 to 48 | 1.05x |
| 48 and beyond | settled, 0.94x and below |
| 3061 to 3069 (bottom edge) | 1.50x |

So a band roughly 24 to 40 px deep at the top, and about 24 at the bottom, sits INSIDE the crop with up
to 60% more noise: no zeros in it, real data, but fewer subs reached it. Under a stretch that reads as a
grainy border, which is what the crop was asked to remove.

**What is NOT wrong**, both checked before concluding anything: the rectangle contains zero absent pixels
by the shipped definition, and every remaining zero in the top band sits in the 25-column left and
61-column right strips the crop already removes; and no pixel outside the crop can reach the screen at
any zoom or pan, which `ViewerAutoCropTests.NothingOutsideTheCropReachesTheScreenWhenZoomedIn` now pins
at 4x with the pan driven to each limit. The geometry and the clip are correct. The DEFINITION is what
falls short.

**Fixing it needs a coverage estimate, not a threshold guess.** Partial coverage is not darker (the row
medians hold at 0.997 of the interior all the way to row 8), so brightness cannot find it; the signature
is noise, and the band's depth is not predictable from the ragged zero region either, since the zeros
reach only to row 11 while the noisy band reaches 32. The self-calibrating shape is to walk in from each
edge until the local noise settles to within a small margin of the interior, which needs its threshold
measured over real masters rather than picked, exactly as the 38 nm filter cut was.

### The band is now cropped, in two tiers, and the walk the paragraph above proposed was refuted first  (FIXED 2026-09-08)

**Measured, not picked.** The corpus is four real masters -- three Astro Pixel Processor composites
(3073 x 3085, one of them the reported file) and TianWen's own 10P Bayer-drizzle master, whose
`.rejection.fits` sidecar is the accumulated per-pixel WEIGHT and therefore ground truth for what its
coverage actually was. The harness is `tools/coverage-edge-walk/`, kept so this can be re-measured
rather than re-argued.

**The statistic that works is p10 of the per-tile sigma of adjacent differences ALONG the band** (tiles
of 64 px, bands 16 px deep). Differencing along the band kills any gradient across it, which is the
direction a coverage ramp runs; the tile percentile keeps a star, a trail or a nebula edge from deciding
the answer. p10 specifically, because it is the one that converged to 1.00x deep inside a real master
where the median stayed at 1.25x -- the quietest tiles carry the least structure.

**"Until the local noise settles to within a small margin of the interior" is wrong, and the 10P
master's left edge is why.** Its vertical-difference noise reads 1.66x the centre at the edge, PEAKS at
2.05x 76 px in, and decays over about 460 px -- all at full coverage, per its own weight map, which puts
the band at 4 px. A canvas edge is fed by fewer distinct dither phases, so its noise is less correlated
and a difference-based sigma reads high there whatever the exposure. Against the frame interior that is
a 312 to 460 px false trim. Three rules were tried and all three took it: a fixed margin against the
interior, the same against the edge's own deep plateau, and a knee/slope test.

| 10P edge | coverage plane says | interior-relative | plateau | slope/knee |
|---|---|---|---|---|
| top | 56 px | 68-76 | 68-76 | 68-76 |
| bottom | 56 px | 48-52 | 48-52 | 48-56 |
| right | 20 px | 12-16 | 12-16 | 8-16 |
| **left** | **4 px** | **312-460** | **312** | **432-456** |

**What ships instead: a band whose END is not visible is not trimmed.** `CoverageEdgeWalk` takes the
shallowest depth from which every sample out to 5% of the span is within 1.15x of the settled level, and
answers `Settled: false` with a trim of zero when there is no such depth. Two independent protections
fall out of that and both are load-bearing: an edge that is not the noisiest part of its own profile is
"nothing to trim" (`MinimumRise`, which is what spares the 10P left edge), and a band deeper than the
bound is refused outright rather than trimmed as far as the bound allows (which would leave a frame
smaller on every edge that still shows the band it was cropped to remove). Pinned by
`CoverageEdgeWalkTests`, whose two synthetic frames are exactly those two shapes.

**On the real corpus, running the shipped code:** the reported file goes from 96.3% of the frame (union)
to 88.5% (left 56, top 92, right 16, bottom 92 px), its two siblings to 89.0% and 89.9%. The 10P master
trims top 72 and bottom 28, refuses its left edge, and finds nothing to do on its right. Its own
`_autocrop.fits` -- which the stacking pipeline builds from the geometric INTERSECTION of the frame
footprints, and which measures clean at every edge -- is left completely untouched, which is the
strongest single check available: the walk declines the file that needs nothing.

**A drizzled frame can defeat the walk outright, so the exact tier wins wherever it exists.** On the
same master's right edge, 69% coverage reads as 1.08x: partial coverage raises the sample noise and
correlates the neighbours at the same time, and the two nearly cancel. So
`Image.LargestCoveredRectangle(Image coverage, ...)` reads the answer off the weight map instead, and
`ViewerActions.ScanForCrop` prefers it -- a fallback, never a cross-check, the same shape as the mount
limits' mechanical tier. Two things make that work:

- **`IntegrationFitsWriter` now stamps `MAPKIND`** (`COVERAGE` or `REJECTION`) into the sidecar,
  because the drizzle strategies put weight where every other strategy puts a rejection FRACTION -- and
  the two are opposite in sense and different in range, so a consumer that guesses gets it exactly
  wrong. Absence is never read as either: a sidecar from before today cannot say it is coverage, so its
  master gets the estimate.
- **The coverage comparison is per 16 x 16 BLOCK, not per pixel.** A drizzle canvas hands neighbouring
  cells different drop counts, so a fully covered interior scatters about 10% either way (the 10P red
  channel's interior p0.1 is 0.847 of its median). Per pixel at 0.95 that rejects pixels everywhere and
  the largest rectangle collapses to 207 x 404 of a 4215 x 2884 frame -- measured, before the block mean
  went in. Over blocks the same interior reads p0.1 = 0.990. 0.99 as the fraction is not usable for the
  same reason; 0.95 is the default, and keeps the noise inside 1.026x of the interior's.

**The status bar says which tier answered** (", by coverage") and when an edge was left alone
(", edge held"), for the same reason it declares the crop at all: the only other evidence is a border
that is missing.

**A border still visible after a GRADIENT CORRECTION is a noise band, and no fit removes it.** That was
the other half of the report, and the measurement says the fit is not the problem:
`BackgroundExtractionOptions.ExcludeUnsettledEdges` exists but defaults OFF, because on a 1536 x 1152
planar ramp with a 32 px border carrying 6x the noise and the 0.3% low sky the real file shows, the
interior model moves 0.018 sigma of the frame's noise with the band in the fit and 0.001 sigma with it
out -- a 16x reduction of a quantity too small to see, since a degree-2 surface cannot follow a 32 px
border however noisy it is. Turning the flexible surface stage on did not change the conclusion (0.005
against 0.001). So the answer to that ring is to CROP it, which is now available; the option is there
for a caller that wants the model itself independent of the border. Pinned, with both pairs of numbers
printed, by `ClassicalBackgroundExtractorTests.ANoisierBorderIsKeptOutOfTheFit`.

### The crop was drawn, not enforced: the cached layer blitted the border back  (found and FIXED 2026-09-09)

Reported the day after the crop shipped: with a crop applied, zooming out brings the ragged corner
back, while the status bar still says `Cropped to 2915x2877 (88.5% of the frame)` and the toolbar
toggle is still lit. The state was never lost -- the border was being painted.

**`TryDrawImageFromCachedLayer` returns before the uncached path's `ClipToShown`.** The direct path
clips the quad to the pane narrowed by the crop; the cached path clipped to the PANE alone. At fit
the discarded border falls outside the pane, so the pane clip hid it, and every zoom anyone had
looked at was at or above fit. Zoom out and the whole frame fits inside the pane, so the blit paints
what the crop had removed. The session that found it had **1309 blits against 253 renders**: the
cached path is not an optimisation the user sees occasionally, it is the one they look at.

**The blit is narrowed, not merely scissored.** The destination rectangle is the intersection of the
pane and the shown region, and the source UVs shrink with it -- narrowing the destination alone
samples the whole pane into the crop's rectangle, which squashes the picture instead of cropping it
and still looks plausible.

**The test that existed re-derived the clip instead of observing it.**
`NothingOutsideTheCropReachesTheScreenWhenZoomedIn` recomputes `ClipToShown` in its own body from the
placement, so it models the rule rather than asserting what the renderer declared, and it stays green
with the bug present (verified: back the fix out and exactly one test fails, the new one).
`TheCachedLayerBlitIsNarrowedToTheCrop` asserts the rectangle handed to `TryDrawCachedLayer`, driving
the cached-layer seam through its `protected virtual` hooks -- which is also the first test coverage
that path has had.

**A crop no longer re-fits the view** (raised in the same breath: *"it shouldn't actually refit the
frame on crop, just cropping it"*). Setting `ZoomToFit` alongside the rectangle made the picture jump
and grow at the moment the user was studying its edge -- a second change nobody asked for on top of
the one they did. Fit stays one keypress away and, with a crop in force, fits the CROP. Pinned by
`ViewerControllerTests.ApplyingACropLeavesTheZoomAndPanAlone`.

**The crop SURVIVES an enhance; what does not survive is switching it off.** Reported as "the crop is
removed once auto-enhance finishes", and the code says otherwise -- neither `TryApplyPendingEnhance`
nor `RevertEnhance` touches `DisplayCrop`, and driving the running viewer confirmed it: after a
93-second BlurX + GraXpert + StarXTerminator + denoise program the status bar still read
`Crop 2915x2877` and the ring was still gone. What the report caught is the TOGGLE. Switching the crop
off and pressing again re-SCANS, and on enhanced pixels both tiers are blind -- the coverage sidecar
belongs to the file on disk, the absent-pixel test needs exact zeros, and the edge walk needs a noise
step, and a deblur, a gradient correction and a denoise leave none of the three. The second press
therefore answered `Nothing to crop: the frame is covered edge to edge` and the crop could never come
back. `ViewerController._rememberedCrop` keeps the rectangle, so switching it back on restores it in
the same press rather than scanning; the restore is synchronous, which is what
`SwitchingTheCropOffAndOnAgainRestoresItWithoutScanning` asserts (a scan would leave it null until its
task landed). Un-cropping no longer re-fits either, for the same reason cropping does not.

**Enhance is fed the CROP (decided 2026-09-09, on the evidence below).** `DisplayCrop` used to reach
the renderer, the status bar, the toolbar state and both exports, and nothing else, so GraXpert, BlurX
and NoiseX all saw the canvas ring. They are spatial models and the ring is exact ZERO -- a cliff a CNN
reads as structure and smears inward, which is what a border still visible after a gradient correction
IS. Masking it was not available either: `SharpenPipeline` fills non-finite samples with the CHANNEL
MEAN at its boundary, deliberately ("the enhancers ... compute non-NaN-aware global normalisation, so a
single NaN poisons the whole output"), and exact zeros pass through untouched. So the crop is cut
before the pipeline sees anything, and four things follow:

- **The result IS the crop.** `EnhanceActions.EnhanceAsync` takes the rectangle, `Image.Crop`s the
  input, and the enhanced document is that size.
- **`WCS.CroppedTo` translates the solution**, because a crop is a pure translation of the pixel grid:
  only CRPIX moves, the CD matrix is a derivative and SIP is relative to CRPIX. It exists so no caller
  hand-edits CRPIX -- these are the 0-based in-memory values and a stray -1 here is exactly the
  off-by-one `WcsPixelOriginTests` exists to prevent. Star detection re-runs on the cropped pixels, so
  the object and star overlays land without further work; a sign error would put every marker off by
  the crop origin and still look plausible, which is why the test asserts the SKY position of the
  crop's own corner rather than the CRPIX numbers.
- **`AstroImageDocument.SourceCrop` records where the pixels came from**, and the crop button gates on
  it: there is nothing left to take off, and nothing to put back either, since both scan tiers are
  blind on enhanced pixels. The document owning that rather than the viewer holding a flag is what
  keeps it true -- a flag would need clearing on every path that replaces the document.
- **Reverting restores both** the full frame and the crop that was on it.

Pinned by `EnhanceActionsTests.ACropIsCutBeforeThePipelineAndTravelsWithTheResult` (+ its no-crop
twin) and `ViewerAutoCropTests.TheCropButtonIsDisabledOnceAnEnhanceHasBakedACropIn`.

**The A/B split then showed two different frames, which is the one thing it must never do** (reported
2026-09-09 the moment the cropped enhance was first driven: *"A|B is warping, before is showing me the
uncropped"*). The retained "before" textures are the pre-enhance pixels -- kept by moving three GPU
handles aside, at no copy -- and after a cropped enhance those are the UNCROPPED original while the live
half is the crop. Drawn on the live quad they stretch by the crop's ratio and bring the canvas ring back
with them. The fix is pure geometry and costs nothing: the comparison half gets its OWN quad, same
scale, origin backed off by `SourceCrop`'s offset, so the crop's pixel (0, 0) lands where the live half
draws it. Only when that half samples the retained pixels (`SplitCompareController.ComparesPixels`) -- a
pinned-settings comparison is the same frame and must not move. `BeforeImageSize` on the renderer is
what makes the two frames comparable at all; zero means "same frame as live", which is what every
comparison was until now. Pinned by
`ViewerAutoCropTests.TheBeforeHalfIsPlacedByItsOwnFrameWhenAnEnhanceBakedACropIn`.

### A/B is TWO comparisons where a crop is involved, and they want opposite things  (2026-09-09)

Stated by the user after driving the aligned split: *"there's two behaviours. open frame, press crop,
A|B should compare crop vs uncrop. open frame, crop, run enhance, A|B should compare before=crop vs
after=cropped + enhanced."* Both are right, and the rules are opposites -- which is why one clip could
never serve both, and why the first attempt at this section argued for keeping the extents different
and the second for clipping them the same.

- **Crop, no enhance -> `SplitCompare.CropExtent`,** labelled "Uncropped | Cropped". The halves
  deliberately cover DIFFERENT areas: same pixels, same dials, one side showing the band and ring the
  crop took off. It answers the question a crop actually raises, and nothing else in the app can --
  comparing pixels needs an enhance to have retained some, and comparing settings puts two
  identically-framed halves on screen, which is what a crop used to light up. The implementation is one
  clip: the quad already covers the whole frame at the right place (the placement backs its origin off
  by the crop), so the comparison half is simply NOT narrowed.
- **Crop then enhance -> `BeforePixels`, bounded to the live quad.** Here the retained texture is the
  uncropped original and the live frame is the crop, so an unbounded half changes the FRAME as well as
  the pixels and the comparison answers two questions at once. Both halves show the same region and
  differ only by the enhancement.

**Retained pixels still win** (`Toggle`, and `AdoptBeforePixels` for an enhance that lands while the
split is already up, which now also switches out of `CropExtent`): a user who just enhanced is asking
about the enhance. **Both press dispatchers pass the crop state** -- the keyboard through
`Split.Toggle`, the button through `ViewerActions.HandleToolbarAction` -- because those two have
silently disagreed before (P17's single-click) and a mode chosen one way and not the other is exactly
the shape that survives review. Pinned by `ACropWithNoEnhanceComparesCroppedAgainstUncropped`,
`AnEnhancedCropComparesTheSameRegionOnBothHalves` and `TheCompareButtonPicksTheCropComparisonToo`, each
red with only its own rule removed.

**One real race fell out of the full suite, in the status line rather than the pixels.**
`EnhanceActions` built its progress sink with `Progress<T>`, which posts each report to the captured
context -- and inside `Task.Run` there is none, so reports go to the pool and one queued during the run
can execute AFTER the terminal `Enhanced (...)` message, leaving a finished enhance reading
`Enhancing: gradient-correction (0%)` for good. It surfaced as one failure in 5,782 tests, never as
something anyone saw on screen (the upload path clears the line a frame later, most of the time).
`SynchronousProgress<T>` (TianWen.Lib) reports inline, so the caller's final write always wins; the
private `SyncProgress` the HOSTING side had already grown for the same reason folds into it, since two
copies of one rule is how they drift. Pinned by `SynchronousProgressTests`, on the MECHANISM -- an
ordering bug cannot be pinned by the code that suffers it, which is exactly why this one was a
one-in-a-suite flake rather than a test.

**The measurement that decided it, and what is still unmeasured:** `DisplayCrop` reaches the
renderer, the status bar, the toolbar state, `DisplayRasterExport` and `AnnotatedRasterExport`, and
nothing else -- so GraXpert, BlurX and NoiseX all see the canvas ring and the under-exposed band. That
is defensible for a VIEW crop -- the viewer must not quietly destroy data an enhance then works from --
and questionable for the gradient corrector, which masks the canvas ring as absent anyway
([background-extraction.md](background-extraction.md), 0.3 percent of a frame at the median) but has
no such protection against the under-exposed BAND inside it. The live run is evidence for changing it:
enhanced with the ring in, the frame comes back with a soft bright band right around its edge and the
ragged corner intact, while the interior improves (stars 5887 -> 12905, HFR 2.49 -> 1.65). And note
`SharpenPipeline` sanitises non-finite samples to the CHANNEL MEAN at its boundary -- deliberately,
since "the enhancers ... compute non-NaN-aware global normalisation, so a single NaN poisons the whole
output" -- so **NaN is a fill here, not a mask**, and exact zeros pass through untouched. Of the three candidates -- crop the input, fill
outside-crop with the channel mean, mirror-pad and discard -- the first shipped: it is the only one
that hands the models no fabricated pixels at all. **Not measured: how far inside the frame the smear
actually reaches**, i.e. whether the 56/92/16/92 px this crop removes is deep enough to contain it.
Cropping first makes the question moot for a cropped view and leaves it open for an uncropped one.

## P26. The `?` panel cannot report a bug  (FIXED 2026-09-08)

From the user's notes 2026-09-07: *"in the help menu allow to auto-create an issue, with attaching logs
etc"*.

The `?` panel is already where the answers a report needs are known: P11 put the version there, and
`AiCapabilities.ProbeAsync` puts the enhancer status and the searched directories beside it. Logs sit at
`%LOCALAPPDATA%/TianWen/Logs/<date>/FitsViewer_*.log`, one file per process, so "this session's log" is
a specific file rather than a guess.

- **Prefer a PREPARED issue over an automatic one.** Opening the browser at a GitHub issue URL with the
  title and a filled-in environment block is one `Process.Start`: no token, no API, no dependency. It
  also leaves the user reading what they are about to file, which a silent POST does not. The log is
  attached by the user (GitHub takes a drag-drop), or the button copies its path.
- **Never attach a log the user has not seen.** The log carries folder names, and folder names carry
  places and target names.
- **It composes with P13's documentation** (shipped on the org site) rather than duplicating it: the
  panel gains a second link, not a second body of text.

**Shipped as two action rows on the panel**, sitting between the AI block and the shortcut list: "Open
the user guide in a browser" and "Report a problem (prepares an issue, sends nothing)". Everything else
on that panel is a fact and still does nothing when clicked. `BugReportLink` builds both URLs and is
where the reasoning lives.

**The report is PREPARED, not filed**, which is a deliberate narrowing of the note's "auto-create an
issue, with attaching logs etc". The link carries a title and an environment block (build, OS, and the
panel's own AI lines) and opens in the browser, so there is no token, no API client, no network code and
nothing that can rot; it also works for someone not signed in, since GitHub keeps the draft through the
sign-in. And it leaves the user reading what they are about to send, which a silent POST does not.

**No log is attached and no absolute path appears anywhere in it.** The detail a maintainer wants is in
the log, and a log lists every folder opened: folder names carry target and site names, and a run of them
says where someone was and when they were not at home. So the body says where the logs are in
`%LOCALAPPDATA%`-relative form, states that one was deliberately withheld, and asks for it. An absolute
path would carry the account name; the open document's path would carry the target being shot. Pinned by
`BugReportLinkTests.TheBodyNamesNoAbsolutePathAndAttachesNoLog`, seen red with an absolute path
substituted, which is exactly the shape a well-meaning "attach this file" convenience would take.

**Both rows post `OpenUrlSignal`** rather than starting a process, because opening a URL is a shell call
and this assembly is shared with the WebAssembly build. That exposed a real gap: only the GUI subscribed
to that signal, so the FITS viewer's own host would have shown two rows that silently did nothing. It now
subscribes too, best-effort, on the same reasoning the GUI states.

**The AI block is the only unbounded part** (one line per probed directory), so an oversized one is
dropped with a note rather than risking a link a browser truncates: a body cut off mid-word is obvious to
the user, a dropped query parameter is not. `MaxUrlLength` is 6000, well inside every browser's limit.

## P27. Escape with a panel open: verified, not fixed, because it was never broken  (2026-09-08)

The viewer closed while the `?` panel was open and Escape had just been sent, so this was written up as
"the panel does not consume Escape" and a check was added to the Escape branch. **That diagnosis was
wrong and the check has been removed.** Kept as an entry because the investigation is the useful part.

**The mechanism was already complete.** `DropdownMenuState<T>` implements `IKeyboardClaimant`,
`PixelWidgetBase.RenderDropdownMenu` assigns `Ui.KeyboardClaimant = dropdown` **as it paints**, its
`HandleKeyDown` closes on Escape and returns true, and `HandleViewerKey` asks the claimant before
anything else. Escape never reaches the exit while a panel is open. Verified three ways: a unit test with
the added check disabled, and twice in the running viewer, which survived both times.

**What the added check really was: a second copy of a rule**, and of the one rule whose own comment says
it exists so that "a second overlay added here would otherwise need its own line". Worse, it worked in a
test that could not have caught its own uselessness: setting `IsOpen` and sending the key WITHOUT a
render skips the claim, so the test drove a state the viewer is never in, passed against the special
case, and would have passed with the real mechanism deleted.

**What was genuinely missing was the test**, so that is what remains. `ViewerEscapeTests` now opens a
panel and PAINTS it, which is what makes it the claimant, then asserts Escape closes it and asks for no
exit; that Escape with nothing open still exits; and that the second of two presses exits, which is the
sequence a user performs and which also pins the release rule (a closed overlay declines the key rather
than being cleared, which is what makes a stale claim harmless). Disabling the claimant call fails
exactly the two cases that depend on it and leaves the exit case green.

**The original close is unexplained and could not be reproduced**, including with the same
click-screenshot-Escape sequence that preceded it. The likeliest reading is that the panel was already
closed when the synthetic key landed, in which case exiting was correct behaviour.

**Two dead properties turned up while auditing what else Escape should dismiss.**
`ViewerState.ShowDebayerMenu` and `ShowStretchFactorMenu` have no reader and no writer anywhere in the
tree, left over from before the shared dropdown. They cannot trap a key, but state that looks like a live
overlay is a trap for exactly the question that found them, so they are worth deleting.

## P28. The menu can name the object under the cursor, and the atlas link opens the atlas  (FIXED 2026-09-08)

Two additions to P17's menu, both about the distance between what it already knew and what it did.

**A click on a marked object is nearly always about the object**, and the menu could offer that
object's coordinates but not its name. Two entries now lead when one resolves -- `Copy object name`
and `Copy catalogue number` -- because they answer different questions: the name is what a person
searches for, the designation is what another tool takes. An object whose only name IS its
designation gets one entry, not two carrying the same string.

**It never blocks and never triggers the catalogue load.** `FindObjectAt` reads the object DB only
where it has already been created, the same test the Overlays button uses, so a right-click on a
freshly opened viewer costs nothing and simply offers no object entries. Waiting there would freeze
the menu on the first press for a full Tycho-2 init, which is the one thing a context menu may not
do.

**The tolerance is a fraction of the FIELD, not a fixed radius**: 2% of the field width, floored at
half an arcminute so a deep zoom still has a target and capped at half a degree so a wide field does
not claim a galaxy on the far side of the frame. A click is a gesture aimed at something on screen,
so what counts as "on it" scales with how much sky the frame covers. Without a plate scale there is
no field to take a fraction of, and the answer is nothing rather than a guess.

**Nearest wins, over the overlay's own grid cell** (`ICelestialObjectDB.DeepSkyCoordinateGrid`, the
same gather `OverlayEngine` draws from), so the menu can only ever name something the overlay would
have drawn. Solar-system bodies are skipped explicitly: they live in the DB at NaN/NaN by design,
and a still frame's click is not asking about them.

**`Copy sky atlas link` became `Open in sky atlas`.** Copying was the first shape and it left the
reader the other half of the job -- paste the link somewhere, in a browser they had to find
themselves, to see the sky they had just clicked on. Nothing is lost, since a browser's address bar
holds the same link with the page it names in front of it. It goes out as an `OpenUrlSignal` rather
than starting a process, because opening a URL is a shell call and `TianWen.UI.Abstractions` is
shared with the WebAssembly build; that is also why `ImageContextMenuItem` grew an `Action` instead
of the handler special-casing one label.

---

## P29. A touchscreen pinch does not zoom  (FIXED 2026-09-09)

**Nothing was wrong with the gesture recognition.** `SdlEventLoop` has recognised two-finger pinches
for as long as the sky map has zoomed with them, and it classifies the device while it is at it
(`TouchDeviceType.Direct` -> `PinchSource.Touchscreen`, anything else -> `Touchpad`). Both halves of
the viewer's path then threw the result away: `tianwen-fits`'s `Program.cs` wired no `OnPinch` at all,
so the event was raised into a null delegate, and `ImageRendererBase`'s input switch had no case for
one, so the GUI -- which does forward it -- dropped it at the tab. **A dropped event and an unread
device look identical from the glass**, which is why this reads as "the touchscreen does not work"
rather than as a missing branch.

**The zoom itself is the wheel's, seeded the same way.** `PanZoomController.ZoomByFactor` already
existed for exactly this ("for keyboard / pinch"), so a pinch is the wheel path with a factor that
comes from the fingers instead of a notch: seed the controller from `ViewerState`, run it, write the
transform back. The anchor is the finger midpoint, gated to the image pane like the wheel is, so the
image point between the fingers stays between the fingers.

**The scale is per EVENT, not cumulative from the start of the gesture**, and every piece of prose
around it said otherwise. `InputEvent.Pinch`'s XML doc in DIR.Lib said "absolute from pinch start",
`SdlWindowView.OnPinch`'s said "absolute since start", and inside one method of `SdlEventLoop` the
comment above the division said absolute while the comment on the re-base three lines below said
per-frame. The code was right and both consumers (this and `SkyMapTab`) already treated it as
relative; reading it as absolute would leave a whole pinch stuck at its first event's factor. All
four are corrected, and the field the wrong comments were describing is renamed `PinchStartDist` ->
`PinchRefDist` (internal, SdlVulkan.Renderer) -- it is the start distance only for the first
dispatch, so the name was the load-bearing half of the error and a comment saying "not the start
distance" beside a field called `PinchStartDist` would have rotted straight back.

**A TOUCHPAD pinch is deliberately left alone**, which is the opposite of what `SkyMapTab` does with
the same event, and the divergence is in the wheel rather than in the pinch. Windows fires the finger
events AND a mouse wheel for one touchpad pinch: the sky map anchors its wheel zoom at the view CENTRE
(cursor-anchoring spins the field near the pole), so it has to prefer the pinch and suppress the wheel
for a 300 ms grace window to get a cursor anchor at all. This viewer's wheel is already
cursor-anchored, and a touchpad pinch reports the cursor as its midpoint -- so the wheel path already
gives the answer the pinch would, and acting on both would simply zoom twice per gesture. Trackpad
zoom is therefore untouched by this change.

**The gesture must not also PAN, and that is the half with no visible symptom until it is wrong.** A
touchscreen synthesizes mouse events from the FIRST finger, so the press that opens a pinch reads as
a press-and-drag: without suppression the image is dragged by one finger while the same gesture zooms
it. `BeginViewportPan` refuses to arm while a pinch is live -- refused there because both hosts arm
the pan themselves (the standalone viewer has its own press dispatch), so it is the one place both go
through -- and each pinch event ends any pan already in flight. After `PinchEnd` the pan stays
disarmed until a fresh press, so the finger still on the glass when its partner lifts cannot resume a
drag from an anchor the zoom has since moved out from under.

**The pre-pinch travel is deliberately kept**, unlike `SkyMapTab`, which rewinds the centre to where
the drag started. Between the two fingers landing, finger one moves a few pixels and rewinding that
is invisible -- but the same rewind teleports the view when a deliberate one-finger pan turns into a
pinch without lifting, and on an image that is the more likely gesture.

Pinned by `ViewerPinchZoomTests` (8 tests; 6 fail with the switch cases removed -- the two that
survive are the negative ones, which a dropped event satisfies for free).

## P30. `Auto` renders an enhanced frame as a flat colour field  (FIXED 2026-09-10)

Reported 2026-09-10 from the 10P drizzle master: after Enhance, with **no SPCC**, the whole frame
renders crimson. The user's own hypothesis on sight -- *"this might be graxpert doing auto-bkg
balancing"* -- points at the right mechanism.

**This is a defect, not a tuning miss.** The viewer's default stretch mode is `Auto`, so an enhanced
image currently cannot be viewed in the mode it opens in.

**The trap is already documented once, and the guard exists in the wrong renderer.** From CLAUDE.md's
stacking section: *"Shadows derive from the pedestal-SUBTRACTED median -- a no-op on raw masters, but
an enhanced (GraXpert-flattened) master needs `MasterPreviewRenderer.WithZeroPedestal` or subtracting
the floor explodes or blacks out a drizzle frame."* That switch is on `MasterPreviewRenderer`, the
STACKING pipeline's renderer. The viewer renders through `AstroImageDocument.ComputeStretchUniforms`,
which reads `PerChannelStats[c].Pedestal` in four places and has no equivalent.

**The hypothesis, stated so it can be refuted:** GraXpert flattens the background toward zero, so on
the enhanced pixels each channel's pedestal and median very nearly coincide. `Auto` resolves to
**Unlinked** with no calibration (the toolbar in the report reads "Auto (Unlinked)"), and Unlinked
gives every channel its OWN curve -- so three nearly-zero denominators yield three very different
gains and whichever channel wins paints the frame.

**Measure before fixing, because two different faults produce this same screen:**

- If the post-enhance per-channel MEDIANS are near-identical and only the rescale diverges, it is the
  zero-pedestal case, and the fix is the viewer's own version of `WithZeroPedestal`.
- If the medians themselves are far apart, GraXpert has genuinely moved the colour balance, and the
  fix is inheritance (as `InheritColorCalibration` already does for the SPCC triple) rather than
  re-solving background neutralisation on flattened pixels.

**Instrumented rather than eyeballed** (`ViewerController.LogStretchBasis`, Debug, on document
replacement only -- open / enhance / revert). It logs what the SHADER is given,
`Basis.PerChannelStats`, NOT what the document measured: those differ whenever a display anchor is
held, and an anchor surviving an enhance is a FOURTH candidate nothing above had considered -- a curve
solved from the pre-enhance pixels has nothing to do with flattened ones. A diagnostic that can
disagree with the screen is worse than no diagnostic.

**The pre-enhance reading, measured 2026-09-10 on the 10P drizzle master** (`anchored=False`):

| channel | pedestal | median | MAD | bg |
|---|---|---|---|---|
| 0 (R) | 0 | 0.00872816 | 0.00012225 | 0.00850286 |
| 1 (G) | 0 | 0.0320287 | 0.000223673 | 0.0314466 |
| 2 (B) | 0 | 0.0195926 | 0.000223881 | 0.0193248 |
| luma | 0.0250097 | 0.00115969 | 0.00017443 | - |

**Two things this already settles, before the enhanced half is read:**

- **The zero-pedestal story cannot be what the enhance INTRODUCES here, because the pedestal is
  already 0 on all three channels** -- and this frame renders correctly as it stands. So pedestal ~=
  median is not sufficient to break the render, and the documented `WithZeroPedestal` trap, which is
  what the first version of this entry led with off the CLAUDE.md note alone, is the wrong lead.
  Writing the guess down and then measuring is what caught that.
- **The channels start far from balanced** -- green is 3.7x red -- and Unlinked's per-channel curves
  are exactly what normally hides that. So the question is not "did the balance move" in the abstract
  but whether the enhance WIDENS that spread or collapses the MADs, which are already of order 1e-4:
  a rescale going as 1/MAD is around 5000x, so a small change in MAD moves the gain a great deal.

**Prediction recorded before the reading** (so it can be refuted): medians drop sharply toward each
other and toward zero as the flattened background is removed, MADs stay the same order of magnitude,
`anchored` stays False. If MADs instead collapse an order of magnitude the fix is a MAD FLOOR, not a
pedestal switch; if `anchored` comes back True the anchor is the whole answer.


**Note `InheritColorCalibration` does NOT cover this case.** It carries the SPCC triple measured on
the original stars, and the report has no SPCC at all -- so nothing is inherited and neutralisation is
re-solved on the enhanced pixels, which is exactly the path in question.

**What it actually was: the mode CHOICE, not the chosen mode's arithmetic.** The user's first words
were the diagnosis -- *"the Auto for stretch mode didn't quite work"*, later sharpened to *"that is
mostly what i was complaining about, that it didn't chose the right mode"* -- and switching to Linked
by hand rendered the frame correctly. Two investigations went straight past that, both because they
asked what the curve DID instead of which mode should have been picked.

**The rule, in the one resolver** (`StretchModeExtensions.ResolveAuto`): a colour frame whose
background has already been extracted resolves **Linked**, even with nothing calibrated. The
justification is the resolver's own doc comment turned back on itself -- Unlinked exists to neutralise
a background that has NOT been neutralised. After a gradient correction it has been (the three
measured medians landed within 0.15 percent of each other), so Unlinked fits three curves to three
nearly identical inputs and what separates them is the noise rather than the sky: the MADs still
spanned 1.7x, and the channel with the smallest one took the largest 1/MAD gain and painted the frame.

- **Provenance, not inference.** `AstroImageDocument.BackgroundAlreadyExtracted`, set by the enhance
  path because both canonical programs run a `GradientCorrectionStep`. The alternative -- deciding
  from the pixels that three channel backgrounds "look equal enough" -- needs a threshold nobody can
  defend. **Its cost is a real gap: a frame flattened in ANOTHER tool and opened here carries no
  provenance and still gets the wrong answer.** That is the outstanding follow-up.
- **Ordered after the narrowband guard.** A non-photometric frame keeps Unlinked even when flattened:
  an HOO composite's channels are not brought into agreement by flattening (OIII sits in two of them),
  and what makes that case avoid Linked is a white balance fitted to a premise that never held, which
  flattening does not repair.
- **The toolbar label resolves from the same four inputs**, or the button names a mode the picture is
  not in.
- The new parameter defaults to false, so the Explorer thumbnail -- a separate caller of the same
  resolver, passing neither optional flag -- renders exactly as before. Pinned by
  `StretchAutoModeTests`, including that default-preservation case.

**Two hypotheses died on the way, and both are worth keeping written down:**

1. **The pedestal double-count -- REAL, fixed, and not this bug.**
   `GetPedestralMedianAndMADScaledToUnit` computes its median with `removePedestral: true` and then
   reports the pedestal ALONGSIDE it, so a consumer that subtracts the pedestal from that median
   subtracts it twice. Latent everywhere and silent while `MinValue` is 0; a gradient correction adds
   its model's median back per plane onto the pedestal field, which made it 0.019361 against a median
   of 0.00072 -- twenty-five times over -- and drove every channel negative. `Image.WithZeroPedestal`
   (hoisted from `MasterPreviewRenderer`, which had always made this call) fixes it, and **hoisting is
   what exposed that the original guard tested `MinValue` alone and ignored the pedestal**, so the
   stacking preview would have missed this same frame shape. Kept, with tests. **It did not fix the
   screen**, which is the point: it was a second real defect sitting on the same path.
2. **A per-channel MAD spread -- REFUTED by measurement.** The story was that Unlinked's per-channel
   curves diverge on the 1.7x MAD spread. Driving the MEASURED enhanced statistics through the real
   solver in Unlinked (`EnhancedFrameStretchTests`) renders the background neutral to within 0.02 --
   and the raw frame, which renders correctly, has a WIDER spread (1.83x). The test was written to
   confirm the hypothesis and killed it instead.

**The instrumentation is kept at the user's request** (*"the curves is always useful to check"*):
`ViewerController.LogStretchBasis` logs the statistics a stretch is solved from on every document
replacement (open / enhance / revert), and `ImageRendererBase.PreparedStretch` reports the derived
curve to the inspector. Two rules learned building it. It logs `Basis.PerChannelStats`, what the
SHADER gets, rather than what the document measured -- those differ whenever a display anchor is held,
and an anchor surviving an enhance was a fourth candidate nothing had considered. And logging the
INPUTS alone was not enough: it distinguished the four candidate faults and then could not see the
curve derived from them, which is exactly where the second investigation stalled.

## P31. Pan is dead in the strip where the collapsed file list used to be  (FIXED 2026-09-10)

Reported as *"even when the list is collapsed, pan doesn't work in that region (where it was
before)"* -- a band of the picture, with nothing drawn in it, in which a drag did nothing.

**Not a stale hit region.** `ClickableRegionTracker.BeginFrame()` clears regions every frame and hits
are honoured only while `RegionsAreCurrent`, so the obvious suspect was already impossible. It is the
scroll controller: its extent is set inside `RenderFileList`, which the caller gates on
`ViewerState.ShowFileList`, so a collapsed list leaves `_fileListScroll` holding the band the list
had. It viewport-gates against that stale extent, claims any press landing inside it as a scroll
gesture and returns true, so the press never reached the pan.

**The user's own diagnostic is what identified it**: *"funnily enough it doesn't behave like that in
the RIGHT panel, where you can drag even though you are on the panel"*. The info panel owns no scroll
controller, so a press there falls through and the image drags from on top of it -- same gesture,
opposite outcome, and only one of the two panels has a controller. That comparison went straight past
the region tracker to the one component that differs.

The fix is one gate, `HandleFileListScroll(state, evt) => state.ShowFileList && _fileListScroll.HandleInput(evt)`,
used by both the press and the move call sites (a collapsed list must not steer a drag that began on
the picture either). Gated rather than extent-zeroed, so there is ONE place that says **a hidden
widget consumes no input** -- the same rule the sky palette follows through `SkyBackdropActive`.
Pinned by two cases in `ViewerFileListResizeTests`: the collapsed band pans, and the OPEN list still
does not (the half a "never consult the controller" fix would cost). The list is painted open FIRST in
both, because that is what gives the controller its extent -- a viewer that never showed the list
cannot reproduce this at all.

## P32. A failed catalogue load would rethrow on every frame  (FIXED 2026-09-10)

Nobody reported this one. It came out of the user asking *"AsyncLazy&lt;T&gt;.Value you sure? any
pointers?"* about a claim in a code comment. The claim held -- `AsyncLazy<T>.Value` is
`Nullable<Result<T>>`, a non-blocking PEEK, which is what keeps the catalogue's build off the render
thread -- but checking it one level down found the real hazard: **`Result<T>.Value` RETHROWS when the
result is a failure** ("Extracts the actual result. Exception: this result is not successful"), and
three draw paths reached for it directly (the frame's object overlay, the context menu's
nearest-object lookup, the sky map's catalogue hand-off).

A catalogue that fails to load is an anticipated state, not a theoretical one -- the warm path has an
`onError` that puts the reason in the status bar. Before the fix, that same failure would then have
been rethrown from every subsequent frame instead of once. `ImageRendererBase.LoadedCatalog`
(`CelestialObjectDB?.Value is { } result && result.TryGet(out var db) ? db : null`) is now the only
read from a draw path: `TryGet` asks without throwing, and the null-while-building behaviour that
hides the build cost is unchanged.

**Two things worth keeping.** An `IsValueCreated` guard was added here first, for the wrong reason, and
reverted -- the peek needs no guard, and adding one implies the property blocks. And the challenge that
found this was aimed at a comment, not at a bug: the comment was right, and answering it properly is
what surfaced a defect one call deeper that no test and no session had reached.

## P33. The info strip: statistics roll up into a table, white balance moves to a toolbar popover  (DONE 2026-09-11)

**The ask.** "The statistics take a shitton of space and white balance should maybe be something
that can be uncollapsed"; then, on seeing the first cut, "stats look a bit bad now as we do not use
monospaced font anymore. We should either have a table or see if we can impl some clever 	 tab
stuff", "if there's a way to compress it a bit more", and for the white balance "maybe we actually
introduce a new button with three colour circles or something, that opens a menu with those three
sliders (and a reset)", which "can be in active state if wb is not 1/1/1".

**Statistics.** The section rolls up to its heading (`DrawCollapsibleHeading`, the equipment tab's
`[+]` / `[-]` convention, the whole heading row one click target), collapsed by default
(`ViewerState.InfoPanelStatisticsCollapsed`). Open, it is a TABLE (`InfoPanelData.GetStatisticsTable`
+ `DrawTable`): a header and one row per channel with mean, median, MAD and measured background, plus
a Luma row carrying only its background -- five rows for a colour frame where there were thirteen
lines. The old lines aligned their four rows per channel by padding with spaces, which lines nothing
up in a proportional face; the table draws each cell at a measured column stop, numbers right
aligned, so decimal points fall under one another whatever the face. Numbers are as short as their
size allows (`InfoPanelData.Compact`: four decimals below one, two to ten, one to a thousand, none
beyond; a null median or MAD is a dash).

**White balance.** Gone from the strip. `ToolbarAction.WhiteBalance` is a mark-only button in the
colour group (three overlapping discs in the Bayer swatch's red, green and blue), enabled for a
colour source, lit while the EFFECTIVE white balance -- the calibration composed with the manual
fine-tune, what the shader multiplies by -- is anything but neutral to a thousandth. It opens a
popover (`ImageRendererBase.WhiteBalancePanel.cs`, the strip's section moved whole: provenance line,
three sliders, Auto, Reset) that is a menu in everything but its contents: painted with the
dropdowns so its regions win; a full-window backdrop registered under it closes it on a press
anywhere else and consumes that press, the button included, which is how a second press closes what
the first opened; it claims the keyboard as it paints so Escape reaches it through the one claimant
check rather than a second Escape branch; and `OverlayOwnsPointer` names it, so chrome beneath does
not hover. Closed, it clears its slider bands: a drag where a track was does nothing.

**How it is pinned.** Through the hit tracker, not pixels (`ViewerInfoPanelCollapseTests`,
`ViewerWhiteBalancePopoverTests`): a rolled-up section or a closed popover has REGISTERED nothing
below its heading. Removing the band clearing on close fails exactly one test. Two test lessons: a
press between two frames lands on the regions the last paint registered, so a test that closes the
popover must render before pressing the button under its backdrop again; and a button's position in
a proportional face is not guessable, so the scan records the point it found each button at and
presses there.

**Not done.** The Wavelet Sharpen section keeps its always-open form (it only appears on the live
stacked view). A one-line summary on the rolled-up Statistics heading was considered and left out.

