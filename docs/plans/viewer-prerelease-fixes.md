# Viewer pre-release fixes

Defects found while packaging `tianwen-fits` as **Astro Photo Viewer** for the Microsoft Store
(see `packaging/windows/msix/README.md`). Packaging the viewer meant opening a folder of real
processed exports for the first time in a while, which is how a cluster of these surfaced at once:
a Store listing puts the viewer in front of people whose folders look like this one, so these are
release blockers rather than backlog.

Everything below was reproduced on
`C:\Users\<user>\OneDrive\Pictures\Astro\My` and `..\Tests` (15 TIFFs, 3 FITS).

## Status

| # | Item | State |
|---|------|-------|
| P1 | TIFF Predictor 2 never inverted -> images decode to their own derivative | **FIXED**, awaiting NuGet release |
| P2 | TIFF reader allocated per strip (2k-5.5k strips per file) | **FIXED** with P1 |
| P3 | Plate solve of a non-FITS document fails, and aborts the solver chain | NOT STARTED |
| P4 | CMYK TIFF renders as a negative | **BACKLOGGED** -> [todo/imaging](../todo/imaging.md) |
| P5 | LZW TIFF unsupported | NOT STARTED |
| P6 | Un-clicking Calibrate does not restore the previous / slider WB | NOT STARTED |
| P7 | File list is imperative: no declared regions, no cursor, no tooltip | NOT STARTED |
| P8 | A solver exception puts raw binary in the status bar | NOT STARTED |
| P9 | Stray text fragment at the left edge (needs confirmation) | UNCONFIRMED |
| P10 | Hand-off may select the adjacent row (needs confirmation) | UNCONFIRMED |

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

---

## Phasing

| Phase | Items | Rationale |
|-------|-------|-----------|
| A | P1, P2 | Done. Blocked only on a `SharpAstro.Tiff` release; TianWen's codec pin is a wildcard within the minor, so CI picks it up without a pin edit. |
| B | P3, P8 | Same user-visible failure, and P8 is the reason P3 looked like a parse bug rather than a solver-chain bug. |
| C | P7 | Self-contained, and it unblocks UI testing of everything else in the viewer. |
| D | P6 | Correctness of presentation. (P4 was here; backlogged 2026-08-20.) |
| E | P5 | Genuine feature work (an LZW decoder), and the only item whose absence is already documented scope. |
| F | P9, P10 | Reproduce first; do not fix from a single screenshot. |

## Verification

- **P1/P2**: `dotnet test tests/SharpAstro.Codecs.Tests --filter TiffPredictorTests` in `Codecs`,
  plus re-running the corpus sweep and confirming every predictor file lands under ~0.03 roughness.
  Already done; re-run after the release to confirm the packaged build picks it up.
- **P3**: plate-solve an open TIFF and a `.fz`, and confirm the log's `attempts` line names each
  solver and its reason rather than the run ending on an exception.
- **P7**: `describe_ui` must list one region per visible row with a label, and `click_label` on a
  filename must select it. That is the acceptance test the current pane fails.
- **P6**: visual, against the specific files named above.
